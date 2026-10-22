using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.ExamplePlugins;

/// <summary>Editable SDK-only example. Reads original bytes and never trusts provider dimensions.</summary>
public sealed class ImageDimensionsFilter : IImageFilterPlugin, IPluginSettingsValidator
{
    public const string PluginId = "meowchan.example.image-dimensions";
    public const string FilterKey = "dimensions";
    private static readonly SettingsSchema Schema = new("1", new SettingDefinition[] {
        new("minimumWidth", SettingScope.FilterRule, SettingKind.Integer, "Minimum width", Default: new("minimumWidth", SettingKind.Integer, Integer: 0), Minimum: 0, Maximum: 16777216),
        new("minimumHeight", SettingScope.FilterRule, SettingKind.Integer, "Minimum height", Default: new("minimumHeight", SettingKind.Integer, Integer: 0), Minimum: 0, Maximum: 16777216),
        new("orientation", SettingScope.FilterRule, SettingKind.Enum, "Orientation", Default: new("orientation", SettingKind.Enum, Text: "any"), Options: new SettingOption[] {
            new("any", "Any"), new("landscape", "Landscape"), new("portrait", "Portrait"), new("square", "Square") }),
        new("aspectRatio", SettingScope.FilterRule, SettingKind.Text, "Aspect ratio", "Optional width / height, using a decimal point.", MaximumLength: 32),
        new("aspectRatioTolerance", SettingScope.FilterRule, SettingKind.Text, "Aspect ratio tolerance", "Absolute difference from width / height.", Default: new("aspectRatioTolerance", SettingKind.Text, Text: "0.01"), MaximumLength: 32)
    });

    public PluginDescription Describe() => new(PluginId, "Image dimensions (example)", "1.0.0", SdkContract.Version,
        "Selects static JPEG and PNG originals by their actual width, height and aspect ratio.",
        Array.Empty<SourceDescription>(), new FilterDescription[] {
            new(FilterKey, "Image dimensions", "Static JPEG / PNG only. Unreadable formats are technical omissions.",
                FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, Schema, new[] { "image/jpeg", "image/png" }) },
        new SettingsSchema("1", Array.Empty<SettingDefinition>()), new InformationEntry[] {
            new("example", "Editable example", InformationKind.Text, "No image decoding, external process, network access or image copies.") }, "MeowChan");

    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        PluginCallContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FieldValidationError> errors = request.Scope == SettingScope.FilterRule ? Validate(request.Settings) : Array.Empty<FieldValidationError>();
        return Task.FromResult(errors);
    }

    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        if (request.FilterKey != FilterKey) return new(ImageFilterOutcome.Unavailable, "unknown_filter");
        if (request.OriginalSize > SdkLimits.MaximumImageBytes) return new(ImageFilterOutcome.Error, "image_byte_limit");
        IReadOnlyList<FieldValidationError> errors = Validate(request.RuleSettings);
        if (errors.Count > 0) return new(ImageFilterOutcome.Error, "invalid_rule_parameters", errors[0].Message);
        try
        {
            (int width, int height) = await ReadDimensionsAsync(context.Images, request.AssetToken, request.OriginalSize, cancellationToken).ConfigureAwait(false);
            if ((long)width * height > SdkLimits.MaximumDecodedPixels) return new(ImageFilterOutcome.Error, "image_pixel_limit");
            Dictionary<string, SettingValue> values = request.RuleSettings.Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
            long minimumWidth = values.GetValueOrDefault("minimumWidth")?.Integer ?? 0;
            long minimumHeight = values.GetValueOrDefault("minimumHeight")?.Integer ?? 0;
            string orientation = values.GetValueOrDefault("orientation")?.Text ?? "any";
            if (width < minimumWidth || height < minimumHeight) return new(ImageFilterOutcome.NoMatch, "below_minimum_dimensions");
            if (orientation == "landscape" && width <= height || orientation == "portrait" && width >= height || orientation == "square" && width != height)
                return new(ImageFilterOutcome.NoMatch, "orientation_mismatch");
            string? ratioText = values.GetValueOrDefault("aspectRatio")?.Text;
            if (!string.IsNullOrWhiteSpace(ratioText))
            {
                decimal ratio = decimal.Parse(ratioText, CultureInfo.InvariantCulture);
                decimal tolerance = decimal.Parse(values.GetValueOrDefault("aspectRatioTolerance")?.Text ?? "0.01", CultureInfo.InvariantCulture);
                if (Math.Abs((decimal)width / height - ratio) > tolerance) return new(ImageFilterOutcome.NoMatch, "aspect_ratio_mismatch");
            }
            return new(ImageFilterOutcome.Match, "dimensions_match");
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException)
        { return new(ImageFilterOutcome.Error, "unreadable_image_header", "The original does not have a supported readable JPEG or PNG header."); }
    }

    private static IReadOnlyList<FieldValidationError> Validate(SettingsSnapshot settings)
    {
        List<FieldValidationError> errors = SdkValidation.ValidateSettings(Schema, settings, SettingScope.FilterRule)
            .Select(issue => new FieldValidationError(issue.Path, issue.Code, issue.Message)).ToList();
        foreach (SettingValue value in settings.Values)
        {
            if (value.Key is not ("aspectRatio" or "aspectRatioTolerance") || string.IsNullOrWhiteSpace(value.Text) && value.Key == "aspectRatio") continue;
            if (!decimal.TryParse(value.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal number)
                || number < 0 || value.Key == "aspectRatio" && number == 0)
                errors.Add(new(value.Key, "invalid_decimal", "Use a positive ratio or a nonnegative tolerance with a decimal point."));
        }
        return errors;
    }

    private static async Task<(int, int)> ReadDimensionsAsync(IImageAssetReader reader, string token, long size, CancellationToken cancellationToken)
    {
        async Task<byte[]> Read(long offset, int count)
        {
            if (offset < 0 || count < 1 || offset + count > size) throw new EndOfStreamException();
            ImageAssetChunk chunk = await reader.ReadImageAsync(token, offset, count, cancellationToken).ConfigureAwait(false);
            if (chunk.Offset != offset || chunk.Bytes.Length != count) throw new EndOfStreamException();
            return chunk.Bytes;
        }
        byte[] first = await Read(0, 2).ConfigureAwait(false);
        if (first[0] == 137 && first[1] == 80)
        {
            byte[] header = await Read(0, 33).ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})
                || !header.AsSpan(8, 8).SequenceEqual(new byte[] {0,0,0,13,73,72,68,82})) throw new InvalidDataException();
            uint crc = 0xffffffff;
            for (int i = 12; i < 29; i++) { crc ^= header[i]; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(29, 4))) throw new InvalidDataException();
            uint width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException();
            return ((int)width, (int)height);
        }
        if (first[0] != 255 || first[1] != 216) throw new InvalidDataException();
        long cursor = 2;
        while (cursor < size)
        {
            byte[] marker = await Read(cursor++, 1).ConfigureAwait(false);
            if (marker[0] != 255) throw new InvalidDataException();
            do { marker = await Read(cursor++, 1).ConfigureAwait(false); } while (marker[0] == 255);
            byte code = marker[0];
            if (code is 0 or 0xd9 or 0xda) throw new InvalidDataException();
            if (code == 0xd8 || code is >= 0xd0 and <= 0xd7 || code == 1) continue;
            byte[] lengthBytes = await Read(cursor, 2).ConfigureAwait(false);
            int length = (lengthBytes[0] << 8) | lengthBytes[1];
            if (length < 2 || cursor + length > size) throw new InvalidDataException();
            if (code is >= 0xc0 and <= 0xcf && code is not (0xc4 or 0xc8 or 0xcc))
            {
                if (length < 8) throw new InvalidDataException();
                byte[] frame = await Read(cursor + 2, 6).ConfigureAwait(false);
                int height = (frame[1] << 8) | frame[2], width = (frame[3] << 8) | frame[4];
                if (width == 0 || height == 0) throw new InvalidDataException();
                return (width, height);
            }
            cursor += length;
        }
        throw new InvalidDataException();
    }
}
