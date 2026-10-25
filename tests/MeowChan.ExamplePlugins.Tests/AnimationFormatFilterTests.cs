using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MeowChan.PluginSdk;

namespace MeowChan.ExamplePlugins.Tests;

public sealed class AnimationFormatFilterTests
{
    [Fact]
    public void DescriptionIsPassiveSdkOnlyAndSettingsAreValid()
    {
        var plugin = new AnimationFormatFilter();
        Assert.Empty(SdkValidation.ValidateDescriptions([plugin.Describe()]));
        FilterDescription filter = Assert.Single(plugin.Describe().Filters);
        Assert.Equal(FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, filter.RequiredAccess);
        Assert.Equal(new[] { "format", "animation" }, filter.Settings.Fields.Select(field => field.Key));
        Assert.DoesNotContain(plugin.GetType().Assembly.GetReferencedAssemblies(), reference => reference.Name is "libmeowchan" or "Avalonia" or "MeowChan.PluginHost");
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("gif")]
    [InlineData("webp")]
    public async Task ActualBytesDecideFormatDespiteMismatchingMimeAndExtension(string format)
    {
        byte[] bytes = format switch { "jpeg" => Jpeg(), "png" => Png(), "gif" => Gif(1), _ => Webp(0) };
        var result = await Evaluate(bytes, format, "any");
        Assert.Equal(ImageFilterOutcome.Match, result.Outcome);
        Assert.Equal(ImageFilterOutcome.NoMatch, (await Evaluate(bytes, format == "gif" ? "png" : "gif", "any")).Outcome);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(bytes, "any", "static")).Outcome);
        Assert.Equal(ImageFilterOutcome.NoMatch, (await Evaluate(bytes, "any", "animated")).Outcome);
    }

    [Theory]
    [InlineData("gif", 1, false)] [InlineData("gif", 2, true)]
    [InlineData("png", 1, false)] [InlineData("png", 2, true)]
    [InlineData("webp", 1, false)] [InlineData("webp", 2, true)]
    public async Task OneEvidencedFrameIsStaticAndTwoAreAnimated(string format, int frames, bool animated)
    {
        byte[] bytes = format switch { "gif" => Gif(frames), "png" => Png(frames), _ => Webp(frames) };
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(bytes, format, animated ? "animated" : "static")).Outcome);
        Assert.Equal(ImageFilterOutcome.NoMatch, (await Evaluate(bytes, format, animated ? "static" : "animated")).Outcome);
    }

    [Fact]
    public async Task ApngDefaultImageCanBeOutsideAnimationAndSequenceIsStillChecked()
    {
        byte[] bytes = Png(1, defaultOutsideAnimation: true);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(bytes, "png", "static")).Outcome);
        byte[] badSequence = Png(2, sequenceFault: true);
        Assert.Equal("invalid_container", (await Evaluate(badSequence, "png", "animated")).ReasonCode);
    }

    [Theory]
    [InlineData("gif")]
    [InlineData("png")]
    [InlineData("webp")]
    public async Task TruncatedContainerNeverBecomesStaticOrAnimated(string format)
    {
        byte[] complete = format switch { "gif" => Gif(2), "png" => Png(2), _ => Webp(2) };
        byte[] broken = complete[..^1];
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(broken, format, "static")).Outcome);
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(broken, format, "animated")).Outcome);
    }

    [Theory]
    [InlineData("jpeg")] [InlineData("png")] [InlineData("gif")] [InlineData("webp")]
    public async Task RecognizedButTruncatedHeadersAreErrorsRatherThanUnknownFormats(string format)
    {
        byte[] complete = format switch { "jpeg" => Jpeg(), "png" => Png(), "gif" => Gif(1), _ => Webp(0) };
        byte[] broken = complete[..(format == "jpeg" ? 7 : 10)];
        Assert.Equal("invalid_container", (await Evaluate(broken, format, "any")).ReasonCode);
    }

    [Fact]
    public async Task CanvasAndFrameBoundsRemainCheckedWithoutPixelDecoding()
    {
        byte[] header = [0, 0, 16, 1, 0, 0, 16, 1, 8, 6, 0, 0, 0]; // 4097² > SDK pixel limit.
        byte[] png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Concat(Chunk("IHDR", header)).ToArray();
        Assert.Equal("image_pixel_limit", (await Evaluate(png, "png", "any")).ReasonCode);
        byte[] gif = Gif(1);
        gif[24] = 2; // First image width exceeds the one-pixel logical screen.
        Assert.Equal("invalid_container", (await Evaluate(gif, "gif", "static")).ReasonCode);
        byte[] webp = Webp(1);
        int anmf = Encoding.ASCII.GetString(webp).IndexOf("ANMF", StringComparison.Ordinal);
        webp[anmf + 8 + 6] = 1; // ANMF width two vs canvas one.
        Assert.Equal("invalid_container", (await Evaluate(webp, "webp", "static")).ReasonCode);
    }

    [Fact]
    public async Task FormatOnlyScanDoesNotRequireAnAnimationScan()
    {
        byte[] incompleteGif = Gif(2)[..^1];
        var reader = new Bytes(incompleteGif);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(incompleteGif, "gif", "any", reader)).Outcome);
        Assert.True(reader.Reads.Sum(read => read.Count) < 32);
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(incompleteGif, "gif", "static")).Outcome);
    }

    [Fact]
    public async Task ApngDeclaredFramesAndControlCrcMustMatchActualFrames()
    {
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(Png(2, declaredFrames: 3), "png", "animated")).Outcome);
        byte[] corruptControl = Png(2);
        corruptControl[33 + 8] ^= 1;
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(corruptControl, "png", "animated")).Outcome);
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(Png(0, declaredFrames: 1), "png", "static")).Outcome);
    }

    [Fact]
    public async Task AnimationFlagWithoutFramesAndExtremeChunkLengthAreErrors()
    {
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(Webp(0, animationFlagOnly: true), "webp", "static")).Outcome);
        byte[] png = Png();
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(33, 4), uint.MaxValue);
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(png, "png", "static")).Outcome);
        byte[] webp = Webp(2);
        BinaryPrimitives.WriteUInt32LittleEndian(webp.AsSpan(16, 4), uint.MaxValue);
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(webp, "webp", "animated")).Outcome);
    }

    [Fact]
    public async Task OptionalUnknownBlocksAndLargeSkippedPayloadKeepBoundaries()
    {
        byte[] payload = new byte[65537];
        byte[] bytes = Png(extra: Chunk("teSt", payload));
        var reader = new Bytes(bytes);
        Assert.Equal(ImageFilterOutcome.Match, (await Evaluate(bytes, "png", "static", reader)).Outcome);
        Assert.All(reader.Reads, read => Assert.InRange(read.Count, 1, SdkLimits.MaximumImageChunkBytes));
        Assert.True(reader.Reads.Sum(read => read.Count) < 1024);
        byte[] withCritical = Png(extra: Chunk("TeST", [1]));
        Assert.Equal(ImageFilterOutcome.Error, (await Evaluate(withCritical, "png", "static")).Outcome);
    }

    [Fact]
    public async Task UnknownFormatIsUnavailableAndOversizeIsAnExplicitTechnicalError()
    {
        Assert.Equal(ImageFilterOutcome.Unavailable, (await Evaluate(Encoding.ASCII.GetBytes("arbitrary unsupported data"))).Outcome);
        var reader = new Bytes(Gif(1));
        ImageFilterResult result = await new AnimationFormatFilter().EvaluateAsync(Request(reader.Data, "any", "any") with { OriginalSize = SdkLimits.MaximumImageBytes + 1 }, Context(reader));
        Assert.Equal("image_byte_limit", result.ReasonCode);
        Assert.Empty(reader.Reads);
    }

    [Fact]
    public async Task ExcessiveBlocksDoNotFallBackToAStaticDecision()
    {
        byte[] repeated = Enumerable.Range(0, AnimationFormatFilter.MaximumContainerBlocks + 1)
            .SelectMany(_ => new byte[] { 33, 254, 0 }).ToArray();
        byte[] gif = Gif(1);
        byte[] bytes = gif[..19].Concat(repeated).Concat(gif[19..]).ToArray();
        ImageFilterResult result = await Evaluate(bytes, "gif", "static");
        Assert.Equal(ImageFilterOutcome.Error, result.Outcome);
        Assert.Equal("container_scan_limit", result.ReasonCode);
    }

    [Fact]
    public async Task CancellationDuringAReadPropagatesAndCannotProduceAMatch()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new Bytes(Png()) { BeforeRead = () => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Evaluate(reader.Data, "png", "static", reader, cancellation.Token));
    }

    [Fact]
    public async Task InvalidRuleValuesAndShortHostChunksAreTechnicalErrors()
    {
        var reader = new Bytes(Gif(1));
        Assert.Equal("invalid_rule_parameters", (await Evaluate(reader.Data, "invented", "static", reader)).ReasonCode);
        Assert.Empty(reader.Reads);
        reader.ShortRead = true;
        Assert.Equal("invalid_container", (await Evaluate(reader.Data, "gif", "static", reader)).ReasonCode);
    }

    private static Task<ImageFilterResult> Evaluate(byte[] bytes, string format = "any", string animation = "any", Bytes? reader = null, CancellationToken cancellationToken = default)
    {
        reader ??= new Bytes(bytes);
        return new AnimationFormatFilter().EvaluateAsync(Request(bytes, format, animation), Context(reader), cancellationToken);
    }
    private static ImageFilterRequest Request(byte[] bytes, string format, string animation) => new(AnimationFormatFilter.FilterKey,
        new(new(new("fixture", "b", "1"), "m"), "1", 0, new Uri("https://fixture.invalid/wrong.txt"), "wrong.txt", "text/plain"),
        "input", "media", "asset", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "rule",
        new("1", [new("format", SettingKind.Enum, Text: format), new("animation", SettingKind.Enum, Text: animation)]), "build", "config", "grant");
    private static PluginCallContext Context(Bytes reader) => new(new("1", new("1", []), new Dictionary<string, SettingsSnapshot>()), new Forbidden(), reader, new Forbidden(), new Forbidden());
    private sealed class Bytes(byte[] data) : IImageAssetReader
    {
        public byte[] Data { get; } = data;
        public List<(long Offset, int Count)> Reads { get; } = [];
        public Action? BeforeRead { get; init; }
        public bool ShortRead { get; set; }
        public Task<ImageAssetChunk> ReadImageAsync(string assetToken, long offset, int count, CancellationToken cancellationToken = default)
        {
            Assert.Equal("asset", assetToken); Assert.InRange(count, 1, SdkLimits.MaximumImageChunkBytes);
            BeforeRead?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            Assert.InRange(offset, 0, Data.Length - count);
            Reads.Add((offset, count));
            return Task.FromResult(new ImageAssetChunk(offset, Data.AsSpan((int)offset, ShortRead ? count - 1 : count).ToArray(), offset + count == Data.Length));
        }
    }
    private sealed class Forbidden : IHostHttp, IPluginResources, IPluginLog
    {
        public Task<HostHttpResult> SendAsync(HostHttpRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No network access.");
        public string? ResolvePath(string logicalName) => throw new InvalidOperationException("No resources.");
        public void Write(PluginLogLevel level, string message) => throw new InvalidOperationException("No content logging.");
    }
    private static byte[] Jpeg() => [255, 216, 255, 192, 0, 11, 8, 0, 1, 0, 1, 1, 1, 17, 0, 255, 217];
    private static byte[] Gif(int frames)
    {
        List<byte> bytes = [.. Encoding.ASCII.GetBytes("GIF89a"), 1, 0, 1, 0, 128, 0, 0, 0, 0, 0, 255, 255, 255];
        for (int i = 0; i < frames; i++) bytes.AddRange(new byte[] { 44, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 68, 1, 0 });
        bytes.Add(59); return bytes.ToArray();
    }
    private static byte[] Png(int frames = 0, bool defaultOutsideAnimation = false, uint? declaredFrames = null, bool sequenceFault = false, byte[]? extra = null)
    {
        List<byte> bytes = [137, 80, 78, 71, 13, 10, 26, 10];
        bytes.AddRange(Chunk("IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0]));
        if (frames > 0 || declaredFrames != null)
        {
            byte[] actl = new byte[8]; BinaryPrimitives.WriteUInt32BigEndian(actl, declaredFrames ?? (uint)frames);
            bytes.AddRange(Chunk("acTL", actl));
        }
        uint sequence = 0;
        if (defaultOutsideAnimation) bytes.AddRange(Chunk("IDAT", [1]));
        for (int frame = 0; frame < frames; frame++)
        {
            byte[] control = new byte[26]; BinaryPrimitives.WriteUInt32BigEndian(control, sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(4), 1); BinaryPrimitives.WriteUInt32BigEndian(control.AsSpan(8), 1);
            bytes.AddRange(Chunk("fcTL", control));
            if (frame == 0 && !defaultOutsideAnimation) bytes.AddRange(Chunk("IDAT", [1]));
            else
            {
                byte[] data = new byte[5]; BinaryPrimitives.WriteUInt32BigEndian(data, sequenceFault ? 99 : sequence++); data[4] = 1;
                bytes.AddRange(Chunk("fdAT", data));
            }
        }
        if (frames == 0 && !defaultOutsideAnimation) bytes.AddRange(Chunk("IDAT", [1]));
        if (extra != null) bytes.AddRange(extra);
        bytes.AddRange(Chunk("IEND", [])); return bytes.ToArray();
    }
    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] bytes = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)data.Length); Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4); data.CopyTo(bytes, 8);
        uint crc = uint.MaxValue;
        foreach (byte value in bytes.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (int i = 0; i < 8; i++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), ~crc); return bytes;
    }
    private static byte[] Webp(int animationFrames, bool animationFlagOnly = false)
    {
        List<byte> chunks = [];
        if (animationFrames > 0 || animationFlagOnly)
        {
            chunks.AddRange(RiffChunk("VP8X", [2, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
            chunks.AddRange(RiffChunk("ANIM", new byte[6]));
            for (int i = 0; i < animationFrames; i++) chunks.AddRange(RiffChunk("ANMF", new byte[16].Concat(RiffChunk("VP8L", [47, 0, 0, 0, 0])).ToArray()));
        }
        else chunks.AddRange(RiffChunk("VP8L", [47, 0, 0, 0, 0]));
        byte[] size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(chunks.Count + 4));
        return Encoding.ASCII.GetBytes("RIFF").Concat(size).Concat(Encoding.ASCII.GetBytes("WEBP")).Concat(chunks).ToArray();
    }
    private static byte[] RiffChunk(string type, byte[] payload)
    {
        byte[] length = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)payload.Length);
        return Encoding.ASCII.GetBytes(type).Concat(length).Concat(payload).Concat(payload.Length % 2 == 0 ? Array.Empty<byte>() : new byte[] { 0 }).ToArray();
    }
}
