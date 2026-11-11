using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

public sealed class MediaTagKeyCarryOverTests
{
    private static readonly ResourceId Thread = new("source", "board", "42");
    private const string Sha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string OtherSha = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";

    private static string Key(char c) => "att-v2:" + new string(c, 64);

    private static MediaAttachmentBinding Binding(char key, string post = "100", int position = 0, string asset = "asset-1",
        string sha = Sha, string url = "https://example.test/f.png", ResourceId? thread = null)
        => new(new AttachmentRevisionReference(thread ?? Thread, post, position, Key(key)), "acq-" + key, asset,
            new MediaProviderMetadata("m1", "original", url, "f.png", null, null, null),
            new MediaActualIntegrity(5, "00", sha),
            new MediaAcceptanceRecord(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch));

    private static Dictionary<string, MediaAttachmentBinding> Bindings(params MediaAttachmentBinding[] items)
        => items.ToDictionary(item => item.Reference.RevisionKey, StringComparer.Ordinal);

    [Fact]
    public void ACommitHandsOverFromEveryEarlierKeyOfTheSameAttachmentAndBytesOnly()
    {
        var bindings = Bindings(Binding('a'), Binding('b'), Binding('c', sha: OtherSha), Binding('d', position: 1),
            Binding('e', asset: "asset-2"), Binding('f', url: "https://example.test/g.png"), Binding('g', post: "101"));
        var fresh = bindings[Key('b')].Reference;

        var handovers = MediaTagKeyCarryOver.Handovers(bindings, [fresh]);

        MediaTagKeyHandover only = Assert.Single(handovers);
        Assert.Equal(Key('a'), only.FromRevisionKey);
        Assert.Equal(fresh, only.To);
        Assert.Equal(Sha, only.Sha256);
        // A reference that is not (or no longer) bound the same way hands nothing over.
        Assert.Empty(MediaTagKeyCarryOver.Handovers(bindings, [fresh with { RevisionKey = Key('z') }]));
    }

    [Fact]
    public void AReconcileOnlyHandsOverFromKeysThatCarryTagsWithinOneChain()
    {
        var bindings = Bindings(Binding('a'), Binding('b'), Binding('c'), Binding('d', sha: OtherSha), Binding('e', position: 1));

        var handovers = MediaTagKeyCarryOver.ReconcileHandovers(bindings, new HashSet<string> { Key('a'), Key('d'), Key('e') });

        // a -> b and a -> c; d and e are alone in their chains (other bytes, other position).
        Assert.Equal(new[] { (Key('a'), Key('b')), (Key('a'), Key('c')) },
            handovers.Select(item => (item.FromRevisionKey, item.To.RevisionKey)).Order().ToArray());
        Assert.All(handovers, item => Assert.Equal(Sha, item.Sha256));
        Assert.Empty(MediaTagKeyCarryOver.ReconcileHandovers(bindings, new HashSet<string>()));
        // A tag on a later key reaches the earlier ones too: the rule has no direction, and applying it twice is a no-op.
        Assert.Equal(2, MediaTagKeyCarryOver.ReconcileHandovers(bindings, new HashSet<string> { Key('c') }).Count);
    }

    [Fact]
    public async Task APendingScanOpensOnlyThreadsThatCarryMediaTags()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId tag = store.Define("cat").Tag;
        ResourceId second = new("source", "board", "43");
        foreach (ResourceId thread in new[] { Thread, second })
            Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(thread, "100", 0, Key('a')), "m1", "original",
                TransferMediaRole.Original, Sha, out PrivateMediaTagTarget? target, out _) && store.SetMedia(tag, target!, true).Changed);
        store.Define("unused");
        List<ResourceId> asked = new();

        var pending = await MediaTagKeyCarryOver.FindPendingAsync(store.ReadSnapshot(), (thread, _) =>
        {
            asked.Add(thread);
            return Task.FromResult<IReadOnlyList<string>>(new[] { Path.Combine(temp.Path, "no-such-manifest-directory") });
        }, CancellationToken.None);

        // Exactly the two tagged threads were looked at; a directory without a manifest is skipped, not an error.
        Assert.Equal(new[] { Thread, second }.OrderBy(item => item.Thread), asked.OrderBy(item => item.Thread));
        Assert.Empty(pending);
        using TempDirectory empty = new();
        using ProfileLease emptyLease = ProfileLease.Acquire(empty.Path);
        Assert.Empty(await MediaTagKeyCarryOver.FindPendingAsync(new PrivateTagStore(empty.Path, emptyLease).ReadSnapshot(),
            (_, _) => throw new InvalidOperationException("an untagged profile must not open any thread"), CancellationToken.None));
    }
}
