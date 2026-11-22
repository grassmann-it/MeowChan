using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Runtime;
using MeowChan.PluginSdk;
using MeowChan.Tags;

namespace MeowChan.PluginHost.Taggers;

/// <summary>A host-reviewed pair, with a digest of the evidence covering both licenses. Model imports cannot supply approval.</summary>
internal sealed record LocalTaggerExecutionApproval(string ManifestSha256, string AdapterSha256, string ModelLicense,
    string AdapterLicense, string LicenseEvidenceSha256)
{
    internal bool Matches(LocalTaggerModelPackage package, PluginArtifact adapter) =>
        package.ManifestSha256 == ManifestSha256 && package.Manifest.License == ModelLicense
        && CompilerWire.IsHash(AdapterSha256) && string.Equals(adapter.AssemblySha256, AdapterSha256, StringComparison.OrdinalIgnoreCase)
        && LocalTaggerModelManifest.IsIdentity(AdapterLicense) && CompilerWire.IsHash(LicenseEvidenceSha256);
}

/// <summary>Runs a host-approved CPU adapter and model through a real worker.</summary>
internal sealed class LocalTaggerWorker(LocalTaggerModelStore models, TagSuggestionStore suggestions, PluginWorkerManager workers,
    PluginWorkerLaunch launch, LocalTaggerExecutionApproval approval)
{
    internal Task<TaggerJobResult> RunAsync(PluginArtifact adapter, PluginConfiguration configuration, string taggerKey,
        string approvedManifestSha256, IReadOnlyList<TaggerJobItem> items, SettingsSnapshot settings,
        IProgress<TaggerJobProgress>? progress = null, CancellationToken cancellationToken = default)
        => models.WithVerifiedModelAsync(async package =>
        {
            if (package is null || package.ManifestSha256 != approvedManifestSha256 || !approval.Matches(package, adapter))
                return new TaggerJobResult(TaggerJobStatus.ModelUnavailable, items.Count, 0, 0, 0, 0, 0, 0);

            if (adapter.Resources.Count != 0)
                throw new PluginWorkerException("resource", "The local CPU adapter must embed its own resources.");

            PluginArtifact bound = adapter with
            {
                ExecutionRevision = CompilerWire.HashParts(adapter.ExecutionRevision, approval.ManifestSha256,
                    approval.AdapterSha256, approval.LicenseEvidenceSha256),
                VerifyAssemblyOnLoad = true,
                ResourceRootPath = package.DirectoryPath,
                Resources =
                [
                    new("model-manifest", LocalTaggerModelManifest.FileName, package.ManifestSha256),
                    new("model-weights", LocalTaggerModelManifest.WeightsFileName, package.Manifest.Sha256)
                ]
            };

            await using IPluginWorkerClient worker = await workers.StartAsync(launch, bound, configuration, OfflineServices.Instance,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            TaggerModelRequirement requirement = package.Manifest.Requirement;
            TaggerDescription? description = worker.Description.TaggersOrEmpty().SingleOrDefault(value => value.Key == taggerKey);
            if (description is null || description.NetworkHosts.Count != 0 || description.SupportedMedia != TaggerMediaKind.Image
                || description.Models.Count != 1 || description.Models[0] != requirement)
                throw new PluginWorkerException("capability", "The local CPU adapter must declare the exact installed image model.");

            HashSet<(string Name, TaggerTagCategory Category)> vocabulary = package.Manifest.Tags.Select(tag => (tag.Name, tag.Category)).ToHashSet();
            TaggerRegistry registry = new();
            TaggerHostBinding binding = new(configuration, OfflineServices.Instance, OfflineServices.Instance, RequireVerifiedModelHash: true);
            TaggerRegistration registration = registry.Register(worker.Description with { Taggers = [description] }, binding, InvokeAsync).Single();
            try
            {
                TaggerJobRunner runner = new(registry, suggestions, new InstalledModel(requirement));
                return await runner.RunAsync(new(registration, items, settings), progress, cancellationToken).ConfigureAwait(false);
            }
            finally { registry.Unregister(registration.PluginId); }

            async Task<TaggerBatchResult> InvokeAsync(TaggerBatchRequest request, PluginCallContext context,
                IProgress<TaggerBatchProgress>? _, CancellationToken token)
            {
                TaggerBatchResult result = await ((PluginWorkerClient)worker).TagLocalBatchAsync(request, context, token).ConfigureAwait(false);
                if (result.Items.SelectMany(item => item.Suggestions).Any(tag => !vocabulary.Contains((tag.Name, tag.Category))))
                    throw new PluginWorkerException("validation", "Local tagger results must match the verified model and tag list.");
                return result;
            }
        }, cancellationToken);

    private sealed class InstalledModel(TaggerModelRequirement requirement) : ITaggerModelCatalog
    {
        public bool IsInstalled(TaggerModelRequirement model) => model == requirement;
    }

    private sealed class OfflineServices : IPluginHostServices, IPluginResources, IPluginLog
    {
        internal static OfflineServices Instance { get; } = new();

        public Task<HostHttpResult> SendHttpAsync(HostHttpRequest request, PluginOperationContext operation, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Local taggers do not have host HTTP access.");

        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, PluginOperationContext operation,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Image reads must use the current batch.");

        public string? ResolvePath(string logicalName) => null;

        public void Write(PluginLogLevel level, string message) { }
    }
}
