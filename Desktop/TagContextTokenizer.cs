using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace MeowChan.Desktop;

/// <summary>
/// Words of a file name or a thread title, and joined pairs of neighbouring words. <see cref="Words"/> are candidates
/// for an exact or prefix match against existing tag names; <see cref="Phrases"/> ("blue_sky" from "blue", "sky") are
/// candidates for an exact match only. Nothing here is a tag; the ranking only keeps what an existing tag matches.
/// </summary>
internal sealed record TagTokens(IReadOnlyList<string> Words, IReadOnlyList<string> Phrases)
{
    internal static TagTokens Empty { get; } = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// Splits a file name or title into match candidates. Pure and allocation-bounded: at most
/// <see cref="MaximumScannedCharacters"/> characters are read and at most the requested number of words and of phrases
/// is returned. A word is a run of letters, digits and combining marks; every other character (including
/// <c>_ - . ( ) [ ]</c> and space) separates words. Words have at least <see cref="MinimumScalars"/> characters (a word
/// written entirely in Han, kana or Hangul, which has no spaces between words, at least <see cref="MinimumCjkScalars"/>),
/// are not purely numeric (camera and 4chan timestamps), and repeat at most once. A trailing known media extension of a file name
/// is dropped. The words keep their text (Unicode NFC); matching uses the tag-name comparison key.
/// </summary>
internal static class TagContextTokenizer
{
    internal const int MinimumScalars = 3;
    internal const int MinimumCjkScalars = 1;
    /// <summary>Longer words (more than this many characters) are skipped; no tag name has one in practice and it bounds match work.</summary>
    internal const int MaximumWordScalars = 64;
    /// <summary>Input beyond this many UTF-16 code units is not read (a documented technical ceiling, not a setting).</summary>
    internal const int MaximumScannedCharacters = 4096;

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "png", "gif", "webp", "bmp", "avif", "jxl", "heic", "heif", "tif", "tiff", "svg", "ico",
        "mp4", "webm", "mkv", "mov", "avi", "m4v", "ogv", "wmv", "flv", "mpg", "mpeg", "3gp", "apng", "mp3", "ogg",
        "opus", "flac", "wav", "m4a", "aac", "swf", "zip", "rar", "7z", "pdf", "txt"
    };

    /// <param name="text">A file name (<paramref name="isFileName"/>) or any text such as a thread title.</param>
    /// <param name="maxWords">Words and phrases returned at most each; 0 returns nothing.</param>
    /// <param name="isFileName">Drops a directory part and a trailing media extension first.</param>
    internal static TagTokens Tokenize(string? text, int maxWords, bool isFileName = false, CancellationToken cancellation = default)
    {
        if (string.IsNullOrEmpty(text) || maxWords <= 0) return TagTokens.Empty;
        ReadOnlySpan<char> span = text.AsSpan(0, Math.Min(text.Length, MaximumScannedCharacters));
        if (isFileName) span = StripFileName(span);
        List<string> words = new();
        List<string> phrases = new();
        HashSet<string> seenWords = new(StringComparer.Ordinal), seenPhrases = new(StringComparer.Ordinal);
        string? previous = null;
        // Raw words are bounded by the scanned characters; the loop stops once both lists are full.
        foreach (string raw in RawWords(span))
        {
            cancellation.ThrowIfCancellationRequested();
            if (words.Count >= maxWords && phrases.Count >= maxWords) break;
            if (words.Count < maxWords && Accepted(raw) && seenWords.Add(Key(raw))) words.Add(raw);
            if (previous != null && phrases.Count < maxWords && !IsCjk(previous) && !IsCjk(raw))
            {
                string phrase = previous + "_" + raw;
                if (ScalarCount(phrase) <= MaximumWordScalars * 2 && seenPhrases.Add(Key(phrase))) phrases.Add(phrase);
            }
            previous = raw;
        }
        return words.Count == 0 && phrases.Count == 0 ? TagTokens.Empty : new TagTokens(words.AsReadOnly(), phrases.AsReadOnly());
    }

    /// <summary>True when the word is not empty and every character is Han, kana or Hangul (scripts written without spaces between words).</summary>
    internal static bool IsCjk(string word)
    {
        if (word.Length == 0) return false;
        foreach (Rune rune in word.EnumerateRunes()) if (!IsCjkRune(rune)) return false;
        return true;
    }

    internal static bool IsCjkRune(Rune rune)
    {
        int value = rune.Value;
        return value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2FA1F or >= 0xF900 and <= 0xFAFF
            or 0x3005 or >= 0x3040 and <= 0x309F or >= 0x30A0 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F
            or >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F;
    }

    internal static int ScalarCount(string value)
    {
        int count = 0;
        foreach (Rune _ in value.EnumerateRunes()) count++;
        return count;
    }

    /// <summary>The tag-name comparison key of a word (NFC, invariant upper case), or the word itself when it cannot be normalized.</summary>
    internal static string Key(string word) => TagSuggestionIndex.QueryKey(word) ?? word;

    private static bool Accepted(string word)
    {
        int scalars = ScalarCount(word);
        if (scalars > MaximumWordScalars) return false;
        if (scalars < (IsCjk(word) ? MinimumCjkScalars : MinimumScalars)) return false;
        foreach (Rune rune in word.EnumerateRunes()) if (!Rune.IsDigit(rune)) return true;
        return false; // purely numeric
    }

    private static ReadOnlySpan<char> StripFileName(ReadOnlySpan<char> name)
    {
        int separator = name.LastIndexOfAny('/', '\\');
        if (separator >= 0) name = name[(separator + 1)..];
        int dot = name.LastIndexOf('.');
        if (dot > 0 && MediaExtensions.Contains(name[(dot + 1)..].ToString())) name = name[..dot];
        return name;
    }

    private static List<string> RawWords(ReadOnlySpan<char> span)
    {
        List<string> result = new();
        StringBuilder current = new();
        foreach (Rune rune in span.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            bool mark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            // A combining mark belongs to the letter before it; a stray one at the start of a run separates like punctuation.
            if (Rune.IsLetterOrDigit(rune) || mark && current.Length > 0)
            {
                current.Append(rune.ToString());
                continue;
            }
            Flush(current, result);
        }
        Flush(current, result);
        return result;
    }

    private static void Flush(StringBuilder current, List<string> result)
    {
        if (current.Length == 0) return;
        string word = current.ToString();
        current.Clear();
        try { word = word.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return; }
        result.Add(word);
    }
}
