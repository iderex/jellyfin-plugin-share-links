using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ShareLinks;
using Xunit;

namespace Jellyfin.Plugin.ShareLinks.Tests;

/// <summary>
/// What #35 asks of the store: a write that cannot be observed half done, writers
/// that cannot lose each other's work, and readers that do not queue behind
/// either.
/// </summary>
/// <remarks>
/// Every test here works in a directory it makes under the temporary directory
/// and removes afterwards. Nothing starts a server, nothing needs a privilege,
/// and nothing reaches the network, which is the rule <c>docs/testing.md</c>
/// states and this file obeys rather than restates.
/// </remarks>
public sealed class ShareStoreTests : IDisposable
{
    private readonly string _directory;

    public ShareStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "share-links-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover directory under the temporary directory is not worth
            // failing a green suite over.
        }
    }

    private string StorePath => Path.Combine(_directory, "shares.json");

    private static ShareRecord ARecord(string name) => new ShareRecord
    {
        SchemaVersion = ShareRecord.CurrentSchemaVersion,
        Id = Guid.NewGuid(),
        ItemId = Guid.NewGuid(),
        InvitedUserIds = new[] { Guid.NewGuid() },
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ExpiresAt = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
        TokenHash = name,
    };

    private static IReadOnlyList<string> NamesIn(IReadOnlyList<ShareRecord> records)
        => records.Select(record => record.TokenHash).OrderBy(name => name, StringComparer.Ordinal).ToList();

    [Fact]
    public async Task AStoreThatWasNeverWrittenReadsAsNoRecords()
    {
        var store = new ShareStore(StorePath);

        Assert.Empty(await store.ReadAsync());
    }

    [Fact]
    public async Task WhatIsWrittenIsWhatComesBack()
    {
        var store = new ShareStore(StorePath);

        await store.MutateAsync(_ => new[] { ARecord("one"), ARecord("two") });

        Assert.Equal(new[] { "one", "two" }, NamesIn(await store.ReadAsync()));
    }

    [Fact]
    public async Task TheChangeSeesWhatIsAlreadyThere()
    {
        var store = new ShareStore(StorePath);

        await store.MutateAsync(_ => new[] { ARecord("first") });
        await store.MutateAsync(existing => existing.Append(ARecord("second")).ToList());

        Assert.Equal(new[] { "first", "second" }, NamesIn(await store.ReadAsync()));
    }

    /// <summary>
    /// The lost update, which is the ordinary failure rather than the exotic one.
    /// </summary>
    /// <remarks>
    /// Sixty writers, each appending one record, all started before any of them is
    /// awaited. Without the writer lock each one reads a list, appends to it and
    /// writes it back, and every pair that overlapped keeps one of the two
    /// records. The assertion is on the whole set rather than on the count, so a
    /// run that kept sixty records by writing one of them twice is red too.
    /// </remarks>
    [Fact]
    public async Task EveryRecordWrittenByManyTasksAtOnceSurvives()
    {
        var store = new ShareStore(StorePath);
        var expected = Enumerable.Range(0, 60).Select(i => "writer-" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture)).ToList();

        await Task.WhenAll(expected.Select(name => Task.Run(async () =>
            await store.MutateAsync(existing => existing.Append(ARecord(name)).ToList()))));

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal).ToList(), NamesIn(await store.ReadAsync()));
    }

    /// <summary>
    /// A write that stops partway through producing its bytes.
    /// </summary>
    /// <remarks>
    /// This is the server killed mid-write, modelled at the only place a test can
    /// stand: the seam the store exposes for it. The bytes that were produced are
    /// real bytes in a real file on the disk. What the test then asserts is that
    /// the store still holds the records from before, and that reading it does not
    /// throw, because a store that survives the crash and cannot be read
    /// afterwards has not survived it.
    /// </remarks>
    [Fact]
    public async Task AWriteThatDiesPartwayLeavesThePreviousRecordsIntactAndReadable()
    {
        var store = new ShareStore(StorePath);
        await store.MutateAsync(_ => new[] { ARecord("before") });

        var dying = new StoreThatDiesPartwayThroughWriting(StorePath);
        await Assert.ThrowsAsync<IOException>(() => dying.MutateAsync(_ => new[] { ARecord("after") }));

        Assert.Equal(new[] { "before" }, NamesIn(await store.ReadAsync()));
    }

    [Fact]
    public async Task AWriteThatDiesPartwayLeavesNothingBesideTheStore()
    {
        var store = new ShareStore(StorePath);
        await store.MutateAsync(_ => new[] { ARecord("before") });

        var dying = new StoreThatDiesPartwayThroughWriting(StorePath);
        await Assert.ThrowsAsync<IOException>(() => dying.MutateAsync(_ => new[] { ARecord("after") }));

        Assert.Equal(new[] { "shares.json" }, Directory.GetFiles(_directory).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// A read while a write is in flight.
    /// </summary>
    /// <remarks>
    /// The write is parked inside the seam with the writer lock held, and the read
    /// is asked of the same store from another task. It returns the records from
    /// before the write, promptly, which is the whole claim: a listing does not
    /// wait for whatever share is being created.
    /// <para>
    /// The read goes to the instance that is parked, not to a second instance on
    /// the same path, and that is the whole of what makes this test bite. The lock
    /// is an object one store owns, so a read asked of a different instance is not
    /// contending for it and would come back promptly however the read is written.
    /// The first version of this test did that and passed with a read that took
    /// the writer lock, which is the failure it was written to refuse.
    /// </para>
    /// <para>
    /// The passing run does not wait at all. The ten seconds are what a run whose
    /// read DOES queue behind the writer spends before it is called red, and they
    /// are here so that such a run fails with a sentence rather than hanging the
    /// test host until something else kills it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReadDoesNotQueueBehindAWriteInFlight()
    {
        var seed = new ShareStore(StorePath);
        await seed.MutateAsync(_ => new[] { ARecord("before") });

        var parked = new StoreThatParksInsideItsWrite(StorePath);
        var write = parked.MutateAsync(_ => new[] { ARecord("after") });
        await parked.HasReachedTheWrite;

        var read = parked.ReadAsync();
        var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(ReferenceEquals(finished, read), "the read did not finish while a write was in flight, so readers are queueing behind writers");
        Assert.Equal(new[] { "before" }, NamesIn(await read));

        parked.LetTheWriteFinish();
        await write;
        Assert.Equal(new[] { "after" }, NamesIn(await parked.ReadAsync()));
    }

    /// <summary>
    /// A store whose bytes are damaged is refused rather than read as empty.
    /// </summary>
    /// <remarks>
    /// Returning an empty list here is the failure this whole type is written
    /// against wearing the costume of handling it: the caller cannot tell a
    /// server with no shares from a server that lost all of them, and the first
    /// thing it does with the answer is write a fresh empty store over whatever
    /// was left.
    /// </remarks>
    [Fact]
    public async Task AStoreThatCannotBeParsedIsRefusedRatherThanReadAsEmpty()
    {
        await File.WriteAllTextAsync(StorePath, "[{\"SchemaVersion\": 1, \"Id\":");
        var store = new ShareStore(StorePath);

        var refused = await Assert.ThrowsAsync<ShareStoreUnreadableException>(() => store.ReadAsync());

        Assert.Equal(StorePath, refused.Path);
        Assert.Contains("not being treated as an empty store", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStoreCanBeEmptied()
    {
        var store = new ShareStore(StorePath);
        await store.MutateAsync(_ => new[] { ARecord("one") });

        await store.MutateAsync(_ => Array.Empty<ShareRecord>());

        Assert.Empty(await store.ReadAsync());
    }

    [Fact]
    public async Task TheDirectoryIsMadeWhenItIsMissing()
    {
        var nested = Path.Combine(_directory, "made", "by", "the", "store", "shares.json");
        var store = new ShareStore(nested);

        await store.MutateAsync(_ => new[] { ARecord("one") });

        Assert.Equal(new[] { "one" }, NamesIn(await store.ReadAsync()));
    }

    /// <summary>
    /// Writes some of the bytes and then fails, the way a process that is killed does.
    /// </summary>
    private sealed class StoreThatDiesPartwayThroughWriting : ShareStore
    {
        public StoreThatDiesPartwayThroughWriting(string path)
            : base(path)
        {
        }

        protected override async Task WriteRecordsAsync(Stream destination, IReadOnlyList<ShareRecord> records, CancellationToken cancellationToken)
        {
            var half = System.Text.Encoding.UTF8.GetBytes("[{\"SchemaVersion\": 1, \"Id\": \"");
            await destination.WriteAsync(half, cancellationToken);
            await destination.FlushAsync(cancellationToken);
            throw new IOException("the process died here");
        }
    }

    /// <summary>
    /// Stops inside the write, holding the writer lock, until it is let go.
    /// </summary>
    private sealed class StoreThatParksInsideItsWrite : ShareStore
    {
        private readonly TaskCompletionSource _reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public StoreThatParksInsideItsWrite(string path)
            : base(path)
        {
        }

        public Task HasReachedTheWrite => _reached.Task;

        public void LetTheWriteFinish() => _released.TrySetResult();

        protected override async Task WriteRecordsAsync(Stream destination, IReadOnlyList<ShareRecord> records, CancellationToken cancellationToken)
        {
            _reached.TrySetResult();
            await _released.Task;
            await base.WriteRecordsAsync(destination, records, cancellationToken);
        }
    }
}
