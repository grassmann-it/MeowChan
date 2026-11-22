namespace MeowChan.PluginSdk;

/// <summary>
/// The tagger contract has its own version, independent of <see cref="SdkContract.Version"/>, the product and the
/// private worker protocol. A tagger proposes tags for media; it never writes tags. The host stores every result as a
/// suggestion in a review state that only the user turns into a tag (accept) or discards (reject).
/// </summary>
public static class TaggerContract
{
    public const string Version = "1.0.0";

    /// <summary>Exactly <see cref="Version"/> by ordinal comparison; no other tagger contract version is accepted.</summary>
    public static bool IsSupported(string? version) => string.Equals(version, Version, StringComparison.Ordinal);
}

/// <summary>
/// Fixed technical ceilings of tagger contract 1.0.0. They are not user limits: the host's tagger settings
/// (each with default, minimum and maximum) choose the effective value inside these ceilings.
/// </summary>
public static class TaggerLimits
{
    /// <summary>Most taggers one plugin may declare.</summary>
    public const int MaximumTaggersPerPlugin = 16;
    /// <summary>Most media items in one batch request.</summary>
    public const int MaximumBatchItems = 64;
    /// <summary>Largest input medium (256 MiB). Image bytes are additionally bounded by <see cref="SdkLimits.MaximumImageBytes"/>.</summary>
    public const long MaximumInputBytes = 256L * 1024 * 1024;
    /// <summary>Most suggestions per item a tagger may return.</summary>
    public const int MaximumSuggestionsPerItem = 512;
    /// <summary>Largest tag name in UTF-8 bytes (matches booru tag names).</summary>
    public const int MaximumTagNameBytes = 256;
    /// <summary>Most model requirements one tagger may declare.</summary>
    public const int MaximumModelsPerTagger = 8;
    /// <summary>Most hosts a tagger may declare for optional, consent-gated network access.</summary>
    public const int MaximumNetworkHosts = 8;
    /// <summary>Longest time range end of a time-bound suggestion (24 hours).</summary>
    public const long MaximumTimeRangeMilliseconds = 24L * 60 * 60 * 1000;
}

/// <summary>The media kinds a tagger accepts. Audio is reserved for the later audio package and is not sent yet.</summary>
[Flags]
public enum TaggerMediaKind { None = 0, Image = 1, Animation = 2, Video = 4, Audio = 8 }

/// <summary>
/// Category of a proposed tag. The first values follow <see cref="BooruTagCategory"/>; Rating is a content-rating
/// proposal (general, sensitive, questionable, explicit), never the user's own star rating.
/// </summary>
public enum TaggerTagCategory { Unknown, General, Artist, Copyright, Character, Meta, Rating }

/// <summary>
/// One model the tagger needs. Models are data packs installed by the user (never bundled with a plugin and never
/// downloaded by a tagger). The host runs a tagger only when every declared model is installed with this identity.
/// </summary>
public sealed record TaggerModelRequirement(string ModelId, string Revision, string License, long SizeBytes,
    string? Sha256 = null);

/// <summary>
/// Describes one tagger of a plugin. <see cref="NetworkHosts"/> is empty for an offline tagger (the normal case);
/// a nonempty list means the tagger cannot work without those exact hosts, and the host never lets it run before
/// the user consented to exactly that host list.
/// </summary>
public sealed record TaggerDescription(string Key, string Name, string Description, string ContractVersion,
    TaggerMediaKind SupportedMedia, IReadOnlyList<string> SupportedMimeTypes,
    IReadOnlyList<TaggerModelRequirement> Models, SettingsSchema Settings)
{
    /// <summary>Exact hosts for optional network access; empty means offline. Purpose must be Metadata.</summary>
    public IReadOnlyList<HttpHostDeclaration> NetworkHosts { get; init; } = Array.Empty<HttpHostDeclaration>();

    /// <summary>The tagger's own input ceiling (at most <see cref="TaggerLimits.MaximumInputBytes"/>).</summary>
    public long MaximumInputBytes { get; init; } = TaggerLimits.MaximumInputBytes;

    /// <summary>The tagger's own batch ceiling (at most <see cref="TaggerLimits.MaximumBatchItems"/>).</summary>
    public int MaximumBatchItems { get; init; } = TaggerLimits.MaximumBatchItems;
}

/// <summary>
/// One medium of a batch. <see cref="ItemId"/> is an opaque, batch-local identity minted by the host (no path, no
/// archive identity). The bytes are read only through <see cref="PluginCallContext.Images"/> with
/// <see cref="AssetToken"/>, which the host binds to this batch.
/// </summary>
public sealed record TaggerMediaInput(string ItemId, TaggerMediaKind Kind, string MimeType, long Length,
    string ContentSha256, string AssetToken, int? PixelWidth = null, int? PixelHeight = null,
    long? DurationMilliseconds = null);

/// <summary>
/// A batch of media for one tagger. <see cref="MinimumConfidence"/> is the floor below which the tagger omits a
/// suggestion (raw scores at or above it are returned unchanged; display thresholds are a host concern).
/// </summary>
public sealed record TaggerBatchRequest(string TaggerKey, string BatchId, IReadOnlyList<TaggerMediaInput> Items,
    double MinimumConfidence, int MaximumSuggestionsPerItem, SettingsSnapshot TaggerSettings,
    string ConfigurationRevision);

/// <summary>Optional time range of a suggestion inside a video or animation, in milliseconds from the start.</summary>
public sealed record TaggerTimeRange(long StartMilliseconds, long EndMilliseconds);

/// <summary>
/// One proposed tag. <see cref="Confidence"/> is the raw model score in [0, 1]. Names are the model's vocabulary
/// (for example Danbooru-style names); the host never treats a suggestion as a confirmed tag.
/// </summary>
public sealed record TaggerSuggestion(string Name, TaggerTagCategory Category, double Confidence,
    TaggerTimeRange? Range = null);

/// <summary>Tagged includes an empty suggestion list. The other outcomes are technical omissions, not a judgement.</summary>
public enum TaggerItemOutcome { Tagged, Unsupported, TooLarge, ModelUnavailable, Cancelled, Error }

public sealed record TaggerItemResult(string ItemId, TaggerItemOutcome Outcome,
    IReadOnlyList<TaggerSuggestion> Suggestions, string? ReasonCode = null);

/// <summary>
/// Which model produced the batch. <see cref="ModelId"/> and <see cref="ModelRevision"/> must name one declared
/// <see cref="TaggerModelRequirement"/>; a tagger without models reports its own algorithm identity and version.
/// </summary>
public sealed record TaggerModelProvenance(string ModelId, string ModelRevision, string? ModelSha256 = null,
    string? Runtime = null);

/// <summary>Answers exactly the requested batch: every requested item once, in any order, nothing else.</summary>
public sealed record TaggerBatchResult(string TaggerKey, string BatchId, TaggerModelProvenance Provenance,
    IReadOnlyList<TaggerItemResult> Items);

/// <summary>Progress inside one batch; the host reports job progress across batches.</summary>
public sealed record TaggerBatchProgress(int CompletedItems, int TotalItems);

/// <summary>Per-plugin extension of <see cref="PluginDescription"/>: null or empty means the plugin has no tagger.</summary>
public static class TaggerDescriptionExtensions
{
    public static IReadOnlyList<TaggerDescription> TaggersOrEmpty(this PluginDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return description.Taggers ?? Array.Empty<TaggerDescription>();
    }
}
