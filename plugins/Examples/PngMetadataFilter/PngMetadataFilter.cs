using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.ExamplePlugins;

/// <summary>SDK-only PNG text-key presence check. Text values are never decoded, retained or logged.</summary>
public sealed class PngMetadataFilter : IImageFilterPlugin, IPluginSettingsValidator
{
    public const string PluginId = "meowchan.filter.png-metadata";
    public const string FilterKey = "png-metadata-presence";
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
    private static readonly uint[] CrcTable = BuildCrcTable();
    private static readonly SettingsSchema Schema = new("1", new SettingDefinition[]
    {
        new("field", SettingScope.FilterRule, SettingKind.Enum, "PNG metadata field",
            "Exact, case-sensitive PNG text keyword. An empty value still counts as present.",
            Default: new("field", SettingKind.Enum, Text: "parameters"), Options: new SettingOption[]
            { new("parameters", "parameters"), new("workflow", "workflow"), new("prompt", "prompt"), new("custom", "Custom PNG keyword") }),
        new("customKeyword", SettingScope.FilterRule, SettingKind.Text, "Custom PNG keyword",
            "Used for Custom only. 1–79 Latin-1 bytes; printable PNG keyword characters, no leading, trailing or repeated spaces. Comparison is case-sensitive.", MaximumLength: 79),
        new("presence", SettingScope.FilterRule, SettingKind.Enum, "Keep images when the field is",
            "Checks tEXt, zTXt and iTXt headers, including fields after image data. Compressed text content is not validated.",
            Default: new("presence", SettingKind.Enum, Text: "present"), Options: new SettingOption[]
            { new("present", "Present"), new("absent", "Absent") })
    });

    public PluginDescription Describe() => new(PluginId, "PNG metadata presence", "1.0.0", SdkContract.Version,
        "Keeps PNG originals according to the presence of an exact text metadata keyword. Does not identify AI images or inspect prompt text.",
        Array.Empty<SourceDescription>(), new FilterDescription[]
        {
            new(FilterKey, "PNG metadata field", "PNG tEXt, zTXt and iTXt keyword presence only. A field with an empty value is present; non-PNG files are not assessable.",
                FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, Schema, new[] { "image/png" })
        }, new("1", Array.Empty<SettingDefinition>()), new InformationEntry[]
        {
            new("scope", "What is checked", InformationKind.Text, "Exact field names and PNG structure/CRCs. No decompression, text extraction, AI classification, tagging or network access."),
            new("limits", "Limits", InformationKind.Text, "Existing 32 MiB image and 16 megapixel budgets; at most 64 KiB per asset read. Technical errors never mean the field is absent.")
        }, "MeowChan");

    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        PluginCallContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(request.Scope == SettingScope.FilterRule
            ? Validate(request.Settings) : (IReadOnlyList<FieldValidationError>)Array.Empty<FieldValidationError>());
    }

    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.FilterKey != FilterKey) return new(ImageFilterOutcome.Unavailable, "unknown_filter");
        if (request.OriginalSize < 0) return new(ImageFilterOutcome.Error, "invalid_image_size");
        if (request.OriginalSize > SdkLimits.MaximumImageBytes) return new(ImageFilterOutcome.Error, "image_byte_limit");
        IReadOnlyList<FieldValidationError> errors = Validate(request.RuleSettings);
        if (errors.Count > 0) return new(ImageFilterOutcome.Error, "invalid_rule_parameters", errors[0].Message);
        string field = Value(request.RuleSettings, "field", "parameters");
        string keyword = field == "custom" ? Value(request.RuleSettings, "customKeyword", "") : field;
        bool requirePresent = Value(request.RuleSettings, "presence", "present") == "present";
        try
        {
            bool? found = await ScanAsync(context.Images, request.AssetToken, request.OriginalSize, keyword, cancellationToken).ConfigureAwait(false);
            if (found == null) return new(ImageFilterOutcome.Unavailable, "unsupported_format", "Only PNG originals can be assessed.");
            return new(found.Value == requirePresent ? ImageFilterOutcome.Match : ImageFilterOutcome.NoMatch,
                found.Value ? "metadata_present" : "metadata_absent");
        }
        catch (PngLimitException exception) { return new(ImageFilterOutcome.Error, exception.Reason); }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException)
        { return new(ImageFilterOutcome.Error, "invalid_png_structure", "PNG structure, text-field header or checksum is invalid or incomplete."); }
    }

    private static string Value(SettingsSnapshot settings, string key, string fallback) =>
        settings.Values.FirstOrDefault(value => value.Key == key)?.Text ?? fallback;

    private static IReadOnlyList<FieldValidationError> Validate(SettingsSnapshot settings)
    {
        List<FieldValidationError> errors = SdkValidation.ValidateSettings(Schema, settings, SettingScope.FilterRule)
            .Select(issue => new FieldValidationError(issue.Path, issue.Code, issue.Message)).ToList();
        if (Value(settings, "field", "parameters") == "custom" && !IsKeyword(Value(settings, "customKeyword", "")))
            errors.Add(new("customKeyword", "invalid_png_keyword", "Use an exact 1–79 byte PNG Latin-1 keyword without leading, trailing or consecutive spaces."));
        return errors;
    }

    private static bool IsKeyword(string keyword)
    {
        if (keyword.Length is < 1 or > 79 || keyword[0] == ' ' || keyword[^1] == ' ') return false;
        bool space = false;
        foreach (char c in keyword)
        {
            if (!IsKeywordByte(c) || c == ' ' && space) return false;
            space = c == ' ';
        }
        return true;
    }
    private static bool IsKeywordByte(int value) => value is >= 32 and <= 126 or >= 161 and <= 255;

    private static async Task<bool?> ScanAsync(IImageAssetReader reader, string token, long size, string keyword,
        CancellationToken cancellationToken)
    {
        async Task<byte[]> Read(long offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset < 0 || count is < 1 or > SdkLimits.MaximumImageChunkBytes || offset > size - count) throw new EndOfStreamException();
            ImageAssetChunk chunk = await reader.ReadImageAsync(token, offset, count, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Offset != offset || chunk.Bytes.Length != count || chunk.IsEnd != (offset + count == size)) throw new EndOfStreamException();
            return chunk.Bytes;
        }
        if (size < Signature.Length)
        {
            if (size > 0 && (await Read(0, (int)size).ConfigureAwait(false)).AsSpan().SequenceEqual(Signature.AsSpan(0, (int)size)))
                throw new InvalidDataException();
            return null;
        }
        if (!(await Read(0, Signature.Length).ConfigureAwait(false)).AsSpan().SequenceEqual(Signature)) return null;
        long cursor = Signature.Length;
        bool headerSeen = false, paletteSeen = false, imageSeen = false, imageEnded = false, found = false;
        long imageBytes = 0;
        int chunks = 0, colorType = -1, bitDepth = 0;
        while (cursor < size)
        {
            if (++chunks > SdkLimits.MaximumCollectionCount) throw new PngLimitException("png_chunk_limit");
            byte[] header = await Read(cursor, 8).ConfigureAwait(false);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            if (length > int.MaxValue || length > size - cursor - 12) throw new InvalidDataException();
            for (int i = 4; i < 8; i++)
                if (!(header[i] is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z')) throw new InvalidDataException();
            if ((header[6] & 32) != 0) throw new InvalidDataException(); // PNG reserved bit.
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
            const uint Ihdr = 0x49484452, Plte = 0x504c5445, Idat = 0x49444154, Iend = 0x49454e44;
            const uint Text = 0x74455874, Ztxt = 0x7a545874, Itxt = 0x69545874;
            if (!headerSeen && (type != Ihdr || length != 13) || headerSeen && type == Ihdr) throw new InvalidDataException();
            if (type == Plte && (paletteSeen || imageSeen || length is < 3 or > 768 || length % 3 != 0 || colorType is 0 or 4)) throw new InvalidDataException();
            if (type == Idat && (imageEnded || colorType == 3 && !paletteSeen)) throw new InvalidDataException();
            if (type == Iend && (length != 0 || !imageSeen || imageBytes == 0)) throw new InvalidDataException();
            if ((header[4] & 32) == 0 && type is not (Ihdr or Plte or Idat or Iend)) throw new InvalidDataException();
            if (imageSeen && type != Idat) imageEnded = true;
            TextHeader? text = type is Text or Ztxt or Itxt ? new(type, keyword) : null;
            uint crc = UpdateCrc(uint.MaxValue, header, 4, 4);
            long data = cursor + 8, remaining = length;
            byte[]? ihdr = null;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, SdkLimits.MaximumImageChunkBytes);
                byte[] bytes = await Read(data, count).ConfigureAwait(false);
                crc = UpdateCrc(crc, bytes, 0, bytes.Length);
                if (type == Ihdr) ihdr = bytes; // IHDR is exactly 13 bytes.
                text?.Read(bytes);
                data += count; remaining -= count;
            }
            byte[] checksum = await Read(data, 4).ConfigureAwait(false);
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(checksum)) throw new InvalidDataException();
            if (text != null) { text.Finish(); found |= text.Matches; }
            if (type == Ihdr)
            {
                byte[] bytes = ihdr ?? throw new InvalidDataException();
                uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4)), height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4, 4));
                if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) throw new InvalidDataException();
                if ((long)width * height > SdkLimits.MaximumDecodedPixels) throw new PngLimitException("image_pixel_limit");
                bitDepth = bytes[8]; colorType = bytes[9];
                bool depthValid = colorType switch { 0 => bitDepth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => bitDepth is 8 or 16, 3 => bitDepth is 1 or 2 or 4 or 8, _ => false };
                if (!depthValid || bytes[10] != 0 || bytes[11] != 0 || bytes[12] > 1) throw new InvalidDataException();
                headerSeen = true;
            }
            if (type == Plte)
            {
                if (colorType == 3 && length / 3 > 1u << bitDepth) throw new InvalidDataException();
                paletteSeen = true;
            }
            if (type == Idat) { imageSeen = true; imageBytes += length; }
            cursor = data + 4;
            if (type == Iend)
            {
                if (cursor != size) throw new InvalidDataException();
                return found;
            }
        }
        throw new InvalidDataException(); // Absence requires a complete stream through IEND.
    }

    private sealed class TextHeader(uint type, string selectedKeyword)
    {
        private readonly byte[] _keyword = new byte[79];
        private int _length, _phase, _utf8Remaining;
        private byte _continuationMinimum = 0x80, _continuationMaximum = 0xbf;
        public bool Matches { get; private set; }
        public void Read(byte[] bytes)
        {
            foreach (byte value in bytes)
            {
                if (_phase == 9) return; // The value is deliberately not inspected.
                if (_phase == 0)
                {
                    if (value == 0)
                    {
                        if (_length == 0 || _keyword[_length - 1] == 32) throw new InvalidDataException();
                        Matches = _length == selectedKeyword.Length;
                        for (int i = 0; Matches && i < _length; i++) Matches = _keyword[i] == selectedKeyword[i];
                        _phase = type == 0x74455874 ? 9 : 1;
                    }
                    else
                    {
                        if (_length >= 79 || !IsKeywordByte(value) || value == 32 && (_length == 0 || _keyword[_length - 1] == 32)) throw new InvalidDataException();
                        _keyword[_length++] = value;
                    }
                }
                else if (type == 0x7a545874)
                {
                    if (value != 0) throw new InvalidDataException(); // zTXt compression method.
                    _phase = 9;
                }
                else if (_phase == 1)
                {
                    if (value > 1) throw new InvalidDataException(); // iTXt compression flag.
                    _phase = 2;
                }
                else if (_phase == 2)
                {
                    if (value != 0) throw new InvalidDataException(); // iTXt compression method.
                    _phase = 3;
                }
                else if (_phase == 3)
                {
                    if (value == 0) _phase = 4;
                    else if (!(value is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9' or (byte)'-')) throw new InvalidDataException();
                }
                else if (_phase == 4)
                {
                    if (value == 0)
                    {
                        if (_utf8Remaining != 0) throw new InvalidDataException();
                        _phase = 9;
                    }
                    else ValidateUtf8(value);
                }
            }
        }
        public void Finish() { if (_phase != 9) throw new InvalidDataException(); }
        private void ValidateUtf8(byte value)
        {
            if (_utf8Remaining > 0)
            {
                if (value < _continuationMinimum || value > _continuationMaximum) throw new InvalidDataException();
                _utf8Remaining--; _continuationMinimum = 0x80; _continuationMaximum = 0xbf;
                return;
            }
            if (value < 0x80) return;
            if (value is >= 0xc2 and <= 0xdf) _utf8Remaining = 1;
            else if (value is >= 0xe0 and <= 0xef)
            {
                _utf8Remaining = 2;
                if (value == 0xe0) _continuationMinimum = 0xa0;
                if (value == 0xed) _continuationMaximum = 0x9f;
            }
            else if (value is >= 0xf0 and <= 0xf4)
            {
                _utf8Remaining = 3;
                if (value == 0xf0) _continuationMinimum = 0x90;
                if (value == 0xf4) _continuationMaximum = 0x8f;
            }
            else throw new InvalidDataException();
        }
    }

    private sealed class PngLimitException(string reason) : Exception
    { public string Reason { get; } = reason; }
    private static uint UpdateCrc(uint crc, byte[] bytes, int offset, int count)
    {
        for (int i = offset; i < offset + count; i++) crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
        return crc;
    }
    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            table[i] = crc;
        }
        return table;
    }
}
