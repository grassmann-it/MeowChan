using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Localization;

/// <summary>
/// Plural categories for whole counts (CLDR cardinal rules for integers; fractions never reach this code).
/// Texts.Count looks up <c>key.&lt;category&gt;</c> and falls back to the base key, which therefore holds the
/// general ("other") form. Russian-style languages put their "many" form in the base key and add <c>key.few</c>
/// for 2-4. Czech/Slovak: one = 1, few = 2-4, other = everything else (their "many" is for fractions only).
/// Portuguese: Brazilian pt and plain pt treat 0 and 1 as "one"; European pt-PT only 1. Chinese, Japanese,
/// Korean, Vietnamese, Thai, Indonesian and Malay have no plural forms and always use the base key.
/// </summary>
internal static class PluralRules
{
    internal const string Other = "other";
    private static readonly string[] Suffixes = { "zero", "one", "two", "few", "many" };

    internal static string Category(string language, long count)
    {
        long n = Math.Abs(count);
        int dash = language.IndexOf('-');
        string code = (dash < 0 ? language : language[..dash]).ToLowerInvariant();
        switch (code)
        {
            case "ru":
            case "uk":
            case "be":
                if (n % 10 == 1 && n % 100 != 11) return "one";
                return n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "few" : "many";
            case "pl":
                if (n == 1) return "one";
                return n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "few" : "many";
            case "cs":
            case "sk":
                return n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : Other;
            case "fr":
                return n <= 1 ? "one" : Other;
            case "pt":
                // CLDR: pt (Brazil) one = 0 and 1; pt-PT one = 1 only.
                return language.EndsWith("-PT", StringComparison.OrdinalIgnoreCase) ? (n == 1 ? "one" : Other) : (n <= 1 ? "one" : Other);
            case "ja":
            case "zh":
            case "ko":
            case "vi":
            case "th":
            case "id":
            case "ms":
                return Other;
            default:
                return n == 1 ? "one" : Other;
        }
    }

    internal static string Variant(string key, string category) => category == Other ? key : key + "." + category;

    /// <summary>The key a plural variant belongs to ("posts.count.few" gives "posts.count"), or null.</summary>
    internal static string? BaseKey(string key)
    {
        int dot = key.LastIndexOf('.');
        return dot > 0 && Array.IndexOf(Suffixes, key[(dot + 1)..]) >= 0 ? key[..dot] : null;
    }
}
