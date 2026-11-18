using System.Security.Cryptography;
using System.Text;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

// Controller contract tests only. The recording adapter has no workers, network or activation path.
public sealed class BundledChangeApprovalTests
{
    [Theory]
    [InlineData("description-changed")]
    [InlineData("configuration-incompatible")]
    public async Task ReviewableButNotOrdinaryConfirmableRowDispatchesAndProducesSavedReceipt(string blocker)
    {
        RecordingStore store = new();
        store.Row = store.Row with { BlockedReason = blocker };
        var choice = Choice(store);
        Assert.False(store.Row.CanConfirm); // Regression: preview admission must use CanReviewChanges.
        Assert.True(store.Row.CanReviewChanges);
        PluginMultiApproval session = new(store);
        await session.SubmitAsync([choice], () => true);
        var write = Assert.Single(store.Writes);
        Assert.Equal(store.Row, write.Row);
        Assert.Equal(73, write.Revision);
        Assert.True(write.Changes);
        Assert.False(write.Rights);
        Assert.Equal(SdkJson.Serialize(choice.ChangeConfiguration!), SdkJson.Serialize(write.Configuration));
        var receipt = Assert.Single(session.Receipts);
        Assert.Equal(PluginApprovalState.Saved, receipt.State);
        Assert.Equal(74L, receipt.Revision);
        Assert.Null(session.IssueKey);
        Assert.False(session.MayOffer(store.Row.PluginId));
        await session.SubmitAsync([choice], () => true);
        Assert.Single(store.Writes); // Consumed control cannot repeat the save.
    }

    [Fact]
    public async Task NoAcknowledgementCannotDispatchEvenWithSeparateRightsConsent()
    {
        RecordingStore store = new();
        PluginMultiApproval session = new(store);
        await session.SubmitAsync([Choice(store) with { AcknowledgeChanges = false, AuthorizePermissions = true }], () => true);
        Assert.Empty(store.Writes);
        Assert.Equal("plugins.approval.needsReview", session.IssueKey);
        Assert.Equal(PluginApprovalState.NotExecuted, Assert.Single(session.Receipts).State);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("fingerprint")]
    [InlineData("payload")]
    [InlineData("execution")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task FreshPreviewMustMatchFrozenRowAndRevision(string mutation)
    {
        RecordingStore store = new();
        var choice = Choice(store);
        var changed = mutation switch
        {
            "fingerprint" => store.Row with { ChangeFingerprint = "foreign fingerprint" },
            "payload" => store.Row with { ChangeReviewJson = store.Row.ChangeReviewJson + " " },
            "execution" => store.Row with { NewExecutionRevision = "foreign execution" },
            _ => store.Row
        };
        store.Preview = new(mutation == "revision" ? 74 : 73,
            mutation == "missing" ? [] : mutation == "duplicate" ? [changed, changed] : [changed]);
        PluginMultiApproval session = new(store);
        await session.SubmitAsync([choice], () => true);
        Assert.Empty(store.Writes);
        Assert.Equal(PluginApprovalState.NeedsReview, Assert.Single(session.Receipts).State);
        Assert.Equal("plugins.approval.needsReview", session.IssueKey);
    }

    [Theory]
    [InlineData("Reauthorization")]
    [InlineData("Repair")]
    [InlineData("ChangeReview")]
    public async Task ChangeReviewCannotJoinABatch(string kind)
    {
        var otherKind = Enum.Parse<PluginApprovalKind>(kind);
        RecordingStore store = new();
        var other = store.Row with { PluginId = "other", BlockedReason = otherKind == PluginApprovalKind.Repair ? null : "inherited-grants",
            PermissionReviewJson = "{}", PermissionFingerprint = "rights" };
        var second = otherKind == PluginApprovalKind.ChangeReview
            ? Choice(store) with { Repair = store.Row with { PluginId = "other" } }
            : new PluginApprovalChoice(otherKind, 73, Repair: other);
        PluginMultiApproval session = new(store);
        await session.SubmitAsync([Choice(store), second], () => true);
        Assert.Empty(store.Writes);
        Assert.All(session.Receipts, receipt => Assert.Equal(PluginApprovalState.NotExecuted, receipt.State));
        Assert.Equal("plugins.approval.needsReview", session.IssueKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSpecialRightsAreNeverInferredFromChangeAcknowledgement(bool authorize)
    {
        RecordingStore store = new(requiresRights: true);
        PluginMultiApproval session = new(store);
        await session.SubmitAsync([Choice(store) with { AuthorizePermissions = authorize }], () => true);
        var write = Assert.Single(store.Writes);
        Assert.True(write.Changes);
        Assert.Equal(authorize, write.Rights);
        Assert.Equal(authorize ? PluginApprovalState.Saved : PluginApprovalState.Failed, Assert.Single(session.Receipts).State);
        Assert.Equal(authorize ? 74 : 73, store.Revision);
    }

    [Fact]
    public async Task SubmitOwnsNestedConfigurationBeforeAwaitingPreview()
    {
        RecordingStore store = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforePreview = async () => { entered.SetResult(); await release.Task; };
        List<SettingValue> plugin = [new("label", SettingKind.Text, Text: "frozen plugin")];
        List<SettingValue> source = [new("limit", SettingKind.Integer, Integer: 17)];
        Dictionary<string, SettingsSnapshot> sources = new() { ["source"] = new("1", source) };
        PluginConfiguration configuration = new("frozen revision", new("1", plugin), sources);
        byte[] expected = SdkJson.Serialize(configuration);
        List<PluginApprovalChoice> choices = [Choice(store) with { ChangeConfiguration = configuration }];
        PluginMultiApproval session = new(store);
        Task save = session.SubmitAsync(choices, () => true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            plugin.Clear(); source[0] = new("limit", SettingKind.Integer, Integer: 49);
            sources.Clear(); choices.Clear();
        }
        finally { release.TrySetResult(); await save; }
        Assert.Equal(expected, SdkJson.Serialize(Assert.Single(store.Writes).Configuration));
        var receipt = Assert.Single(session.Receipts);
        Assert.Equal(PluginApprovalState.Saved, receipt.State);
        Assert.Equal(expected, SdkJson.Serialize(receipt.Choice.ChangeConfiguration!));
    }

    [Theory]
    [InlineData("description")]
    [InlineData("configuration")]
    [InlineData("permissions")]
    public async Task RehashedMaterialRefreshAtSameExecutionNeedsANewAcknowledgedChoice(string material)
    {
        RecordingStore store = new();
        var oldChoice = Choice(store);
        var review = SdkJson.Deserialize<BundledPluginChangeReview>(Encoding.UTF8.GetBytes(store.Row.ChangeReviewJson!));
        review = material switch
        {
            "description" => review with { NewDescription = review.NewDescription with { Name = "Newly displayed name" } },
            "configuration" => review with { PreviousConfiguration = review.PreviousConfiguration with { Revision = "new basis" } },
            _ => review with { Permissions = new([], ["newly-restricted-source"]) }
        };
        string json = Encoding.UTF8.GetString(SdkJson.Serialize(review));
        store.Row = store.Row with { ChangeReviewJson = json,
            ChangeFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) };
        Assert.Equal(oldChoice.Repair!.NewExecutionRevision, store.Row.NewExecutionRevision);
        Assert.True(PluginsPage.TryReadBundledChangeReview(store.Row, out _)); // Not malformed/stale-hash rejection.

        PluginMultiApproval session = new(store);
        await session.SubmitAsync([oldChoice], () => true);
        Assert.Empty(store.Writes);
        Assert.Equal(PluginApprovalState.NeedsReview, Assert.Single(session.Receipts).State);
        Assert.True(session.MayOffer(store.Row.PluginId));

        var refreshed = Choice(store) with { ChangeConfiguration = oldChoice.ChangeConfiguration! with { Revision = "fresh correction" },
            AcknowledgeChanges = false, AuthorizePermissions = true };
        await session.SubmitAsync([refreshed], () => true);
        Assert.Empty(store.Writes); // Refresh and separate rights consent cannot resurrect the old acknowledgement.
        Assert.Equal(PluginApprovalState.NotExecuted, session.Receipts[1].State);
        Assert.Equal("plugins.approval.needsReview", session.IssueKey);

        await session.SubmitAsync([refreshed with { AcknowledgeChanges = true }], () => true);
        var write = Assert.Single(store.Writes);
        Assert.Equal(store.Row, write.Row);
        Assert.Equal(SdkJson.Serialize(refreshed.ChangeConfiguration!), SdkJson.Serialize(write.Configuration));
        Assert.Equal(new[] { PluginApprovalState.NeedsReview, PluginApprovalState.NotExecuted, PluginApprovalState.Saved },
            session.Receipts.Select(receipt => receipt.State));
        Assert.Equal(74L, session.Receipts[2].Revision);
        Assert.Null(session.IssueKey);
    }

    private static PluginApprovalChoice Choice(RecordingStore store) => new(PluginApprovalKind.ChangeReview, 73,
        Repair: store.Row, ChangeConfiguration: new("edited", new("1", [new("label", SettingKind.Text, Text: "corrected")]),
            new Dictionary<string, SettingsSnapshot>()), AcknowledgeChanges: true);

    private sealed class RecordingStore : IPluginProfileStore
    {
        internal long Revision { get; private set; } = 73;
        internal BundledCandidateRepair Row { get; set; }
        internal BundledCandidateRepairPreview? Preview { get; set; }
        internal Func<Task>? BeforePreview { get; set; }
        internal List<(BundledCandidateRepair Row, PluginConfiguration Configuration, bool Changes, bool Rights, long Revision)> Writes { get; } = [];
        private readonly bool _requiresRights;
        internal RecordingStore(bool requiresRights = false)
        {
            _requiresRights = requiresRights;
            SettingsSchema empty = new("1", []);
            // Inert on purpose: SdkValidation rejects a plugin without a source or filter unless it is explicitly blocked, and the
            // review reader validates both descriptions.
            PluginDescription description = new("fixture", "Fixture", "1.0", SdkContract.Version, "Inert fixture", [], [], empty, [])
            { DeclarationStatus = PluginDeclarationStatus.Blocked };
            var review = new BundledPluginChangeReview(description, description,
                new("old", new("1", []), new Dictionary<string, SettingsSnapshot>()),
                new([], requiresRights ? ["source"] : []));
            string json = Encoding.UTF8.GetString(SdkJson.Serialize(review));
            Row = new("fixture", "old-id", "new-id", "slot", "/old", "/new", "old-execution", "new-execution", true, "description-changed")
            { ChangeReviewJson = json, ChangeFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))) };
        }
        public event Action<PluginLifecycleChange>? LifecycleChanged { add { } remove { } }
        public event Action<PluginGrantChange>? GrantChanged { add { } remove { } }
        public Task<PluginProfileSnapshot> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new PluginProfileSnapshot(Revision, [], []));
        public async Task<BundledCandidateRepairPreview> PreviewBundledRepairsAsync(CancellationToken cancellationToken = default)
        {
            if (BeforePreview is { } before) await before();
            return Preview ?? new(Revision, [Row]);
        }
        public Task<PluginProfileSnapshot> ConfirmBundledChangeAsync(BundledCandidateRepair repair, PluginConfiguration configuration,
            bool acknowledgeChanges, bool authorizePermissions, long expectedRevision, CancellationToken cancellationToken = default)
        {
            Writes.Add((repair, configuration, acknowledgeChanges, authorizePermissions, expectedRevision));
            if (expectedRevision != Revision) throw new PluginProfileConflictException(expectedRevision, Revision);
            if (!acknowledgeChanges || _requiresRights && !authorizePermissions) throw new InvalidOperationException("Missing explicit consent");
            Revision++;
            return ReadAsync(cancellationToken);
        }
        // Repair, reselection and reauthorization use the interface's fail-closed default methods.
        private static Task<PluginProfileSnapshot> Forbidden() => throw new InvalidOperationException("Unexpected mutation API");
        public Task<PluginProfileSnapshot> ObserveCandidateAsync(PluginCandidateObservation candidate, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> ApplyStartupAsync(IReadOnlyList<PluginCandidateObservation> candidates, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SelectCandidateAsync(string pluginId, string candidateId, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SaveConfigurationAsync(string pluginId, string candidateId, PluginConfiguration configuration, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SelectRollbackAsync(string pluginId, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SetEnabledAsync(string pluginId, bool enabled, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> SetGrantAsync(string pluginId, string filterKey, PluginGrantScope scope, FilterDataAccess access, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
        public Task<PluginProfileSnapshot> ReportActivationFailureAsync(string pluginId, string executionRevision, string problem, long expectedRevision, CancellationToken cancellationToken = default) => Forbidden();
    }
}
