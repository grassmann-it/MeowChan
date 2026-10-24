using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Passive catalog data over the real profile store. Artifacts are never executed.
/// Any compiler, describe, worker or probe call is a failure, not a simulated pass.
/// The separate PluginManagementChecks retains the real compiler/worker proof.
/// </summary>
internal sealed class PluginUxFixture
{
    internal const string SourceKey = "pux-source-candidate";
    internal const string FilterKey = "pux-filter-candidate";
    internal const string UnknownKey = "pux-unknown-candidate";
    internal const string AlternateKey = "pux-source-alternate";
    internal const string SourcePluginId = "sample.arbitrary.purpose";
    internal const string FilterPluginId = "sample.arbitrary.filter";
    internal const string SourceId = "pux-source";
    internal const string Purpose = "Reads saved discussion threads and provides links to their original media.";
    internal const string FilterPurpose = "Keeps images that match the dimensions selected in a media rule.";
    internal PluginProfileStore Store { get; }
    internal PluginManagementServices Services { get; }
    internal PluginCandidateObservation Source { get; }
    internal PluginCandidateObservation Filter { get; }
    internal int ForbiddenCalls { get; private set; }
    private readonly Inventory _inventory;

    private PluginUxFixture(string root)
    {
        Store = new(root);
        SettingsSchema empty = new("1", Array.Empty<SettingDefinition>());
        SettingsSchema settings = new("1", new[] { new SettingDefinition("label", SettingScope.Plugin,
            SettingKind.Text, "Label / Beschriftung", Default: new("label", SettingKind.Text, Text: "saved label")) });
        SettingsSchema sourceSettings = new("1", new[] { new SettingDefinition("limit", SettingScope.Source,
            SettingKind.Integer, "Limit / Grenze", "A whole number from 1 to 50.", true,
            new("limit", SettingKind.Integer, Integer: 10), Minimum: 1, Maximum: 50) });
        SourceDescription source = new(SourceId, "Discussion source", new Uri("https://pux.example.test/"),
            SourceCapabilities.ThreadRead | SourceCapabilities.OriginalMedia,
            new[] { new UrlRoute(SourceResourceKind.Thread, "pux.example.test", "/{board}/thread/{thread}", IsCanonical: true) },
            new[] { new HttpHostDeclaration("pux.example.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia) }, sourceSettings);
        PluginDescription sourceDescription = new(SourcePluginId, "Discussion reader", "1.0.0", SdkContract.Version,
            Purpose, new[] { source }, Array.Empty<FilterDescription>(), settings,
            new[] { new InformationEntry("retained", "Source information", InformationKind.Text,
                "Catalog information is a saved snapshot; it does not promise live website availability.") });
        PluginDescription filterDescription = new(FilterPluginId, "Image dimensions", "2.0.0", SdkContract.Version,
            FilterPurpose, Array.Empty<SourceDescription>(),
            new[] { new FilterDescription("dimensions", "Image dimensions", FilterPurpose,
                FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, empty, new[] { "image/png", "image/jpeg" }) },
            empty, Array.Empty<InformationEntry>());
        Source = new(SourceKey, SourcePluginId, Artifact(root, 'a'), sourceDescription,
            CandidatePath: Path.Combine(root, "source files", "discussion reader ü.cs"));
        Filter = new(FilterKey, FilterPluginId, Artifact(root, 'b'), filterDescription,
            CandidatePath: Path.Combine(root, "image-filter.plugin.json"));
        _inventory = new(new[] {
            new PluginInstallationCandidate(SourceKey, Source.CandidatePath!, PluginCandidateKind.Source, 100, Array.Empty<PluginBuildDiagnostic>()),
            new PluginInstallationCandidate(FilterKey, Filter.CandidatePath!, PluginCandidateKind.PrebuiltManifest, 100, Array.Empty<PluginBuildDiagnostic>()),
            new PluginInstallationCandidate(UnknownKey, Path.Combine(root, "not yet described ü.cs"), PluginCandidateKind.Source, 10,
                Array.Empty<PluginBuildDiagnostic>()) });
        Services = new(Store, new ForbiddenCompiler(() => ForbiddenCalls++), _inventory, root,
            (_, _) => { ForbiddenCalls++; throw new InvalidOperationException("UX display unexpectedly described a worker."); },
            (_, _, _) => { ForbiddenCalls++; throw new InvalidOperationException("UX display unexpectedly started a worker."); },
            (_, _, _, _, _) => { ForbiddenCalls++; throw new InvalidOperationException("UX display unexpectedly probed a site."); });
    }

    internal static async Task<PluginUxFixture> CreateAsync(string root)
    {
        Directory.CreateDirectory(root);
        PluginUxFixture fixture = new(root);
        PluginProfileSnapshot state = await fixture.Store.ApplyStartupAsync(new[] { fixture.Source, fixture.Filter }, 0);
        await fixture.Store.SetEnabledAsync(FilterPluginId, false, state.Revision);
        return fixture;
    }

    internal async Task<PluginProfileSnapshot> StageSettingsAsync()
    {
        PluginProfileSnapshot state = await Store.ReadAsync();
        PluginReleaseSnapshot active = state.Plugins.Single(p => p.PluginId == SourcePluginId).Active!;
        var configuration = active.Configuration with { Revision = "pux-next-configuration",
            PluginSettings = new("1", new[] { new SettingValue("label", SettingKind.Text, Text: "next-start label") }) };
        return await Store.SaveConfigurationAsync(SourcePluginId, SourceKey, configuration, state.Revision);
    }

    internal async Task AddAlternateAsync()
    {
        PluginProfileSnapshot state = await Store.ReadAsync();
        await Store.ObserveCandidateAsync(Source with { CandidateId = AlternateKey,
            Artifact = Source.Artifact! with { ExecutionRevision = new string('c', 64) },
            CandidatePath = Path.Combine(Path.GetDirectoryName(Source.CandidatePath!)!, "discussion reader alternate.cs") }, state.Revision);
    }

    internal async Task FailSourceAsync(bool missing)
    {
        PluginProfileSnapshot state = await Store.ReadAsync();
        if (missing)
        {
            _inventory.Remove(SourceKey);
            await Store.ApplyStartupAsync(new[] { Filter }, state.Revision);
        }
        else await Store.ObserveCandidateAsync(Source with { Artifact = null, Description = null,
            Problem = "Fixture compiler rejected an unsupported schema contract." }, state.Revision);
    }

    private static PluginArtifact Artifact(string root, char hash)
    {
        string value = new(hash, 64);
        return new(value, value, Path.Combine(root, value + ".dll"), value, value, value,
            Array.Empty<PluginArtifactResource>());
    }

    private sealed class Inventory(IEnumerable<PluginInstallationCandidate> candidates) : IPluginInstallationInventory
    {
        private readonly List<PluginInstallationCandidate> _candidates = candidates.ToList();
        internal void Remove(string key) => _candidates.RemoveAll(c => c.CandidateId == key);
        public Task<PluginInventory> ScanAsync(string directory, CancellationToken cancellationToken = default)
            => Task.FromResult(new PluginInventory(_candidates.ToArray(), false, Array.Empty<PluginBuildDiagnostic>()));
    }

    private sealed class ForbiddenCompiler(Action called) : IPluginCompilationService
    {
        private Exception Fail() { called(); return new InvalidOperationException("UX display unexpectedly invoked compilation or cache maintenance."); }
        public Task<PluginBuildResult> BuildAsync(string sourcePath, IReadOnlyList<PluginResourceInput>? resources = null,
            CancellationToken cancellationToken = default) => throw Fail();
        public Task<PluginArtifact?> FindArtifactAsync(string executionRevision, CancellationToken cancellationToken = default) => throw Fail();
        public Task<PluginCacheCleanupResult> CleanupAsync(IReadOnlySet<string> retainedExecutionRevisions,
            IReadOnlySet<string> retainedCompilationFingerprints, TimeSpan minimumAge,
            CancellationToken cancellationToken = default) => throw Fail();
    }
}
