using System;
using System.Buffers;
using System.Globalization;
using System.Text;

namespace MeowChan.Tags;

/// <summary>A sanitized error code. Values never contain or echo rejected tag-name input.</summary>
internal enum TagNameError
{
    None,
    Null,
    Blank,
    InputTooLong,
    InvalidUnicode,
    InvalidCharacter,
    TooManyScalars,
    TooManyUtf8Bytes
}

/// <summary>
/// A bounded, immutable private tag name. <see cref="Display"/> is NFC text after edge trimming;
/// <see cref="Key"/> is invariant case comparison text (<c>Display.ToUpperInvariant()</c>, then NFC).
/// The comparison is deliberately simple invariant casing, not locale-sensitive casing and not a
/// promise of Unicode full case folding. This value has no persistence, UI, schema, source-tag,
/// adoption, or merge behavior.
/// </summary>
internal sealed class PrivateTagName : IEquatable<PrivateTagName>
{
    internal const int MaximumScalars = 128;
    internal const int MaximumUtf8Bytes = 512;

    /// <summary>
    /// Defensive pre-normalization bound in UTF-16 code units. It limits scanning and Unicode
    /// normalization work before the exact normalized scalar/UTF-8 limits are applied.
    /// </summary>
    internal const int MaximumUtf16CodeUnits = 4096;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private PrivateTagName(string display, string key)
    {
        Display = display;
        Key = key;
    }

    internal string Display { get; }

    /// <summary>Ordinal case-comparison key; it is intentionally separate from preserved display text.</summary>
    internal string Key { get; }

    internal static bool TryCreate(string? candidate, out PrivateTagName? tagName, out TagNameError error)
    {
        tagName = null;
        if (candidate is null)
        {
            error = TagNameError.Null;
            return false;
        }

        try
        {
            // This O(1) input cap comes before all scalar scans, trimming, and normalization.
            if (candidate.Length > MaximumUtf16CodeUnits)
            {
                error = TagNameError.InputTooLong;
                return false;
            }

            // Inspect before trimming so whitespace which .NET trims but which is forbidden here
            // (for example tab or newline) cannot disappear before validation.
            if (!TryInspect(candidate, out _, out error)) return false;
            string trimmed = candidate.Trim();
            if (trimmed.Length == 0)
            {
                error = TagNameError.Blank;
                return false;
            }

            string display = trimmed.Normalize(NormalizationForm.FormC);
            if (!TryInspect(display, out int scalarCount, out error)) return false;
            if (scalarCount > MaximumScalars)
            {
                error = TagNameError.TooManyScalars;
                return false;
            }
            if (Utf8.GetByteCount(display) > MaximumUtf8Bytes)
            {
                error = TagNameError.TooManyUtf8Bytes;
                return false;
            }

            string key = display.ToUpperInvariant().Normalize(NormalizationForm.FormC);
            tagName = new PrivateTagName(display, key);
            error = TagNameError.None;
            return true;
        }
        catch (Exception caught) when (caught is ArgumentException or InvalidOperationException
            or DecoderFallbackException or OverflowException)
        {
            // This boundary accepts untrusted strings and returns a constant code; it never
            // propagulates an exception whose message could include part of the candidate.
            tagName = null;
            error = TagNameError.InvalidUnicode;
            return false;
        }
    }

    private static bool TryInspect(string value, out int scalarCount, out TagNameError error)
    {
        scalarCount = 0;
        error = TagNameError.None;
        for (int offset = 0; offset < value.Length;)
        {
            OperationStatus status = Rune.DecodeFromUtf16(value.AsSpan(offset), out Rune rune, out int consumed);
            if (status != OperationStatus.Done || consumed <= 0)
            {
                error = TagNameError.InvalidUnicode;
                return false;
            }

            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                error = TagNameError.InvalidCharacter;
                return false;
            }

            scalarCount++;
            offset += consumed;
        }
        return true;
    }

    public bool Equals(PrivateTagName? other) =>
        other is not null && string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is PrivateTagName other && Equals(other);

    public override int GetHashCode() => string.GetHashCode(Key, StringComparison.Ordinal);

    public override string ToString() => Display;

    public static bool operator ==(PrivateTagName? left, PrivateTagName? right) =>
        ReferenceEquals(left, right) || left is not null && right is not null && left.Equals(right);

    public static bool operator !=(PrivateTagName? left, PrivateTagName? right) => !(left == right);
}
