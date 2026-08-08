# How an invited guest comes to have an account

This is the comparison issue #51 asks for, the choice it asks to be made, and the
account lifecycle that follows from the choice.

The question is not decoration. A share is for an invited guest, that guest signs
in, and there are no anonymous links, so an account has to exist before a link is
worth anything. What is open is who makes it, who holds its credential, and who
takes it away again.

Everything below about the server was read out of the packages this plugin
compiles against, at the version `Directory.Build.props` pins:

    grep -n 'JellyfinVersion' Directory.Build.props
    15:        <JellyfinVersion>10.11.11</JellyfinVersion>

## The two shapes

### The operator prepares the account

The operator creates an account on the server before sharing anything, hands the
guest its credential by whatever route they already use, and this plugin only
ever names an account that is already there.

The plugin owns nothing about that account. It reads a user by identifier and
writes a share record naming it. Creation, the credential, the sign-in path and
removal all stay where the server already put them, and the plugin's whole
surface is the share.

What it costs is the operator's time, once per guest, before a share can exist at
all, and a second thing that matters more. An account the operator made is an
account somebody may be using for their own watching. Narrowing it to the shape
`docs/guest-capabilities.md` sets would take away permissions its owner had, and
the end of a share could not remove it, because removing it would remove a person
the operator has. So this shape can confine and clean up nothing, and it is the
shape #58 exists for.

### The plugin creates the account with the invitation

Making a share makes the account it is for. The operator names a guest, and what
comes back is a link and a credential to send with it.

The plugin then owns the name, the initial credential, the account policy,
whether the account is disabled, and whether it is deleted. Each of those is a
surface it would not otherwise have, and the last one is the most destructive
thing this plugin can do to a server.

What it buys is that every account it acts on is an account it made. Confinement
takes nothing away from anybody, because the account had nothing before the
plugin gave it something. The end of a share can be the end of the account. And
the operator does nothing between deciding to share and having a link to send.

## The choice

The plugin creates the account, and it owns that account end to end. This is
decision 2 of #94, answered there rather than here, and the reasons above are why
it is the answer rather than an argument being reopened.

Three costs come with it and are named rather than left to be discovered.

The plugin holds a credential at the moment it mints one, which nothing in the
first shape ever does.

A guest account outlives nothing on its own. It is a real account on the server
until something removes it, so an operator who shares fifty things has fifty
accounts unless the removal below actually runs.

The plugin can delete an account. The guard that keeps it from deleting the wrong
one does not exist yet, and it is at the end of this document rather than left
implied.

## The lifecycle, end to end

### Creating it

The operator supplies the name the guest will be known by. The server makes the
account:

    grep -A7 'name="M:MediaBrowser.Controller.Library.IUserManager.CreateUserAsync(System.String)"' \
      ~/.nuget/packages/jellyfin.controller/10.11.11/lib/net9.0/MediaBrowser.Controller.xml
    <member name="M:MediaBrowser.Controller.Library.IUserManager.CreateUserAsync(System.String)">
        <summary>
        Creates a user with the specified name.
        </summary>
        <param name="name">The name of the new user.</param>
        <returns>The created user.</returns>
        <exception cref="T:System.ArgumentNullException"><paramref name="name"/> is <c>null</c> or empty.</exception>
        <exception cref="T:System.ArgumentException"><paramref name="name"/> already exists.</exception>

A name that is taken is refused back to the operator, who picks another one. The
plugin does not append a number to make the name unique, because a name the
plugin invented is a name nobody recognises in the server's user list, and that
list is where an operator goes to find out who these people are.

The policy is then set to the one `docs/guest-capabilities.md` writes down, which
is where every switch and its default is argued.

### The credential

The plugin mints it once, shows it once beside the link, and does not write it
down. What the server keeps of it is the server's business; this plugin keeps
nothing, so a copy of the store is not a set of credentials any more than it is a
set of links.

It comes from the routine that already draws token bytes, rather than from a
second draw of its own. That is not a preference. One routine draws from the
cryptographic generator and a second file drawing from it is refused:

    bash .github/scripts/enforce-greppable-invariants.sh
    ok        token-bytes-come-from-one-routine
    Every invariant held.

So the credential has the length, the encoding and the source that `ShareTokens`
decides, and it is 43 characters of base64url. That is an unpleasant thing to
type on a television remote, it is the honest cost of not opening a second
source of secret material, and it belongs on the list of awkward cases in #86
rather than being smoothed over here.

Setting it is `IUserManager.ChangePassword(System.Guid,System.String)`.

### Signing in

The ordinary server sign-in, on the server's own page. This plugin adds no
sign-in surface, no cookie of its own and no bearer of its own, and the guest
route takes the caller's identity from the server rather than from the link,
which is #53.

Whether an account this plugin created can authenticate on a server that has
been configured to delegate authentication elsewhere was not measured. It is the
one question an operator running such a server has to answer before relying on
any of this, and nothing here answers it for them.

### Forgetting it

The plugin offers no reset of its own, deliberately. A reset path belonging to
the plugin would be a second way into an account the plugin created, and the
people who would find it are the people who have the link.

The server carries `StartForgotPasswordProcess`, `RedeemPasswordResetPin` and
`ResetPassword` on the same interface as everything above. How each behaves on a
given install was not measured here. What an operator does in practice is set the
guest's password again from the server's own user page, or revoke the share and
issue a new one, and the second is the better answer when the credential may have
gone somewhere it should not have.

### While the share is live

Nothing. The plugin does not touch the account again until the share ends.

### When the share ends

Expiry and revocation end a share the same way here. When the last live share
naming an account has ended, the account is disabled rather than deleted:
`IsDisabled` on the policy, which `docs/guest-capabilities.md` already lists.

Disabled rather than deleted, because deletion is not reversible and an operator
who revoked the wrong share has nothing to put back. The account stops working at
that moment, which is the property revocation actually needs.

Deletion follows the record. When the last record naming the account is deleted
under the retention rule, the account goes with it. The retention length is
ninety days by decision 8 of #94 and is a setting rather than a constant, which
is #29, and what a record holds about a person until then is #31.

The last live share matters and not the first. An account named by two shares
stays live while either does, or revoking one share would quietly break the
other.

### The thing that has to exist before any of this deletes anything

A record names its invited accounts and says nothing about where they came from:

    git grep -n 'InvitedUserIds\|CreatedByUserId' -- Jellyfin.Plugin.ShareLinks/ShareRecord.cs
    Jellyfin.Plugin.ShareLinks/ShareRecord.cs:95:    public required IReadOnlyList<Guid> InvitedUserIds { get; init; }
    Jellyfin.Plugin.ShareLinks/ShareRecord.cs:105:    public required Guid CreatedByUserId { get; init; }

`CreatedByUserId` is the operator who made the share, not the provenance of the
guests. So a list of identifiers is all the removal path would have, and an
identifier in that list naming an account this plugin did not create is an
account this plugin would delete. A store carried forward from before this
decision is one way that happens, and a record edited by hand is another.

Until the record carries that fact, nothing may delete an account. #144 is where
the field, the migration for records written without it, and the refusal are
held.

## What this document is, and what it is not

It is a decision and a lifecycle. It is not code. Nothing in the tree creates,
credentials, disables or deletes an account:

    git grep -n 'IUserManager' -- Jellyfin.Plugin.ShareLinks Jellyfin.Plugin.ShareLinks.Tests ; echo "exit=$?"
    exit=1

What would prove the lifecycle rather than state it is the create route in #67,
the confinement in #52, the revocation in #46 and the removal guard in #144.
Every one of those is open, and this document is what they are built against.
