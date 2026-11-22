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

/// <summary>
/// Tagger contract (<see cref="TaggerContract.Version"/>): proposes tags for a batch of media. A tagger reads bytes
/// only through the bound asset reader, never downloads models or other data, and returns suggestions only; the host
/// validates the result against the request and keeps the suggestions in a review state. Cancellation stops the
/// batch; a tagger may report the remaining items as <see cref="TaggerItemOutcome.Cancelled"/> or throw
/// <see cref="OperationCanceledException"/>.
/// </summary>
public interface ITaggerPlugin : IPlugin
{
    Task<TaggerBatchResult> TagAsync(TaggerBatchRequest request, PluginCallContext context,
        IProgress<TaggerBatchProgress>? progress = null, CancellationToken cancellationToken = default);
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
