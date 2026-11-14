using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Storage;

/// <summary>
/// Validates the small amount of media type information that is available at the HTTP boundary.
/// Provider extensions are not trusted as bytes, but they provide the expected type for the bounded
/// original-download path. The response type and a format signature must agree before MediaStore can
/// publish the file.
/// </summary>
internal static class MediaMimeValidation
{
    private const int MaximumSignatureBytes = 12;

    internal static string? FromExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".webm" => "video/webm",
        ".mp4" or ".m4v" => "video/mp4",
        ".zip" => "application/zip",
        ".pdf" => "application/pdf",
        _ => null
    };

    internal static void ValidateContentType(string? expected, string? actual)
    {
        if (expected == null) return;
        string? normalized = Normalize(actual);
        if (normalized == null || !string.Equals(expected, normalized, StringComparison.Ordinal))
            throw new InvalidDataException($"The media Content-Type '{actual ?? "<missing>"}' does not match the expected MIME type '{expected}'.");
    }

    internal static Stream WrapBody(Stream body, string expected)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);
        return new SignatureValidatingStream(body, expected);
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().ToLowerInvariant();
        // Some servers still emit this non-standard alias for JPEG. It is not accepted as a
        // different format, but the canonical expected type remains image/jpeg.
        return normalized == "image/jpg" ? "image/jpeg" : normalized;
    }

    private static bool SignatureMatches(string expected, ReadOnlySpan<byte> bytes) => expected switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[..3].SequenceEqual(new byte[] { 0xff, 0xd8, 0xff }),
        "image/png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
        "image/gif" => bytes.Length >= 6 && (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8)),
        "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        "image/avif" => IsFtyp(bytes, "avif"u8) || IsFtyp(bytes, "avis"u8),
        "video/webm" => bytes.Length >= 4 && bytes[..4].SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 }),
        "video/mp4" => IsFtyp(bytes, "isom"u8) || IsFtyp(bytes, "iso2"u8) || IsFtyp(bytes, "mp41"u8) || IsFtyp(bytes, "mp42"u8) || IsFtyp(bytes, "avc1"u8) || IsFtyp(bytes, "M4V "u8),
        "application/zip" => bytes.Length >= 4 && (bytes[..4].SequenceEqual(new byte[] { 0x50, 0x4b, 0x03, 0x04 })
            || bytes[..4].SequenceEqual(new byte[] { 0x50, 0x4b, 0x05, 0x06 }) || bytes[..4].SequenceEqual(new byte[] { 0x50, 0x4b, 0x07, 0x08 })),
        "application/pdf" => bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8),
        _ => true
    };

    private static bool IsFtyp(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> brand)
        => bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8) && bytes[8..12].SequenceEqual(brand);

    private static int RequiredBytes(string expected) => expected switch
    {
        "image/jpeg" => 3,
        "image/png" => 8,
        "image/gif" => 6,
        "image/webp" or "image/avif" or "video/mp4" => 12,
        "video/webm" or "application/zip" => 4,
        "application/pdf" => 5,
        _ => 0
    };

    private sealed class SignatureValidatingStream(Stream inner, string expected) : Stream
    {
        private readonly byte[] _prefix = new byte[MaximumSignatureBytes];
        private int _prefixLength;
        private bool _validated;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0) return 0;
            int read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            ValidateRead(buffer.Span[..read], read == 0);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (count == 0) return 0;
            int read = inner.Read(buffer, offset, count);
            ValidateRead(buffer.AsSpan(offset, read), read == 0);
            return read;
        }

        private void ValidateRead(ReadOnlySpan<byte> read, bool endOfStream)
        {
            if (_validated) return;
            int copy = Math.Min(read.Length, _prefix.Length - _prefixLength);
            read[..copy].CopyTo(_prefix.AsSpan(_prefixLength));
            _prefixLength += copy;
            int required = RequiredBytes(expected);
            if (required == 0) { _validated = true; return; }
            if (_prefixLength >= required || endOfStream)
            {
                if (!SignatureMatches(expected, _prefix.AsSpan(0, _prefixLength)))
                    throw new InvalidDataException($"The media bytes do not match the expected MIME type '{expected}'.");
                _validated = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
