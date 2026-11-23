using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

public sealed class TagRuleTests
{
    private static readonly string MediaHash = new('a', 64);
    private static readonly ResourceId Thread = new("source", "board", "42");
    private static readonly TagSuggestionProvenance Model = new("reference", "tagger", "1.0", "model", "r1", new string('b', 64));
    private static readonly TagRuleSet GirlAlias = new([new(TagRuleKind.Alias, "girl", "1girl")]);

    [Fact]
    public void ScopedRulesProjectAliasesNamespacesAndParentsWithoutChangingTheOriginal()
    {
        TagRuleSet rules = new([
            new(TagRuleKind.Alias, "girl", "1girl"),
            new(TagRuleKind.Namespace, "1girl", "character", new(PluginId: "catalog")),
            new(TagRuleKind.Parent, "girl", "person"),
            new(TagRuleKind.Parent, "girl", "portrait"),
            new(TagRuleKind.Parent, "person", "people"),
            new(TagRuleKind.Parent, "portrait", "photography"),
            new(TagRuleKind.Ignore, "portrait", Scope: new("source", "board")),
            new(TagRuleKind.Ignore, "1girl", Scope: new("source", "hidden"))
        ]);
        TagRuleInput input = new("girl", Bound("100"), new TagRuleOrigin.Provider("catalog"));
        TagRuleProjection projected = rules.Project(input);

        Assert.Same(input, projected.Input);
        Assert.Equal("girl", projected.Input.OriginalName);
        Assert.Equal("character:1girl", projected.Canonical!.Display);
        Assert.Equal(["person", "people"], projected.Parents.Select(name => name.Display));

        TagRuleProjection otherSource = rules.Project(input with { Subject = Bound("100", source: "other") });
        Assert.Equal(["person", "portrait", "people", "photography"], otherSource.Parents.Select(name => name.Display));
        TagRuleProjection hidden = rules.Project(input with { Subject = Bound("100", board: "hidden"), Origin = new TagRuleOrigin.Provider() });
        Assert.True(hidden.IsIgnored);
        Assert.Equal("girl", hidden.Input.OriginalName);
        Assert.Empty(hidden.Parents);
        Assert.Equal("long_hair", rules.Project(input with { OriginalName = "long_hair" }).Canonical!.Display);
    }

    [Fact]
    public void ProviderClaimsSurvivePreviewApplyUndoAndReloadIncludingUnmappableTags()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        BooruCatalogStore catalog = new(temp.Path, lease);
        BooruPostClaimTag[] original =
        [
            new("Girl", BooruPostClaimTagCategory.Character),
            new("portrait", BooruPostClaimTagCategory.General),
            new(new string('x', PrivateTagName.MaximumScalars + 1), BooruPostClaimTagCategory.Artist)
        ];
        catalog.UpsertPost(new BooruPostClaim("danbooru", "100", BooruPostClaimPresence.Tags, Tags: original),
            DateTimeOffset.UnixEpoch);
        byte[] raw = File.ReadAllBytes(catalog.FilePath);
        BooruPostClaim post = Assert.Single(catalog.Read().Posts).Post;
        MediaTagSubject subject = Bound("100");
        TagRuleInput[] inputs = post.Tags.Select(tag => Provider(tag.Name, subject)).ToArray();
        PrivateTagStore tags = new(temp.Path, lease);
        TagRuleSet rules = new([
            new(TagRuleKind.Alias, "girl", "1girl"),
            new(TagRuleKind.Parent, "1girl", "person"),
            new(TagRuleKind.Ignore, "portrait")
        ]);

        TagRulePreview preview = tags.PreviewTagRules(rules, inputs, expectedRevision: 0);

        Assert.Equal(original.Select(tag => tag.Name), preview.Projections.Select(row => row.Input.OriginalName));
        Assert.True(preview.Projections[1].IsIgnored);
        Assert.False(preview.Projections[2].IsIgnored);
        Assert.Equal(TagNameError.TooManyScalars, preview.Projections[2].MappingError);
        Assert.Equal(["1girl", "person"], preview.Changes.Select(change => change.Name.Display));
        TagRuleCommit applied = tags.ApplyTagRules(preview);
        Assert.Equal(2, applied.Commit.Snapshot.TagsOf(subject).Count);
        Assert.Empty(tags.UndoTagRules(applied.Undo!).Snapshot.Tags);
        Assert.Equal(raw, File.ReadAllBytes(catalog.FilePath));
        Assert.Equal(original, Assert.Single(new BooruCatalogStore(temp.Path, lease).Read().Posts).Post.Tags);
    }

    [Fact]
    public void OverlappingMappingsIdentifyBothRulesAndDisjointScopesRemainUsable()
    {
        TagRuleSet rules = new([
            new(TagRuleKind.Alias, "girl", "1girl", new("source")),
            new(TagRuleKind.Namespace, "girl", "character", new(PluginId: "catalog"))
        ]);
        TagRuleInput input = new("girl", Bound("100"), new TagRuleOrigin.Provider("catalog"));

        TagRuleException error = Assert.Throws<TagRuleException>(() => rules.Project(input));
        Assert.Equal(TagRuleError.AmbiguousMapping, error.Error);
        Assert.Equal(1, error.RuleIndex);
        Assert.Equal(0, error.RelatedRuleIndex);
        Assert.Equal("character:girl", rules.Project(input with { Subject = Bound("100", source: "other") }).Canonical!.Display);
        Assert.Equal("1girl", rules.Project(input with { Origin = new TagRuleOrigin.Provider() }).Canonical!.Display);
    }

    [Fact]
    public void AliasAndParentCyclesAreReportedIncludingAParentCollapsedByAnAlias()
    {
        TagRuleInput input = Provider("a", Bound("100"));
        TagRule[][] cycles = [
            [new(TagRuleKind.Alias, "a", "b"), new(TagRuleKind.Alias, "b", "a")],
            [new(TagRuleKind.Parent, "a", "b"), new(TagRuleKind.Parent, "b", "a")],
            [new(TagRuleKind.Namespace, "a", "artist"), new(TagRuleKind.Parent, "artist:a", "a")]
        ];
        foreach (TagRule[] rules in cycles)
        {
            TagRuleException error = Assert.Throws<TagRuleException>(() => new TagRuleSet(rules).Project(input));
            Assert.Equal(TagRuleError.Cycle, error.Error);
            Assert.InRange(error.RuleIndex!.Value, 0, rules.Length - 1);
        }
    }

    [Fact]
    public void PreviewAndApplyReportTheSameExactDeltaAndUndoKeepsEarlierTags()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject.Bound already = Bound("100"), first = Bound("101"), second = Bound("102"), unselected = Bound("103");
        TagId girl = store.DefineAndSetMediaSubjects("1girl", [already]).Tag;
        TagId other = store.DefineAndSetMediaSubjects("other", [first]).Tag;
        PrivateTagStoreSnapshot before = store.ReadSnapshot();
        string path = Path.Combine(temp.Path, PrivateTagStore.FileName);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");

        TagRulePreview preview = store.PreviewTagRules(GirlAlias,
            [Provider("girl", already), Provider("girl", first), Provider("girl", second), Provider("girl", second)], before.Revision);

        Assert.Equal([first, second], preview.AffectedSubjects);
        Assert.Equal(["1girl", "1girl"], preview.Changes.Select(change => change.Name.Display));
        Assert.All(preview.Projections, row => Assert.Equal("girl", row.Input.OriginalName));
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));

        TagRuleCommit applied = store.ApplyTagRules(preview);
        Assert.Equal(preview.AffectedSubjects, applied.Preview.AffectedSubjects);
        Assert.Equal(before.Revision + 1, applied.Commit.Snapshot.Revision);
        Assert.All(preview.AffectedSubjects, subject => Assert.True(applied.Commit.Snapshot.Carries(girl, subject)));
        Assert.False(applied.Commit.Snapshot.Carries(girl, unselected));

        PrivateTagStoreSnapshot undone = store.UndoTagRules(applied.Undo!).Snapshot;
        Assert.Equal(before.Tags, undone.Tags);
        Assert.Equal(before.MediaAssignments, undone.MediaAssignments);
        Assert.True(undone.Carries(girl, already));
        Assert.True(undone.Carries(other, first));
    }

    [Fact]
    public void APrivateAliasReplacesOnlyTheSelectedAssignmentEvenAcrossHandedOverKeys()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaTagSubject.Bound selected = Bound("100"), handedOver = Bound("100", key: 'b');
        PrivateTagCommit before = store.DefineAndSetMediaSubjects("girl", [selected, handedOver]);
        TagRulePreview preview = store.PreviewTagRules(GirlAlias, [new("girl", selected, new TagRuleOrigin.Private())], before.Snapshot.Revision);

        Assert.Equal([("girl", false), ("1girl", true)], preview.Changes.Select(change => (change.Name.Display, change.Assigned)));
        TagRuleCommit applied = store.ApplyTagRules(preview);
        Assert.Equal("1girl", Assert.Single(applied.Commit.Snapshot.TagsOf(selected)).Name.Display);
        Assert.Equal("girl", Assert.Single(applied.Commit.Snapshot.TagsOf(handedOver)).Name.Display);
        PrivateTagCommit undone = store.UndoTagRules(applied.Undo!);
        Assert.Equal(before.Snapshot.Tags, undone.Snapshot.Tags);
        Assert.Equal(before.Snapshot.MediaAssignments, undone.Snapshot.MediaAssignments);
    }

    [Fact]
    public void PreviewOnAnEmptyStoreRegistersNothingAndUndoDropsTheNewLooseRegistration()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        Assert.True(LocalMediaObservation.TryCreate(Thread, "/media/42", "a.png", new string('c', 64), 100, MediaHash, out var observation));
        MediaTagSubject.Local subject = new(observation!, Expected: null);
        string[] before = Directory.GetFileSystemEntries(temp.Path);

        TagRulePreview preview = store.PreviewTagRules(GirlAlias, [Provider("girl", subject)], expectedRevision: 0);
        Assert.Equal(subject, Assert.Single(preview.AffectedSubjects));
        Assert.Equal(before, Directory.GetFileSystemEntries(temp.Path));
        Assert.Equal(LocalMediaResolutionState.NewOccurrence, store.ReadSnapshot().ResolveLocal(observation!).State);

        TagRuleCommit applied = store.ApplyTagRules(preview);
        Assert.Equal("1girl", Assert.Single(applied.Commit.Snapshot.TagsOf(subject)).Name.Display);
        Assert.True(applied.Commit.Snapshot.ResolveLocal(observation!).IsRegistered);
        PrivateTagStoreSnapshot undone = store.UndoTagRules(applied.Undo!).Snapshot;
        Assert.Empty(undone.Tags);
        Assert.Empty(undone.LocalAssignments);
        Assert.Equal(LocalMediaResolutionState.NewOccurrence, undone.ResolveLocal(observation!).State);
    }

    [Fact]
    public void SuggestionsKeepRawNamesAndProvenanceWhileSameBytesCanBeAcceptedSeparately()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagSuggestionStore suggestions = new(lease);
        TagSuggestionSnapshot pending = suggestions.CommitRuns([Run(Model, "girl")]);
        MediaTagSubject first = Bound("100"), second = Bound("101");
        Assert.Empty(pending.PendingFor(first, 101, 0.35)); // A different observed length never binds this suggestion.
        TagRulePreview preview = store.PreviewTagRules(GirlAlias, pending.PendingFor(first, 100, 0.35), expectedRevision: 0);
        TagRuleProjection row = Assert.Single(preview.Projections);
        var origin = Assert.IsType<TagRuleOrigin.Model>(row.Input.Origin);
        Assert.Equal(("girl", "1girl", Model, 0.8), (row.Input.OriginalName, row.Canonical!.Display, origin.Suggestion.Provenance, origin.Suggestion.Confidence));
        byte[] raw = File.ReadAllBytes(suggestions.FilePath);

        TagRuleCommit applied = store.ApplyTagRules(preview);
        Assert.Single(applied.Commit.Snapshot.TagsOf(first));
        Assert.Empty(applied.Commit.Snapshot.TagsOf(second));
        TagRulePreview another = store.PreviewTagRules(GirlAlias, pending.PendingFor(second, 100, 0.35), applied.Commit.Snapshot.Revision);
        Assert.Equal(second, Assert.Single(another.AffectedSubjects));
        TagRuleCommit both = store.ApplyTagRules(another);
        Assert.Single(both.Commit.Snapshot.TagsOf(second));
        Assert.Equal(raw, File.ReadAllBytes(suggestions.FilePath));
        Assert.Equal(PrivateTagStoreError.StaleRevision,
            Assert.Throws<PrivateTagStoreException>(() => store.UndoTagRules(applied.Undo!)).Error);
        PrivateTagStoreSnapshot undone = store.UndoTagRules(both.Undo!).Snapshot;
        Assert.Single(undone.TagsOf(first));
        Assert.Empty(undone.TagsOf(second));
    }

    [Fact]
    public void ANewPrivateRevisionRefusesTheWholePreviewAndItsOldRevisionCannotBePreviewedAgain()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagRuleInput[] inputs = [Provider("girl", Bound("100")), Provider("girl", Bound("101"))];
        TagRulePreview preview = store.PreviewTagRules(GirlAlias, inputs, expectedRevision: 0);
        PrivateTagStoreSnapshot newer = store.Define("other").Snapshot;

        Assert.Equal(PrivateTagStoreError.StaleRevision, Assert.Throws<PrivateTagStoreException>(() => store.ApplyTagRules(preview)).Error);
        Assert.Equal(PrivateTagStoreError.StaleRevision,
            Assert.Throws<PrivateTagStoreException>(() => store.PreviewTagRules(GirlAlias, inputs, expectedRevision: 0)).Error);
        PrivateTagStoreSnapshot after = store.ReadSnapshot();
        Assert.Equal(newer.Fingerprint, after.Fingerprint);
        Assert.Empty(after.MediaAssignments);
    }

    [Fact]
    public void ARejectionInvalidatesTheWholePreviewAndStillHidesTheProposalAcrossTaggers()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagSuggestionStore suggestions = new(lease);
        MediaTagSubject subject = Bound("100");
        TagSuggestionSnapshot pending = suggestions.CommitRuns([Run(Model, "girl")]);
        TagRuleInput[] selected = [Provider("other", subject), .. pending.PendingFor(subject, 100, 0.35)];
        TagRulePreview preview = store.PreviewTagRules(GirlAlias, selected, expectedRevision: 0);
        suggestions.Decide([Assert.Single(pending.Suggestions).Id], TagSuggestionState.Rejected, pending.Revision);

        Assert.Equal(TagSuggestionStoreError.StaleRevision,
            Assert.Throws<TagSuggestionStoreException>(() => store.ApplyTagRules(preview)).Error);
        Assert.False(File.Exists(Path.Combine(temp.Path, PrivateTagStore.FileName)));
        TagSuggestionSnapshot rerun = suggestions.CommitRuns([Run(Model with { PluginId = "second-tagger" }, "girl")]);
        Assert.Equal(TagSuggestionState.Rejected, Assert.Single(rerun.Suggestions).State);
        Assert.Empty(rerun.PendingFor(subject, 100, 0.35));
        Assert.Empty(rerun.PendingFor(Bound("101"), 100, 0.35));
    }

    [Fact]
    public void ADisplayFoldMatchingTwoExistingPrivateAliasesIsDiagnosed()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId first = store.Define("first").Tag, second = store.Define("second").Tag;
        store.AddAlias(first, "long_hair");
        PrivateTagStoreSnapshot before = store.AddAlias(second, "#long hair").Snapshot;

        TagRuleException ambiguous = Assert.Throws<TagRuleException>(() => store.PreviewTagRules(new([]),
            [Provider("long hair", Bound("100"))], before.Revision));
        Assert.Equal(TagRuleError.AmbiguousMapping, ambiguous.Error);
        Assert.Equal(before.Fingerprint, store.ReadSnapshot().Fingerprint);
        TagRulePreview exact = store.PreviewTagRules(new([]), [Provider("long_hair", Bound("100"))], before.Revision);
        Assert.Equal("first", Assert.Single(exact.Projections).Canonical!.Display);
    }

    [Fact]
    public void ExistingPrivateAliasesParticipateInTheGraphAndItsCycleChecks()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        TagId canonical = store.Define("1girl").Tag;
        PrivateTagStoreSnapshot before = store.AddAlias(canonical, "girl").Snapshot;
        TagRuleInput input = Provider("girl", Bound("100"));

        TagRulePreview preview = store.PreviewTagRules(new([new(TagRuleKind.Parent, "1girl", "person")]), [input], before.Revision);
        TagRuleProjection row = Assert.Single(preview.Projections);
        Assert.Equal("1girl", row.Canonical!.Display);
        Assert.Equal("person", Assert.Single(row.Parents).Display);
        Assert.Equal(["1girl", "person"], preview.Changes.Select(change => change.Name.Display));

        TagRuleException cycle = Assert.Throws<TagRuleException>(() => store.PreviewTagRules(
            new([new(TagRuleKind.Alias, "1girl", "girl")]), [input], before.Revision));
        Assert.Equal(TagRuleError.Cycle, cycle.Error);
        Assert.Equal(0, cycle.RuleIndex);
        Assert.Equal(before.Fingerprint, store.ReadSnapshot().Fingerprint);
    }

    [Fact]
    public void IgnoredSuggestionsAreAnUnchangedPreviewAndSettingsLimitTheWholeBatch()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagLimits limits = new() { TagLimit = PrivateTagLimits.MinimumTagLimit };
        PrivateTagStore store = new(temp.Path, lease, limits);
        TagRuleInput input = Provider("girl", Bound("100"));
        TagRulePreview ignored = store.PreviewTagRules(new([new(TagRuleKind.Ignore, "girl")]), [input], expectedRevision: 0);
        Assert.True(Assert.Single(ignored.Projections).IsIgnored);
        Assert.Empty(ignored.AffectedSubjects);
        TagRuleCommit commit = store.ApplyTagRules(ignored);
        Assert.False(commit.Commit.Changed);
        Assert.Null(commit.Undo);

        TagRuleSet overLimit = new(Enumerable.Range(0, limits.TagLimit).Select(index => new TagRule(TagRuleKind.Parent, "girl", $"parent {index}")));
        Assert.Equal(PrivateTagStoreError.TagLimitReached,
            Assert.Throws<PrivateTagStoreException>(() => store.PreviewTagRules(overLimit, [input], expectedRevision: 0)).Error);
        Assert.False(File.Exists(Path.Combine(temp.Path, PrivateTagStore.FileName)));
    }

    private static TagRuleInput Provider(string name, MediaTagSubject subject) => new(name, subject, new TagRuleOrigin.Provider());

    private static TagSuggestionRunInput Run(TagSuggestionProvenance provenance, string tag) =>
        new(MediaHash, 100, provenance, TagSuggestionStore.TaggedOutcome, [new(tag, "General", 0.8)]);

    private static MediaTagSubject.Bound Bound(string post, string source = "source", string board = "board", char key = 'a')
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(new AttachmentRevisionReference(new ResourceId(source, board, "42"), post,
            0, "att-v2:" + new string(key, 64)), "media-" + post, "original", TransferMediaRole.Original, MediaHash, out var target, out _));
        return new(target!);
    }
}
