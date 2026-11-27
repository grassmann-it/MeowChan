using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

public sealed class QuickTagStampJoinedUndoTests
{
    [Fact]
    public async Task OneUndoRevertsEveryNewAssignmentAtTheOriginalOccurrences()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        PrivateTagStore store = new(profile.Path, lease);
        TagId a = store.Define("a").Tag, b = store.Define("b").Tag, c = store.Define("c").Tag;
        // The last post references the first file. Its assignment still belongs to a separate occurrence.
        MediaTagSubject[] subjects = Enumerable.Range(0, 41).Select(index => Subject(index, index % 40)).ToArray();
        MediaTagSubject outside = Subject(41, 0);
        store.SetMediaSubjects(a, [subjects[0], outside], true);
        store.SetMediaSubjects(b, [subjects[^1]], true);
        using QuickTagSession quick = Session(store, subjects, ["a", "b", "c"], undoDepth: 1);

        QuickTagStampResult result = await quick.ApplyStampJoinedAsync();

        Assert.True(result.Complete);
        Assert.Equal(3, result.Given);
        Assert.Equal(3, Assert.Single(quick.UndoSteps).Parts.Count);
        PrivateTagStoreSnapshot stamped = store.ReadSnapshot();
        foreach (MediaTagSubject subject in subjects)
            Assert.Equal(new[] { "a", "b", "c" }, stamped.TagsOf(subject).Select(tag => tag.Name.Display).Order());
        Assert.Equal(a, Assert.Single(stamped.TagsOf(outside)).Id);

        await quick.UndoAsync();

        PrivateTagStoreSnapshot restored = store.ReadSnapshot();
        Assert.False(quick.CanUndo);
        Assert.Equal(a, Assert.Single(restored.TagsOf(subjects[0])).Id);
        Assert.Equal(b, Assert.Single(restored.TagsOf(subjects[^1])).Id);
        Assert.Equal(a, Assert.Single(restored.TagsOf(outside)).Id);
        Assert.All(subjects[1..^1], subject => Assert.Empty(restored.TagsOf(subject)));
        Assert.NotNull(restored.Find(c));
    }

    [Fact]
    public async Task ARefusedWriteStopsImmediatelyAndTheSuccessfulPrefixIsOneUndoStep()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        PrivateTagStore store = new(profile.Path, lease);
        TagId a = store.Define("a").Tag, b = store.Define("b").Tag, c = store.Define("c").Tag, d = store.Define("d").Tag;
        MediaTagSubject subject = Subject(0, 0);
        List<TagId> attempted = new();
        using QuickTagSession quick = Session(store, [subject], ["a", "b", "c", "d"], undoDepth: 1,
            beforeWrite: (tag, assigned) =>
            {
                if (!assigned) return;
                attempted.Add(tag);
                if (tag == c) throw new PrivateTagStoreException(PrivateTagStoreError.CapacityExceeded);
            });

        QuickTagStampResult result = await quick.ApplyStampJoinedAsync();

        Assert.Equal(QuickTagStampOutcome.Partial, result.Outcome);
        Assert.Equal(2, result.Given);
        Assert.Equal(new[] { a, b, c }, attempted);
        Assert.Equal(MediaTagSession.MessageFor(PrivateTagStoreError.CapacityExceeded), quick.Status.MessageKey);
        Assert.Equal("a, b", quick.Status.Name);
        Assert.Equal(2, Assert.Single(quick.UndoSteps).Parts.Count);
        Assert.False(store.ReadSnapshot().Carries(d, subject));

        await quick.UndoAsync();
        Assert.Empty(store.ReadSnapshot().TagsOf(subject));
        Assert.False(quick.CanUndo);
    }

    [Fact]
    public async Task OrdinaryStampStillKeepsOneUndoStepPerTag()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        PrivateTagStore store = new(profile.Path, lease);
        TagId first = store.Define("first").Tag;
        store.Define("second");
        MediaTagSubject subject = Subject(0, 0);
        using QuickTagSession quick = Session(store, [subject], ["first", "second"]);

        Assert.Equal(2, await quick.ApplyStampAsync());
        Assert.Equal(2, quick.UndoSteps.Count);
        await quick.UndoAsync();

        Assert.Equal(first, Assert.Single(store.ReadSnapshot().TagsOf(subject)).Id);
        Assert.True(quick.CanUndo);
    }

    private static QuickTagSession Session(PrivateTagStore store, IReadOnlyList<MediaTagSubject> subjects, string[] names,
        int undoDepth = QuickTagOptions.DefaultUndoDepth, Action<TagId, bool>? beforeWrite = null)
    {
        MediaSelectionTagSession target = MediaSelectionTagSession.ForSubjects(subjects,
            () => new(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, targets, revision, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, targets, revision, token)),
            (tag, targets, assigned, revision, token) =>
            {
                beforeWrite?.Invoke(tag, assigned);
                return Task.FromResult(store.SetMediaSubjects(tag, targets, assigned, revision, token));
            });
        return new QuickTagSession(QuickTagTarget.For(target), undoDepth, new QuickTagStamp(names));
    }

    private static MediaTagSubject Subject(int postIndex, int fileIndex)
    {
        string hash = fileIndex.ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
        string post = (42 + postIndex).ToString(System.Globalization.CultureInfo.InvariantCulture);
        AttachmentRevisionReference reference = new(new ResourceId("fixture", "g", "42"), post, 0, "att-v1:" + hash);
        Assert.True(PrivateMediaTagTarget.TryCreate(reference, "image", "original", TransferMediaRole.Original, hash, out var target, out _));
        return new MediaTagSubject.Bound(target!);
    }
}
