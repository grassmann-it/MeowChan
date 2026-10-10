using Microsoft.Data.Sqlite;
using oChan.Archive;
using oChan.Persistence;
using oChan.Rules;

namespace oChan.Tests;

public sealed class MediaRuleDecisionTests
{
    private static readonly ResourceId Thread = new ResourceId("4chan", "g", "123");

    [Fact]
    public async Task ExclusionsSurviveChangedRulesMetadataAndRestartWhileNewCandidatesUseNewRules()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        MediaRuleSet firstRules = await fixture.Rules.SaveAsync(new[] { OnlyJpeg() });
        CandidateMetadata initial = fixture.Metadata("1", extension: ".png");
        CandidateDecision first = await fixture.Catalog.GetOrCreateDecisionAsync(initial, fixture.Rules);
        MediaRuleSet newRules = await fixture.Rules.SaveAsync(Array.Empty<MediaRule>());
        CandidateMetadata changed = fixture.Metadata("1", extension: ".webm", size: 300,
            url: "https://changed.example/new-address", target: "changed-target.webm", md5: "new-hash", name: "new-name.webm");

        CandidateDecision repeated = await fixture.Catalog.GetOrCreateDecisionAsync(changed, fixture.Rules);
        await fixture.RestartAsync();
        CandidateDecision restored = await fixture.Catalog.GetOrCreateDecisionAsync(changed, fixture.Rules, previouslySaved: true);
        CandidateDecision fresh = await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("2", extension: ".png"), fixture.Rules);

        Assert.Equal(MediaSelectionOutcome.Excluded, repeated.Selection.Outcome);
        Assert.Equal(MediaSelectionReason.TypeExcluded, restored.Selection.Reason);
        Assert.Equal(firstRules.Revision, restored.Selection.EffectiveRule.Revision);
        Assert.Equal(first.DecidedAtUtc, restored.DecidedAtUtc);
        Assert.Equal(initial.DestinationPath, restored.Metadata.DestinationPath);
        Assert.Equal(changed.DownloadUri, restored.Metadata.DownloadUri);
        Assert.Equal(300, restored.Metadata.ExpectedSize);
        Assert.Equal("new-hash", restored.Metadata.ExpectedMd5);
        Assert.Equal("new-name.webm", restored.Metadata.OriginalFileName);
        Assert.Equal(".webm", restored.Metadata.Extension);
        Assert.Equal(".png", restored.Selection.Extension);
        Assert.Equal(initial.ExpectedSize, restored.Selection.SizeBytes);
        Assert.Equal(MediaSelectionOutcome.Selected, fresh.Selection.Outcome);
        Assert.Equal(newRules.Revision, fresh.Selection.EffectiveRule.Revision);
        Assert.Equal(2, (await fixture.Catalog.GetDecisionsAsync(Thread)).Count);
    }

    [Fact]
    public async Task SelectedIntentKeepsOriginalTargetAndRuleAfterLaterExclusion()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        CandidateMetadata metadata = fixture.Metadata("1", legacy: "legacy.jpg");
        CandidateDecision first = await fixture.Catalog.GetOrCreateDecisionAsync(metadata, fixture.Rules);
        await fixture.Rules.SaveAsync(new[] { ExcludeAll() });

        CandidateDecision again = await fixture.Catalog.GetOrCreateDecisionAsync(
            fixture.Metadata("1", target: "other.jpg", legacy: "other-legacy.jpg"), fixture.Rules);

        Assert.Equal(MediaSelectionOutcome.Selected, again.Selection.Outcome);
        Assert.Equal(first.Selection.EffectiveRule.Revision, again.Selection.EffectiveRule.Revision);
        Assert.Equal(metadata.DestinationPath, again.Metadata.DestinationPath);
        Assert.Equal(metadata.LegacyPath, again.Metadata.LegacyPath);
        Assert.False(File.Exists(metadata.DestinationPath));
    }

    [Fact]
    public async Task PreviouslySavedMigrationSelectsOldAssetsWithoutClaimingNewRuleAllowsThem()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        await fixture.Rules.SaveAsync(new[] { ExcludeAll() });

        CandidateDecision migrated = await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("old"), fixture.Rules, previouslySaved: true);
        CandidateDecision fresh = await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("new"), fixture.Rules);
        await fixture.RestartAsync();
        IReadOnlyList<CandidateDecision> restored = await fixture.Catalog.GetDecisionsAsync(Thread);

        Assert.Equal(MediaSelectionOutcome.Selected, migrated.Selection.Outcome);
        Assert.Equal(MediaSelectionReason.PreviouslySaved, migrated.Selection.Reason);
        Assert.Empty(migrated.Selection.EffectiveRule.Types.Extensions);
        Assert.Equal(MediaSelectionOutcome.Excluded, fresh.Selection.Outcome);
        Assert.Equal(MediaSelectionReason.PreviouslySaved, Assert.Single(restored, item => item.Metadata.Key.MediaId == "old").Selection.Reason);
    }

    [Fact]
    public async Task EffectiveValuesOriginsAndObservedUnknownsRoundTripCompletely()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        MediaRuleScope board = MediaRuleScope.ForBoard(Thread.Source, Thread.Board);
        MediaRuleScope thread = MediaRuleScope.ForThread(Thread.Source, Thread.Board, Thread.Thread!);
        MediaRuleSet rules = await fixture.Rules.SaveAsync(new[]
        {
            new MediaRule(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { "jpg", "webm" }),
                sizeLimit: new MediaSizeLimit(MediaSizeLimitMode.Limited, 100)),
            new MediaRule(MediaRuleScope.ForSource(Thread.Source), sizeLimit: new MediaSizeLimit(MediaSizeLimitMode.Limited, 50)),
            new MediaRule(board, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { "JPG" })),
            new MediaRule(thread, sizeLimit: MediaSizeLimit.Unlimited)
        });
        await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1", size: null), fixture.Rules);
        await fixture.RestartAsync();

        MediaSelectionResult selection = Assert.Single(await fixture.Catalog.GetDecisionsAsync(Thread)).Selection;

        Assert.Equal(MediaSelectionReason.AllowedUnknownSize, selection.Reason);
        Assert.Null(selection.SizeBytes);
        Assert.Equal(".jpg", selection.Extension);
        Assert.Equal(rules.Revision, selection.EffectiveRule.Revision);
        Assert.Equal(new[] { ".jpg" }, selection.EffectiveRule.Types.Extensions);
        Assert.Equal(MediaTypeSelectionMode.Selected, selection.EffectiveRule.Types.Mode);
        Assert.Equal(MediaSizeLimitMode.Unlimited, selection.EffectiveRule.SizeLimit.Mode);
        Assert.Null(selection.EffectiveRule.SizeLimit.Bytes);
        Assert.Equal(new MediaRuleOrigin(board, rules.Revision), selection.EffectiveRule.TypesOrigin);
        Assert.Equal(new MediaRuleOrigin(thread, rules.Revision), selection.EffectiveRule.SizeLimitOrigin);
    }

    [Fact]
    public async Task BuiltinOriginsAndMissingMetadataRoundTripWithoutLiveProvidersOrRuleLoading()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1", extension: null, size: null), fixture.Rules);
        await fixture.RestartAsync();
        MediaRuleStore unloadedRules = new MediaRuleStore(fixture.Directory.Path, fixture.Lease);

        CandidateDecision existing = await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1", extension: null, size: null), unloadedRules);

        Assert.Null(existing.Selection.EffectiveRule.TypesOrigin.Scope);
        Assert.Null(existing.Selection.EffectiveRule.SizeLimitOrigin.Scope);
        Assert.Null(existing.Selection.Extension);
        Assert.Null(existing.Selection.SizeBytes);
        Assert.Equal(MediaSelectionOutcome.Selected, existing.Selection.Outcome);
    }

    [Fact]
    public async Task DecisionPersistenceIsIndependentOfDisabledPostCapture()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        await fixture.Catalog.RegisterThreadAsync(new ArchiveThreadRegistration(Thread,
            "https://boards.4chan.org/g/thread/123", "Media only", Path.Combine(fixture.Directory.Path, "media"), false));
        await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1"), fixture.Rules);
        await fixture.RestartAsync();

        Assert.False(await fixture.Catalog.GetCaptureEnabledAsync(Thread));
        Assert.Single(await fixture.Catalog.GetDecisionsAsync(Thread));
    }

    [Fact]
    public async Task SourceThreadAndVariantAreSeparateWhileUrlsAndHashesAreNotIdentity()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1"), fixture.Rules);
        await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1", variant: "preview"), fixture.Rules);
        ResourceId otherSource = new ResourceId("8kun", "g", "123");
        ResourceId otherThread = new ResourceId("4chan", "g", "456");
        await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1", resource: otherSource), fixture.Rules);
        await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1", resource: otherThread), fixture.Rules);
        await fixture.Catalog.GetOrCreateDecisionAsync(fixture.Metadata("1", url: "https://other.example/x", md5: "changed"), fixture.Rules);

        Assert.Equal(2, (await fixture.Catalog.GetDecisionsAsync(Thread)).Count);
        Assert.Single(await fixture.Catalog.GetDecisionsAsync(otherSource));
        Assert.Single(await fixture.Catalog.GetDecisionsAsync(otherThread));
    }

    [Fact]
    public async Task ConcurrentFirstSubmissionsCreateOneDurableDecision()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        CandidateMetadata metadata = fixture.Metadata("1");
        Task<CandidateDecision>[] submissions = Enumerable.Range(0, 16)
            .Select(_ => fixture.Catalog.GetOrCreateDecisionAsync(metadata, fixture.Rules)).ToArray();

        CandidateDecision[] decisions = await Task.WhenAll(submissions);

        CandidateDecision stored = Assert.Single(await fixture.Catalog.GetDecisionsAsync(Thread));
        Assert.All(decisions, decision => Assert.Equal(stored.DecidedAtUtc, decision.DecidedAtUtc));
        Assert.All(decisions, decision => Assert.Equal(stored.Selection.EffectiveRule.Revision, decision.Selection.EffectiveRule.Revision));
    }

    [Fact]
    public async Task ConcurrentRuleChangeProducesOneConsistentFirstRevision()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        string oldRevision = fixture.Rules.Current.Revision;
        CandidateMetadata metadata = fixture.Metadata("1");

        Task<CandidateDecision> admission = fixture.Catalog.GetOrCreateDecisionAsync(metadata, fixture.Rules);
        Task<MediaRuleSet> save = fixture.Rules.SaveAsync(new[] { ExcludeAll() });
        CandidateDecision decided = await admission;
        MediaRuleSet changed = await save;
        CandidateDecision repeat = await fixture.Catalog.GetOrCreateDecisionAsync(metadata, fixture.Rules);

        Assert.Contains(decided.Selection.EffectiveRule.Revision, new[] { oldRevision, changed.Revision });
        Assert.Equal(decided.Selection.EffectiveRule.Revision, repeat.Selection.EffectiveRule.Revision);
        Assert.Equal(decided.Selection.Outcome, repeat.Selection.Outcome);
        Assert.Equal(decided.Selection.EffectiveRule.Revision == oldRevision
            ? MediaSelectionOutcome.Selected : MediaSelectionOutcome.Excluded, decided.Selection.Outcome);
    }

    [Fact]
    public async Task FailedDecisionInsertReturnsNoIntentAndLaterAttemptUsesCurrentRules()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        await ExecuteSqlAsync(fixture.Catalog.DatabasePath,
            "CREATE TRIGGER reject_candidate BEFORE INSERT ON media_decisions BEGIN SELECT RAISE(ABORT, 'fixture disk failure'); END;");
        CandidateMetadata metadata = fixture.Metadata("1");

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Catalog.GetOrCreateDecisionAsync(metadata, fixture.Rules));
        Assert.Empty(await fixture.Catalog.GetDecisionsAsync(Thread));
        await fixture.Rules.SaveAsync(new[] { ExcludeAll() });
        await ExecuteSqlAsync(fixture.Catalog.DatabasePath, "DROP TRIGGER reject_candidate;");
        CandidateDecision retry = await fixture.Catalog.GetOrCreateDecisionAsync(metadata, fixture.Rules);

        Assert.Equal(MediaSelectionOutcome.Excluded, retry.Selection.Outcome);
        Assert.False(File.Exists(metadata.DestinationPath));
    }

    [Fact]
    public async Task FailedMetadataUpdateKeepsOldIntentAndAcquisitionData()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        CandidateMetadata original = fixture.Metadata("1");
        CandidateDecision first = await fixture.Catalog.GetOrCreateDecisionAsync(original, fixture.Rules);
        await ExecuteSqlAsync(fixture.Catalog.DatabasePath,
            "CREATE TRIGGER reject_refresh BEFORE UPDATE ON media_decisions BEGIN SELECT RAISE(ABORT, 'fixture disk failure'); END;");

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Catalog.GetOrCreateDecisionAsync(
            fixture.Metadata("1", url: "https://new.example/image"), fixture.Rules));
        CandidateDecision durable = Assert.Single(await fixture.Catalog.GetDecisionsAsync(Thread));

        Assert.Equal(original.DownloadUri, durable.Metadata.DownloadUri);
        Assert.Equal(first.DecidedAtUtc, durable.DecidedAtUtc);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":2}")]
    public async Task DamagedDecisionNeverBecomesANewFirstDecision(string invalidSelection)
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        CandidateMetadata metadata = fixture.Metadata("1");
        await fixture.Catalog.GetOrCreateDecisionAsync(metadata, fixture.Rules);
        using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Catalog.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        using SqliteCommand corrupt = connection.CreateCommand();
        corrupt.CommandText = "UPDATE media_decisions SET selection_json=$json;";
        corrupt.Parameters.AddWithValue("$json", invalidSelection);
        await corrupt.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.GetDecisionsAsync(Thread));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Catalog.GetOrCreateDecisionAsync(metadata, fixture.Rules));
    }

    [Fact]
    public async Task CancelledOperationCreatesNoDecision()
    {
        await using DecisionFixture fixture = await DecisionFixture.CreateAsync();
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Catalog.GetOrCreateDecisionAsync(
            fixture.Metadata("1"), fixture.Rules, cancellationToken: cancellation.Token));

        Assert.Empty(await fixture.Catalog.GetDecisionsAsync(Thread));
    }

    private static MediaRule OnlyJpeg() => new MediaRule(MediaRuleScope.Global,
        types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { ".jpg" }));
    private static MediaRule ExcludeAll() => new MediaRule(MediaRuleScope.Global,
        types: new MediaTypeSelection(MediaTypeSelectionMode.Selected));

    private static async Task ExecuteSqlAsync(string path, string sql)
    {
        using SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DecisionFixture : IAsyncDisposable
    {
        public TempDirectory Directory { get; }
        public ProfileLease Lease { get; }
        public MediaRuleStore Rules { get; private set; }
        public ArchiveCatalog Catalog { get; private set; }

        private DecisionFixture(TempDirectory directory, ProfileLease lease, MediaRuleStore rules, ArchiveCatalog catalog)
        {
            Directory = directory;
            Lease = lease;
            Rules = rules;
            Catalog = catalog;
        }

        public static async Task<DecisionFixture> CreateAsync()
        {
            TempDirectory directory = new TempDirectory();
            ProfileLease lease = ProfileLease.Acquire(directory.Path);
            try
            {
                MediaRuleStore rules = new MediaRuleStore(directory.Path, lease);
                await rules.LoadAsync();
                ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
                return new DecisionFixture(directory, lease, rules, catalog);
            }
            catch { lease.Dispose(); directory.Dispose(); throw; }
        }

        public CandidateMetadata Metadata(string mediaId, string? extension = ".jpg", long? size = 10,
            string? url = null, string? target = null, string? md5 = null, string? name = null,
            string? legacy = null, ResourceId? resource = null, string variant = "original")
        {
            return new CandidateMetadata(new CandidateKey(resource ?? Thread, mediaId, variant),
                new Uri(url ?? "https://media.example/" + mediaId + ".jpg"),
                Path.Combine(Directory.Path, "media", target ?? mediaId + ".jpg"), size, md5, name,
                legacy == null ? null : Path.Combine(Directory.Path, "legacy", legacy), extension);
        }

        public async Task RestartAsync()
        {
            await Catalog.DisposeAsync();
            Rules = new MediaRuleStore(Directory.Path, Lease);
            await Rules.LoadAsync();
            Catalog = await ArchiveCatalog.OpenAsync(Directory.Path, Lease);
        }

        public async ValueTask DisposeAsync()
        {
            await Catalog.DisposeAsync();
            Lease.Dispose();
            Directory.Dispose();
        }
    }
}
