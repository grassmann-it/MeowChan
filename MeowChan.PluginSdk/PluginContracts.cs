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

/// <summary>
/// Booru post search, independent of boards and threads. Metadata only through IHostHttp: no retry
/// loops and no media download. A source with PostSearch needs SDK 1.1.0 and this interface.
/// </summary>
public interface IBooruSourcePlugin : IPlugin
{
    Task<BooruSearchResult> SearchPostsAsync(BooruSearchRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default);

    Task<BooruPostResult> ReadPostAsync(BooruPostRequest request, PluginCallContext context,
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
