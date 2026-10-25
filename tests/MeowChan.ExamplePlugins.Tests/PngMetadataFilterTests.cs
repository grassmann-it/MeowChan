using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MeowChan.ExamplePlugins;
using MeowChan.PluginSdk;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class PngMetadataFilterTests
{
    [Fact]
    public void DescriptionIsPassiveSdkOnlyAndRequestsOnlyTheDocumentedAccess()
    {
        var description = new PngMetadataFilter().Describe();
        Assert.Equal(PngMetadataFilter.PluginId, description.PluginId);
        Assert.Empty(description.Sources);
        Assert.Empty(SdkValidation.ValidateDescription(description));
        var filter = Assert.Single(description.Filters);
        Assert.Equal(PngMetadataFilter.FilterKey, filter.Key);
        Assert.Equal(FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, filter.RequiredAccess);
        Assert.Equal(new[] { "image/png" }, filter.SupportedMimeTypes);
    }

    [Theory]
    [InlineData("tEXt")]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    public async Task EveryTextChunkKindRecognizesEmptyValuesBeforeAndAfterImageData(string type)
    {
        foreach (bool after in new[] { false, true })
        {
            var chunk = TextChunk(type, "parameters", []);
            byte[] png = after ? Png(after: [chunk]) : Png(before: [chunk]);
            var result = await Evaluate(png);
            Assert.Equal(new(ImageFilterOutcome.Match, "metadata_present"), result);
            var absent = await Evaluate(png, presence: "absent");
            Assert.Equal(new(ImageFilterOutcome.NoMatch, "metadata_present"), absent);
        }
    }

    [Fact]
    public async Task AbsenceRequiresCompleteScanIncludingMetadataAfterImageData()
    {
        byte[] png = Png(before: [TextChunk("tEXt", "other", [1])],
            after: [TextChunk("zTXt", "workflow", [2]), TextChunk("iTXt", "parameters", [3])]);
        Reader reader = new(png);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(reader)).Outcome);
        Assert.Equal(png.LongLength, reader.Reads.Sum(read => (long)read.Count));
        Assert.Equal(ImageFilterOutcome.NoMatch, (await Evaluate(png, field: "prompt")).Outcome);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(png, field: "prompt", presence: "absent")).Outcome);
    }

    [Fact]
    public async Task DuplicatesAndKeywordCaseDoNotChangeExactPresence()
    {
        byte[] png = Png(after: [TextChunk("tEXt", "Prompt", []), TextChunk("iTXt", "Prompt", [1])]);
        Assert.Equal(ImageFilterOutcome.NoMatch, (await Evaluate(png, field: "prompt")).Outcome);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(png, field: "custom", custom: "Prompt")).Outcome);
        byte[] latin1 = Png(after: [TextChunk("tEXt", "Création", [])]);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(latin1, field: "custom", custom: "Création")).Outcome);
    }

    [Theory]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    public async Task LargeCompressedValuesAreStreamedWithoutDecompressionOrLogging(string type)
    {
        // Deliberately not a valid compressed stream: presence makes no promise
        // about the text value. The parser checks headers/CRC and never inflates it.
        byte[] payload = Enumerable.Repeat((byte)0xff, 2 * 1024 * 1024 + 17).ToArray();
        byte[] png = Png(after: [TextChunk(type, "workflow", payload, compressed: true)]);
        Reader reader = new(png);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(reader, field: "workflow")).Outcome);
        Assert.All(reader.Reads, read => Assert.InRange(read.Count, 1, SdkLimits.MaximumImageChunkBytes));
        Assert.Contains(reader.Reads, read => read.Count == SdkLimits.MaximumImageChunkBytes);
        Assert.Equal(png.LongLength, reader.Reads.Sum(read => (long)read.Count));
    }

    [Fact]
    public async Task InternationalHeaderCanCrossReadBoundariesWithoutBufferingItsText()
    {
        byte[] translated = Encoding.UTF8.GetBytes(new string('x', 65518) + "😀" + new string('y', 70000));
        byte[] header = Encoding.Latin1.GetBytes("prompt\0").Concat(new byte[] { 0, 0 })
            .Concat(Encoding.ASCII.GetBytes("en-US\0")).Concat(translated).Append((byte)0).ToArray();
        byte[] png = Png(after: [Chunk("iTXt", header)]);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(png, field: "prompt")).Outcome);
    }

    [Theory]
    [InlineData(" t")]
    [InlineData("t ")]
    [InlineData("a  b")]
    [InlineData("")]
    [InlineData("é\u0100")]
    [InlineData("bad\u007f")]
    public async Task InvalidCustomKeywordsAreRuleErrorsWithoutReadingMedia(string keyword)
    {
        Reader reader = new(Png());
        Assert.Equal("invalid_rule_parameters", (await Evaluate(reader, field: "custom", custom: keyword)).ReasonCode);
        Assert.Empty(reader.Reads);
    }

    [Fact]
    public async Task CustomKeywordByteLimitsAreExact()
    {
        string key = new('x', 79);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(Png(after: [TextChunk("tEXt", key, [])]), field: "custom", custom: key)).Outcome);
        Assert.Equal("invalid_rule_parameters", (await Evaluate(Png(), field: "custom", custom: key + "x")).ReasonCode);
    }

    [Fact]
    public async Task CorruptLaterChunkCannotTurnAnEarlierMatchOrApparentAbsenceIntoAResult()
    {
        byte[] bad = TextChunk("tEXt", "other", [1]);
        bad[^1] ^= 1;
        byte[] png = Png(before: [TextChunk("tEXt", "parameters", [])], after: [bad]);
        Assert.Equal("invalid_png_structure", (await Evaluate(png)).ReasonCode);
        Assert.Equal("invalid_png_structure", (await Evaluate(png, field: "workflow", presence: "absent")).ReasonCode);
    }

    public static IEnumerable<object[]> InvalidTextHeaders()
    {
        yield return ["tEXt", Encoding.Latin1.GetBytes(new string('x', 80) + "\0")];
        yield return ["tEXt", Encoding.Latin1.GetBytes("parameters")];
        yield return ["tEXt", Encoding.Latin1.GetBytes(" bad\0")];
        yield return ["tEXt", Encoding.Latin1.GetBytes("bad \0")];
        yield return ["tEXt", Encoding.Latin1.GetBytes("bad  key\0")];
        yield return ["tEXt", new byte[] { 0 }];
        yield return ["tEXt", new byte[] { 127, 0 }];
        yield return ["zTXt", Encoding.Latin1.GetBytes("parameters\0")];
        yield return ["zTXt", Encoding.Latin1.GetBytes("parameters\0").Append((byte)1).ToArray()];
        yield return ["iTXt", Encoding.Latin1.GetBytes("parameters\0").Concat(new byte[] { 2, 0, 0, 0 }).ToArray()];
        yield return ["iTXt", Encoding.Latin1.GetBytes("parameters\0").Concat(new byte[] { 0, 1, 0, 0 }).ToArray()];
        yield return ["iTXt", Encoding.Latin1.GetBytes("parameters\0").Concat(new byte[] { 0, 0, 0 }).ToArray()];
        yield return ["iTXt", Encoding.Latin1.GetBytes("parameters\0").Concat(new byte[] { 0, 0, 255, 0, 0 }).ToArray()];
        yield return ["iTXt", Encoding.Latin1.GetBytes("parameters\0").Concat(new byte[] { 0, 0, 0, 0xc0, 0x80, 0 }).ToArray()];
        yield return ["iTXt", Encoding.Latin1.GetBytes("parameters\0").Concat(new byte[] { 0, 0, 0, 0xed, 0xa0, 0x80, 0 }).ToArray()];
        yield return ["iTXt", Encoding.Latin1.GetBytes("parameters\0").Concat(new byte[] { 0, 0, 0, 0xe2, 0 }).ToArray()];
    }

    [Theory]
    [MemberData(nameof(InvalidTextHeaders))]
    public async Task MalformedRelevantHeadersAreTechnicalErrors(string type, byte[] bytes)
    {
        ImageFilterResult result = await Evaluate(Png(after: [Chunk(type, bytes)]), presence: "absent");
        Assert.Equal(new(ImageFilterOutcome.Error, "invalid_png_structure",
            "PNG structure, text-field header or checksum is invalid or incomplete."), result);
    }

    [Fact]
    public async Task BrokenBoundsChecksumsAndStreamTerminationAreNeverAbsence()
    {
        byte[] good = Png(after: [TextChunk("tEXt", "other", [])]);
        var invalid = new List<byte[]>
        {
            good[..7],
            good[..^12], good.Concat(new byte[] { 0 }).ToArray(),
            Png(after: [Chunk("IHDR", new byte[13])]),
            Png(after: [Chunk("ABCD", [])]),
            Png(after: [TextChunk("tEXt", "other", []), Chunk("IDAT", [1])])
        };
        byte[] tooLong = good.ToArray(); BinaryPrimitives.WriteUInt32BigEndian(tooLong.AsSpan(8, 4), uint.MaxValue); invalid.Add(tooLong);
        foreach (int changedByte in new[] { 16, good.Length - 1 })
        { byte[] crc = good.ToArray(); crc[changedByte] ^= 1; invalid.Add(crc); }
        foreach (byte[] png in invalid)
            Assert.Equal("invalid_png_structure", (await Evaluate(png, presence: "absent")).ReasonCode);
    }

    [Fact]
    public async Task ActualBytesDetermineSupportAndExistingBudgetsRemainEnforced()
    {
        Assert.Equal(ImageFilterOutcome.Unavailable, (await Evaluate(Encoding.ASCII.GetBytes("not a PNG, regardless of MIME"))).Outcome);
        Assert.Equal(ImageFilterOutcome.Unavailable, (await Evaluate([])).Outcome);
        byte[] oversizedPixels = Png(width: 100000, height: 100000);
        Assert.Equal("image_pixel_limit", (await Evaluate(oversizedPixels)).ReasonCode);
        Reader reader = new(Png());
        ImageFilterRequest oversized = Request(reader.Bytes, "parameters", "present", null) with { OriginalSize = SdkLimits.MaximumImageBytes + 1 };
        Assert.Equal("image_byte_limit", (await new PngMetadataFilter().EvaluateAsync(oversized, Context(reader))).ReasonCode);
        Assert.Empty(reader.Reads);
    }

    [Fact]
    public async Task InterruptedAndMismatchedAssetReadsNeverYieldMatchOrAbsence()
    {
        foreach (string fault in new[] { "offset", "short", "end" })
        {
            Reader reader = new(Png()) { Fault = fault };
            Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(reader, presence: "absent")).Outcome);
        }
        using CancellationTokenSource cancellation = new();
        Reader cancelled = new(Png(after: [TextChunk("zTXt", "parameters", new byte[200000])]))
        { OnRead = count => { if (count == 10) cancellation.Cancel(); } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Evaluate(cancelled, token: cancellation.Token));
        Assert.Equal(10, cancelled.Reads.Count);
    }

    private static Task<ImageFilterResult> Evaluate(byte[] bytes, string field = "parameters", string presence = "present", string? custom = null) =>
        Evaluate(new Reader(bytes), field, presence, custom);
    private static Task<ImageFilterResult> Evaluate(Reader reader, string field = "parameters", string presence = "present", string? custom = null, CancellationToken token = default) =>
        new PngMetadataFilter().EvaluateAsync(Request(reader.Bytes, field, presence, custom), Context(reader), token);
    private static ImageFilterRequest Request(byte[] bytes, string field, string presence, string? custom)
    {
        List<SettingValue> values = [new("field", SettingKind.Enum, Text: field), new("presence", SettingKind.Enum, Text: presence)];
        if (custom != null) values.Add(new("customKeyword", SettingKind.Text, Text: custom));
        return new(PngMetadataFilter.FilterKey, new(new(new("fixture", "b", "1"), "media"), "1", 0,
            new("https://fixture.invalid/image.png"), MimeType: "image/png"), "input", "media", "one-asset", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), "rule", new("1", values), "build", "config", "grant");
    }
    private static PluginCallContext Context(Reader reader)
    {
        NoServices none = new(); SettingsSnapshot empty = new("1", []);
        return new(new("config", empty, new Dictionary<string, SettingsSnapshot>()), none, reader, none, none);
    }
    private sealed class Reader(byte[] bytes) : IImageAssetReader
    {
        internal byte[] Bytes { get; } = bytes;
        internal List<(long Offset, int Count)> Reads { get; } = [];
        internal string? Fault { get; init; }
        internal Action<int>? OnRead { get; init; }
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("one-asset", assetToken);
            Assert.InRange(count, 1, SdkLimits.MaximumImageChunkBytes);
            Assert.InRange(offset, 0, Bytes.Length - count);
            Reads.Add((offset, count)); OnRead?.Invoke(Reads.Count);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ImageAssetChunk(Fault == "offset" ? offset + 1 : offset,
                Bytes.AsSpan((int)offset, Fault == "short" ? count - 1 : count).ToArray(), Fault == "end" || offset + count == Bytes.Length));
        }
    }
    private sealed class NoServices : IHostHttp, IPluginResources, IPluginLog
    {
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Filter cannot use HTTP.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("Single-file filter needs no resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("Metadata values must not be logged.");
    }
    private static byte[] Png(byte[][]? before = null, byte[][]? after = null, uint width = 1, uint height = 1)
    {
        byte[] ihdr = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), height); ihdr[8] = 8; ihdr[9] = 6;
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionMode.Compress, leaveOpen: true)) zlib.Write(new byte[] { 0, 0, 0, 0, 255 });
        using MemoryStream png = new(); png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }); png.Write(Chunk("IHDR", ihdr));
        foreach (byte[] chunk in before ?? []) png.Write(chunk);
        png.Write(Chunk("IDAT", compressed.ToArray()));
        foreach (byte[] chunk in after ?? []) png.Write(chunk);
        png.Write(Chunk("IEND", [])); return png.ToArray();
    }
    private static byte[] TextChunk(string type, string keyword, byte[] value, bool compressed = false)
    {
        byte[] key = Encoding.Latin1.GetBytes(keyword).Append((byte)0).ToArray();
        byte[] header = type switch
        { "tEXt" => key, "zTXt" => key.Append((byte)0).ToArray(), "iTXt" => key.Concat(new byte[] { compressed ? (byte)1 : (byte)0, 0, 0, 0 }).ToArray(), _ => throw new ArgumentException() };
        return Chunk(type, header.Concat(value).ToArray());
    }
    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] chunk = new byte[data.Length + 12]; BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4); data.CopyTo(chunk, 8);
        uint crc = uint.MaxValue;
        foreach (byte value in chunk.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (int i = 0; i < 8; i++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(data.Length + 8, 4), ~crc); return chunk;
    }
}
