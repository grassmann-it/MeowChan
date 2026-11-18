using System;
using System.Collections.Generic;

namespace MeowChan.Tags;

/// <summary>
/// Finds <c>/name/</c> markers such as <c>/rpt/</c> in a thread title or first post and offers them
/// as tag names. Purely textual and bounded: no network, no learned ranking, no other pattern.
/// A result is only a suggestion; nothing is assigned here.
/// </summary>
internal static class SlashTagSuggestions
{
    internal const int DefaultSuggestionCount = 8;
    internal const int MinimumSuggestionCount = 3;
    internal const int MaximumSuggestionCount = 20;
    // Compatibility name: the old no-options API still offers at most eight suggestions.
    internal const int MaximumSuggestions = DefaultSuggestionCount;
    /// <summary>Per input text; later text is not inspected.</summary>
    internal const int MaximumScannedCharacters = 8192;
    internal const int MaximumTokenCharacters = 32;

    /// <summary>
    /// Distinct suggestions in order of first appearance across the given texts. A marker needs a
    /// boundary on both sides (not a letter, digit or slash), so paths, URLs and dates do not match.
    /// The token is lowercased; equality follows the normal tag-name comparison.
    /// </summary>
    internal static IReadOnlyList<PrivateTagName> Detect(params string?[] texts) => Detect(DefaultSuggestionCount, texts);

    /// <summary>The result count is configurable; the per-text scan and per-token safety ceilings are unchanged.</summary>
    internal static IReadOnlyList<PrivateTagName> Detect(int suggestionCount, params string?[] texts)
    {
        if (suggestionCount is < MinimumSuggestionCount or > MaximumSuggestionCount)
            throw new ArgumentOutOfRangeException(nameof(suggestionCount));
        ArgumentNullException.ThrowIfNull(texts);
        List<PrivateTagName> found = new();
        foreach (string? text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            int limit = Math.Min(text.Length, MaximumScannedCharacters);
            for (int index = 0; index < limit && found.Count < suggestionCount; index++)
            {
                if (text[index] != '/' || index > 0 && IsWordOrSlash(text[index - 1])) continue;
                int end = index + 1;
                bool letter = false;
                while (end < limit && end - index - 1 < MaximumTokenCharacters && IsTokenCharacter(text[end]))
                {
                    letter |= char.IsLetter(text[end]);
                    end++;
                }
                int length = end - index - 1;
                if (length == 0 || !letter || end >= limit || text[end] != '/'
                    || end + 1 < text.Length && IsWordOrSlash(text[end + 1])) continue;
                string candidate = "/" + text.Substring(index + 1, length).ToLowerInvariant() + "/";
                if (PrivateTagName.TryCreate(candidate, out PrivateTagName? name, out _) && !found.Contains(name!))
                    found.Add(name!);
                index = end; // The closing slash is consumed and cannot open another marker.
            }
            if (found.Count >= suggestionCount) break;
        }
        return found.AsReadOnly();
    }

    private static bool IsTokenCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '+' or '-';
    private static bool IsWordOrSlash(char value) => char.IsLetterOrDigit(value) || value == '/';
}
