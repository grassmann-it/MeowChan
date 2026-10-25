using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed class PluginProfileStoreTests : IDisposable
{
    private readonly string profile = Path.Combine(Path.GetTempPath(), "meowchan-activation-" + Guid.NewGuid().ToString("N"));
    private static readonly SettingsSchema Empty = new("1", Array.Empty<SettingDefinition>());
    private PluginProfileStore Store() => new(profile);
    public void Dispose() { if (Directory.Exists(profile)) Directory.Delete(profile, true); }

    [Fact]
    public async Task OptionalDefaultAppliesAtFirstObservationAndStartupWithoutChangingLegacyDefault()
    {
        PluginProfileStore store = new(profile, pluginId => pluginId != "test.plugin");
        PluginCandidateObservation candidate = Candidate("a");
        PluginProfileSnapshot state = await store.ObserveCandidateAsync(candidate, 0);
        Assert.False(Assert.Single(state.Plugins).Enabled);
        Assert.False(Assert.Single((await Store().ReadAsync()).Plugins).Enabled);
        Assert.Null(state.Plugins[0].Active);
        state = await store.ApplyStartupAsync([candidate], state.Revision);
        Assert.False(state.Plugins[0].Enabled);
        Assert.NotNull(state.Plugins[0].Active);

        PluginCandidateObservation independent = candidate with { CandidateId = "independent.cs", PluginId = "independent.plugin",
            Description = candidate.Description! with { PluginId = "independent.plugin" } };
        state = await store.ApplyStartupAsync([candidate, independent], state.Revision);
        Assert.True(state.Plugins.Single(plugin => plugin.PluginId == independent.PluginId).Enabled);

        PluginCandidateObservation legacy = candidate with { CandidateId = "legacy.cs", PluginId = "legacy.plugin",
            Description = candidate.Description! with { PluginId = "legacy.plugin" } };
        state = await Store().ApplyStartupAsync([candidate, independent, legacy], state.Revision);
        Assert.True(state.Plugins.Single(plugin => plugin.PluginId == legacy.PluginId).Enabled);
        Assert.False(state.Plugins.Single(plugin => plugin.PluginId == candidate.PluginId).Enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstStartupWithMultipleCandidatesKeepsDefaultThroughExplicitSelection(bool reverse)
    {
        PluginProfileStore store = new(profile, _ => false);
        PluginCandidateObservation a = Candidate("a");
        PluginCandidateObservation b = Candidate("b") with { CandidateId = "other.cs" };
        PluginProfileSnapshot state = await store.ApplyStartupAsync(reverse ? [b, a] : [a, b], 0);
        Assert.False(Assert.Single(state.Plugins).Enabled);
        Assert.Null(state.Plugins[0].Active);
        Assert.Null(state.Plugins[0].SelectedCandidateId);
        Assert.Empty(state.Grants);
        state = await store.SelectCandidateAsync("test.plugin", "local.cs", state.Revision);
        state = await store.ApplyStartupAsync([b, a], state.Revision);
        Assert.False(state.Plugins[0].Enabled);
        Assert.Equal("a", Active(state).ExecutionRevision);
        Assert.Empty(state.Grants);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SavedIdentityChoicesAndGrantsOverrideLaterHostDefaults(bool savedEnabled, bool nextDefault)
    {
        PluginProfileStore original = Store();
        PluginCandidateObservation candidate = Candidate("a");
        PluginProfileSnapshot state = await original.ApplyStartupAsync([candidate], 0);
        state = await original.SetEnabledAsync("test.plugin", savedEnabled, state.Revision);
        state = await original.SetGrantAsync("test.plugin", "dimensions", new("source", "board"), FilterDataAccess.OriginalImage, state.Revision);
        PluginGrantSnapshot grant = Assert.Single(state.Grants);
        PluginProfileStore policyStore = new(profile, _ => nextDefault);
        state = await policyStore.ObserveCandidateAsync(Candidate("b"), state.Revision);
        Assert.Equal(savedEnabled, Assert.Single(state.Plugins).Enabled);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await new PluginProfileStore(profile, _ => nextDefault).ApplyStartupAsync([candidate], state.Revision);
            Assert.Equal(savedEnabled, Assert.Single(state.Plugins).Enabled);
            Assert.Equal(grant, Assert.Single(state.Grants));
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ProvisionalOwnerCannotBypassResolvedDefaultOrLoseDisableChoice(bool resolvedDefault, bool provisionalEnabled, bool viaObservation)
    {
        PluginCandidateObservation unknown = new("local.cs", "unavailable-local", null, null, "Compile error");
        PluginProfileStore original = Store();
        PluginProfileSnapshot state = await original.ApplyStartupAsync([unknown], 0);
        state = await original.SetEnabledAsync(unknown.PluginId, provisionalEnabled, state.Revision);
        PluginProfileStore policyStore = new(profile, pluginId => pluginId != "test.plugin" || resolvedDefault);
        PluginCandidateObservation repaired = Candidate("fixed");
        state = viaObservation
            ? await policyStore.ObserveCandidateAsync(repaired, state.Revision)
            : await policyStore.ApplyStartupAsync([repaired], state.Revision);
        Assert.Equal("test.plugin", Assert.Single(state.Plugins).PluginId);
        Assert.Equal(resolvedDefault && provisionalEnabled, state.Plugins[0].Enabled);
        Assert.Equal(state.Plugins[0].Enabled, Assert.Single((await Store().ReadAsync()).Plugins).Enabled);
        Assert.Empty(state.Grants);
        state = await policyStore.ApplyStartupAsync([repaired], state.Revision);
        Assert.Equal(resolvedDefault && provisionalEnabled, state.Plugins[0].Enabled);
        Assert.Equal("fixed", Active(state).ExecutionRevision);
    }

    [Fact]
    public async Task DisabledDefaultDoesNotCreateABuildForAnUnavailableCandidate()
    {
        PluginProfileStore store = new(profile, _ => false);
        PluginCandidateObservation broken = new("local.cs", "test.plugin", null, null, "Compile error");
        PluginProfileSnapshot state = await store.ApplyStartupAsync([broken], 0);
        Assert.False(Assert.Single(state.Plugins).Enabled);
        Assert.Null(state.Plugins[0].Active);
        Assert.Null(state.Plugins[0].LastGood);
        Assert.Equal("Compile error", state.Plugins[0].Problem);
        state = await store.SetEnabledAsync("test.plugin", true, state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Null(state.Plugins[0].LastGood);
        Assert.Empty(state.Grants);
    }

    [Fact]
    public async Task OptionalDefaultNeverRepairsOrOverwritesCorruptProfile()
    {
        PluginProfileStore store = new(profile, _ => false);
        await store.ApplyStartupAsync([Candidate("a")], 0);
        byte[] corrupt = "{truncated"u8.ToArray();
        await File.WriteAllBytesAsync(store.StorePath, corrupt);
        await Assert.ThrowsAsync<PluginProfileCorruptException>(() => store.ObserveCandidateAsync(Candidate("b"), 1));
        await Assert.ThrowsAsync<PluginProfileCorruptException>(() => store.ApplyStartupAsync([Candidate("b")], 1));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Fact]
    public async Task SettingsAndTestBuildWaitForRestartAndEnableUsesCurrentRelease()
    {
        PluginProfileStore store = Store();
        PluginCandidateObservation first = Candidate("a", NumberSchema("1", 1));
        PluginProfileSnapshot state = await store.ApplyStartupAsync([first], 0);
        PluginConfiguration configuration = Active(state).Configuration with { PluginSettings = Values("1", 7) };
        state = await store.SaveConfigurationAsync("test.plugin", "local.cs", configuration, state.Revision);
        Assert.Equal(1, Number(Active(state)));
        Assert.Equal(7, Number(state.Plugins[0].Pending!));
        PluginLifecycleChange? change = null;
        store.LifecycleChanged += item => change = item;
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        Assert.False(change!.Enabled);
        state = await store.SetEnabledAsync("test.plugin", true, state.Revision);
        Assert.True(change.Enabled);
        Assert.Equal(1, Number(Active(state)));
        state = await store.ObserveCandidateAsync(Candidate("b", NumberSchema("1", 1)), state.Revision);
        Assert.Equal("a", Active(state).ExecutionRevision);
        state = await Store().ApplyStartupAsync([first], state.Revision);
        Assert.Equal(7, Number(Active(state)));
        Assert.Null(state.Plugins[0].Pending);
    }

    [Fact]
    public async Task BrokenUpdateHasNoFallbackAndExplicitRollbackStaysPinnedAcrossRestarts()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a", NumberSchema("1", 2))], 0);
        PluginCandidateObservation broken = new("local.cs", "test.plugin", null, null, "Compiler error CS1002");
        state = await Store().ApplyStartupAsync([broken], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal("a", state.Plugins[0].LastGood!.ExecutionRevision);
        state = await Store().SelectRollbackAsync("test.plugin", state.Revision);
        Assert.Null(state.Plugins[0].Active);
        state = await Store().ApplyStartupAsync([broken], state.Revision);
        Assert.Equal("a", Active(state).ExecutionRevision);
        state = await Store().ApplyStartupAsync([Candidate("b", NumberSchema("2", 9))], state.Revision);
        Assert.Equal("a", Active(state).ExecutionRevision);
        Assert.Equal(2, Number(Active(state)));
        state = await store.SelectCandidateAsync("test.plugin", "local.cs", state.Revision);
        state = await Store().ApplyStartupAsync([Candidate("b", NumberSchema("2", 9))], state.Revision);
        Assert.Equal("b", Active(state).ExecutionRevision);
        Assert.Null(state.Plugins[0].Pinned);
    }

    [Fact]
    public async Task RollbackRestoresBuildAndItsOwnSettingsTogether()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a", NumberSchema("1", 3))], 0);
        SettingsSchema next = new("2", [new("flag", SettingScope.Plugin, SettingKind.Boolean, "Flag", Default: new("flag", SettingKind.Boolean, Boolean: true))]);
        state = await store.ApplyStartupAsync([Candidate("b", next)], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Contains("correction", state.Plugins[0].Problem);
        PluginConfiguration corrected = new("draft", new("2", [new("flag", SettingKind.Boolean, Boolean: false)]), new Dictionary<string, SettingsSnapshot>());
        state = await store.SaveConfigurationAsync("test.plugin", "local.cs", corrected, state.Revision);
        state = await Store().ApplyStartupAsync([Candidate("b", next)], state.Revision);
        Assert.Equal("b", Active(state).ExecutionRevision);
        state = await store.SelectRollbackAsync("test.plugin", state.Revision);
        state = await Store().ApplyStartupAsync([Candidate("b", next)], state.Revision);
        Assert.Equal("a", Active(state).ExecutionRevision);
        Assert.Equal("1", Active(state).Configuration.PluginSettings.SchemaVersion);
        Assert.Equal(3, Number(Active(state)));
    }

    [Fact]
    public async Task AdditiveSchemaKeepsValuesAndAddsDefaultsButNarrowedBoundsDoNotReset()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a", NumberSchema("1", 5))], 0);
        SettingsSchema expanded = new("2", [..NumberSchema("2", 99).Fields,
            new("text", SettingScope.Plugin, SettingKind.Text, "Text", Default: new("text", SettingKind.Text, Text: "new"))]);
        state = await Store().ApplyStartupAsync([Candidate("b", expanded)], state.Revision);
        Assert.Equal(5, Number(Active(state)));
        Assert.Equal("new", Active(state).Configuration.PluginSettings.Values.Single(value => value.Key == "text").Text);
        SettingsSchema narrowed = expanded with { Version = "3", Fields = expanded.Fields.Select(field => field.Key == "count" ? field with { Maximum = 2, Default = null } : field).ToArray() };
        state = await Store().ApplyStartupAsync([Candidate("c", narrowed)], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Contains("outside", state.Plugins[0].Problem);
        Assert.Equal(5, Number(state.Plugins[0].LastGood!));
    }

    [Fact]
    public async Task InvalidDraftDoesNotAdvanceRevisionOrOverwritePending()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a", NumberSchema("1", 1))], 0);
        PluginConfiguration invalid = Active(state).Configuration with { PluginSettings = Values("wrong-schema", 8) };
        await Assert.ThrowsAsync<PluginConfigurationValidationException>(() => store.SaveConfigurationAsync("test.plugin", "local.cs", invalid, state.Revision));
        PluginProfileSnapshot reread = await Store().ReadAsync();
        Assert.Equal(state.Revision, reread.Revision);
        Assert.Null(reread.Plugins[0].Pending);
        Assert.Equal(1, Number(Active(reread)));
    }

    [Fact]
    public async Task ConcurrentIndependentStoresRejectOneStaleSave()
    {
        PluginProfileSnapshot state = await Store().ApplyStartupAsync([Candidate("a", NumberSchema("1", 1))], 0);
        Task<Exception?> Save(long value) => Record.ExceptionAsync(async () =>
            await Store().SaveConfigurationAsync("test.plugin", "local.cs", Active(state).Configuration with { PluginSettings = Values("1", value) }, state.Revision));
        Exception?[] outcomes = await Task.WhenAll(Save(11), Save(22));
        Assert.Single(outcomes, item => item is null);
        Assert.Single(outcomes, item => item is PluginProfileConflictException);
        PluginProfileSnapshot persisted = await Store().ReadAsync();
        Assert.Equal(state.Revision + 1, persisted.Revision);
        Assert.Contains(Number(persisted.Plugins[0].Pending!), new long[] { 11, 22 });
        await Assert.ThrowsAsync<PluginProfileConflictException>(() => Store().SetEnabledAsync("test.plugin", false, state.Revision));
    }

    [Fact]
    public async Task CorruptStoreIsNeverResetByReadOrSaveAndOrphanTemporaryFileIsIgnored()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(store.StorePath)!, ".state-orphan.tmp"), "partial update");
        Assert.Equal(state.Revision, (await Store().ReadAsync()).Revision);
        byte[] corrupt = "{truncated"u8.ToArray();
        await File.WriteAllBytesAsync(store.StorePath, corrupt);
        await Assert.ThrowsAsync<PluginProfileCorruptException>(() => Store().ReadAsync());
        await Assert.ThrowsAsync<PluginProfileCorruptException>(() => Store().ApplyStartupAsync([Candidate("b")], state.Revision));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Fact]
    public async Task DisabledAndMissingPluginsRetainSettingsGrantsAndArtifactReferences()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source", "b"), FilterDataAccess.OriginalImage, state.Revision);
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        state = await Store().ApplyStartupAsync([], state.Revision);
        Assert.False(state.Plugins[0].Enabled);
        Assert.Null(state.Plugins[0].Active);
        Assert.NotNull(state.Plugins[0].LastGood);
        Assert.Single(state.Grants);
        Assert.Contains("a", state.RetainedExecutionRevisions);
        Assert.Contains("code-a", state.RetainedCompilationFingerprints);
        state = await Store().SetEnabledAsync("test.plugin", true, state.Revision);
        Assert.Null(state.Plugins[0].Active);
    }

    [Fact]
    public async Task GrantsAreScopedRevisionedAndRevocationInvalidatesOnlyMatchingTokens()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source", "a"), FilterDataAccess.OriginalImage, state.Revision);
        PluginGrantAuthorizationSnapshot a = PluginGrantAuthorization.Resolve(state, "test.plugin", "dimensions", "source", "a", "rule");
        PluginGrantAuthorizationSnapshot b = PluginGrantAuthorization.Resolve(state, "test.plugin", "dimensions", "source", "b", "rule");
        Assert.True(a.Allows(FilterDataAccess.OriginalImage));
        Assert.False(b.Allows(FilterDataAccess.OriginalImage));
        PluginGrantChange? changed = null;
        store.GrantChanged += item => changed = item;
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source", "a"), FilterDataAccess.None, state.Revision);
        Assert.Equal(2, changed!.Grant.Revision);
        Assert.NotEqual(a.Revision, PluginGrantAuthorization.Resolve(state, "test.plugin", "dimensions", "source", "a", "rule").Revision);
        Assert.Equal(b.Revision, PluginGrantAuthorization.Resolve(state, "test.plugin", "dimensions", "source", "b", "rule").Revision);
        Assert.Equal(FilterDataAccess.None, (await Store().ReadAsync()).Grants[0].Access);
    }

    [Fact]
    public async Task RuleSettingsValidationDoesNotCreateASecondRuleParameterStore()
    {
        PluginProfileStore store = Store();
        SettingsSchema ruleSchema = new("r1", [new("count", SettingScope.FilterRule, SettingKind.Integer, "Width", IsRequired: true)]);
        PluginCandidateObservation candidate = Candidate("a");
        candidate = candidate with { Description = candidate.Description! with { Filters = [candidate.Description.Filters[0] with { Settings = ruleSchema }] } };
        PluginProfileSnapshot state = await store.ApplyStartupAsync([candidate], 0);
        Assert.Empty(PluginSettingsMigration.ValidateFilterRuleSettings(Active(state).Description, "dimensions", Values("r1", 100)));
        Assert.Empty(PluginSettingsMigration.ValidateFilterRuleSettings(Active(state).Description, "dimensions", Values("r1", 200)));
        Assert.NotEmpty(PluginSettingsMigration.ValidateFilterRuleSettings(Active(state).Description, "dimensions", new("r1", [])));
        Assert.Equal(state.Revision, (await store.ReadAsync()).Revision);
    }

    [Fact]
    public async Task DuplicateCandidatesRequireSelectionAndNeverReplaceSelectedFailedUpdate()
    {
        PluginProfileStore store = Store();
        PluginCandidateObservation a = Candidate("a");
        PluginCandidateObservation b = Candidate("b") with { CandidateId = "other.cs" };
        PluginProfileSnapshot state = await store.ApplyStartupAsync([a, b], 0);
        Assert.Null(state.Plugins[0].Active);
        state = await store.SelectCandidateAsync("test.plugin", "local.cs", state.Revision);
        state = await Store().ApplyStartupAsync([a, b], state.Revision);
        Assert.Equal("a", Active(state).ExecutionRevision);
        state = await Store().ApplyStartupAsync([a with { Artifact = null, Description = null, Problem = "broken" }, b], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Equal("local.cs", state.Plugins[0].SelectedCandidateId);
    }

    [Fact]
    public async Task SnapshotMutationCannotChangeDurableOrActiveState()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a", NumberSchema("1", 1))], 0);
        List<SettingValue> values = Assert.IsType<List<SettingValue>>(Active(state).Configuration.PluginSettings.Values);
        values.Clear();
        Assert.Equal(1, Number(Active(await store.ReadAsync())));
    }

    [Fact]
    public async Task ExplicitCandidateChoiceResolvesCrossPluginSourceConflictWithoutFilesystemOrder()
    {
        SourceDescription source = new("shared", "Shared source", new("https://fixture.test"), SourceCapabilities.ThreadRead,
            [new(SourceResourceKind.Thread, "fixture.test", "/{board}/thread/{thread}", IsCanonical: true)],
            [new("fixture.test", HttpHostPurpose.Metadata)], Empty);
        PluginCandidateObservation a = Candidate("a");
        a = a with { Description = a.Description! with { Sources = [source] } };
        PluginCandidateObservation b = Candidate("b") with { CandidateId = "other.cs", PluginId = "other.plugin" };
        b = b with { Description = b.Description! with { PluginId = "other.plugin", Sources = [source] } };
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([a, b], 0);
        Assert.All(state.Plugins, plugin => Assert.Null(plugin.Active));
        state = await store.SelectCandidateAsync("other.plugin", "other.cs", state.Revision);
        state = await Store().ApplyStartupAsync([b, a], state.Revision);
        Assert.Equal("b", state.Plugins.Single(plugin => plugin.PluginId == "other.plugin").Active!.ExecutionRevision);
        Assert.Null(state.Plugins.Single(plugin => plugin.PluginId == "test.plugin").Active);
        state = await store.SelectCandidateAsync("test.plugin", "local.cs", state.Revision);
        state = await Store().ApplyStartupAsync([b, a], state.Revision);
        Assert.Equal("a", state.Plugins.Single(plugin => plugin.PluginId == "test.plugin").Active!.ExecutionRevision);
        Assert.Null(state.Plugins.Single(plugin => plugin.PluginId == "other.plugin").Active);
    }

    [Fact]
    public async Task ChangedPluginIdentityIsAnIsolatedUnavailableUpdateWithoutTransferringGrants()
    {
        PluginCandidateObservation original = Candidate("a");
        PluginCandidateObservation independent = Candidate("independent") with { CandidateId = "independent.cs", PluginId = "independent.plugin" };
        independent = independent with { Description = independent.Description! with { PluginId = independent.PluginId } };
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([original, independent], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source", "board"), FilterDataAccess.OriginalImage, state.Revision);
        PluginCandidateObservation changed = Candidate("changed") with { PluginId = "changed.plugin" };
        changed = changed with { Description = changed.Description! with { PluginId = changed.PluginId } };
        state = await Store().ApplyStartupAsync([changed, independent], state.Revision);
        PluginActivationSnapshot unavailable = state.Plugins.Single(plugin => plugin.PluginId == "test.plugin");
        Assert.Null(unavailable.Active);
        Assert.Equal("a", unavailable.LastGood!.ExecutionRevision);
        Assert.Contains("identity changed", unavailable.Problem);
        Assert.Equal("independent", state.Plugins.Single(plugin => plugin.PluginId == independent.PluginId).Active!.ExecutionRevision);
        Assert.DoesNotContain(state.Plugins, plugin => plugin.PluginId == "changed.plugin");
        Assert.Equal("test.plugin", Assert.Single(state.Grants).PluginId);
        state = await Store().ApplyStartupAsync([changed, independent], state.Revision);
        Assert.Null(state.Plugins.Single(plugin => plugin.PluginId == "test.plugin").Active);
        Assert.NotNull(state.Plugins.Single(plugin => plugin.PluginId == independent.PluginId).Active);
    }

    [Fact]
    public async Task StoreMetadataDoesNotBecomeADropInInstallationCandidate()
    {
        PluginProfileStore store = Store();
        await store.ApplyStartupAsync([Candidate("a")], 0);
        Assert.Equal(Path.Combine(profile, "plugin-state", "state.json"), store.StorePath);
        Assert.False(Directory.Exists(Path.Combine(profile, "plugins")));
        Assert.True(File.Exists(Path.Combine(profile, "plugin-state", "state.lock")));
    }

    [Fact]
    public async Task RepairedUnknownCandidateGetsItsDescribedIdentityAndRetainsDisableChoiceAndLoaderPath()
    {
        string loader = Path.Combine(profile, "plugins", "local.cs");
        PluginProfileStore store = Store();
        PluginCandidateObservation unknown = new("local.cs", "unavailable-local", null, null, "Compile error", loader);
        PluginProfileSnapshot state = await store.ApplyStartupAsync([unknown], 0);
        state = await store.SetEnabledAsync(unknown.PluginId, false, state.Revision);
        PluginCandidateObservation repaired = Candidate("fixed") with { CandidatePath = loader };
        state = await store.ObserveCandidateAsync(repaired, state.Revision);
        Assert.Equal("test.plugin", Assert.Single(state.Plugins).PluginId);
        Assert.False(state.Plugins[0].Enabled);
        state = await Store().ApplyStartupAsync([repaired], state.Revision);
        Assert.Equal("fixed", state.Plugins[0].Active!.ExecutionRevision);
        state = await Store().ApplyStartupAsync([], state.Revision);
        Assert.Equal(loader, Assert.Single(state.Plugins[0].Candidates).CandidatePath);
        Assert.False(state.Plugins[0].Enabled);
    }

    [Fact]
    public async Task StartupCanReclassifyAnUnknownCandidateWithoutASeparateTestObservation()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([new("local.cs", "unavailable-local", null, null, "Compile error")], 0);
        state = await Store().ApplyStartupAsync([Candidate("fixed")], state.Revision);
        Assert.Equal("test.plugin", Assert.Single(state.Plugins).PluginId);
        Assert.Equal("fixed", Active(state).ExecutionRevision);
    }

    [Fact]
    public async Task OpenReaderRetainsOldRevisionWhileNewRevisionsArePublished()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        byte[] original = await File.ReadAllBytesAsync(store.StorePath);
        await using FileStream reader = new(store.StorePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        for (int i = 0; i < 20; i++)
        {
            state = await store.SetEnabledAsync("test.plugin", i % 2 == 0, state.Revision);
            PluginProfileSnapshot published = await Store().ReadAsync();
            Assert.Equal(i + 2, published.Revision);
            Assert.Equal(i % 2 == 0, Assert.Single(published.Plugins).Enabled);
        }
        byte[] retained = new byte[checked((int)reader.Length)];
        await reader.ReadExactlyAsync(retained);
        Assert.Equal(original, retained);
        Assert.Equal(21, (await Store().ReadAsync()).Revision);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.StorePath)!, ".state-*.tmp"));
    }

    [Fact]
    public async Task WindowsReaderWithoutDeleteSharingRejectsWriteWithoutChangingRevision()
    {
        if (!OperatingSystem.IsWindows()) return;
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        byte[] original = await File.ReadAllBytesAsync(store.StorePath);
        await using (FileStream reader = new(store.StorePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Exception? error = await Record.ExceptionAsync(() => store.SetEnabledAsync("test.plugin", false, state.Revision));
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString() ?? "Write unexpectedly succeeded.");
            Assert.Equal(original, await File.ReadAllBytesAsync(store.StorePath));
        }
        Assert.Equal(state.Revision, (await Store().ReadAsync()).Revision);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.StorePath)!, ".state-*.tmp"));
    }

    [Fact]
    public async Task ReadWaitsForWriterLeaseRatherThanObservingItsTemporaryMissingName()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        string directory = Path.GetDirectoryName(store.StorePath)!;
        string hidden = Path.Combine(directory, ".state-during-replacement.tmp");
        Task<PluginProfileSnapshot> pending;
        await using (FileStream writer = new(Path.Combine(directory, "state.lock"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            File.Move(store.StorePath, hidden);
            try
            {
                pending = Store().ReadAsync();
                Assert.False(pending.IsCompleted);
            }
            finally { File.Move(hidden, store.StorePath); }
        }
        PluginProfileSnapshot observed = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(state.Revision, observed.Revision);
        Assert.Equal("a", Assert.Single(observed.Plugins).Active!.ExecutionRevision);
    }

    [Fact]
    public async Task ReadWaitingForWriterLeaseCanBeCancelledWithoutChangingProfile()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        byte[] original = await File.ReadAllBytesAsync(store.StorePath);
        using CancellationTokenSource cancellation = new();
        await using (FileStream writer = new(Path.Combine(Path.GetDirectoryName(store.StorePath)!, "state.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Task<PluginProfileSnapshot> pending = Store().ReadAsync(cancellation.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(original, await File.ReadAllBytesAsync(store.StorePath));
        }
        Assert.Equal(state.Revision, (await Store().ReadAsync()).Revision);
        PluginProfileSnapshot updated = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        Assert.Equal(state.Revision + 1, updated.Revision);
    }

    [Fact]
    public async Task WindowsReadOnlyCoordinationFileStillAllowsProfileReads()
    {
        if (!OperatingSystem.IsWindows()) return;
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        string lockPath = Path.Combine(Path.GetDirectoryName(store.StorePath)!, "state.lock");
        FileAttributes original = File.GetAttributes(lockPath);
        File.SetAttributes(lockPath, original | FileAttributes.ReadOnly);
        try
        {
            PluginProfileSnapshot observed = await Store().ReadAsync();
            Assert.Equal(state.Revision, observed.Revision);
            Assert.Equal("a", Assert.Single(observed.Plugins).Active!.ExecutionRevision);
        }
        finally { File.SetAttributes(lockPath, original); }
    }

    [Fact]
    public async Task FirstReadCreatesOnlyCoordinationFilesAndKeepsTheProfileEmpty()
    {
        PluginProfileStore store = Store();
        Assert.False(Directory.Exists(profile));
        PluginProfileSnapshot state = await store.ReadAsync();
        Assert.Equal(0, state.Revision);
        Assert.Empty(state.Plugins);
        Assert.Empty(state.Grants);
        Assert.False(File.Exists(store.StorePath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(store.StorePath)!, "state.lock")));
        state = await store.ApplyStartupAsync([Candidate("a")], state.Revision);
        Assert.Equal(1, state.Revision);
        Assert.Single(state.Plugins);
    }

    [Fact]
    public async Task ConcurrentReadersAlwaysObserveACompleteAtomicRevision()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a")], 0);
        Task reader = Task.Run(async () =>
        {
            for (int i = 0; i < 100; i++)
            {
                PluginProfileSnapshot observed = await Store().ReadAsync();
                Assert.Single(observed.Plugins);
                Assert.Equal("a", Active(observed).ExecutionRevision);
            }
        });
        for (int i = 0; i < 20; i++) state = await store.SetEnabledAsync("test.plugin", i % 2 == 0, state.Revision);
        await reader;
        Assert.Equal(21, (await Store().ReadAsync()).Revision);
    }

    [Fact]
    public async Task AllRequiredFieldKindsPersistAndMissingRequiredValueDoesNotReset()
    {
        SettingsSchema schema = new("all", [
            new("text", SettingScope.Plugin, SettingKind.Text, "Text", IsRequired: true),
            new("int", SettingScope.Plugin, SettingKind.Integer, "Integer", IsRequired: true, Minimum: 1, Maximum: 10),
            new("bool", SettingScope.Plugin, SettingKind.Boolean, "Boolean", IsRequired: true),
            new("enum", SettingScope.Plugin, SettingKind.Enum, "Enum", IsRequired: true, Options: [new("one", "One")]),
            new("uri", SettingScope.Plugin, SettingKind.Uri, "URI", IsRequired: true)]);
        PluginProfileStore store = Store();
        PluginCandidateObservation candidate = Candidate("a", schema);
        PluginProfileSnapshot state = await store.ApplyStartupAsync([candidate], 0);
        Assert.Null(state.Plugins[0].Active);
        Assert.Contains("Required setting", state.Plugins[0].Problem);
        PluginConfiguration valid = new("draft", new("all", [new("text", SettingKind.Text, Text: "saved"),
            new("int", SettingKind.Integer, Integer: 3), new("bool", SettingKind.Boolean, Boolean: false),
            new("enum", SettingKind.Enum, Text: "one"), new("uri", SettingKind.Uri, Text: "https://fixture.test/path")]),
            new Dictionary<string, SettingsSnapshot>());
        state = await store.SaveConfigurationAsync("test.plugin", "local.cs", valid, state.Revision);
        state = await Store().ApplyStartupAsync([candidate], state.Revision);
        Assert.Equal(5, Active(state).Configuration.PluginSettings.Values.Count);
        PluginConfiguration missing = valid with { PluginSettings = valid.PluginSettings with { Values = valid.PluginSettings.Values.Where(value => value.Key != "bool").ToArray() } };
        await Assert.ThrowsAsync<PluginConfigurationValidationException>(() => store.SaveConfigurationAsync("test.plugin", "local.cs", missing, state.Revision));
        Assert.Equal(5, Active(await Store().ReadAsync()).Configuration.PluginSettings.Values.Count);
    }

    [Fact]
    public async Task PinnedSettingsRemainEditableWhenCurrentCandidateIsBroken()
    {
        PluginProfileStore store = Store();
        PluginProfileSnapshot state = await store.ApplyStartupAsync([Candidate("a", NumberSchema("1", 1))], 0);
        PluginCandidateObservation broken = new("local.cs", "test.plugin", null, null, "broken");
        state = await store.ApplyStartupAsync([broken], state.Revision);
        state = await store.SelectRollbackAsync("test.plugin", state.Revision);
        PluginConfiguration settings = state.Plugins[0].Pinned!.Configuration with { PluginSettings = Values("1", 4) };
        state = await store.SaveConfigurationAsync("test.plugin", "local.cs", settings, state.Revision);
        state = await Store().ApplyStartupAsync([broken], state.Revision);
        Assert.Equal("a", Active(state).ExecutionRevision);
        Assert.Equal(4, Number(Active(state)));
        state = await Store().ApplyStartupAsync([broken], state.Revision);
        Assert.Equal(4, Number(Active(state)));
    }

    private static PluginReleaseSnapshot Active(PluginProfileSnapshot state) => Assert.IsType<PluginReleaseSnapshot>(state.Plugins[0].Active);
    private static long Number(PluginReleaseSnapshot release) => release.Configuration.PluginSettings.Values.Single(value => value.Key == "count").Integer!.Value;
    private static SettingsSnapshot Values(string version, long value) => new(version, [new("count", SettingKind.Integer, Integer: value)]);
    private static SettingsSchema NumberSchema(string version, long defaultValue) => new(version,
        [new("count", SettingScope.Plugin, SettingKind.Integer, "Count", Default: new("count", SettingKind.Integer, Integer: defaultValue))]);
    private static PluginCandidateObservation Candidate(string build, SettingsSchema? schema = null)
    {
        PluginDescription description = new("test.plugin", "Synthetic plugin", "1.0", SdkContract.Version, "Test filter", [],
            [new("dimensions", "Dimensions", "Image size filter", FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, Empty, ["image/png"])],
            schema ?? Empty, []);
        PluginArtifact artifact = new("code-" + build, build, "/fixture/" + build + ".dll", new string('a', 64), "sdk", "compiler", []);
        return new("local.cs", "test.plugin", artifact, description);
    }
}
