using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.ExamplePlugins;

/// <summary>Bounded original-container inspection, never pixel decoding or provider-MIME inference.</summary>
public sealed class AnimationFormatFilter : IImageFilterPlugin, IPluginSettingsValidator
{
    public const string PluginId = "meowchan.filter.animation-format";
    public const string FilterKey = "animation-format";
    public const int MaximumContainerBlocks = 16384;
    private static readonly SettingsSchema Schema = new("1", new SettingDefinition[] {
        new("format", SettingScope.FilterRule, SettingKind.Enum, "Actual file format", "Read from the original bytes, independently of the filename or reported MIME type.",
            Default: new("format", SettingKind.Enum, Text: "any"), Options: new SettingOption[] {
                new("any", "Any supported format"), new("jpeg", "JPEG"), new("png", "PNG (including APNG)"), new("gif", "GIF"), new("webp", "WebP") }),
        new("animation", SettingScope.FilterRule, SettingKind.Enum, "Animation", "Animated means more than one structurally evidenced frame. This inspects containers, not pixel decodability.",
            Default: new("animation", SettingKind.Enum, Text: "any"), Options: new SettingOption[] {
                new("any", "Any"), new("static", "Static (one frame)"), new("animated", "Animated (multiple frames)") })
    });

    public PluginDescription Describe() => new(PluginId, "File format and animation", "1.0.0", SdkContract.Version,
        "Select JPEG, PNG/APNG, GIF and WebP originals by their actual container format and evidenced frame count.",
        Array.Empty<SourceDescription>(), new FilterDescription[] {
            new(FilterKey, "File format and animation", "Bounded container inspection. Unknown formats and incomplete animation scans are technical omissions.",
                FilterDataAccess.CandidateMetadata | FilterDataAccess.OriginalImage, Schema,
                new[] { "image/jpeg", "image/png", "image/gif", "image/webp" }) },
        new SettingsSchema("1", Array.Empty<SettingDefinition>()), new InformationEntry[] {
            new("limits", "Inspection limits", InformationKind.Text, "At most 16,384 container blocks and the existing SDK byte budget; no rendering, native decoder, network request or image copy.") }, "MeowChan");

    public Task<IReadOnlyList<FieldValidationError>> ValidateSettingsAsync(SettingsValidationRequest request,
        PluginCallContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FieldValidationError>>(request.Scope == SettingScope.FilterRule
            ? Validate(request.Settings) : Array.Empty<FieldValidationError>());
    }

    private static FieldValidationError[] Validate(SettingsSnapshot settings) =>
        SdkValidation.ValidateSettings(Schema, settings, SettingScope.FilterRule)
            .Select(issue => new FieldValidationError(issue.Path, issue.Code, issue.Message)).ToArray();

    public async Task<ImageFilterResult> EvaluateAsync(ImageFilterRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.FilterKey != FilterKey) return new(ImageFilterOutcome.Unavailable, "unknown_filter");
        if (request.OriginalSize < 0) return new(ImageFilterOutcome.Error, "invalid_asset_length");
        if (request.OriginalSize > SdkLimits.MaximumImageBytes) return new(ImageFilterOutcome.Error, "image_byte_limit");
        FieldValidationError[] errors = Validate(request.RuleSettings);
        if (errors.Length != 0) return new(ImageFilterOutcome.Error, "invalid_rule_parameters", errors[0].Message);
        var values = request.RuleSettings.Values.ToDictionary(value => value.Key, StringComparer.Ordinal);
        string format = values.GetValueOrDefault("format")?.Text ?? "any";
        string animation = values.GetValueOrDefault("animation")?.Text ?? "any";
        try
        {
            Inspector inspector = new(context.Images, request.AssetToken, request.OriginalSize, cancellationToken);
            string actual = await inspector.IdentifyAsync().ConfigureAwait(false);
            if (format != "any" && format != actual) return new(ImageFilterOutcome.NoMatch, "format_mismatch");
            if (animation == "any") return new(ImageFilterOutcome.Match, "format_match");
            bool animated = await inspector.IsAnimatedAsync(actual).ConfigureAwait(false);
            return animated == (animation == "animated")
                ? new(ImageFilterOutcome.Match, "format_animation_match")
                : new(ImageFilterOutcome.NoMatch, "animation_mismatch");
        }
        catch (UnsupportedFormatException) { return new(ImageFilterOutcome.Unavailable, "unsupported_format", "The original is not a supported JPEG, PNG/APNG, GIF or WebP container."); }
        catch (ScanLimitException) { return new(ImageFilterOutcome.Error, "container_scan_limit", "Container inspection reached its bounded block or byte limit."); }
        catch (PixelLimitException) { return new(ImageFilterOutcome.Error, "image_pixel_limit", "The container canvas exceeds the existing pixel limit."); }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or OverflowException)
        { return new(ImageFilterOutcome.Error, "invalid_container", "Relevant image-container structure is incomplete or inconsistent."); }
        catch (IOException) { return new(ImageFilterOutcome.Error, "asset_read_failed", "The original bytes could not be read."); }
    }

    private sealed class UnsupportedFormatException : Exception { }
    private sealed class ScanLimitException : Exception { }
    private sealed class PixelLimitException : Exception { }
    private sealed class Inspector(IImageAssetReader reader, string token, long size, CancellationToken cancellation)
    {
        private int _blocks;
        private long _bytesRead;
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        private void Block()
        {
            cancellation.ThrowIfCancellationRequested();
            if (++_blocks > MaximumContainerBlocks) throw new ScanLimitException();
        }
        private void Range(long offset, long count)
        {
            if (offset < 0 || count < 0 || offset > size || count > size - offset) throw new InvalidDataException();
        }
        private async Task<byte[]> Read(long offset, int count)
        {
            cancellation.ThrowIfCancellationRequested();
            Range(offset, count);
            if (count == 0) return Array.Empty<byte>();
            if (count > SdkLimits.MaximumImageChunkBytes || _bytesRead > SdkLimits.MaximumImageBytes - count) throw new ScanLimitException();
            _bytesRead += count;
            ImageAssetChunk chunk = await reader.ReadImageAsync(token, offset, count, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (chunk.Offset != offset || chunk.Bytes.Length != count || chunk.IsEnd != (offset + count == size)) throw new InvalidDataException();
            return chunk.Bytes;
        }
        private static uint Big(byte[] bytes, int at = 0) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at, 4));
        private static uint Little(byte[] bytes, int at = 0) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
        private static int Short(byte[] bytes, int at) => bytes[at] | bytes[at + 1] << 8;
        private static int Triple(byte[] bytes, int at) => bytes[at] | bytes[at + 1] << 8 | bytes[at + 2] << 16;
        private static string Code(byte[] bytes, int at) => Encoding.ASCII.GetString(bytes, at, 4);
        private static void Canvas(long width, long height)
        {
            if (width <= 0 || height <= 0) throw new InvalidDataException();
            if (width > SdkLimits.MaximumDecodedPixels / height) throw new PixelLimitException();
        }
        private static void Crc(byte[] typeAndData, uint expected)
        {
            uint crc = uint.MaxValue;
            foreach (byte value in typeAndData)
            {
                crc ^= value;
                for (int i = 0; i < 8; i++) crc = crc >> 1 ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
            }
            if (~crc != expected) throw new InvalidDataException();
        }
        private async Task<byte[]> PngControl(long start, int length)
        {
            byte[] body = await Read(start + 4, length + 8).ConfigureAwait(false); // type, data, CRC
            Crc(body.AsSpan(0, length + 4).ToArray(), Big(body, length + 4));
            return body.AsSpan(4, length).ToArray();
        }
        private async Task<byte[]> PngHeader()
        {
            byte[] header = await Read(0, 33).ConfigureAwait(false);
            if (!header.AsSpan(0, 8).SequenceEqual(PngSignature) || Big(header, 8) != 13 || Code(header, 12) != "IHDR") throw new InvalidDataException();
            Crc(header.AsSpan(12, 17).ToArray(), Big(header, 29));
            Canvas(Big(header, 16), Big(header, 20));
            byte depth = header[24], color = header[25];
            bool validDepth = color switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => depth is 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => false };
            if (!validDepth || header[26] != 0 || header[27] != 0 || header[28] > 1) throw new InvalidDataException();
            return header;
        }
        private async Task<byte[]> GifHeader()
        {
            byte[] header = await Read(0, 13).ConfigureAwait(false);
            if (Encoding.ASCII.GetString(header, 0, 6) is not ("GIF87a" or "GIF89a")) throw new InvalidDataException();
            Canvas(Short(header, 6), Short(header, 8));
            Range(13, (header[10] & 128) != 0 ? 3L * (2 << (header[10] & 7)) : 0);
            return header;
        }
        private async Task<(int Width, int Height)> JpegHeader()
        {
            long at = 2;
            while (at < size)
            {
                Block();
                if ((await Read(at++, 1).ConfigureAwait(false))[0] != 255) throw new InvalidDataException();
                byte marker;
                do { Block(); marker = (await Read(at++, 1).ConfigureAwait(false))[0]; } while (marker == 255);
                if (marker is 0 or 0xd8 or 0xd9 or 0xda || marker is >= 0xd0 and <= 0xd7) throw new InvalidDataException();
                if (marker == 1) continue;
                byte[] lengthBytes = await Read(at, 2).ConfigureAwait(false);
                int length = lengthBytes[0] << 8 | lengthBytes[1];
                if (length < 2) throw new InvalidDataException();
                Range(at, length);
                if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                {
                    if (length < 11) throw new InvalidDataException();
                    byte[] frame = await Read(at + 2, 6).ConfigureAwait(false);
                    int height = frame[1] << 8 | frame[2], width = frame[3] << 8 | frame[4];
                    if (frame[0] is not (8 or 12 or 16) || frame[5] == 0 || length != 8 + 3 * frame[5]) throw new InvalidDataException();
                    Canvas(width, height);
                    return (width, height);
                }
                at += length;
            }
            throw new InvalidDataException();
        }
        private async Task<(string Type, uint Length)> WebpChunk(long at, long end)
        {
            Block();
            if (at > end - 8) throw new InvalidDataException();
            byte[] header = await Read(at, 8).ConfigureAwait(false);
            uint length = Little(header, 4);
            if ((long)length + (length & 1) > end - at - 8) throw new InvalidDataException();
            if ((length & 1) != 0 && (await Read(at + 8 + length, 1).ConfigureAwait(false))[0] != 0) throw new InvalidDataException();
            return (Code(header, 0), length);
        }
        private async Task<(int Width, int Height)> WebpCodec(string type, long data, uint length)
        {
            if (type == "VP8 ")
            {
                if (length < 10) throw new InvalidDataException();
                byte[] b = await Read(data, 10).ConfigureAwait(false);
                if ((b[0] & 1) != 0 || b[3] != 157 || b[4] != 1 || b[5] != 42) throw new InvalidDataException();
                int width = Short(b, 6) & 16383, height = Short(b, 8) & 16383;
                Canvas(width, height); return (width, height);
            }
            if (type == "VP8L")
            {
                if (length < 5) throw new InvalidDataException();
                byte[] b = await Read(data, 5).ConfigureAwait(false);
                if (b[0] != 47 || (b[4] & 224) != 0) throw new InvalidDataException();
                int width = 1 + (b[1] | (b[2] & 63) << 8), height = 1 + (b[2] >> 6 | b[3] << 2 | (b[4] & 15) << 10);
                Canvas(width, height); return (width, height);
            }
            throw new InvalidDataException();
        }
        private async Task<byte[]> WebpExtended(long data, uint length)
        {
            if (length != 10) throw new InvalidDataException();
            byte[] b = await Read(data, 10).ConfigureAwait(false);
            if ((b[0] & 193) != 0 || b[1] != 0 || b[2] != 0 || b[3] != 0) throw new InvalidDataException();
            Canvas(Triple(b, 4) + 1, Triple(b, 7) + 1); return b;
        }
        public async Task<string> IdentifyAsync()
        {
            byte[] first = await Read(0, (int)Math.Min(size, 12)).ConfigureAwait(false);
            if (first.Length >= 2 && first[0] == 255 && first[1] == 216) { await JpegHeader().ConfigureAwait(false); return "jpeg"; }
            if (first.Length >= 2 && first[0] == 137 && first[1] == 80) { await PngHeader().ConfigureAwait(false); return "png"; }
            if (first.Length >= 3 && Encoding.ASCII.GetString(first, 0, 3) == "GIF") { await GifHeader().ConfigureAwait(false); return "gif"; }
            if (first.Length >= 4 && Code(first, 0) == "RIFF")
            {
                if (first.Length < 12) throw new InvalidDataException();
                if (Code(first, 8) != "WEBP") throw new UnsupportedFormatException();
                if ((long)Little(first, 4) + 8 != size) throw new InvalidDataException();
                (string type, uint length) = await WebpChunk(12, size).ConfigureAwait(false);
                if (type == "VP8X") await WebpExtended(20, length).ConfigureAwait(false);
                else await WebpCodec(type, 20, length).ConfigureAwait(false);
                return "webp";
            }
            throw new UnsupportedFormatException();
        }
        public Task<bool> IsAnimatedAsync(string format) => format switch {
            "png" => PngAnimation(), "gif" => GifAnimation(), "webp" => WebpAnimation(), _ => Task.FromResult(false)
        };
        private async Task<bool> PngAnimation()
        {
            byte[] header = await PngHeader().ConfigureAwait(false);
            uint canvasWidth = Big(header, 16), canvasHeight = Big(header, 20);
            long at = 33;
            uint? declaredFrames = null;
            ulong sequence = 0;
            int frames = 0;
            bool idat = false, idatPayload = false, idatEnded = false, frameControl = false, frameData = false, frameUsesIdat = false, palette = false;
            while (at < size)
            {
                Block();
                byte[] chunk = await Read(at, 8).ConfigureAwait(false);
                uint length = Big(chunk);
                string type = Code(chunk, 4);
                if (length > int.MaxValue || length > size - at - 12) throw new InvalidDataException();
                if (!type.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') || type[2] is >= 'a' and <= 'z') throw new InvalidDataException();
                long next = at + 12 + length;
                if (idat && type != "IDAT") idatEnded = true;
                switch (type)
                {
                    case "IHDR": throw new InvalidDataException();
                    case "PLTE":
                        if (palette || idat || header[25] is 0 or 4 || length == 0 || length > 768 || length % 3 != 0
                            || header[25] == 3 && length / 3 > 1 << header[24]) throw new InvalidDataException();
                        palette = true; break;
                    case "acTL":
                        if (declaredFrames != null || idat || length != 8) throw new InvalidDataException();
                        declaredFrames = Big(await PngControl(at, 8).ConfigureAwait(false));
                        if (declaredFrames == 0) throw new InvalidDataException();
                        if (declaredFrames > MaximumContainerBlocks) throw new ScanLimitException();
                        break;
                    case "fcTL":
                        if (declaredFrames == null || length != 26 || frameControl && !frameData) throw new InvalidDataException();
                        byte[] control = await PngControl(at, 26).ConfigureAwait(false);
                        if (Big(control) != sequence++ || sequence > uint.MaxValue) throw new InvalidDataException();
                        uint width = Big(control, 4), height = Big(control, 8), x = Big(control, 12), y = Big(control, 16);
                        if (width == 0 || height == 0 || width > canvasWidth || height > canvasHeight
                            || x > canvasWidth - width || y > canvasHeight - height || control[24] > 2 || control[25] > 1) throw new InvalidDataException();
                        if (!idat && (x != 0 || y != 0 || width != canvasWidth || height != canvasHeight)) throw new InvalidDataException();
                        frameControl = true; frameData = false; frameUsesIdat = !idat; frames++; break;
                    case "IDAT":
                        if (idatEnded || header[25] == 3 && !palette) throw new InvalidDataException();
                        idat = true; idatPayload |= length > 0;
                        if (frameControl && length > 0) frameData = true;
                        break;
                    case "fdAT":
                        if (!idat || !frameControl || declaredFrames == null || length < 5 || frameUsesIdat) throw new InvalidDataException();
                        if (Big(await Read(at + 8, 4).ConfigureAwait(false)) != sequence++ || sequence > uint.MaxValue) throw new InvalidDataException();
                        frameData = true; break;
                    case "IEND":
                        if (length != 0 || !idatPayload || next != size || frameControl && !frameData) throw new InvalidDataException();
                        await PngControl(at, 0).ConfigureAwait(false);
                        if (declaredFrames != null && (frames != declaredFrames || !frameControl)) throw new InvalidDataException();
                        return frames > 1;
                    default:
                        if (type[0] is >= 'A' and <= 'Z') throw new InvalidDataException();
                        break;
                }
                at = next;
            }
            throw new InvalidDataException();
        }
        private async Task<long> GifSubBlocks(long at, bool requirePayload)
        {
            long payload = 0;
            while (true)
            {
                Block();
                int length = (await Read(at++, 1).ConfigureAwait(false))[0];
                if (length == 0)
                {
                    if (requirePayload && payload == 0) throw new InvalidDataException();
                    return at;
                }
                Range(at, length); payload += length; at += length;
            }
        }
        private async Task<bool> GifAnimation()
        {
            byte[] header = await GifHeader().ConfigureAwait(false);
            int width = Short(header, 6), height = Short(header, 8), frames = 0;
            long at = 13 + ((header[10] & 128) != 0 ? 3 * (2 << (header[10] & 7)) : 0);
            while (at < size)
            {
                Block();
                byte marker = (await Read(at++, 1).ConfigureAwait(false))[0];
                if (marker == 59)
                {
                    if (frames == 0 || at != size) throw new InvalidDataException();
                    return frames > 1;
                }
                if (marker == 33)
                {
                    byte label = (await Read(at++, 1).ConfigureAwait(false))[0];
                    if (label == 249)
                    {
                        byte[] control = await Read(at, 6).ConfigureAwait(false);
                        if (control[0] != 4 || control[5] != 0 || (control[1] & 224) != 0 || ((control[1] >> 2) & 7) > 3) throw new InvalidDataException();
                        at += 6;
                    }
                    else
                    {
                        if (label is 1 or 255)
                        {
                            int fixedLength = (await Read(at++, 1).ConfigureAwait(false))[0];
                            if (fixedLength != (label == 1 ? 12 : 11)) throw new InvalidDataException();
                            Range(at, fixedLength); at += fixedLength;
                        }
                        at = await GifSubBlocks(at, false).ConfigureAwait(false);
                    }
                    continue;
                }
                if (marker != 44) throw new InvalidDataException();
                byte[] image = await Read(at, 9).ConfigureAwait(false); at += 9;
                int x = Short(image, 0), y = Short(image, 2), fw = Short(image, 4), fh = Short(image, 6);
                if (fw == 0 || fh == 0 || x > width - fw || y > height - fh || (image[8] & 24) != 0) throw new InvalidDataException();
                long paletteLength = (image[8] & 128) != 0 ? 3 * (2 << (image[8] & 7)) : 0;
                if (paletteLength == 0 && (header[10] & 128) == 0) throw new InvalidDataException();
                Range(at, paletteLength); at += paletteLength;
                byte lzw = (await Read(at++, 1).ConfigureAwait(false))[0];
                if (lzw < 2 || lzw > 8) throw new InvalidDataException();
                at = await GifSubBlocks(at, true).ConfigureAwait(false); frames++;
            }
            throw new InvalidDataException();
        }
        private async Task<bool> WebpFrame(long data, uint length, int canvasWidth, int canvasHeight)
        {
            if (length < 16) throw new InvalidDataException();
            byte[] frame = await Read(data, 16).ConfigureAwait(false);
            int x = Triple(frame, 0) * 2, y = Triple(frame, 3) * 2, width = Triple(frame, 6) + 1, height = Triple(frame, 9) + 1;
            if (x > canvasWidth - width || y > canvasHeight - height || (frame[15] & 252) != 0) throw new InvalidDataException();
            long at = data + 16, end = data + length;
            bool image = false, alpha = false;
            while (at < end)
            {
                (string type, uint count) = await WebpChunk(at, end).ConfigureAwait(false);
                if (type == "ALPH")
                {
                    if (alpha || image || count == 0) throw new InvalidDataException();
                    alpha = true;
                }
                else if (type is "VP8 " or "VP8L")
                {
                    if (image || alpha && type == "VP8L") throw new InvalidDataException();
                    var dimensions = await WebpCodec(type, at + 8, count).ConfigureAwait(false);
                    if (dimensions.Width != width || dimensions.Height != height) throw new InvalidDataException();
                    image = true;
                }
                else throw new InvalidDataException();
                at += 8 + count + (count & 1);
            }
            if (!image) throw new InvalidDataException();
            return true;
        }
        private async Task<bool> WebpAnimation()
        {
            long at = 12;
            bool extended = false, animated = false, animationControl = false, image = false, alpha = false;
            int canvasWidth = 0, canvasHeight = 0, frames = 0;
            while (at < size)
            {
                (string type, uint length) = await WebpChunk(at, size).ConfigureAwait(false);
                switch (type)
                {
                    case "VP8X":
                        if (at != 12 || extended) throw new InvalidDataException();
                        byte[] extension = await WebpExtended(at + 8, length).ConfigureAwait(false);
                        extended = true; animated = (extension[0] & 2) != 0;
                        canvasWidth = Triple(extension, 4) + 1; canvasHeight = Triple(extension, 7) + 1;
                        break;
                    case "ANIM":
                        if (!extended || !animated || animationControl || frames != 0 || image || length != 6) throw new InvalidDataException();
                        animationControl = true; break;
                    case "ANMF":
                        if (!animated || !animationControl || image) throw new InvalidDataException();
                        await WebpFrame(at + 8, length, canvasWidth, canvasHeight).ConfigureAwait(false); frames++;
                        break;
                    case "ALPH":
                        if (!extended || animated || alpha || image || length == 0) throw new InvalidDataException();
                        alpha = true; break;
                    case "VP8 ": case "VP8L":
                        if (animated || image || alpha && type == "VP8L") throw new InvalidDataException();
                        var dimensions = await WebpCodec(type, at + 8, length).ConfigureAwait(false);
                        if (extended && (dimensions.Width != canvasWidth || dimensions.Height != canvasHeight)) throw new InvalidDataException();
                        image = true; break;
                    default: break; // Bounded optional RIFF metadata/unknown chunks are not frames.
                }
                at += 8 + length + (length & 1);
            }
            if (animated ? !animationControl || frames == 0 : !image) throw new InvalidDataException();
            return frames > 1;
        }
    }
}
