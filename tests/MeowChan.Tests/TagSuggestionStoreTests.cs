using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.Tests;

public sealed class TagSuggestionStoreTests : IDisposable
{
    private const string MediaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MediaB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly TagSuggestionProvenance R1 = new("test.tagger", "reference", "1.0", "reference-model", "r1");
    private static readonly TagSuggestionProvenance R2 = R1 with { ModelRevision = "r2" };
    private static readonly TagSuggestionProvenance OtherTagger = R1 with { TaggerKey = "other" };
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("meowchan-tag-suggestions-");
    private readonly ProfileLease _lease;
    private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    public TagSuggestionStoreTests() => _lease = ProfileLease.Acquire(_directory.FullName);

    public void Dispose()
    {
        _lease.Dispose();
        _directory.Delete(recursive: true);
    }

    private TagSuggestionStore Store() => new(_lease, () => _now);

    private static TagSuggestionRunInput Run(string media, TagSuggestionProvenance provenance, params (string Name, double Confidence)[] tags)
        => new(media, 100, provenance, TagSuggestionStore.TaggedOutcome,
            tags.Select(tag => new TagSuggestionCandidate(tag.Name, "General", tag.Confidence)).ToArray());

    [Fact]
    public void CommittedRunsArePendingPersistAndFilterOnlyInTheView()
    {
        TagSuggestionStore store = Store();
        TagSuggestionSnapshot snapshot = store.CommitRuns([Run(MediaA, R1, ("cat", 0.9), ("dog", 0.2)), Run(MediaB, R1, ("tree", 0.6))]);
        Assert.Equal(1, snapshot.Revision);
        Assert.All(snapshot.Suggestions, value => Assert.Equal(TagSuggestionState.Pending, value.State));
        Assert.Equal(["cat"], snapshot.PendingFor(MediaA, 0.35).Select(value => value.Name));
        Assert.Equal(["cat", "dog"], snapshot.PendingFor(MediaA, 0.05).Select(value => value.Name));
        Assert.True(snapshot.HasCompletedRun(MediaA, R1));
        Assert.False(snapshot.HasCompletedRun(MediaA, R2));

        TagSuggestionSnapshot reloaded = Store().Load();
        Assert.Equal(snapshot.Revision, reloaded.Revision);
        Assert.Equal(snapshot.Suggestions.Select(value => (value.Id, value.Name, value.Confidence, value.Provenance)),
            reloaded.Suggestions.Select(value => (value.Id, value.Name, value.Confidence, value.Provenance)));
        Assert.Empty(Directory.GetFiles(_directory.FullName, "*.tmp"));
    }

    [Fact]
    public void OnlyTaggedRunsCountAsDoneAndCarrySuggestions()
    {
        TagSuggestionStore store = Store();
        TagSuggestionSnapshot snapshot = store.CommitRuns([new TagSuggestionRunInput(MediaA, 100, R1, "Error",
            [new TagSuggestionCandidate("cat", "General", 0.9)])]);
        Assert.Empty(snapshot.Suggestions);
        Assert.False(snapshot.HasCompletedRun(MediaA, R1));
        Assert.Equal("Error", Assert.Single(snapshot.Runs).Outcome);
        snapshot = store.CommitRuns([Run(MediaA, R1, ("cat", 0.9))]);
        Assert.True(snapshot.HasCompletedRun(MediaA, R1));
        Assert.Single(snapshot.Runs);
    }

    [Fact]
    public void DecisionsAreUserActionsWithAnAppendOnlyHistoryAndUndo()
    {
        TagSuggestionStore store = Store();
        TagSuggestionSnapshot snapshot = store.CommitRuns([Run(MediaA, R1, ("cat", 0.9), ("dog", 0.8))]);
        long cat = snapshot.Suggestions.Single(value => value.Name == "cat").Id;
        long dog = snapshot.Suggestions.Single(value => value.Name == "dog").Id;
        snapshot = store.Decide([cat], TagSuggestionState.Accepted, snapshot.Revision);
        snapshot = store.Decide([dog], TagSuggestionState.Rejected, snapshot.Revision);
        Assert.Equal(TagSuggestionState.Accepted, snapshot.Suggestions.Single(value => value.Id == cat).State);
        Assert.Equal(_now, snapshot.Suggestions.Single(value => value.Id == cat).DecidedAt);

        TagSuggestionStoreException stale = Assert.Throws<TagSuggestionStoreException>(() => store.Decide([cat], TagSuggestionState.Pending, snapshot.Revision - 1));
        Assert.Equal(TagSuggestionStoreError.StaleRevision, stale.Error);
        Assert.Equal(TagSuggestionStoreError.InvalidTransition,
            Assert.Throws<TagSuggestionStoreException>(() => store.Decide([cat], TagSuggestionState.Rejected, snapshot.Revision)).Error);
        Assert.Equal(TagSuggestionStoreError.InvalidInput,
            Assert.Throws<TagSuggestionStoreException>(() => store.Decide([cat], TagSuggestionState.Superseded, snapshot.Revision)).Error);
        Assert.Equal(TagSuggestionStoreError.UnknownSuggestion,
            Assert.Throws<TagSuggestionStoreException>(() => store.Decide([999], TagSuggestionState.Accepted, snapshot.Revision)).Error);

        snapshot = store.Decide([cat], TagSuggestionState.Pending, snapshot.Revision);
        Assert.Equal(TagSuggestionState.Pending, snapshot.Suggestions.Single(value => value.Id == cat).State);
        Assert.Null(snapshot.Suggestions.Single(value => value.Id == cat).DecidedAt);
        Assert.Equal([(cat, TagSuggestionState.Pending, TagSuggestionState.Accepted), (dog, TagSuggestionState.Pending, TagSuggestionState.Rejected),
            (cat, TagSuggestionState.Accepted, TagSuggestionState.Pending)],
            snapshot.Events.Select(value => (value.SuggestionId, value.From, value.To)));
        Assert.Equal([1L, 2L, 3L], snapshot.Events.Select(value => value.Sequence));
    }

    [Fact]
    public void ANewModelRevisionSupersedesPendingButNeverOverridesDecisions()
    {
        TagSuggestionStore store = Store();
        TagSuggestionSnapshot snapshot = store.CommitRuns([Run(MediaA, R1, ("cat", 0.9), ("dog", 0.8), ("bird", 0.7))]);
        long cat = snapshot.Suggestions.Single(value => value.Name == "cat").Id;
        long dog = snapshot.Suggestions.Single(value => value.Name == "dog").Id;
        snapshot = store.Decide([cat], TagSuggestionState.Accepted, snapshot.Revision);
        snapshot = store.Decide([dog], TagSuggestionState.Rejected, snapshot.Revision);

        snapshot = store.CommitRuns([Run(MediaA, R2, ("cat", 0.95), ("dog", 0.9), ("bird", 0.75), ("fish", 0.5))]);
        Assert.Equal(TagSuggestionState.Accepted, snapshot.Suggestions.Single(value => value.Id == cat).State);
        Assert.Equal(TagSuggestionState.Rejected, snapshot.Suggestions.Single(value => value.Id == dog).State);
        Assert.Equal(TagSuggestionState.Superseded, snapshot.Suggestions.Single(value => value.Name == "bird" && value.Provenance == R1).State);
        Assert.Equal(["bird", "fish"], snapshot.PendingFor(MediaA, 0.05).Select(value => value.Name));
        Assert.All(snapshot.PendingFor(MediaA, 0.05), value => Assert.Equal(R2, value.Provenance));
        Assert.True(snapshot.HasCompletedRun(MediaA, R1) && snapshot.HasCompletedRun(MediaA, R2));
    }

    [Fact]
    public void ARerunOfTheSameRevisionReplacesItsPendingSuggestions()
    {
        TagSuggestionStore store = Store();
        store.CommitRuns([Run(MediaA, R1, ("cat", 0.9), ("dog", 0.8))]);
        TagSuggestionSnapshot snapshot = store.CommitRuns([Run(MediaA, R1, ("cat", 0.7))]);
        StoredTagSuggestion only = Assert.Single(snapshot.PendingFor(MediaA, 0.05));
        Assert.Equal(("cat", 0.7, TagSuggestionState.Pending), (only.Name, only.Confidence, only.State));
        Assert.Single(snapshot.Runs);
        Assert.Equal(2, snapshot.Events.Count);
        Assert.All(snapshot.Events, value => Assert.Equal(TagSuggestionState.Superseded, value.To));
        Assert.All(snapshot.Events, value => Assert.True(value.SuggestionId < only.Id));
        Assert.Equal(only.Id, Assert.Single(Store().Load().PendingFor(MediaA, 0.05)).Id);
    }

    [Fact]
    public void ACompletedTaggerKeepsItsSuggestionAfterAnotherTaggersEmptyRerun()
    {
        TagSuggestionStore store = Store();
        store.CommitRuns([Run(MediaA, R1, ("cat", 0.9))]);
        TagSuggestionSnapshot snapshot = store.CommitRuns([Run(MediaA, OtherTagger, ("cat", 0.6), ("cat", 0.5))]);
        Assert.Equal([R1, OtherTagger], snapshot.PendingFor(MediaA, 0.05).Select(value => value.Provenance));
        StoredTagSuggestion other = Assert.Single(snapshot.Suggestions, value => value.Provenance == OtherTagger);

        store.CommitRuns([Run(MediaA, R1)]);

        TagSuggestionSnapshot resumed = Store().Load();
        Assert.True(resumed.HasCompletedRun(MediaA, OtherTagger));
        StoredTagSuggestion pending = Assert.Single(resumed.PendingFor(MediaA, 0.05));
        Assert.Equal(other.Id, pending.Id);
        Assert.Equal(("cat", 0.6, OtherTagger), (pending.Name, pending.Confidence, pending.Provenance));
        Assert.Equal(TagSuggestionState.Superseded, Assert.Single(resumed.Suggestions, value => value.Provenance == R1).State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DecisionsSuppressMatchingProposalsAcrossTaggersUntilUndo(bool accept)
    {
        TagSuggestionState decision = accept ? TagSuggestionState.Accepted : TagSuggestionState.Rejected;
        TagSuggestionStore store = Store();
        TagSuggestionSnapshot snapshot = store.CommitRuns([Run(MediaA, R1, ("cat", 0.4))]);
        long cat = Assert.Single(snapshot.Suggestions).Id;
        snapshot = store.CommitRuns([Run(MediaA, OtherTagger, ("cat", 0.9))]);
        long otherCat = Assert.Single(snapshot.Suggestions, value => value.Provenance == OtherTagger).Id;

        snapshot = store.Decide([cat], decision, snapshot.Revision);
        Assert.Empty(Store().Load().PendingFor(MediaA, 0.8));

        TagSuggestionProvenance thirdTagger = OtherTagger with { PluginId = "third.plugin" };
        snapshot = store.CommitRuns([Run(MediaA, thirdTagger, ("cat", 0.95)), Run(MediaB, thirdTagger, ("cat", 0.95))]);
        Assert.Empty(snapshot.PendingFor(MediaA, 0.05));
        Assert.Single(snapshot.PendingFor(MediaB, 0.05));
        Assert.DoesNotContain(snapshot.Suggestions, value => value.MediaSha256 == MediaA && value.Provenance == thirdTagger);

        snapshot = store.Decide([cat], TagSuggestionState.Pending, snapshot.Revision);
        Assert.Equal([otherCat, cat], snapshot.PendingFor(MediaA, 0.05).Select(value => value.Id));
    }

    [Fact]
    public void RepeatedWritesReuseOneStagingFileAndDoNotCreateSnapshots()
    {
        TagSuggestionStore store = Store();
        string stage = Path.Combine(_directory.FullName, "." + TagSuggestionStore.FileName + ".tmp");
        File.WriteAllText(stage, "abandoned staging data");
        TagSuggestionSnapshot pending = store.CommitRuns([Run(MediaA, R1, ("cat", 0.9))]);
        long id = Assert.Single(pending.Suggestions).Id;
        TagSuggestionSnapshot decided = store.Decide([id], TagSuggestionState.Rejected, pending.Revision);
        store.Decide([id], TagSuggestionState.Pending, decided.Revision);

        Assert.False(File.Exists(stage));
        Assert.DoesNotContain(Directory.EnumerateFiles(_directory.FullName),
            path => Path.GetFileName(path).StartsWith(TagSuggestionStore.FileName + ".", StringComparison.Ordinal));
        Assert.Equal(TagSuggestionState.Pending, Assert.Single(Store().Load().Suggestions).State);
    }

    [Fact]
    public void LostCommitAuthorityLeavesThePrimaryAndDeletesTheStage()
    {
        TagSuggestionStore store = Store();
        store.CommitRuns([Run(MediaA, R1, ("cat", 0.9))]);
        string primary = File.ReadAllText(store.FilePath);
        string stage = Path.Combine(_directory.FullName, "." + TagSuggestionStore.FileName + ".tmp");

        Assert.Throws<OperationCanceledException>(() => store.CommitRuns([Run(MediaB, R1, ("dog", 0.8))], () =>
        {
            Assert.True(File.Exists(stage));
            throw new OperationCanceledException();
        }, CancellationToken.None));

        Assert.Equal(primary, File.ReadAllText(store.FilePath));
        Assert.False(File.Exists(stage));
    }

    [Fact]
    public void PerItemCapKeepsTheBestScores()
    {
        TagSuggestionStore store = Store();
        store.Settings = TagSuggestionSettings.Default with { MaximumSuggestionsPerItem = 2 };
        TagSuggestionSnapshot snapshot = store.CommitRuns([Run(MediaA, R1, ("a", 0.2), ("b", 0.9), ("c", 0.5))]);
        Assert.Equal(["b", "c"], snapshot.Suggestions.OrderByDescending(value => value.Confidence).Select(value => value.Name));
        Assert.Equal(2, Assert.Single(snapshot.Runs).SuggestionCount);
    }

    [Fact]
    public void InvalidInputIsRefusedWithoutWriting()
    {
        TagSuggestionStore store = Store();
        TagSuggestionRunInput valid = Run(MediaA, R1, ("cat", 0.9));
        foreach (TagSuggestionRunInput invalid in new[]
                 {
                     valid with { MediaSha256 = "ABC" }, valid with { MediaSha256 = MediaA.ToUpperInvariant() }, valid with { MediaLength = 0 },
                     valid with { Provenance = R1 with { ModelId = " padded" } },
                     valid with { Candidates = [new TagSuggestionCandidate("cat", "General", 1.5)] },
                     valid with { Candidates = [new TagSuggestionCandidate("cat", "General", double.NaN)] },
                     valid with { Candidates = [new TagSuggestionCandidate("line\nbreak", "General", 0.5)] },
                     valid with { Candidates = [new TagSuggestionCandidate("cat", "General", 0.5, 10, null)] },
                     valid with { Candidates = [new TagSuggestionCandidate("cat", "General", 0.5, 10, 10)] }
                 })
            Assert.Equal(TagSuggestionStoreError.InvalidInput, Assert.Throws<TagSuggestionStoreException>(() => store.CommitRuns([invalid])).Error);
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void UnreadableOrNewerFilesArePreservedAndMakeTheStoreReadOnly()
    {
        TagSuggestionStore store = Store();
        File.WriteAllText(store.FilePath, "{broken");
        TagSuggestionSnapshot failed = store.Load();
        Assert.Equal(TagSuggestionStoreState.Failed, failed.State);
        Assert.Equal(TagSuggestionStoreError.NotWritable,
            Assert.Throws<TagSuggestionStoreException>(() => store.CommitRuns([Run(MediaA, R1, ("cat", 0.9))])).Error);
        Assert.Equal("{broken", File.ReadAllText(store.FilePath));

        File.WriteAllText(store.FilePath, """{"format":"meowchan-tag-suggestions","version":2,"revision":7}""");
        Assert.Equal(TagSuggestionStoreState.Unsupported, store.Load().State);
        Assert.Throws<TagSuggestionStoreException>(() => store.CommitRuns([Run(MediaA, R1, ("cat", 0.9))]));
        Assert.Contains("\"version\":2", File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void ANewStoreAndItsPersistedEmptyDocumentAreWritable()
    {
        TagSuggestionStore store = Store();
        Assert.Equal(TagSuggestionSnapshot.Empty, store.Load());
        Assert.False(File.Exists(store.FilePath));

        TagSuggestionSnapshot empty = store.CommitRuns([]);
        TagSuggestionStore reopened = Store();
        TagSuggestionSnapshot persisted = reopened.Load();
        Assert.True(persisted.CanWrite);
        Assert.Equal(empty.Revision, persisted.Revision);
        Assert.Empty(persisted.Suggestions);
        Assert.Empty(persisted.Runs);
        Assert.Empty(persisted.Events);
        Assert.Equal(1L, Assert.Single(reopened.CommitRuns([Run(MediaA, R1, ("cat", 0.9))]).Suggestions).Id);
    }

    [Theory]
    [InlineData("{ }\n")]
    [InlineData("""{"format":"meowchan-tag-suggestions","version":1,"revision":0,"nextId":1,"nextSequence":1,"suggestions":[],"events":[]}""")]
    public void AnExistingDocumentWithMissingFieldsIsPreservedByteForByte(string json)
    {
        TagSuggestionStore store = Store();
        File.WriteAllText(store.FilePath, json);
        byte[] original = File.ReadAllBytes(store.FilePath);

        TagSuggestionSnapshot snapshot = store.Load();
        Assert.Equal(TagSuggestionStoreState.Failed, snapshot.State);
        Assert.Equal(TagSuggestionStoreError.NotWritable,
            Assert.Throws<TagSuggestionStoreException>(() => store.CommitRuns([Run(MediaA, R1, ("cat", 0.9))])).Error);
        Assert.Equal(TagSuggestionStoreError.NotWritable,
            Assert.Throws<TagSuggestionStoreException>(() => store.Decide([1L], TagSuggestionState.Rejected, snapshot.Revision)).Error);
        Assert.Equal(original, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void LegacyCountersAreRepairedUsingSurvivingSuggestionsAndReviewEvents()
    {
        TagSuggestionStore store = Store();
        store.CommitRuns([Run(MediaA, R1, ("cat", 0.9)), Run(MediaB, R1, ("dog", 0.8))]);
        store.CommitRuns([Run(MediaB, R1)]);
        JsonNode document = JsonNode.Parse(File.ReadAllText(store.FilePath))!;
        document["suggestions"]!.AsArray().RemoveAt(1);
        document["nextId"] = 1;
        document["nextSequence"] = 1;
        File.WriteAllText(store.FilePath, document.ToJsonString());

        TagSuggestionStore reopened = Store();
        Assert.True(reopened.Load().CanWrite);
        TagSuggestionSnapshot snapshot = reopened.CommitRuns([Run(MediaB, R1, ("bird", 0.7))]);
        StoredTagSuggestion bird = Assert.Single(snapshot.PendingFor(MediaB, 0.05));
        Assert.Equal(3L, bird.Id);
        snapshot = reopened.Decide([bird.Id], TagSuggestionState.Accepted, snapshot.Revision);
        Assert.Equal([1L, 2L], snapshot.Events.Select(value => value.Sequence));
    }

    [Fact]
    public void TheSizeLimitStopsGrowthButNotDecisions()
    {
        TagSuggestionStore store = Store();
        TagSuggestionSnapshot snapshot = store.CommitRuns([Run(MediaA, R1, ("cat", 0.9))]);
        long size = new FileInfo(store.FilePath).Length;
        Assert.True(size < TagSuggestionSettings.StoreLimitMiBRange.Minimum * 1024L * 1024);
        // At the lowest selectable limit (4 MiB), 40 media with 512 long names each would grow the file past it.
        store.Settings = TagSuggestionSettings.Default with { StoreLimitMiB = TagSuggestionSettings.StoreLimitMiBRange.Minimum, MaximumSuggestionsPerItem = 512 };
        TagSuggestionRunInput[] huge = Enumerable.Range(0, 40).Select(media => Run(media.ToString("x64"), R1,
            Enumerable.Range(0, 512).Select(index => (new string('t', 200) + index, 0.5)).ToArray())).ToArray();
        Assert.Equal(TagSuggestionStoreError.LimitReached, Assert.Throws<TagSuggestionStoreException>(() => store.CommitRuns(huge)).Error);
        Assert.Equal(size, new FileInfo(store.FilePath).Length);
        snapshot = store.Decide([snapshot.Suggestions[0].Id], TagSuggestionState.Accepted, snapshot.Revision);
        Assert.Equal(2, snapshot.Revision);
    }

    [Fact]
    public void SettingsHaveDefaultsBoundsAndRefuseOutOfRangeValues()
    {
        TagSuggestionSettings defaults = TagSuggestionSettings.Default;
        Assert.True(defaults.IsValid);
        Assert.Equal((35, 8, 32, 64, 120, 32), (defaults.ConfidenceThresholdPercent, defaults.BatchSize, defaults.MaximumInputMiB,
            defaults.MaximumSuggestionsPerItem, defaults.BatchTimeoutSeconds, defaults.StoreLimitMiB));
        Assert.Equal(0.05, TagSuggestionSettings.ConfidenceFloor);
        Assert.Equal(32L * 1024 * 1024, defaults.MaximumInputBytes);
        // The maxima are the tagger contract ceilings (TaggerLimits in the SDK): 64 items, 256 MiB, 512 suggestions.
        Assert.Equal((64, 256, 512), (TagSuggestionSettings.BatchSizeRange.Maximum, TagSuggestionSettings.MaximumInputMiBRange.Maximum,
            TagSuggestionSettings.MaximumSuggestionsPerItemRange.Maximum));
        Assert.Equal(TagSuggestionStore.MaximumBytes, TagSuggestionSettings.StoreLimitMiBRange.Maximum * 1024L * 1024);

        TagSuggestionSettings outside = defaults with { ConfidenceThresholdPercent = 99, BatchSize = 0, StoreLimitMiB = 1000 };
        Assert.False(outside.IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => Store().Settings = outside);
        TagSuggestionSettings clamped = outside.Clamped();
        Assert.True(clamped.IsValid);
        Assert.Equal((95, 1, 256), (clamped.ConfidenceThresholdPercent, clamped.BatchSize, clamped.StoreLimitMiB));
    }
}
