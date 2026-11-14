using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

// TP3: the adapter between the offline context ranking (TP0) and the suggestion strip of the quick tag surface.
public sealed class QuickTagContextSourceTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private static PrivateTagDefinition Tag(string name)
    {
        Assert.True(PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _));
        return new PrivateTagDefinition(new TagId(Guid.NewGuid()), parsed!);
    }

    private static ResourceId Thread(string id) => new("source", "board", id);
    private static string Hex(char value) => new(value, 64);

    private static PrivateMediaTagTarget Media(string thread, string post, int position, string sha)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread(thread), post, position, "att-v2:" + Hex('a')),
            "m", "original", TransferMediaRole.Original, sha, out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    private static PrivateTagStoreSnapshot Snap(IEnumerable<PrivateTagDefinition> tags,
        IEnumerable<(PrivateTagDefinition Tag, string Thread)>? threads = null,
        IEnumerable<(PrivateTagDefinition Tag, PrivateMediaTagTarget Target)>? media = null) => new(1, "fp", tags,
        (threads ?? Array.Empty<(PrivateTagDefinition, string)>()).Select(item => new PrivateThreadTagAssignment(item.Tag.Id, Thread(item.Thread))),
        (media ?? Array.Empty<(PrivateTagDefinition, PrivateMediaTagTarget)>()).Select(item => new PrivateMediaTagAssignment(item.Tag.Id, item.Target)),
        null, Array.Empty<PrivateLocalMediaTagAssignment>());

    private static QuickTagSuggestionRequest Request(PrivateTagStoreSnapshot snapshot, int limit = 9, params TagId[] excluded) =>
        new(snapshot, new HashSet<TagId>(excluded), limit);

    // ---- mapping ----------------------------------------------------------------------------------------------------

    [Fact]
    public void EveryRankingReasonMapsOntoAStripReasonAndNoneIsLost()
    {
        Assert.Equal(QuickTagReason.SameFile, QuickTagContextSource.Map(TagSuggestionReason.SameFile));
        Assert.Equal(QuickTagReason.Thread, QuickTagContextSource.Map(TagSuggestionReason.ThreadTag));
        Assert.Equal(QuickTagReason.Thread, QuickTagContextSource.Map(TagSuggestionReason.ThreadMedia));
        Assert.Equal(QuickTagReason.SourceTag, QuickTagContextSource.Map(TagSuggestionReason.SourceTag));
        Assert.Equal(QuickTagReason.Recent, QuickTagContextSource.Map(TagSuggestionReason.Recent));
        Assert.Equal(QuickTagReason.Neighbour, QuickTagContextSource.Map(TagSuggestionReason.Neighbour));
        Assert.Equal(QuickTagReason.FileName, QuickTagContextSource.Map(TagSuggestionReason.FileName));
        Assert.Equal(QuickTagReason.Title, QuickTagContextSource.Map(TagSuggestionReason.ThreadTitle));
        Assert.Equal(QuickTagReason.Title, QuickTagContextSource.Map(TagSuggestionReason.SlashMarker));
        foreach (TagSuggestionReason reason in Enum.GetValues<TagSuggestionReason>())
            Assert.True(Enum.IsDefined(QuickTagContextSource.Map(reason)), reason.ToString());
    }

    [Fact]
    public void SuggestRanksByContextAndOffersTagNameScoreReasonAndUses()
    {
        PrivateTagDefinition thread = Tag("thr"), other = Tag("other"), same = Tag("same");
        string sha = Hex('1');
        PrivateTagStoreSnapshot snapshot = Snap(new[] { thread, other, same }, threads: new[] { (thread, "cur") },
            media: new[] { (same, Media("elsewhere", "1", 0, sha)), (other, Media("else2", "1", 0, Hex('7'))) });
        using QuickTagContextSource source = new(new TagContext { Thread = Thread("cur"), Sha256 = sha }, new TagRecentList());

        IReadOnlyList<QuickTagSuggestion> shown = source.Suggest(Request(snapshot));

        Assert.Equal(new[] { "same", "thr" }, shown.Take(2).Select(item => item.Name.Display));
        Assert.Equal(QuickTagReason.SameFile, shown[0].Reason);
        Assert.Equal(QuickTagReason.Thread, shown[1].Reason);
        Assert.Equal(same.Id, shown[0].Id);
        Assert.True(shown[0].Score > shown[1].Score);
        Assert.Equal(1, shown[0].Uses);
        Assert.DoesNotContain(shown, item => item.Reason == QuickTagReason.SameFile && item.Name.Display == "other");
    }

    [Fact]
    public void ExcludedTagsAndTheLimitAreHonouredAndAnEmptyContextFallsBackToUse()
    {
        PrivateTagDefinition a = Tag("a"), b = Tag("b"), c = Tag("c");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { a, b, c }, threads: new[] { (a, "t1"), (b, "t1"), (c, "t1") });
        using QuickTagContextSource source = new(new TagContext { Thread = Thread("t1") }, new TagRecentList());

        IReadOnlyList<QuickTagSuggestion> limited = source.Suggest(Request(snapshot, limit: 2));
        Assert.Equal(2, limited.Count);

        IReadOnlyList<QuickTagSuggestion> without = source.Suggest(Request(snapshot, 9, a.Id));
        Assert.DoesNotContain(without, item => item.Id == a.Id);
        Assert.Equal(2, without.Count);
    }

    [Fact]
    public async Task AnAcceptedAssignmentFeedsTheRecentListButARemovalDoesNot()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = new(new ResourceId("s", "b", "a"), "Ta", 1, Day, Day, "1", 1, null, 0,
            "att-v1:" + Hex('a'), "/dir/a", "asset-a", Hex('a'), 1, "m1", "original", "fa.png", SavedMediaKind.Image,
            MediaLibraryPresence.Present, false, false);
        MediaTagSession target = new(new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!),
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));
        TagRecentList recent = new();
        using QuickTagContextSource source = new(null, recent);
        using QuickTagSession session = new(QuickTagTarget.For(target));
        source.Attach(session);

        await session.AddAsync("cute");
        TagId cute = store.ReadSnapshot().Tags.Single().Id;
        Assert.Equal(new[] { cute }, recent.Snapshot());

        await session.RemoveAsync(cute);
        Assert.Equal(new[] { cute }, recent.Snapshot());
        await session.UndoAsync();
        Assert.Equal(new[] { cute }, recent.Snapshot());
    }

    // ---- large store: never builds on the calling thread -------------------------------------------------------------

    private static PrivateTagStoreSnapshot Large(out PrivateTagDefinition target)
    {
        List<PrivateTagDefinition> tags = new();
        for (int i = 0; i < QuickTagContextSource.SynchronousBuildLimit + 50; i++) tags.Add(Tag("t" + i.ToString("D5")));
        target = tags[7];
        return Snap(tags, threads: new[] { (target, "cur") });
    }

    [Fact]
    public async Task ALargeStoreAnswersFromTheFallbackAndWarmsInTheBackgroundThenRepaints()
    {
        PrivateTagStoreSnapshot snapshot = Large(out PrivateTagDefinition target);
        Assert.False(TagContextIndex.IsBuilt(snapshot));
        TaskCompletionSource warmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int posted = 0;
        using QuickTagContextSource source = new(new TagContext { Thread = Thread("cur") }, new TagRecentList(), post: action => { posted++; action(); });
        source.Warmed += () => warmed.TrySetResult();

        IReadOnlyList<QuickTagSuggestion> cold = source.Suggest(Request(snapshot, 3));
        Assert.False(cold.Any(item => item.Reason == QuickTagReason.Thread), "the cold answer must not be the context ranking");

        await warmed.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(TagContextIndex.IsBuilt(snapshot));
        Assert.True(posted >= 1);
        IReadOnlyList<QuickTagSuggestion> warm = source.Suggest(Request(snapshot, 3));
        Assert.Equal(target.Id, warm[0].Id);
        Assert.Equal(QuickTagReason.Thread, warm[0].Reason);
    }

    [Fact]
    public async Task ADisposedSourceNeverRaisesWarmedAndAColdFallbackDropsWhatIsCarried()
    {
        PrivateTagStoreSnapshot snapshot = Large(out PrivateTagDefinition target);
        using QuickTagContextSource source = new(new TagContext { Thread = Thread("cur") }, new TagRecentList(), post: action => action());
        bool raised = false;
        source.Warmed += () => raised = true;
        Task warm = source.WarmAsync(snapshot);
        source.Dispose();
        await warm;
        Assert.False(raised);
        // After the build the next answer is the ranking; carried tags are excluded either way.
        using QuickTagContextSource second = new(new TagContext { Thread = Thread("cur") }, new TagRecentList());
        IReadOnlyList<QuickTagSuggestion> shown = second.Suggest(Request(snapshot, 3, target.Id));
        Assert.DoesNotContain(shown, item => item.Id == target.Id);
    }

    // ---- context of a menu origin ----------------------------------------------------------------------------------

    [Fact]
    public void TheContextOfATileCarriesThreadBytesNameTitleAndTheNeighboursButNotBytesForASelection()
    {
        MediaLibraryEntry Make(string thread, char hash) => new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day, "1", 1, null, 0,
            "att-v1:" + Hex(hash), "/dir/" + thread, "asset-" + thread, Hex(hash), 1, "m1", "original", "f" + thread + ".png",
            SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
        MediaLibraryEntry[] shown = { Make("a", 'a'), Make("b", 'b'), Make("c", 'c') };

        TagContext single = QuickTagContextSource.ContextOf(shown[1], shown, 1);
        Assert.Equal(shown[1].Thread, single.Thread);
        Assert.Equal(shown[1].Sha256, single.Sha256);
        Assert.Equal("fb.png", single.FileName);
        Assert.Contains("Tb", single.TitleTexts!);
        Assert.Equal(3, single.Neighbours.Count);
        Assert.Equal(1, single.Neighbours.Index);

        TagContext selection = QuickTagContextSource.ContextOf(shown[1], shown, 1, selection: true);
        Assert.Null(selection.Sha256);
        Assert.Equal(shown[1].Thread, selection.Thread);
    }

    [Fact]
    public void TheNeighbourhoodIsAFrozenWindowAroundTheIndexWithoutTheSubjectItself()
    {
        int[] list = Enumerable.Range(0, 100).ToArray();
        TagNeighbourhood around = QuickTagContextSource.Neighbourhood(list, 50, _ => null, radius: 3);
        Assert.Equal(7, around.Count);
        Assert.Equal(0, QuickTagContextSource.Neighbourhood(list, -1, _ => null).Count);
        Assert.Equal(0, QuickTagContextSource.Neighbourhood(list, 100, _ => null).Count);
        TagNeighbourhood edge = QuickTagContextSource.Neighbourhood(list, 0, _ => null, radius: 3);
        Assert.Equal(4, edge.Count);
    }
}
