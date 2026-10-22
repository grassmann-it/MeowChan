namespace MeowChan.PluginSdk;

/// <summary>
/// A drop-in assembly supplies exactly one public, nonabstract implementation with a public
/// parameterless constructor. Describe is inert: it must not fetch sites or alter host state.
/// </summary>
public interface IPlugin
{
    PluginDescription Describe();
}

public interface ISourcePlugin : IPlugin
{
    Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default);

    Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default);
}

public interface IImageFilterPlugin : IPlugin
{
    Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional domain validation in addition to the host's simple schema checks.</summary>
public interface IPluginSettingsValidator : IPlugin
{
    Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        PluginCallContext context, CancellationToken cancellationToken = default);
}

/// <summary>Services and active settings bound by the worker to the current call lifetime.</summary>
public sealed record PluginCallContext(PluginConfiguration Configuration, IHostHttp Http,
    IImageAssetReader Images, IPluginResources Resources, IPluginLog Log);

public sealed record PluginConfiguration(string Revision, SettingsSnapshot PluginSettings,
    IReadOnlyDictionary<string, SettingsSnapshot> SourceSettings);
