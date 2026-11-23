using System.Runtime.CompilerServices;
using Avalonia.Input;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

// TP2 (PLAN-SCHNELL-TAGGEN-2026-11-14): the gallery tag panel host. The panel itself needs a platform (the viewer is covered by the
// smokes); these tests pin what has no UI: the suggestion adapter and its off-thread warm-up, the controller that follows the gallery,
// the command wiring, the key routing, the undo status across media and the source contracts of the hooks.
[Collection("Localization")]
public sealed class GalleryTagPanelTests : IDisposable
{
    public GalleryTagPanelTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out PrivateTagName? name, out _));
        return name!;
    }

    private static PrivateTagDefinition Tag(string name) => new(new TagId(Guid.NewGuid()), Name(name));
    private static ResourceId Thread(string id) => new("source", "board", id);
    private static string Hex(char value) => new(value, 64);

    private static PrivateMediaTagTarget Media(string thread, string post, string sha, char revision = 'a')
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(Thread(thread), post, 0, "att-v2:" + Hex(revision)),
            "m", "original", TransferMediaRole.Original, sha, out PrivateMediaTagTarget? target, out _));
        return target!;
    }

    private static PrivateTagStoreSnapshot Snap(IEnumerable<PrivateTagDefinition> tags,
        IEnumerable<(PrivateTagDefinition Tag, PrivateMediaTagTarget Target)>? media = null,
        IEnumerable<(PrivateTagDefinition Tag, string Thread)>? threads = null) => new(1, "fp", tags,
        (threads ?? Array.Empty<(PrivateTagDefinition, string)>()).Select(item => new PrivateThreadTagAssignment(item.Tag.Id, Thread(item.Thread))),
        (media ?? Array.Empty<(PrivateTagDefinition, PrivateMediaTagTarget)>()).Select(item => new PrivateMediaTagAssignment(item.Tag.Id, item.Target)),
        null, Array.Empty<PrivateLocalMediaTagAssignment>());

    private static QuickTagSuggestionRequest Request(PrivateTagStoreSnapshot snapshot, params TagId[] carried) =>
        new(snapshot, carried.ToHashSet(), 9);

    // ---- adapter: mapping --------------------------------------------------------------------------------------

    [Fact]
    public void EveryContextReasonMapsOntoOneQuickTagReasonAndTheTextsExist()
    {
        Assert.Equal(QuickTagReason.SameFile, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.SameFile));
        Assert.Equal(QuickTagReason.Thread, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.ThreadTag));
        Assert.Equal(QuickTagReason.Thread, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.ThreadMedia));
        Assert.Equal(QuickTagReason.SourceTag, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.SourceTag));
        Assert.Equal(QuickTagReason.Recent, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.Recent));
        Assert.Equal(QuickTagReason.Neighbour, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.Neighbour));
        Assert.Equal(QuickTagReason.FileName, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.FileName));
        Assert.Equal(QuickTagReason.Title, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.ThreadTitle));
        Assert.Equal(QuickTagReason.Title, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.SlashMarker));
        Assert.Equal(QuickTagReason.Frequent, GalleryTagPanelSuggestions.MapReason(TagSuggestionReason.Frequent));
        // No context reason is left out, and every target reason has a text the chip can show.
        foreach (TagSuggestionReason reason in Enum.GetValues<TagSuggestionReason>())
            Assert.False(string.IsNullOrWhiteSpace(QuickTagReasons.Text(GalleryTagPanelSuggestions.MapReason(reason))), reason.ToString());
        foreach (QuickTagReason reason in Enum.GetValues<QuickTagReason>())
            // The stamp reason (PT5) is no context reason: the stamp decorator (QuickTagStampSource) gives it to the pinned chips.
            Assert.Contains(reason, Enum.GetValues<TagSuggestionReason>().Select(GalleryTagPanelSuggestions.MapReason).Append(QuickTagReason.Frequent).Append(QuickTagReason.Stamp));
        Assert.Throws<ArgumentOutOfRangeException>(() => GalleryTagPanelSuggestions.MapReason((TagSuggestionReason)999));
    }

    [Fact]
    public void ARankedSuggestionMapsOneToOneAndANameWithoutAnOwnTagCreatesIt()
    {
        PrivateTagDefinition red = Tag("red");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { red }, new[] { (red, Media("t", "1", Hex('1'))) });
        IReadOnlyList<RankedTagSuggestion> ranked = TagContextSuggestions.Rank(snapshot, new TagContext
        {
            SourceTags = new[] { "newsrc" }, Thread = Thread("t")
        });
        QuickTagSuggestion own = GalleryTagPanelSuggestions.Map(ranked.Single(item => item.Id == red.Id));
        Assert.Equal((red.Name, (TagId?)red.Id, 1, false), (own.Name, own.Id, own.Uses, own.CreatesTag));
        Assert.Equal(ranked.Single(item => item.Id == red.Id).Score, own.Score);
        QuickTagSuggestion source = GalleryTagPanelSuggestions.Map(ranked.Single(item => item.Origin == TagSuggestionOrigin.Source));
        Assert.True(source.CreatesTag);
        Assert.Equal(QuickTagReason.SourceTag, source.Reason);
    }

    // ---- adapter: ranking and warm-up ---------------------------------------------------------------------------

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (int step = 0; step < 500 && !condition(); step++) await Task.Delay(10);
        Assert.True(condition(), "The condition did not become true in time.");
    }

    [Fact]
    public async Task TheContextRankingFeedsTheStripWithReasonsAndTheRecentListCountsAtOnce()
    {
        PrivateTagDefinition thr = Tag("thr"), rec = Tag("rec"), plain = Tag("plain");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { thr, rec, plain }, threads: new[] { (thr, "cur") });
        List<TagId> recent = new();
        using GalleryTagPanelSuggestions suggestions = new() { Recent = () => recent };
        suggestions.Context = new TagContext { Thread = Thread("cur") };
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        suggestions.Ready += () => ready.TrySetResult();

        suggestions.Warm(snapshot);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(suggestions.IsWarm(snapshot));

        IReadOnlyList<QuickTagSuggestion> first = suggestions.Suggest(Request(snapshot));
        Assert.Equal("thr", first[0].Name.Display);
        Assert.Equal(QuickTagReason.Thread, first[0].Reason);
        Assert.DoesNotContain(first, item => item.Name.Display == "rec" && item.Reason == QuickTagReason.Recent);

        recent.Add(rec.Id);
        IReadOnlyList<QuickTagSuggestion> second = suggestions.Suggest(Request(snapshot));
        Assert.Equal(QuickTagReason.Recent, second.Single(item => item.Name.Display == "rec").Reason);
        // What the target carries is never offered.
        Assert.DoesNotContain(suggestions.Suggest(Request(snapshot, thr.Id)), item => item.Id == thr.Id);
    }

    [Fact]
    public async Task TheWarmUpRunsOnAWorkerAndTheStripNeverWaitsForIt()
    {
        PrivateTagDefinition red = Tag("red");
        PrivateTagStoreSnapshot snapshot = Snap(new[] { red }, new[] { (red, Media("t", "1", Hex('1'))) });
        using ManualResetEventSlim started = new(), gate = new();
        int buildThread = 0;
        using GalleryTagPanelSuggestions suggestions = new(build: (_, token) =>
        {
            buildThread = Environment.CurrentManagedThreadId;
            started.Set();
            gate.Wait(token);
        });
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        suggestions.Ready += () => ready.TrySetResult();
        int caller = Environment.CurrentManagedThreadId;

        IReadOnlyList<QuickTagSuggestion> cold = suggestions.Suggest(Request(snapshot));

        // The call returned while the build is still blocked: it answered from the plain frequency source and did not build anything.
        Assert.True(started.Wait(TimeSpan.FromSeconds(20)));
        Assert.False(gate.IsSet);
        Assert.False(suggestions.IsWarm(snapshot));
        Assert.Equal(new[] { "red" }, cold.Select(item => item.Name.Display));
        Assert.Equal(QuickTagReason.Frequent, cold[0].Reason);
        gate.Set();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(suggestions.IsWarm(snapshot));
        Assert.NotEqual(caller, buildThread);
        Assert.Equal(buildThread, suggestions.WarmThreadId);
    }

    [Fact]
    public async Task AColdSnapshotAnswersFromTheLastRankingWithoutWhatIsCarriedAndNeverFromAnIndexBuiltOnTheCaller()
    {
        PrivateTagDefinition a = Tag("a"), b = Tag("b");
        PrivateTagStoreSnapshot first = Snap(new[] { a, b }, threads: new[] { (a, "cur"), (b, "cur") });
        using ManualResetEventSlim gate = new();
        int builds = 0;
        using GalleryTagPanelSuggestions suggestions = new(build: (snapshot, token) =>
        {
            Interlocked.Increment(ref builds);
            if (!ReferenceEquals(snapshot, first)) gate.Wait(token); // only the second snapshot blocks
            else { TagContextIndex.For(snapshot, token); TagSuggestionIndex.For(snapshot); }
        });
        suggestions.Context = new TagContext { Thread = Thread("cur") };
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        suggestions.Ready += () => ready.TrySetResult();
        suggestions.Warm(first);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(2, suggestions.Suggest(Request(first)).Count);

        // A write published a new snapshot: the strip keeps its ranking (minus the carried tag) while the index builds.
        PrivateTagStoreSnapshot second = Snap(new[] { a, b }, threads: new[] { (a, "cur"), (b, "cur") });
        IReadOnlyList<QuickTagSuggestion> stale = suggestions.Suggest(Request(second, a.Id));
        Assert.Equal(new[] { "b" }, stale.Select(item => item.Name.Display));
        Assert.Equal(QuickTagReason.Thread, stale[0].Reason);
        Assert.False(suggestions.IsWarm(second));
        gate.Set();
    }

    [Fact]
    public async Task DisposingTheSuggestionsCancelsARunningWarmUpAndNothingIsRaisedAfterwards()
    {
        PrivateTagStoreSnapshot snapshot = Snap(new[] { Tag("red") });
        using ManualResetEventSlim started = new();
        bool cancelled = false;
        GalleryTagPanelSuggestions suggestions = new(build: (_, token) =>
        {
            started.Set();
            try { token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)); token.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        });
        int raised = 0;
        suggestions.Ready += () => Interlocked.Increment(ref raised);
        suggestions.Warm(snapshot);
        Assert.True(started.Wait(TimeSpan.FromSeconds(20)));

        suggestions.Dispose();

        await WaitAsync(() => cancelled);
        await Task.Delay(100);
        Assert.Equal(0, raised);
        Assert.False(suggestions.IsWarm(snapshot));
        suggestions.Warm(snapshot); // after Dispose nothing starts
        suggestions.Dispose();
    }

    [Fact]
    public void TheWarmUpIsStructurallyOffTheCallersThreadAndSuggestNeverBuildsAnIndex()
    {
        string source = ReadSource("Desktop/GalleryTagPanelSuggestions.cs");
        int suggest = source.IndexOf("public IReadOnlyList<QuickTagSuggestion> Suggest(", StringComparison.Ordinal);
        int stale = source.IndexOf("private IReadOnlyList<QuickTagSuggestion> Stale(", StringComparison.Ordinal);
        string suggestBody = source[suggest..stale];
        Assert.DoesNotContain("TagContextIndex.For", suggestBody);
        Assert.DoesNotContain("BuildIndexes", suggestBody);
        Assert.DoesNotContain("TagSuggestionIndex.For", suggestBody);
        int warm = source.IndexOf("internal void Warm(", StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => RunWarm(", source[warm..suggest]);
        // The index builders are reached only from the warm-up.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(source, @"TagContextIndex\.For\(").Count);
        // The panel disposes the controller (and with it the warm-up) when the slot closes.
        string host = ReadSource("Desktop/GalleryViewer.TagPanel.cs");
        Assert.Contains("controller?.Dispose();", host);
        Assert.Contains("Suggestions.Dispose();", ReadSource("Desktop/GalleryTagPanelModel.cs"));
    }

    // ---- controller ---------------------------------------------------------------------------------------------

    private sealed class Owner : IGalleryTagPanelOwner
    {
        internal readonly Dictionary<string, Func<IQuickTagTarget?>> Targets = new();
        internal readonly List<GalleryEntry> List = new();
        internal int Index;
        internal bool More;
        internal GalleryEntry? Advanced;
        public IQuickTagTarget? OpenTarget(GalleryEntry entry, out string? unavailableReason)
        {
            IQuickTagTarget? target = Targets[entry.Key.Thread.Thread]();
            unavailableReason = target == null ? "no tag target" : null;
            return target;
        }
        public MediaTagSubject? SubjectOf(GalleryEntry entry, out string? unavailableReason) => throw new NotSupportedException();
        public IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason) => throw new NotSupportedException();
        public IReadOnlyList<GalleryEntry> Entries => List;
        public GalleryTagPanelPosition? Position => Index >= 0 && Index < List.Count ? new GalleryTagPanelPosition(Index + 1, List.Count, More) : null;
        public GalleryTagPanelPosition? CounterPosition { get; set; }
        public TagRecentList Recent { get; } = new();
        public QuickTagOptions Options => QuickTagOptions.Default;
        public TagContextOptions ContextOptions => TagContextOptions.Default;
        public BooruCatalogSnapshot? ReadSourceTags() => null;
        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => throw new NotSupportedException();
        public void OpenAdvanced(GalleryEntry entry) => Advanced = entry;
    }

    private static GalleryEntry GalleryEntryOf(string thread, char sha, string file)
    {
        string directory = Path.Combine(Path.GetTempPath(), "gallery-tag-panel-fixture");
        MediaAssetReference asset = new(Hex(sha), file, "original", file, Path.Combine(directory, file), 3, Hex(sha), "https://offline.invalid/" + file, file, true);
        MediaAssetSnapshot snapshot = new(directory, MediaManifestStatus.Available, 3, "fixture", [asset]);
        return new GalleryEntry(new GalleryAssetKey(Thread(thread), directory, asset.AssetId), snapshot, asset, null, null, false, false);
    }

    private static MediaTagSession SessionFor(PrivateTagStore store, PrivateMediaTagTarget target) =>
        new(new MediaTagSubject.Bound(target), () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, subject, token) => Task.FromResult(store.DefineAndSetMediaSubjects(name, [subject], null, token)),
            (id, subject, assigned, token) => Task.FromResult(store.SetMediaSubjects(id, [subject], assigned, null, token)));

    [Fact]
    public async Task TheControllerFollowsTheGalleryWithAFreshTargetPerMediumAndKeepsTheUndoStack()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateMediaTagTarget targetA = Media("a", "1", Hex('a')), targetB = Media("b", "1", Hex('b'));
        GalleryEntry entryA = GalleryEntryOf("a", 'a', "first.png"), entryB = GalleryEntryOf("b", 'b', "second.png");
        Owner owner = new();
        owner.List.AddRange(new[] { entryA, entryB });
        owner.Targets["a"] = () => QuickTagTarget.For(SessionFor(store, targetA));
        owner.Targets["b"] = () => QuickTagTarget.For(SessionFor(store, targetB));

        using GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, entryA);
        Assert.Equal("first.png", controller.Session.Label);
        Assert.Null(controller.UnavailableReason);
        IQuickTagTarget first = controller.Session.Target;
        await controller.Session.AddAsync("cat");
        Assert.Equal(new[] { "cat" }, store.ReadSnapshot().TagsOf(targetA).Select(tag => tag.Name.Display));

        owner.Index = 1;
        owner.CounterPosition = new GalleryTagPanelPosition(8, 100, false);
        GalleryTagPanelFrame frame = controller.Frame(entryB);
        Assert.Equal("second.png", frame.Label);
        Assert.Equal("Medium 8 of 100", frame.Counter);
        Assert.Same(entryB, controller.ShownEntry());
        Assert.Same(entryA, controller.PreviousEntry());
        Assert.NotSame(first, frame.Target);
        controller.Session.Retarget(frame.Target);
        Assert.Equal("second.png", controller.Session.Label);
        Assert.Equal(1, controller.Session.UndoSteps.Count);

        // A change on the next medium goes to that medium only; the first stays as it was.
        await controller.Session.AddAsync("dog");
        Assert.Equal(new[] { "dog" }, store.ReadSnapshot().TagsOf(targetB).Select(tag => tag.Name.Display));
        Assert.Equal(new[] { "cat" }, store.ReadSnapshot().TagsOf(targetA).Select(tag => tag.Name.Display));
        Assert.Equal(2, controller.Session.UndoSteps.Count);
        controller.OpenAdvanced();
        Assert.Same(entryB, owner.Advanced);
    }

    [Fact]
    public void ACounterTextReadsMediumFourOfTwentySevenInBothLanguagesAndNamesAnOpenEnd()
    {
        Assert.Equal("Medium 4 of 27", MediaPositionText.Long(new GalleryTagPanelPosition(4, 27, false)));
        Assert.Equal("Medium 4 of at least 27", MediaPositionText.Long(new GalleryTagPanelPosition(4, 27, true)));
        Assert.Null(MediaPositionText.Long(null));
        Texts.SetLanguage("de");
        Assert.Equal("Medium 4 von 27", MediaPositionText.Long(new GalleryTagPanelPosition(4, 27, false)));
        Assert.Equal("Medium 4 von mindestens 27", MediaPositionText.Long(new GalleryTagPanelPosition(4, 27, true)));
    }

    [Fact]
    public async Task AMediumWithoutATagTargetKeepsThePanelOnItWithTheReasonAndWritesNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateMediaTagTarget target = Media("a", "1", Hex('a'));
        GalleryEntry tagged = GalleryEntryOf("a", 'a', "a.png"), loose = GalleryEntryOf("x", 'x', "x.png");
        Owner owner = new();
        owner.List.AddRange(new[] { tagged, loose });
        owner.Targets["a"] = () => QuickTagTarget.For(SessionFor(store, target));
        owner.Targets["x"] = () => null;
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, tagged);

        GalleryTagPanelFrame frame = controller.Frame(loose);
        controller.Session.Retarget(frame.Target);

        Assert.Equal("no tag target", frame.UnavailableReason);
        Assert.IsType<GalleryTagUnavailableTarget>(frame.Target);
        Assert.Null(controller.Session.Snapshot);
        Assert.Equal(QuickTagStatusKind.Unavailable, controller.Session.Status.Kind);
        await controller.Session.AddAsync("cat");
        Assert.Empty(store.ReadSnapshot().Tags);
    }

    [Fact]
    public async Task OnlyAGivenTagCountsAsRecentlyUsed()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateMediaTagTarget target = Media("a", "1", Hex('a'));
        GalleryEntry entry = GalleryEntryOf("a", 'a', "a.png");
        Owner owner = new();
        owner.List.Add(entry);
        owner.Targets["a"] = () => QuickTagTarget.For(SessionFor(store, target));
        using GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, entry);

        await controller.Session.AddAsync("cat");
        TagId cat = store.ReadSnapshot().Tags.Single().Id;
        Assert.Equal(new[] { cat }, owner.Recent.Snapshot());
        await controller.Session.AddAsync("dog");
        TagId dog = store.ReadSnapshot().Tags.Single(tag => tag.Name.Display == "dog").Id;
        Assert.Equal(new[] { dog, cat }, owner.Recent.Snapshot());

        await controller.Session.RemoveAsync(cat);
        await controller.Session.UndoAsync();
        // Taking a tag off, and an undo, are no use of it.
        Assert.Equal(new[] { dog, cat }, owner.Recent.Snapshot());
    }

    // ---- undo across media --------------------------------------------------------------------------------------

    [Fact]
    public async Task UndoingAStepOfAnotherMediumNamesThatMediumInTheStatusLine()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateMediaTagTarget targetA = Media("a", "1", Hex('a')), targetB = Media("b", "1", Hex('b'));
        using QuickTagSession session = new(QuickTagTarget.For(SessionFor(store, targetA))) { Label = "first.png" };
        await session.AddAsync("cat");
        session.Label = "second.png";
        session.Retarget(QuickTagTarget.For(SessionFor(store, targetB)));
        await session.AddAsync("dog");

        // The newest step belongs to the shown medium: no medium is named.
        await session.UndoAsync();
        Assert.Equal(QuickTagStatusKind.UndoneAdd, session.Status.Kind);
        Assert.Null(session.Status.Medium);
        Assert.Equal("Undone: “dog” taken off again.", session.Status.Text);

        // The next one was made on the first medium; the panel shows the second.
        await session.UndoAsync();
        Assert.Equal("first.png", session.Status.Medium);
        Assert.Equal("Undone on first.png: “cat” taken off again.", session.Status.Text);
        Assert.Empty(store.ReadSnapshot().TagsOf(targetA));
        Texts.SetLanguage("de");
        Assert.Equal("Rückgängig bei first.png: „cat“ wieder entfernt.", session.Status.Text);
    }

    [Fact]
    public async Task AnUndoOnTheSameMediumOrWithoutNamesStaysUnnamed()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        PrivateMediaTagTarget targetA = Media("a", "1", Hex('a')), targetB = Media("b", "1", Hex('b'));
        using QuickTagSession session = new(QuickTagTarget.For(SessionFor(store, targetA)));
        await session.AddAsync("cat");
        session.Retarget(QuickTagTarget.For(SessionFor(store, targetB)));
        await session.UndoAsync(); // the host never named the media (a popover)
        Assert.Null(session.Status.Medium);
        Assert.Equal("Undone: “cat” taken off again.", session.Status.Text);
    }

    // ---- commands and keys --------------------------------------------------------------------------------------

    [Fact]
    public void EveryWiredQuickTagCommandHasAHandlerInThePanelIncludingTheMarking()
    {
        IReadOnlySet<string> awaiting = QuickTagCommands.AwaitingHandler;
        IEnumerable<string> wired = QuickTagCommands.All.Where(id => !awaiting.Contains(id));
        Assert.Equal(GalleryTagPanelCommands.Handled.OrderBy(x => x, StringComparer.Ordinal), wired.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(17, GalleryTagPanelCommands.Handled.Count); // PT3 wired the stamp (stamp-apply) in the panel
        Assert.DoesNotContain(QuickTagCommands.MarkNext, awaiting);
        Assert.DoesNotContain(QuickTagCommands.MarkPrevious, awaiting);
        foreach (string id in GalleryTagPanelCommands.Handled) Assert.NotNull(GalleryTagPanelCommands.RouteOf(id));
        foreach (string id in awaiting) Assert.Null(GalleryTagPanelCommands.RouteOf(id));
        Assert.Null(GalleryTagPanelCommands.RouteOf("favorites.toggle"));
        Assert.Equal(new QuickTagKeyRoute(QuickTagKeyAction.MarkNext), GalleryTagPanelCommands.RouteOf(QuickTagCommands.MarkNext));
        Assert.Equal(new QuickTagKeyRoute(QuickTagKeyAction.UnmarkPrevious), GalleryTagPanelCommands.RouteOf(QuickTagCommands.MarkPrevious));
    }

    [Theory]
    [InlineData(ShortcutPlatform.Linux)]
    [InlineData(ShortcutPlatform.Windows)]
    [InlineData(ShortcutPlatform.MacOS)]
    public void TheCommandActionsAreTheOnesTheSurfaceRoutesItsOwnKeysTo(ShortcutPlatform platform)
    {
        KeyModifiers primary = QuickTagKeys.PrimaryModifier(platform), choose = QuickTagKeys.ChooseModifier(platform);
        QuickTagKeyRoute Route(Key key, KeyModifiers modifiers, QuickTagFocus focus = QuickTagFocus.Field, bool fieldEmpty = true) =>
            QuickTagKeys.Route(key, modifiers, focus, listOpen: false, fieldEmpty, navigation: true, platform);

        Assert.Equal(GalleryTagPanelCommands.RouteOf(QuickTagCommands.Next), Route(Key.PageDown, KeyModifiers.None));
        Assert.Equal(GalleryTagPanelCommands.RouteOf(QuickTagCommands.Previous), Route(Key.PageUp, KeyModifiers.None));
        Assert.Equal(GalleryTagPanelCommands.RouteOf(QuickTagCommands.CommitNext), Route(Key.Enter, primary));
        Assert.Equal(GalleryTagPanelCommands.RouteOf(QuickTagCommands.ThreadSection), Route(Key.T, primary));
        Assert.Equal(GalleryTagPanelCommands.RouteOf(QuickTagCommands.Undo), Route(Key.Z, primary));
        // Option+digit belongs to text input on macOS while the tag field has focus.
        for (int number = 1; number <= 9; number++)
        {
            Key key = Key.D0 + number;
            QuickTagKeyRoute? suggestion = GalleryTagPanelCommands.RouteOf(QuickTagCommands.Suggestion(number));
            Assert.Equal(suggestion, Route(key, choose, QuickTagFocus.Suggestions));
            Assert.Equal(platform == ShortcutPlatform.MacOS ? QuickTagKeyRoute.None : suggestion,
                Route(key, choose, QuickTagFocus.Field));
        }
        // Tag names stay typeable: a bare digit in the field is text, and Page Down in an open list belongs to the list.
        Assert.Equal(QuickTagKeyRoute.None, Route(Key.D3, KeyModifiers.None));
        Assert.Equal(QuickTagKeyRoute.None, QuickTagKeys.Route(Key.PageDown, KeyModifiers.None, QuickTagFocus.Field, listOpen: true, true, true, platform));
        // Marking moves like paging and also changes the marks (the viewer does the second part).
        Assert.Equal(GalleryTagPanelCommands.RouteOf(QuickTagCommands.MarkNext), Route(Key.PageDown, KeyModifiers.Shift));
        Assert.Equal(GalleryTagPanelCommands.RouteOf(QuickTagCommands.MarkPrevious), Route(Key.PageUp, KeyModifiers.Shift));
        Assert.Equal(1, GalleryTagPanelCommands.Delta(QuickTagNavigation.MarkNext));
        Assert.Equal(-1, GalleryTagPanelCommands.Delta(QuickTagNavigation.UnmarkPrevious));
        Assert.Equal(1, GalleryTagPanelCommands.Delta(QuickTagNavigation.Next));
        Assert.Equal(-1, GalleryTagPanelCommands.Delta(QuickTagNavigation.Previous));
    }

    [Fact]
    public void WithoutNavigationAPopoverLeavesPagingAndCommitAndNextToTheField()
    {
        QuickTagKeyRoute Route(Key key, KeyModifiers modifiers) =>
            QuickTagKeys.Route(key, modifiers, QuickTagFocus.Field, false, true, navigation: false, ShortcutPlatform.Linux);
        Assert.Equal(QuickTagKeyRoute.None, Route(Key.PageDown, KeyModifiers.None));
        Assert.Equal(QuickTagKeyRoute.None, Route(Key.Enter, KeyModifiers.Control));
        Assert.Equal(QuickTagKeyRoute.None, Route(Key.T, KeyModifiers.Control));
    }

    // ---- thread section -----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheThreadSectionAddsRemovesAndRefusesThroughTheSameStoreFunctionsAsTheArchiveBar()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        ResourceId thread = Thread("t1");
        GalleryThreadTagModel model = new(thread, () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (name, token) => Task.FromResult(store.Define(name, null, token)),
            (tag, owner, assigned, token) => Task.FromResult(store.SetThread(tag, owner, assigned, null, token)));
        int updates = 0;
        model.Updated += () => updates++;

        await model.AddAsync("  retro ");
        Assert.Equal(new[] { "retro" }, model.Assigned.Select(tag => tag.Name.Display));
        Assert.Equal(new[] { "retro" }, store.ReadSnapshot().TagsOf(thread).Select(tag => tag.Name.Display));
        // A thread tag marks the thread, not its media.
        Assert.Empty(store.ReadSnapshot().MediaAssignments);
        Assert.Contains(model.Assigned.Single().Id, model.AssignedIds());

        await model.AddAsync("bad\tname");
        Assert.Equal("tags.error.invalidName", model.MessageKey);
        Assert.Single(model.Assigned);
        await model.AddAsync("  ");
        Assert.Equal("tags.error.invalidName", model.MessageKey); // blank text writes nothing and leaves the state alone

        await model.RemoveAsync(model.Assigned.Single().Id);
        Assert.Empty(model.Assigned);
        Assert.Null(model.MessageKey);
        Assert.True(updates >= 4);
    }

    // ---- source contracts of the hooks --------------------------------------------------------------------------

    private static string ReadSource(string relative, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative)));

    [Fact]
    public void TagsRequestedOpensThePanelAndTheDialogIsOnlyReachedThroughAdvanced()
    {
        string actions = ReadSource("Desktop/Shell/MainWindow.GalleryActions.cs");
        string tagsLine = actions.Split('\n').Single(line => line.Contains("_gallery.TagsRequested +="));
        Assert.Contains("OpenGalleryTagPanel()", tagsLine);
        Assert.DoesNotContain("EditGalleryMediaTagsAsync", tagsLine);
        string tags = ReadSource("Desktop/Shell/MainWindow.GalleryTags.cs");
        Assert.Contains("public void OpenAdvanced(GalleryEntry entry) =>", tags);
        Assert.Contains("window.EditGalleryMediaTagsAsync(entry)", tags);
        // The dialog is still the one the gallery always used, and the panel reloads after it.
        Assert.Contains("_gallery?.ReloadTagPanel();", tags);
        // The media change reaches the open panel through the one refresh every medium change already runs.
        Assert.Contains("_gallery.RetargetTagPanel(current);", tags);
        Assert.Contains("RefreshGalleryMediaTags();", ReadSource("Desktop/Shell/MainWindow.GalleryActions.cs"));
    }

    [Fact]
    public void ThePanelUsesTheSharedSlotAndKeepsTagLogicOutOfPlaybackAndDecode()
    {
        string overlay = ReadSource("Desktop/GalleryViewer.Overlay.cs"), overflow = ReadSource("Desktop/GalleryViewer.Overflow.cs");
        // The slot is shared with the post panel: placement, overlay hold and focus need no hook of their own.
        Assert.DoesNotContain("TagPanel", overlay);
        Assert.DoesNotContain("TagPanel", overflow);
        string viewer = ReadSource("Desktop/GalleryViewer.cs");
        Assert.Contains("GalleryTagPanelPosition? sidebarPosition", viewer);
        // V2: the surface is the Tags segment of the side panel host in that slot.
        Assert.Contains("_sidePanel.TagContent = surface;", ReadSource("Desktop/GalleryViewer.TagPanel.cs"));
        Assert.Contains("NavigationEnabled = true", ReadSource("Desktop/GalleryViewer.TagPanel.cs"));
        // Untouched by this package.
        foreach (string untouched in new[] { "Desktop/GalleryImageCache.cs", "Desktop/GalleryDecodeQueue.cs", "Desktop/GalleryFrameFile.cs" })
            Assert.DoesNotContain("TagPanel", ReadSource(untouched));
    }

    [Fact]
    public void ThePostPanelToggleNeverClosesTheTagPanelsPreferenceAndTheWorkViewRowStaysTheSlots()
    {
        string host = ReadSource("Desktop/GalleryViewer.TagPanel.cs");
        // Closing the slot under the tag panel does not rewrite the post panel's "open" preference.
        Assert.Contains("bool tagOnly = !open && ReleaseTagPanel();", ReadSource("Desktop/GalleryViewer.cs"));
        Assert.Contains("!tagOnly) ContextOpenChanged", ReadSource("Desktop/GalleryViewer.cs"));
        Assert.Contains("SetContextOpen(true, notify: false);", host);
        Assert.Contains("SetContextOpen(false, notify: false);", host);
        // No new work-view row: the slot is "gallery.post-panel" in every view, the tags tool is "gallery.toolbar.tags".
        Assert.DoesNotContain("SetId(", host);
        Assert.DoesNotContain("tag-panel", ReadSource("Desktop/WorkViews/WorkViewPolicy.Gallery.cs"));
    }

    [Fact]
    public void EveryNewTextExistsInTheTwelveLanguages()
    {
        string[] keys = GalleryTagPanelCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(12, keys.Length);
        foreach (string key in keys)
        {
            Assert.True(BuiltInCatalog.English.ContainsKey(key), key);
            Assert.True(BuiltInCatalog.German.ContainsKey(key), key);
        }
        string packs = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Root())!, "../../Desktop/Localization/Packs"));
        string[] files = Directory.GetFiles(packs, "*.json");
        Assert.Equal(10, files.Length);
        foreach (string file in files)
        {
            using System.Text.Json.JsonDocument pack = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            System.Text.Json.JsonElement strings = pack.RootElement.GetProperty("strings");
            foreach (GalleryTagPanelCatalogEntry entry in keys.Select(key => new GalleryTagPanelCatalogEntry(key, BuiltInCatalog.English[key])))
            {
                Assert.True(strings.TryGetProperty(entry.Key, out System.Text.Json.JsonElement text), Path.GetFileName(file) + " lacks " + entry.Key);
                string value = text.GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(value), entry.Key);
                // The placeholders of the English text appear in every translation.
                foreach (System.Text.RegularExpressions.Match placeholder in System.Text.RegularExpressions.Regex.Matches(entry.English, @"\{\d+\}"))
                    Assert.Contains(placeholder.Value, value);
            }
        }
    }

    private sealed record GalleryTagPanelCatalogEntry(string Key, string English);

    private static string Root([CallerFilePath] string source = "") => source;
}
