using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginSdk;
using MeowChan.Tags;

namespace MeowChan.PluginHost.Taggers;

internal sealed record LocalTaggerModelSettings
{
    internal static readonly TagSuggestionSettingRange ManifestKiBRange = new(1024, 64, 8192);
    internal static readonly TagSuggestionSettingRange WeightsMiBRange = new(1024, 64, 2048);
    internal static readonly TagSuggestionSettingRange InputTensorMiBRange = new(64, 1, 256);

    internal int MaximumManifestKiB { get; init; } = ManifestKiBRange.Default;
    internal int MaximumWeightsMiB { get; init; } = WeightsMiBRange.Default;
    internal int MaximumInputTensorMiB { get; init; } = InputTensorMiBRange.Default;

    internal int MaximumManifestBytes => MaximumManifestKiB * 1024;
    internal long MaximumWeightsBytes => MaximumWeightsMiB * 1024L * 1024;
    internal long MaximumInputTensorBytes => MaximumInputTensorMiB * 1024L * 1024;

    internal bool IsValid => ManifestKiBRange.Contains(MaximumManifestKiB) && WeightsMiBRange.Contains(MaximumWeightsMiB)
        && InputTensorMiBRange.Contains(MaximumInputTensorMiB);
}

internal sealed record LocalTaggerModelTag(string Name, TaggerTagCategory Category);

// V1 describes a float32 tensor for one image. Channel vectors are in the declared color order.
internal sealed record LocalTaggerPreprocessing(string InputName, string OutputName, string Layout, int Width, int Height,
    string Color, string Resize, string Interpolation, int[]? Pad, double Scale, double[] Mean, double[] StandardDeviation, string ScoreTransform)
{
    internal bool IsValid => LocalTaggerModelManifest.IsIdentity(InputName) && LocalTaggerModelManifest.IsIdentity(OutputName)
        && Layout is "NCHW" or "NHWC" && Width > 0 && Height > 0 && Color is "RGB" or "BGR"
        && Interpolation is "nearest" or "bilinear" or "bicubic"
        && Resize switch
        {
            "stretch" => Pad is null && HasNormalization,
            "fit-center" => Pad is { Length: 3 } && Pad.All(value => value is >= 0 and <= 255) && HasNormalization,
            _ => false
        };

    private bool HasNormalization => double.IsFinite(Scale) && Scale > 0
        && Mean is { Length: 3 } && Mean.All(double.IsFinite)
        && StandardDeviation is { Length: 3 } && StandardDeviation.All(value => double.IsFinite(value) && value > 0)
        && ScoreTransform is "identity" or "sigmoid";

    internal bool FitsInputTensor(long maximumBytes)
    {
        const int bytesPerPixel = 3 * sizeof(float);
        // Two positive Int32 dimensions fit in Int64. Divide the budget before comparing to avoid overflow.
        return (long)Width * Height <= maximumBytes / bytesPerPixel;
    }
}

internal sealed record LocalTaggerModelManifest(string Format, int Version, string Publisher, string ModelId, string Revision,
    long SizeBytes, string Sha256, string License, LocalTaggerModelTag[] Tags, LocalTaggerPreprocessing Preprocessing)
{
    internal const string FormatId = "meowchan-local-tagger";
    internal const string FileName = "manifest.json";
    internal const string WeightsFileName = "model.onnx";

    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter<TaggerTagCategory>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    internal TaggerModelRequirement Requirement => new(ModelId, Revision, License, SizeBytes, Sha256);

    internal static LocalTaggerModelManifest Parse(byte[] bytes, LocalTaggerModelSettings settings)
    {
        LocalTaggerModelManifest? manifest = JsonSerializer.Deserialize<LocalTaggerModelManifest>(bytes, JsonOptions);
        if (manifest is null || manifest.Format != FormatId || manifest.Version != 1
            || !IsIdentity(manifest.Publisher) || !IsIdentity(manifest.ModelId) || !IsIdentity(manifest.Revision)
            || !IsIdentity(manifest.License) || !CompilerWire.IsHash(manifest.Sha256)
            || manifest is { SizeBytes: <= 0 } || manifest.Preprocessing is not { IsValid: true } || !ValidTags(manifest.Tags))
            throw new InvalidDataException("Malformed local tagger model manifest.");
        if (manifest.SizeBytes > settings.MaximumWeightsBytes || !manifest.Preprocessing.FitsInputTensor(settings.MaximumInputTensorBytes))
            throw new LocalTaggerModelException(LocalTaggerModelIssue.Limit);
        return manifest;
    }

    internal static bool IsIdentity(string? text) => !string.IsNullOrWhiteSpace(text)
        && text.Length <= SdkLimits.MaximumIdentifierLength && text == text.Trim() && !text.Any(char.IsControl);

    private static bool ValidTags(LocalTaggerModelTag[]? tags)
    {
        if (tags is not { Length: > 0 }) return false;
        HashSet<LocalTaggerModelTag> unique = new();
        return tags.All(tag => tag is not null && !string.IsNullOrWhiteSpace(tag.Name) && tag.Name == tag.Name.Trim()
            && Encoding.UTF8.GetByteCount(tag.Name) <= TaggerLimits.MaximumTagNameBytes
            && !tag.Name.Any(char.IsControl) && Enum.IsDefined(tag.Category) && unique.Add(tag));
    }
}
