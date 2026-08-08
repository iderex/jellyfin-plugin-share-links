using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ShareLinks;

/// <summary>
/// Reads and writes the file the share records live in (#35).
/// </summary>
/// <remarks>
/// <para>
/// Where the file lives and why it is a file of the plugin's own rather than the
/// configuration or the server's database is <c>docs/share-store.md</c>. This
/// type is the other half of that choice: having taken responsibility for the
/// write, it has to make one that a crash or a second request cannot leave
/// half-done.
/// </para>
/// <para>
/// The failure being defended against is not subtle. A store truncated by a
/// process that died between opening the file and finishing the write is every
/// live share gone at once, and it is silent: the next read succeeds and returns
/// nothing, which is indistinguishable from a server nobody has shared anything
/// on. That is why <see cref="ReadAsync"/> refuses a file it cannot parse instead
/// of returning an empty list.
/// </para>
/// <para>
/// Three rules, each of which has a test that goes red when it is removed.
/// </para>
/// <para>
/// A write never touches the destination until it is complete. The bytes go to a
/// temporary file in the same directory, are flushed through to the device, and
/// only then does the file take the destination's name. A rename within one
/// directory is the cheapest operation either platform offers that a reader
/// cannot observe halfway through, and the temporary file is a sibling rather
/// than a file in the temporary directory because a rename across volumes is a
/// copy, which is exactly what this is avoiding.
/// </para>
/// <para>
/// Writers are serialised. <see cref="MutateAsync"/> reads, applies the change
/// and writes while holding a semaphore, so two administrators creating a share
/// in the same moment cannot both read the same list and write it back with one
/// of the two records missing. The bound on that is stated below and is real: the
/// semaphore is one process's.
/// </para>
/// <para>
/// Readers do not queue behind writers. <see cref="ReadAsync"/> takes no lock at
/// all and opens the file sharing read, write and delete, which is what lets the
/// rename replace a file somebody is reading rather than failing on Windows
/// because a handle is open. A reader either sees the whole of the old file or
/// the whole of the new one; the rename is what makes those the only two answers.
/// </para>
/// <para>
/// What this does not defend against, said rather than left to be discovered. The
/// semaphore is an object in one process, so two servers pointed at one data
/// folder still lose updates; that is not a deployment this plugin supports and
/// nothing here detects it. A filesystem that reorders a rename ahead of the data
/// it renames can still present a truncated file after a power cut, which is why
/// the flush is a flush to the device and not to the operating system's cache,
/// and beyond that it is the filesystem's guarantee rather than this code's. And
/// the format written here is a plain array: the schema version each record
/// carries is #33's, and what reads an older store or refuses a newer one is #37,
/// which this type takes no position on beyond needing bytes to write.
/// </para>
/// </remarks>
public class ShareStore : IDisposable
{
    private readonly SemaphoreSlim _writers = new SemaphoreSlim(1, 1);
    private readonly string _path;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareStore"/> class.
    /// </summary>
    /// <param name="path">The full path of the file the records are written to. Its directory is created if it is missing.</param>
    public ShareStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>
    /// Gets the full path of the file this store reads and writes.
    /// </summary>
    public string Path => _path;

    /// <summary>
    /// Gets the serialiser settings both directions use.
    /// </summary>
    /// <remarks>
    /// One property rather than two call sites, so a round trip cannot be broken
    /// by a setting that was added to the write and forgotten on the read.
    /// </remarks>
    protected static JsonSerializerOptions SerializerOptions { get; } = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Releases the writer lock this store holds.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Reads every record in the store.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The records, or an empty list when the store has never been written.</returns>
    /// <exception cref="ShareStoreUnreadableException">The file exists and does not parse. An empty list is not returned for this case, because a store that lost its contents and a server nobody has shared anything on must not look the same.</exception>
    public async Task<IReadOnlyList<ShareRecord>> ReadAsync(CancellationToken cancellationToken = default)
    {
        // No semaphore here on purpose. A read that waited for the writer would
        // make every listing wait behind whatever share is being created, and the
        // rename below is what makes the lock unnecessary rather than an
        // optimisation somebody removed.
        FileStream stream;
        try
        {
            stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                // Delete matters as much as the other two. Without it, a handle
                // held by a reader refuses the rename on Windows, and the write
                // fails for the duration of somebody else's listing.
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<ShareRecord>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<ShareRecord>();
        }

        await using (stream.ConfigureAwait(false))
        {
            try
            {
                var records = await JsonSerializer.DeserializeAsync<List<ShareRecord>>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
                if (records is null)
                {
                    throw new ShareStoreUnreadableException(_path, "the file holds a JSON null rather than a list of records");
                }

                return new ReadOnlyCollection<ShareRecord>(records);
            }
            catch (JsonException error)
            {
                throw new ShareStoreUnreadableException(_path, error.Message, error);
            }
        }
    }

    /// <summary>
    /// Reads the store, applies a change to what was read, and writes the result back.
    /// </summary>
    /// <param name="change">Takes the records currently in the store and returns the records that should replace them. It is called while this store's writer lock is held, so it sees a list no other writer of this process can be changing underneath it.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>The records that were written.</returns>
    /// <remarks>
    /// The read is inside the lock rather than outside it, which is the whole
    /// point. A caller that reads, thinks, and then calls a plain write has
    /// already lost the update somebody else made in between, and no amount of
    /// locking around the write alone repairs that.
    /// </remarks>
    public async Task<IReadOnlyList<ShareRecord>> MutateAsync(
        Func<IReadOnlyList<ShareRecord>, IReadOnlyList<ShareRecord>> change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _writers.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            var next = change(current) ?? throw new InvalidOperationException("The change returned no list. Return an empty list to empty the store.");
            await WriteAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _writers.Release();
        }
    }

    /// <summary>
    /// Writes the records to an open stream.
    /// </summary>
    /// <param name="destination">The temporary file the records are written to before it is renamed over the store.</param>
    /// <param name="records">The records to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the records have been written to <paramref name="destination"/>.</returns>
    /// <remarks>
    /// This exists as a seam and says so. The interruption this type is written
    /// against is a process that dies partway through producing the bytes, and a
    /// test cannot kill the process it is running in. Overriding this is how the
    /// suite stops halfway through a real write against a real temporary file and
    /// then asks what the store still holds.
    /// </remarks>
    protected virtual async Task WriteRecordsAsync(Stream destination, IReadOnlyList<ShareRecord> records, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(destination, records, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the writer lock this store holds.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/> rather than from a finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _writers.Dispose();
        }

        _disposed = true;
    }

    private async Task WriteAsync(IReadOnlyList<ShareRecord> records, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A sibling of the destination, not a file in the temporary directory. A
        // rename across volumes is a copy, and a copy is the half-written state
        // this whole routine exists to avoid. The suffix carries a fresh
        // identifier so two writers, or a leftover from a run that died, cannot
        // be mistaken for each other.
        var temporary = string.Create(
            CultureInfo.InvariantCulture,
            $"{_path}.{Guid.NewGuid():N}.tmp");

        try
        {
            var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);

            await using (stream.ConfigureAwait(false))
            {
                await WriteRecordsAsync(stream, records, cancellationToken).ConfigureAwait(false);

                // Through the operating system's cache and onto the device. A
                // flush that stops at the cache leaves a rename that can be
                // durable while the bytes it renamed are not, which is the power
                // cut version of the failure this type is about.
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                // CA1849 asks for the awaitable flush, and the awaitable flush is
                // the one on the line above: FlushAsync empties this stream's own
                // buffer into the operating system and stops there. Flush(true) is
                // the only call in the library that asks the device, and there is
                // no asynchronous spelling of it to prefer. Removing it would leave
                // the rename durable while the bytes it renames are not, which is
                // the failure the temporary file exists against.
#pragma warning disable CA1849
                stream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }

            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            // The destination has not been touched at this point, so the store
            // still holds what it held. Take the debris with us rather than
            // leaving a directory that fills up with the failures.
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The store is intact either way, and a cleanup that throws would
            // replace the caller's real failure with this one.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
