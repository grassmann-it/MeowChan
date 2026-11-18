using MeowChan.Interfaces;
using MeowChan.Persistence;
using Xunit.Abstractions;

namespace MeowChan.Tests.PersistenceCorruption;

/// <summary>post-filters.json: like the saved searches, a damaged file loads as a non-writable state (with the good backup offered), never as an empty list.</summary>
internal sealed class PostFilterStoreAdapter : StoreAdapter
{
    internal override string Name => "post-filters";
    internal override string PrimaryName => PostFilterStore.FileName;
    internal override string VersionProperty => "schemaVersion";
    internal override long ReadCeilingBytes => PostFilterStore.MaximumFileBytes;
    internal override StorePolicy Policy => StorePolicy.OfferRecovery;
    // The store opens the final link like the file it names; a write replaces the link, never the file behind it.
    internal override bool FollowsSymbolicLinks => true;
    // An unknown envelope member or a newer schema keeps the document read-only: nothing is lost, so no recovery is offered.
    internal override bool RecoveryExpected(string variant) => variant is not ("future-version" or "unknown-extra-field");

    internal override void Seed(string directory, ProfileLease lease)
    {
        PostFilterStore store = new(directory, lease);
        PostFilterSnapshot first = store.Load();
        PostFilterSnapshot second = store.AddRule(Guid.NewGuid(), new(PostFilterField.Text, PostFilterMatch.Contains, "rule " + Secret, PostFilterAction.Hide, false), first.Revision);
        store.HidePost(new ResourceId("4chan", "g", "100"), "101", false, new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), second.Revision);
    }

    private static string Fingerprint(PostFilterSnapshot snapshot) =>
        snapshot.Revision + ":" + snapshot.HiddenPostsAction + ":" + string.Join("|", snapshot.Rules.Select(rule => rule.Id + "=" + rule.Pattern)) +
        ":" + string.Join("|", snapshot.HiddenPosts.Select(post => post.Thread + "/" + post.Post));

    internal override ReadResult Read(string directory, ProfileLease lease)
    {
        try
        {
            PostFilterSnapshot snapshot = new PostFilterStore(directory, lease).Load();
            return snapshot.State switch
            {
                PostFilterStoreState.Ready => ReadResult.Of(Fingerprint(snapshot)),
                PostFilterStoreState.RecoveryAvailable => ReadResult.Offered(Fingerprint(snapshot)),
                _ => ReadResult.Unusable()
            };
        }
        catch (Exception error) { return ReadResult.Unusable(error); }
    }

    internal override void Mutate(string directory, ProfileLease lease)
    {
        PostFilterStore store = new(directory, lease);
        PostFilterSnapshot current = store.Load();
        store.AddRule(Guid.NewGuid(), new(PostFilterField.Subject, PostFilterMatch.Exact, "third", PostFilterAction.Stub, false), current.Revision);
    }
}

public sealed class PostFilterStoreCorruptionTests(ITestOutputHelper log) : BackedStoreCorruptionTests(log)
{
    internal override StoreAdapter Adapter { get; } = new PostFilterStoreAdapter();

    // A damaged .bak stops the commit with the store's own error; neither file changes and no message names the profile path.
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated-half")]
    [InlineData("trailing-garbage")]
    [InlineData("invalid-utf8-in-string")]
    public void ADamagedBackupIsRefusedWithTheStoresOwnErrorRatherThanAParserException(string variant)
    {
        PostFilterStoreAdapter adapter = new();
        CorruptionScenarios.DamagedBackup(adapter, variant);
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        adapter.Seed(temp.Path, lease);
        File.WriteAllBytes(adapter.BackupPath(temp.Path), Damage.Make(variant, File.ReadAllBytes(adapter.BackupPath(temp.Path)), "schemaVersion"));
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => adapter.Mutate(temp.Path, lease));
        Assert.DoesNotContain(temp.Path, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryInPlaceOfTheFileFailsWithoutNamingTheProfilePath()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        Directory.CreateDirectory(Path.Combine(temp.Path, PostFilterStore.FileName));
        PostFilterSnapshot loaded = new PostFilterStore(temp.Path, lease).Load();
        Assert.False(loaded.CanWrite);
        Assert.DoesNotContain(temp.Path, loaded.Error ?? "", StringComparison.Ordinal);
    }
}
