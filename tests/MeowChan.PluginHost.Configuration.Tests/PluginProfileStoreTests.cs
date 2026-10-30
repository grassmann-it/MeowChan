using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
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

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task InPlaceInstallUpdateRetainsExplicitIdentitySettingsEnablePinAndGrants(bool enabled, bool pinned)
    {
        // Version labels are synthetic package fixtures, not executions of the historical releases.
        string install = Path.Combine(profile, "application", "plugins");
        Directory.CreateDirectory(install);
        string path = Path.Combine(install, "source.cs");
        await File.WriteAllTextAsync(path, "// fixture package 1.0.0");
        PluginInstallationInventory inventory = new();
        var original = Assert.Single((await inventory.ScanAsync(install)).Candidates);
        PluginCandidateObservation v100 = Candidate("v100", NumberSchema("1", 1)) with
        { CandidateId = original.CandidateId, CandidatePath = original.Path };
        PluginProfileStore store = Store();
        var state = await store.ApplyStartupAsync([v100], 0);
        state = pinned
            ? await store.SelectRollbackAsync("test.plugin", state.Revision)
            : await store.SelectCandidateAsync("test.plugin", original.CandidateId, state.Revision);
        // Save on the explicitly chosen release. Rollback deliberately restores its own
        // retained settings rather than borrowing those from a different active snapshot.
        var configured = state.Plugins[0].Pending!.Configuration with { PluginSettings = Values("1", 7) };
        state = await store.SaveConfigurationAsync("test.plugin", original.CandidateId, configured, state.Revision);
        state = await store.ApplyStartupAsync([v100], state.Revision);
        state = await store.SetEnabledAsync("test.plugin", enabled, state.Revision);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source", "board"), FilterDataAccess.OriginalImage, state.Revision);
        var grant = Assert.Single(state.Grants);

        await File.WriteAllTextAsync(path, "// fixture package 1.0.1");
        var updated = Assert.Single((await inventory.ScanAsync(install)).Candidates);
        Assert.Equal(original.CandidateId, updated.CandidateId);
        PluginCandidateObservation v101 = Candidate("v101", NumberSchema("1", 99)) with
        { CandidateId = updated.CandidateId, CandidatePath = updated.Path };
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyStartupAsync([v101], state.Revision);
            var plugin = Assert.Single(state.Plugins);
            Assert.Equal(enabled, plugin.Enabled);
            Assert.Equal(original.CandidateId, plugin.SelectedCandidateId);
            Assert.Equal(pinned ? "v100" : "v101", plugin.Active!.ExecutionRevision);
            Assert.Equal(pinned ? "v100" : null, plugin.Pinned?.ExecutionRevision);
            Assert.Equal(7, Number(plugin.Active));
            Assert.Equal(grant, Assert.Single(state.Grants));
            Assert.Equal(path, Assert.Single(plugin.Candidates).CandidatePath);
            Assert.Null(plugin.Problem);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedInstallPathDoesNotSilentlyReplaceSelectedCandidateOrExplicitPin(bool pinned)
    {
        PluginInstallationInventory inventory = new();
        async Task<PluginCandidateObservation> Package(string version)
        {
            string directory = Path.Combine(profile, version, "plugins");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "source.cs"), "// synthetic " + version);
            var entry = Assert.Single((await inventory.ScanAsync(directory)).Candidates);
            return Candidate(version, NumberSchema("1", 2)) with { CandidateId = entry.CandidateId, CandidatePath = entry.Path };
        }
        var original = await Package("1.0.0");
        var moved = await Package("1.0.1");
        Assert.NotEqual(original.CandidateId, moved.CandidateId);
        var store = Store();
        var state = await store.ApplyStartupAsync([original], 0);
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source", "board"), FilterDataAccess.CandidateMetadata, state.Revision);
        if (pinned) state = await store.SelectRollbackAsync("test.plugin", state.Revision);
        var grant = Assert.Single(state.Grants);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyStartupAsync([moved], state.Revision);
            var plugin = Assert.Single(state.Plugins);
            Assert.False(plugin.Enabled);
            Assert.Equal(original.CandidateId, plugin.SelectedCandidateId);
            Assert.Equal(original.CandidatePath, plugin.Candidates.Single(item => item.CandidateId == original.CandidateId).CandidatePath);
            Assert.False(plugin.Candidates.Single(item => item.CandidateId == original.CandidateId).IsPresent);
            Assert.True(plugin.Candidates.Single(item => item.CandidateId == moved.CandidateId).IsPresent);
            Assert.Equal("1.0.0", plugin.LastGood!.ExecutionRevision);
            Assert.Equal(grant, Assert.Single(state.Grants));
            if (pinned)
            {
                Assert.Equal("1.0.0", plugin.Active!.ExecutionRevision);
                Assert.Equal("1.0.0", plugin.Pinned!.ExecutionRevision);
            }
            else
            {
                Assert.Null(plugin.Active);
                Assert.Null(plugin.Pinned);
                Assert.Contains("missing", plugin.Problem);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundledRepairRequiresExplicitConfirmationAndRetainsSelectionConfigurationAndHistory(bool changedBuild)
    {
        var old = await Delivered("old-install", "one");
        var moved = await Delivered("new-install", changedBuild ? "two" : "one");
        Assert.NotEqual(old.Candidate.CandidateId, moved.Candidate.CandidateId);
        Assert.Equal(old.Proof.SlotId, moved.Proof.SlotId);
        PluginProfileStore store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        var configured = Active(state).Configuration with { PluginSettings = Values("1", 7) };
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId, configured, state.Revision);
        state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        PluginReleaseSnapshot retained = Active(state);
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        var preview = await store.PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        Assert.True(row.CanConfirm); Assert.Equal(changedBuild, row.BuildChanged);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath)); // preview/dismiss is not a write
        state = await store.ConfirmBundledRepairAsync(row, preview.ProfileRevision);
        Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
        Assert.False(state.Plugins[0].Enabled); Assert.Null(state.Plugins[0].Pinned);
        Assert.Null(state.Plugins[0].Active); // confirmation is not runtime activation
        Assert.Equal(7, Number(state.Plugins[0].Pending!));
        Assert.Equal(SdkJson.Serialize(retained), SdkJson.Serialize(state.Plugins[0].LastGood!));
        Assert.Contains(retained.ExecutionRevision, state.RetainedExecutionRevisions);
        Assert.Empty(state.Grants); Assert.Empty(state.SelectedSourceOwners);
        byte[] committed = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<PluginProfileConflictException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(committed, await File.ReadAllBytesAsync(store.StorePath));
        using (JsonDocument envelope = JsonDocument.Parse(committed)) Assert.Equal(2, envelope.RootElement.GetProperty("version").GetInt32());
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
            Assert.Equal(moved.Candidate.CandidateId, Active(state).CandidateId);
            Assert.Equal(moved.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(7, Number(Active(state))); Assert.False(state.Plugins[0].Enabled);
            Assert.Equal(retained.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
        }
        Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
    }

    [Fact]
    public async Task RepairPreservesASavedSettingsEditThatWasStillPendingAtTheMove()
    {
        var old = await Delivered("old", "one"); var moved = await Delivered("new", "two");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 9) }, state.Revision);
        byte[] latest = SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        Assert.Equal(latest, SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
        Assert.Equal(1, Number(state.Plugins[0].LastGood!));
    }

    [Fact]
    public async Task LegacyHistoryIsNeverAttributedByMatchingPluginIdOrFilename()
    {
        var old = await Delivered("legacy", "one"); var moved = await Delivered("new", "two");
        var store = Store();
        var state = await store.ApplyStartupAsync([old.Candidate], 0); // legacy v1 has no delivered proof
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var row = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        Assert.Equal("unproven-history", row.BlockedReason); Assert.Null(row.NewCandidateId);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row, state.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SelectCandidateAsync("test.plugin", moved.Candidate.CandidateId, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId); Assert.Null(state.Plugins[0].Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BundledGrantInheritanceIsBlockedAcrossRelocationAndInPlaceUpdate(bool samePath)
    {
        var old = await Delivered("install", "one");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source", "board"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        var moved = await Delivered(samePath ? "install" : "moved", "two");
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        var row = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        Assert.Equal("inherited-grants", row.BlockedReason);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row, state.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfigurationAsync("test.plugin", moved.Candidate.CandidateId,
            state.Plugins[0].LastGood!.Configuration, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        Assert.Equal(grants, SdkJson.Serialize((await store.ReadAsync()).Grants));
    }

    [Fact]
    public async Task IdenticalExecutionRelocationRetainsExistingGrantWithoutBroadeningIt()
    {
        var old = await Delivered("old", "one"); var moved = await Delivered("new", "one");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source", "board"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync(); var row = Assert.Single(preview.Rows);
        Assert.True(row.CanConfirm); Assert.False(row.BuildChanged);
        state = await store.ConfirmBundledRepairAsync(row, preview.ProfileRevision);
        state = await Store().ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        Assert.NotNull(state.Plugins[0].Active); Assert.Equal(grants, SdkJson.Serialize(state.Grants));
    }

    [Fact]
    public async Task PinsRemainBlockedWithoutMutation()
    {
        var old = await Delivered("old", "one"); var moved = await Delivered("new", "two");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SelectRollbackAsync("test.plugin", state.Revision);
        string pin = state.Plugins[0].Pinned!.ExecutionRevision;
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var pinnedRow = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        Assert.Equal("pinned", pinnedRow.BlockedReason);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(pinnedRow, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        Assert.Equal(pin, state.Plugins[0].Pinned!.ExecutionRevision);
    }

    [Fact]
    public async Task LocalCandidateAndCaseEquivalentDeliveredSlotsCannotWinByEnumerationOrder()
    {
        var old = await Delivered("old", "one"); var moved = await Delivered("new", "two");
        var local = moved.Candidate with { CandidateId = "local-collision", CandidatePath = Path.Combine(profile, "plugins", "fixture.plugin.json") };
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.ApplyBundledStartupAsync([local, moved.Candidate], [moved.Proof], [], state.Revision);
        Assert.Equal("ambiguous", Assert.Single((await store.PreviewBundledRepairsAsync()).Rows).BlockedReason);
        var equivalent = await Delivered("another-root", "two", "FIXTURE.plugin.json");
        Assert.Equal(moved.Proof.SlotId, equivalent.Proof.SlotId);
        state = await store.ApplyBundledStartupAsync([equivalent.Candidate, moved.Candidate], [equivalent.Proof, moved.Proof], [], state.Revision);
        Assert.Equal("ambiguous", Assert.Single((await store.PreviewBundledRepairsAsync()).Rows).BlockedReason);
        Assert.Null(state.Plugins[0].Active);
    }

    [Fact]
    public async Task StaleTamperedAndCancelledConfirmationDoNotWrite()
    {
        var old = await Delivered("old", "one"); var moved = await Delivered("new", "two");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync(); var row = Assert.Single(preview.Rows);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row with { NewExecutionRevision = old.Candidate.Artifact!.ExecutionRevision }, preview.ProfileRevision));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision, cancellation.Token));
        await File.WriteAllTextAsync(moved.Candidate.Artifact!.AssemblyPath, "tampered cache");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        state = await store.SetEnabledAsync("test.plugin", false, state.Revision);
        before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<PluginProfileConflictException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Fact]
    public async Task GrantAddedAfterConfirmationStillPreventsChangedExecutionAtRestart()
    {
        var old = await Delivered("old", "one"); var moved = await Delivered("new", "two");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        state = await Store().ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active); Assert.Contains("grants", state.Plugins[0].Problem);
        Assert.Equal("inherited-grants", Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows).BlockedReason);
    }

    [Fact]
    public async Task FurtherBuildChangeInvalidatesExactConfirmedTarget()
    {
        var old = await Delivered("old", "one"); var moved = await Delivered("new", "two");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        var changed = await Delivered("new", "three");
        state = await Store().ApplyBundledStartupAsync([changed.Candidate], [changed.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        Assert.Contains("changed", state.Plugins[0].Problem);
        Assert.Equal(changed.Candidate.Artifact!.ExecutionRevision, Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows).NewExecutionRevision);
    }

    [Fact]
    public async Task ProvenanceCannotBeCopiedOntoAnotherCandidateOrPluginIdentity()
    {
        var old = await Delivered("old", "one"); var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyBundledStartupAsync([old.Candidate],
            [old.Proof with { PluginId = "forged.plugin" }], [], state.Revision));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ApplyBundledStartupAsync([old.Candidate],
            [old.Proof with { CandidateId = "forged-candidate" }], [], state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Fact]
    public async Task ChangedSourceWithNewlyGrantedHostPermissionIsBlockedAtConfirmation()
    {
        var old = await Delivered("old", "one", source: true); var moved = await Delivered("new", "two", source: true);
        string[] restrictions = [];
        PluginProfileStore store = new(profile, _ => true, () => restrictions);
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync(); var row = Assert.Single(preview.Rows);
        Assert.True(row.CanConfirm);
        restrictions = ["shared"];
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        Assert.Equal("inherited-grants", Assert.Single((await store.PreviewBundledRepairsAsync()).Rows).BlockedReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Fact]
    public async Task SourceOwnershipSurvivesRepairAndCompetingOwnerCannotBeOverridden()
    {
        var old = await Delivered("old", "one", source: true); var moved = await Delivered("new", "two", source: true);
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SelectCandidateAsync("test.plugin", old.Candidate.CandidateId, state.Revision);
        state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        byte[] owners = SdkJson.Serialize(state.SelectedSourceOwners);
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
        state = await store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
        var next = await Delivered("next", "three", source: true);
        var other = next.Candidate with { CandidateId = "local-other", PluginId = "other.plugin",
            Description = next.Candidate.Description! with { PluginId = "other.plugin" } };
        state = await store.ApplyBundledStartupAsync([next.Candidate, other], [next.Proof], [], state.Revision);
        Assert.Equal("source-owner-conflict", Assert.Single((await store.PreviewBundledRepairsAsync()).Rows,
            row => row.PluginId == "test.plugin").BlockedReason);
        Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
    }

    [Fact]
    public async Task ChangedDescriptionAndChangedManifestCannotBeConfirmed()
    {
        var old = await Delivered("old", "one"); var moved = await Delivered("new", "two");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        var changed = moved.Candidate with { Description = moved.Candidate.Description! with { Name = "Different contract" } };
        var proof = moved.Proof with { DescriptionSha256 = Convert.ToHexStringLower(SHA256.HashData(SdkJson.Serialize(changed.Description))) };
        state = await store.ApplyBundledStartupAsync([changed], [proof], [], state.Revision);
        Assert.Equal("description-changed", Assert.Single((await store.PreviewBundledRepairsAsync()).Rows).BlockedReason);
        // A different install path lets us retain immutable provenance for the changed description.
        var intact = await Delivered("intact", "two");
        state = await store.ApplyBundledStartupAsync([intact.Candidate], [intact.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync(); var row = Assert.Single(preview.Rows); Assert.True(row.CanConfirm);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await File.AppendAllTextAsync(intact.Candidate.CandidatePath!, " "); // valid JSON, different manifest digest
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Fact]
    public async Task UnknownStoreVersionCannotBeReadOrOverwrittenByRepair()
    {
        var old = await Delivered("old", "one"); var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        string json = await File.ReadAllTextAsync(store.StorePath);
        Assert.Contains("\"version\":2", json);
        string future = json.Replace("\"version\":2", "\"version\":999", StringComparison.Ordinal);
        await File.WriteAllTextAsync(store.StorePath, future);
        await Assert.ThrowsAsync<PluginProfileCorruptException>(() => store.PreviewBundledRepairsAsync());
        await Assert.ThrowsAsync<PluginProfileCorruptException>(() => store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision));
        Assert.Equal(future, await File.ReadAllTextAsync(store.StorePath));
    }

    [Fact]
    public async Task DeliveredEvidenceRequiresAnExactShippedPathNotAnotherDirectory()
    {
        var old = await Delivered("old", "one");
        var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(Path.GetDirectoryName(old.Candidate.CandidatePath)!)).Candidates,
            c => c.Kind == PluginCandidateKind.PrebuiltManifest);
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginInstallationInventory.RecordDeliveredAsync(Path.Combine(profile, "other"),
            installed, old.Candidate.Artifact!, old.Candidate.Description!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOnlyRequiredOldConfigurationCannotActivateOrOrdinarilySelectChangedSamePathBuild(bool grant)
    {
        var f = await PendingOnlyRequired();
        var state = f.State;
        if (grant) state = await f.Store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] config = SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        byte[] grants = SdkJson.Serialize(state.Grants);
        byte[] owners = SdkJson.Serialize(state.SelectedSourceOwners);
        var changed = await Delivered("pending-old", "two", requiredSetting: true);
        Assert.Equal(f.Candidate.CandidateId, changed.Candidate.CandidateId);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([changed.Candidate], [changed.Proof], [], state.Revision);
            Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
            Assert.Equal(f.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
            Assert.Equal(config, SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
            Assert.Equal(grants, SdkJson.Serialize(state.Grants)); Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Equal(f.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId); Assert.Null(state.Plugins[0].Pinned);
            var preview = await Store().PreviewBundledRepairsAsync(); var row = Assert.Single(preview.Rows);
            Assert.True(row.BuildChanged); Assert.Equal(f.Candidate.Artifact.ExecutionRevision, row.OldExecutionRevision);
            Assert.Equal(grant ? "inherited-grants" : null, row.BlockedReason);
            Assert.Equal(!grant, row.CanConfirm);
            byte[] before = await File.ReadAllBytesAsync(f.Store.StorePath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SelectCandidateAsync("test.plugin", changed.Candidate.CandidateId, state.Revision));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SaveConfigurationAsync("test.plugin", changed.Candidate.CandidateId,
                state.Plugins[0].Pending!.Configuration, state.Revision));
            if (grant) await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ConfirmBundledRepairAsync(row, preview.ProfileRevision));
            Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.StorePath));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PendingOnlyConfirmedReplacementRetainsOldBasisAndBlocksLateRightsAtRestart(bool sourceRestriction, bool samePath)
    {
        var f = await PendingOnlyRequired(source: sourceRestriction);
        var moved = await Delivered(samePath ? "pending-old" : "pending-new", "two", source: sourceRestriction, requiredSetting: true);
        var state = await f.Store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], f.State.Revision);
        byte[] oldConfig = SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        byte[] owners = SdkJson.Serialize(state.SelectedSourceOwners);
        var preview = await f.Store.PreviewBundledRepairsAsync(); var row = Assert.Single(preview.Rows);
        Assert.True(row.CanConfirm); Assert.True(row.BuildChanged);
        state = await f.Store.ConfirmBundledRepairAsync(row, preview.ProfileRevision);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
        Assert.Equal(moved.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
        Assert.Equal(oldConfig, SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
        if (!sourceRestriction) state = await f.Store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], sourceRestriction ? ["shared"] : [], state.Revision);
            Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
            Assert.Contains("grants", state.Plugins[0].Problem);
            Assert.Equal(f.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(oldConfig, SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
            Assert.Equal(grants, SdkJson.Serialize(state.Grants)); Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            var blocked = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
            Assert.True(blocked.BuildChanged); Assert.Equal("inherited-grants", blocked.BlockedReason);
            Assert.Equal(f.Candidate.Artifact!.ExecutionRevision, blocked.OldExecutionRevision);
            byte[] before = await File.ReadAllBytesAsync(f.Store.StorePath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ConfirmBundledRepairAsync(blocked, state.Revision));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SelectCandidateAsync("test.plugin", moved.Candidate.CandidateId, state.Revision));
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SaveConfigurationAsync("test.plugin", moved.Candidate.CandidateId,
                state.Plugins[0].Pending!.Configuration, state.Revision));
            Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.StorePath));
        }
    }

    [Fact]
    public async Task PendingOnlyOldBasisDoesNotTrustAnUnprovenSamePathObservation()
    {
        var f = await PendingOnlyRequired();
        var changed = await Delivered("pending-old", "two", requiredSetting: true);
        var state = await f.Store.ObserveCandidateAsync(changed.Candidate, f.State.Revision); // not a delivered startup proof
        byte[] before = await File.ReadAllBytesAsync(f.Store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SelectCandidateAsync("test.plugin", changed.Candidate.CandidateId, state.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.SaveConfigurationAsync("test.plugin", changed.Candidate.CandidateId,
            state.Plugins[0].Pending!.Configuration, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.StorePath));
        state = await f.Store.ApplyStartupAsync([changed.Candidate], state.Revision);
        Assert.Null(state.Plugins[0].Active); Assert.Contains("provenance", state.Plugins[0].Problem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingOnlySameExecutionRelocationAndSettingsEditsKeepValuesAndRights(bool move)
    {
        var f = await PendingOnlyRequired();
        var state = await f.Store.SaveConfigurationAsync("test.plugin", f.Candidate.CandidateId,
            f.State.Plugins[0].Pending!.Configuration with { PluginSettings = Values("1", 19) }, f.State.Revision);
        state = await f.Store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        var target = move ? await Delivered("pending-new", "one", requiredSetting: true) : (f.Candidate, f.Proof);
        if (move)
        {
            state = await f.Store.ApplyBundledStartupAsync([target.Item1], [target.Item2], [], state.Revision);
            var preview = await f.Store.PreviewBundledRepairsAsync(); var row = Assert.Single(preview.Rows);
            Assert.True(row.CanConfirm); Assert.False(row.BuildChanged);
            state = await f.Store.ConfirmBundledRepairAsync(row, preview.ProfileRevision);
        }
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([target.Item1], [target.Item2], [], state.Revision);
            Assert.Equal(target.Item1.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(19, Number(Active(state))); Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(f.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
        }
    }

    [Fact]
    public async Task PendingOnlyConfirmedChangedReplacementWithoutRightsActivatesOnlyTheConfirmedBuild()
    {
        var f = await PendingOnlyRequired();
        var moved = await Delivered("pending-new", "two", requiredSetting: true);
        var state = await f.Store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], f.State.Revision);
        var preview = await f.Store.PreviewBundledRepairsAsync();
        state = await f.Store.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
            Assert.Equal(moved.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(7, Number(Active(state))); Assert.Equal(f.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Empty(state.Grants);
        }
    }

    [Fact]
    public async Task PendingOnlyLegacyBindingWithNoRetainedOriginalFailsClosed()
    {
        var f = await PendingOnlyRequired();
        var moved = await Delivered("pending-new", "two", requiredSetting: true);
        var state = await f.Store.ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], f.State.Revision);
        var preview = await f.Store.PreviewBundledRepairsAsync();
        state = await f.Store.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        // Simulate an earlier v2 binding: only OLD's execution ID survived when Pending was replaced.
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(f.Store.StorePath))!;
        var payload = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(envelope["payload"]!.GetValue<string>()))!;
        payload["bindings"]![0]!.AsObject().Remove("originalRelease");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        envelope["payload"] = Convert.ToBase64String(bytes);
        envelope["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
        await File.WriteAllTextAsync(f.Store.StorePath, envelope.ToJsonString());
        var blocked = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
        Assert.False(blocked.CanConfirm); Assert.Equal("unproven-history", blocked.BlockedReason);
        state = await Store().ApplyBundledStartupAsync([moved.Candidate], [moved.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
        Assert.Contains("changed or disappeared", state.Plugins[0].Problem);
        Assert.Equal(7, Number(state.Plugins[0].Pending!));
    }

    [Fact]
    public async Task RightsBeforeAnyConfigurationCannotBeLaunderedByRepeatedSamePathInventoryRefresh()
    {
        var old = await Delivered("pending-old", "one", requiredSetting: true);
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        var changed = await Delivered("pending-old", "two", requiredSetting: true);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([changed.Candidate], [changed.Proof], [], state.Revision);
            Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].Pending);
            Assert.Contains("pre-replacement basis", state.Plugins[0].Problem);
            byte[] before = await File.ReadAllBytesAsync(store.StorePath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfigurationAsync("test.plugin", changed.Candidate.CandidateId,
                new("attempt", Values("1", 7), new Dictionary<string, SettingsSnapshot>()), state.Revision));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        }
    }

    [Fact]
    public async Task LaterMoveCannotForgetActivatedTargetAndCompareAgainstOlderPreConfirmationBuild()
    {
        var old = await Delivered("first", "one"); var changed = await Delivered("second", "two");
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.ApplyBundledStartupAsync([changed.Candidate], [changed.Proof], [], state.Revision);
        var preview = await store.PreviewBundledRepairsAsync();
        state = await store.ConfirmBundledRepairAsync(Assert.Single(preview.Rows), preview.ProfileRevision);
        state = await store.ApplyBundledStartupAsync([changed.Candidate], [changed.Proof], [], state.Revision);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        var olderBuildAtNewPath = await Delivered("third", "one");
        state = await store.ApplyBundledStartupAsync([olderBuildAtNewPath.Candidate], [olderBuildAtNewPath.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active);
        var blocked = Assert.Single((await store.PreviewBundledRepairsAsync()).Rows);
        Assert.Equal(changed.Candidate.Artifact!.ExecutionRevision, blocked.OldExecutionRevision);
        Assert.True(blocked.BuildChanged); Assert.Equal("inherited-grants", blocked.BlockedReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnprovenRefreshBeforeFirstConfigurationCannotManufacturePendingReplacement(bool grant)
    {
        var old = await Delivered("unconfigured", "one", requiredSetting: true);
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood); Assert.Null(state.Plugins[0].Pending);
        if (grant) state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        byte[] owners = SdkJson.Serialize(state.SelectedSourceOwners);
        var changed = await Delivered("unconfigured", "two", requiredSetting: true);
        Assert.Equal(old.Candidate.CandidateId, changed.Candidate.CandidateId);
        state = await store.ObserveCandidateAsync(changed.Candidate, state.Revision); // no NEW delivered proof
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        var proposed = new PluginConfiguration("attempt", Values("1", 7), new Dictionary<string, SettingsSnapshot>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfigurationAsync("test.plugin", changed.Candidate.CandidateId, proposed, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SelectCandidateAsync("test.plugin", changed.Candidate.CandidateId, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([changed.Candidate], [changed.Proof], [], state.Revision);
            Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood); Assert.Null(state.Plugins[0].Pending);
            Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId); Assert.Null(state.Plugins[0].Pinned);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants)); Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Contains("pre-replacement basis", state.Plugins[0].Problem);
            var row = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
            Assert.False(row.CanConfirm); Assert.Equal("unproven-history", row.BlockedReason);
            before = await File.ReadAllBytesAsync(store.StorePath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfigurationAsync("test.plugin", changed.Candidate.CandidateId, proposed, state.Revision));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SelectCandidateAsync("test.plugin", changed.Candidate.CandidateId, state.Revision));
            Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactProvenOldAllowsFirstConfigurationAndSameBuildEditsWithoutInventingHistory(bool grant)
    {
        var old = await Delivered("unconfigured", "one", requiredSetting: true);
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood); Assert.Null(state.Plugins[0].Pending);
        if (grant) state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        // Even a refresh remains admissible when it exactly matches the recorded delivered OLD proof.
        state = await store.ObserveCandidateAsync(old.Candidate, state.Revision);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            new("first", Values("1", 7), new Dictionary<string, SettingsSnapshot>()), state.Revision);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            state.Plugins[0].Pending!.Configuration with { PluginSettings = Values("1", 19) }, state.Revision);
        Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
        state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        Assert.Equal(old.Candidate.Artifact.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(19, Number(Active(state))); Assert.Equal(grants, SdkJson.Serialize(state.Grants));
    }

    [Fact]
    public async Task LocalOnlyRequiredCandidateRetainsOrdinaryFirstConfigurationAndSameBuildEdits()
    {
        var required = new SettingsSchema("1", [new("count", SettingScope.Plugin, SettingKind.Integer, "Count", IsRequired: true)]);
        var local = Candidate("local-old", required);
        var store = Store(); var state = await store.ApplyStartupAsync([local], 0);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood); Assert.Null(state.Plugins[0].Pending);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        state = await store.ObserveCandidateAsync(local, state.Revision);
        state = await store.SaveConfigurationAsync("test.plugin", local.CandidateId,
            new("first-local", Values("1", 7), new Dictionary<string, SettingsSnapshot>()), state.Revision);
        state = await store.SaveConfigurationAsync("test.plugin", local.CandidateId,
            state.Plugins[0].Pending!.Configuration with { PluginSettings = Values("1", 19) }, state.Revision);
        state = await Store().ApplyStartupAsync([local], state.Revision);
        Assert.Equal(local.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Equal(19, Number(Active(state))); Assert.Equal(grants, SdkJson.Serialize(state.Grants));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSameCodeProfileLocalAliasUsesEstablishedActiveOrLastGoodWithoutDeliveredRole(bool lastGoodOnly)
    {
        var old = await Delivered("shipped", "one"); var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            Active(state).Configuration with { PluginSettings = Values("1", 7) }, state.Revision);
        state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], state.Revision);
        var established = Active(state);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        if (lastGoodOnly)
        {
            state = await store.ApplyBundledStartupAsync([], [], [], state.Revision);
            Assert.Null(state.Plugins[0].Active); Assert.NotNull(state.Plugins[0].LastGood);
        }
        var local = await LocalAlias(old.Candidate);
        Assert.NotEqual(old.Candidate.CandidateId, local.CandidateId);
        Assert.NotEqual(established.Artifact.AssemblyPath, local.Artifact!.AssemblyPath);
        state = await store.ObserveCandidateAsync(local, state.Revision);
        state = await store.SelectCandidateAsync("test.plugin", local.CandidateId, state.Revision);
        Assert.Equal(local.CandidateId, state.Plugins[0].SelectedCandidateId);
        Assert.Equal(7, Number(state.Plugins[0].Pending!)); Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([old.Candidate, local], [old.Proof], [], state.Revision);
            Assert.Equal(local.CandidateId, Active(state).CandidateId);
            Assert.Equal(established.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(7, Number(Active(state))); Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Equal(local.CandidateId, state.Plugins[0].SelectedCandidateId);
            // Both the shipped and deliberately selected local alias are present.
            // Successful explicit activation retains the pre-existing nonfatal advisory.
            Assert.Equal("Multiple candidates exist; the selected candidate is retained.", state.Plugins[0].Problem);
            using JsonDocument envelope = JsonDocument.Parse(await File.ReadAllTextAsync(store.StorePath));
            using JsonDocument payload = JsonDocument.Parse(envelope.RootElement.GetProperty("payload").GetBytesFromBase64());
            Assert.DoesNotContain(payload.RootElement.GetProperty("provenance").EnumerateArray(),
                proof => proof.GetProperty("candidateId").GetString() == local.CandidateId);
            Assert.Empty(payload.RootElement.GetProperty("bindings").EnumerateArray());
        }
    }

    [Theory]
    [InlineData("description")]
    [InlineData("compilation")]
    [InlineData("assembly")]
    [InlineData("sdk")]
    [InlineData("compiler")]
    [InlineData("resources")]
    public async Task FalseSameExecutionStampDoesNotAuthorizeProfileLocalAlias(string change)
    {
        var old = await Delivered("shipped", "one"); var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        var local = await LocalAlias(old.Candidate);
        var falseSameCode = change switch
        {
            "description" => local with { Description = local.Description! with { Name = "Different metadata" } },
            "compilation" => local with { Artifact = local.Artifact! with { CompilationFingerprint = new string('d', 64) } },
            "assembly" => local with { Artifact = local.Artifact! with { AssemblySha256 = new string('d', 64) } },
            "sdk" => local with { Artifact = local.Artifact! with { SdkFingerprint = new string('d', 64) } },
            "compiler" => local with { Artifact = local.Artifact! with { CompilerFingerprint = new string('d', 64) } },
            _ => local with { Artifact = local.Artifact! with { Resources = [new("extra", "extra.dat", new string('d', 64))] } }
        };
        Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, falseSameCode.Artifact!.ExecutionRevision);
        state = await store.ObserveCandidateAsync(falseSameCode, state.Revision);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SelectCandidateAsync("test.plugin", local.CandidateId, state.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfigurationAsync("test.plugin", local.CandidateId,
            Active(state).Configuration, state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        // A refresh after an exact explicit selection must not use its unchanged execution stamp as proof either.
        state = await store.ObserveCandidateAsync(local, state.Revision);
        state = await store.SelectCandidateAsync("test.plugin", local.CandidateId, state.Revision);
        state = await store.ApplyBundledStartupAsync([old.Candidate, falseSameCode], [old.Proof], [], state.Revision);
        Assert.Null(state.Plugins[0].Active); Assert.Equal(local.CandidateId, state.Plugins[0].SelectedCandidateId);
        Assert.NotNull(state.Plugins[0].LastGood); Assert.Contains("provenance", state.Plugins[0].Problem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLocalAliasNeverFallsBackToDeliveredCandidateWhenMissingOrBroken(bool missing)
    {
        var old = await Delivered("shipped", "one"); var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        state = await store.SetGrantAsync("test.plugin", "dimensions", new("source"), FilterDataAccess.OriginalImage, state.Revision);
        var local = await LocalAlias(old.Candidate);
        state = await store.ObserveCandidateAsync(local, state.Revision);
        state = await store.SelectCandidateAsync("test.plugin", local.CandidateId, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants), owners = SdkJson.Serialize(state.SelectedSourceOwners);
        byte[] config = SdkJson.Serialize(state.Plugins[0].Pending!.Configuration);
        var broken = local with { Artifact = null, Description = null, Problem = "Local alias compile failure" };
        for (int restart = 0; restart < 2; restart++)
        {
            state = await Store().ApplyBundledStartupAsync(missing ? [old.Candidate] : [old.Candidate, broken], [old.Proof], [], state.Revision);
            Assert.Null(state.Plugins[0].Active); Assert.Equal(local.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(config, SdkJson.Serialize(state.Plugins[0].Pending!.Configuration));
            Assert.Equal(grants, SdkJson.Serialize(state.Grants)); Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
            Assert.Null(state.Plugins[0].Pinned); Assert.NotNull(state.Plugins[0].Problem);
            Assert.Contains(state.Plugins[0].Candidates, c => c.CandidateId == old.Candidate.CandidateId && c.IsPresent && c.Problem is null);
            var repair = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
            Assert.False(repair.CanConfirm); Assert.Equal("unproven-history", repair.BlockedReason);
            Assert.Null(repair.NewCandidateId); // the available shipped file is not a repair target for a local choice
        }
    }

    [Theory]
    [InlineData("pending-only")]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    public async Task SameCodeLocalAliasCannotUsePendingOnlyMissingOrAmbiguousBasisAsAnEstablishedRelease(string basisKind)
    {
        var old = await Delivered("shipped", "one", requiredSetting: true); var store = Store();
        var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        if (basisKind == "pending-only")
            state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
                new("required", Values("1", 7), new Dictionary<string, SettingsSnapshot>()), state.Revision);
        var local = await LocalAlias(old.Candidate);
        if (basisKind != "pending-only")
        {
            var changed = await Delivered("shipped", "two", requiredSetting: true);
            state = basisKind == "missing"
                ? await store.ObserveCandidateAsync(changed.Candidate, state.Revision) // known identity, zero matching proof
                : await store.ApplyBundledStartupAsync([changed.Candidate], [changed.Proof], [], state.Revision); // multiple old executions, no retained release
        }
        state = await store.ObserveCandidateAsync(local, state.Revision);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SelectCandidateAsync("test.plugin", local.CandidateId, state.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveConfigurationAsync("test.plugin", local.CandidateId,
            new("attempt", Values("1", 7), new Dictionary<string, SettingsSnapshot>()), state.Revision));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
    }

    private async Task<PluginCandidateObservation> LocalAlias(PluginCandidateObservation retained)
    {
        string local = Path.Combine(profile, "plugins"); Directory.CreateDirectory(local);
        string path = Path.Combine(local, "SelectedSource.cs");
        await File.WriteAllTextAsync(path, "// metadata-only unit fixture; no compiler is invoked by this test");
        var entry = Assert.Single((await new PluginInstallationInventory().ScanAsync(local)).Candidates);
        string material = Path.Combine(profile, "local-material"); Directory.CreateDirectory(material);
        string assembly = Path.Combine(material, "plugin.dll");
        await File.WriteAllBytesAsync(assembly, await File.ReadAllBytesAsync(retained.Artifact!.AssemblyPath));
        return retained with { CandidateId = entry.CandidateId, CandidatePath = entry.Path,
            Artifact = retained.Artifact with { AssemblyPath = assembly, ResourceRootPath = material } };
    }

    private async Task<(PluginProfileStore Store, PluginProfileSnapshot State, PluginCandidateObservation Candidate,
        DeliveredCandidateProvenance Proof)> PendingOnlyRequired(bool source = false)
    {
        var old = await Delivered("pending-old", "one", source: source, requiredSetting: true);
        var store = Store(); var state = await store.ApplyBundledStartupAsync([old.Candidate], [old.Proof], [], 0);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood); Assert.Null(state.Plugins[0].Pending);
        var sourceValues = source ? new Dictionary<string, SettingsSnapshot> { ["shared"] = new("1", []) }
            : new Dictionary<string, SettingsSnapshot>();
        state = await store.SaveConfigurationAsync("test.plugin", old.Candidate.CandidateId,
            new("required-config", Values("1", 7), sourceValues), state.Revision);
        Assert.Null(state.Plugins[0].Active); Assert.Null(state.Plugins[0].LastGood);
        Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].Pending!.ExecutionRevision);
        return (store, state, old.Candidate, old.Proof);
    }

    private async Task<(PluginCandidateObservation Candidate, DeliveredCandidateProvenance Proof)> Delivered(string installation, string build,
        string fileName = "fixture.plugin.json", bool source = false, bool requiredSetting = false)
    {
        string root = Path.Combine(profile, installation, "plugins"); Directory.CreateDirectory(root);
        byte[] assembly = Encoding.UTF8.GetBytes("synthetic offline assembly " + build);
        string sha = Convert.ToHexStringLower(SHA256.HashData(assembly));
        string sdk = new('b', 64), compiler = new('c', 64);
        string compilation = FixtureHashParts("prebuilt-v1", sha, sdk, compiler);
        string execution = FixtureHashParts("artifact-v1", compilation);
        string cache = Path.Combine(profile, "test-artifacts", execution); Directory.CreateDirectory(cache);
        await File.WriteAllBytesAsync(Path.Combine(root, "fixture.dll"), assembly);
        await File.WriteAllBytesAsync(Path.Combine(cache, "plugin.dll"), assembly);
        PluginBundleManifest manifest = new(1, "fixture.dll", sha, sdk, compiler, []);
        await File.WriteAllTextAsync(Path.Combine(root, fileName), JsonSerializer.Serialize(manifest));
        var installed = Assert.Single((await new PluginInstallationInventory().ScanAsync(root)).Candidates,
            c => c.Kind == PluginCandidateKind.PrebuiltManifest);
        PluginArtifact artifact = new(compilation, execution, Path.Combine(cache, "plugin.dll"), sha, sdk, compiler, [], cache);
        SettingsSchema schema = requiredSetting ? new("1", [new("count", SettingScope.Plugin,
            SettingKind.Integer, "Count", IsRequired: true)]) : NumberSchema("1", 1);
        PluginCandidateObservation candidate = Candidate(execution, schema) with
            { CandidateId = installed.CandidateId, CandidatePath = installed.Path, Artifact = artifact };
        if (source)
        {
            SourceDescription description = new("shared", "Shared source", new("https://fixture.test"), SourceCapabilities.ThreadRead,
                [new(SourceResourceKind.Thread, "fixture.test", "/{board}/thread/{thread}", IsCanonical: true)],
                [new("fixture.test", HttpHostPurpose.Metadata)], Empty);
            candidate = candidate with { Description = candidate.Description! with { Sources = [description] } };
        }
        var proof = await PluginInstallationInventory.RecordDeliveredAsync(root, installed, artifact, candidate.Description!);
        return (candidate, proof);
    }

    private static string FixtureHashParts(params string[] parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string part in parts)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(part);
            byte[] length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
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
