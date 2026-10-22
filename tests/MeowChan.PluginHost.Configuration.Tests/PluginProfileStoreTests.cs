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
