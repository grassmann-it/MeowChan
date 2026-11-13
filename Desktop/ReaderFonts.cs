using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MeowChan.Desktop;

/// <summary>Which regional CJK font should answer first: the same Han character is drawn differently per region.</summary>
public enum CjkScript { Japanese, SimplifiedChinese, TraditionalChinese, Korean }

// R6 (reader study 2026-11-13): Noto Sans is the reading font of post bodies only; the UI stays Inter.
// The bundled files are Latin/Greek/Cyrillic subsets (Assets/Fonts, THIRD-PARTY-NOTICES.md). Neither font has
// CJK glyphs, so both font families end with the platform's installed CJK families (nothing is bundled: the
// Noto Sans CJK OTCs are about 20 MB per weight). Linux needs Noto Sans CJK (package noto-fonts-cjk /
// fonts-noto-cjk / google-noto-sans-cjk-fonts); Windows and macOS ship their families with the OS. Without any
// of them, Avalonia's last resort is the platform's own per-character match (fontconfig on Linux). The UI language picks the regional variant that answers first
// (zh-Hans -> SC, zh-Hant -> TC, ja -> JP, ko -> KR); a non-CJK UI follows the OS language, else JP as before.
// Avalonia consults FontManagerOptions.FontFallbacks before a family's own fallback names, so that global list
// (one live instance, CjkFallbackList) is re-ordered the same way; it also covers controls with their own font.
internal static class ReaderFonts
{
    internal const string InterFamily = "avares://Avalonia.Fonts.Inter/Assets#Inter";
    internal const string NotoSansFamily = "avares://MeowChan/Assets/Fonts#Noto Sans";
    /// <summary>Resource keys the Window and reader-body styles bind with DynamicResource; Apply refreshes them.</summary>
    internal const string UiFamilyResource = "Workspace.Font.UI";
    internal const string ReadingFamilyResource = "Workspace.Font.Reading";

    // CJK radicals, symbols and punctuation, kana, bopomofo, hangul, enclosed/compatibility forms,
    // unified ideographs (incl. extension A), compatibility ideographs, vertical and fullwidth forms.
    internal const string CjkRanges = "U+2E80-2FDF,U+3000-30FF,U+3100-312F,U+3130-318F,U+31A0-31FF,U+3200-33FF,"
        + "U+3400-4DBF,U+4E00-9FFF,U+A960-A97F,U+AC00-D7FF,U+F900-FAFF,U+FE10-FE1F,U+FE30-FE4F,U+FF00-FFEF";

    private static readonly CjkScript[] DefaultOrder =
        { CjkScript.Japanese, CjkScript.SimplifiedChinese, CjkScript.TraditionalChinese, CjkScript.Korean };

    /// <summary>The desktop platforms whose installed CJK families differ.</summary>
    internal enum FontPlatform { Linux, Windows, MacOS }

    internal static FontPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? FontPlatform.Windows : OperatingSystem.IsMacOS() ? FontPlatform.MacOS : FontPlatform.Linux;

    /// <summary>
    /// Installed family names of a regional CJK font, best first. Windows: YaHei / JhengHei / Yu Gothic (Meiryo on
    /// systems without Yu Gothic) / Malgun Gothic; macOS: PingFang / Hiragino / Apple SD Gothic Neo; Linux and the
    /// BSDs: Noto Sans CJK (also installed as "Source Han Sans" by some distributions, same design).
    /// </summary>
    internal static IReadOnlyList<string> FamilyNames(CjkScript script, FontPlatform platform) => platform switch
    {
        FontPlatform.Windows => script switch
        {
            CjkScript.Japanese => ["Yu Gothic UI", "Yu Gothic", "Meiryo"],
            CjkScript.SimplifiedChinese => ["Microsoft YaHei UI", "Microsoft YaHei"],
            CjkScript.TraditionalChinese => ["Microsoft JhengHei UI", "Microsoft JhengHei"],
            _ => ["Malgun Gothic"]
        },
        FontPlatform.MacOS => script switch
        {
            CjkScript.Japanese => ["Hiragino Sans"],
            CjkScript.SimplifiedChinese => ["PingFang SC"],
            CjkScript.TraditionalChinese => ["PingFang TC"],
            _ => ["Apple SD Gothic Neo"]
        },
        _ => script switch
        {
            CjkScript.Japanese => ["Noto Sans CJK JP", "Source Han Sans JP"],
            CjkScript.SimplifiedChinese => ["Noto Sans CJK SC", "Source Han Sans SC"],
            CjkScript.TraditionalChinese => ["Noto Sans CJK TC", "Source Han Sans TC"],
            _ => ["Noto Sans CJK KR", "Source Han Sans KR"]
        }
    };

    /// <summary>The preferred installed family name of a regional CJK font on this platform.</summary>
    internal static string FamilyName(CjkScript script) => FamilyNames(script, CurrentPlatform)[0];

    /// <summary>The CJK script a language code asks for, or null for a non-CJK language.</summary>
    internal static CjkScript? ScriptOf(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        string code = language.Trim();
        int dash = code.IndexOf('-');
        string primary = (dash < 0 ? code : code[..dash]).ToLowerInvariant();
        switch (primary)
        {
            case "ja": return CjkScript.Japanese;
            case "ko": return CjkScript.Korean;
            case "zh":
                string rest = dash < 0 ? "" : code[(dash + 1)..].ToLowerInvariant();
                bool traditional = rest.Split('-').Any(part => part is "hant" or "tw" or "hk" or "mo");
                return traditional ? CjkScript.TraditionalChinese : CjkScript.SimplifiedChinese;
            default: return null;
        }
    }

    /// <summary>The script that answers first for the given UI language (OS language, then Japanese, as defaults).</summary>
    internal static CjkScript PreferredScript(string? uiLanguage) =>
        ScriptOf(uiLanguage) ?? ScriptOf(CultureInfo.CurrentUICulture.Name) ?? CjkScript.Japanese;

    /// <summary>
    /// Platform CJK families, the UI language's regional variant first, so that one family answers a whole line
    /// and the line height/baseline do not jump between per-glyph system picks.
    /// </summary>
    internal static IReadOnlyList<string> CjkFallbackFamilies(string? uiLanguage = null) =>
        CjkFallbackFamilies(uiLanguage, CurrentPlatform);

    internal static IReadOnlyList<string> CjkFallbackFamilies(string? uiLanguage, FontPlatform platform)
    {
        CjkScript preferred = PreferredScript(uiLanguage);
        return DefaultOrder.OrderBy(script => script == preferred ? 0 : 1)
            .SelectMany(script => FamilyNames(script, platform)).ToArray();
    }

    /// <summary>The UI font: Inter, then the CJK families. Glyphs Inter lacks come from the first family that has them.</summary>
    internal static string UiFamily(string? uiLanguage = null) =>
        InterFamily + ", " + string.Join(", ", CjkFallbackFamilies(uiLanguage));

    /// <summary>The reading font of post bodies: the bundled Noto Sans subset, Inter (arrows, symbols), then CJK.</summary>
    internal static string ReadingFamily(string? uiLanguage = null) =>
        NotoSansFamily + ", " + InterFamily + ", " + string.Join(", ", CjkFallbackFamilies(uiLanguage));

    /// <summary>
    /// The FontManager's global CJK fallbacks. Avalonia consults FontManagerOptions.FontFallbacks before a
    /// family's own fallback names and keeps the list instance it was given, so this one live list is
    /// re-ordered by <see cref="Apply(IResourceDictionary, string?)"/> when the UI language changes.
    /// </summary>
    internal static readonly CjkFallbackList SharedFallbacks = new(null);

    /// <summary>Startup options: the shared live fallback list, ordered for the OS language until Apply runs.</summary>
    internal static FontManagerOptions ManagerOptions() => new() { FontFallbacks = SharedFallbacks };

    /// <summary>Options with a separate fallback list fixed to <paramref name="uiLanguage"/> (tests, tools).</summary>
    internal static FontManagerOptions ManagerOptions(string? uiLanguage) => new() { FontFallbacks = new CjkFallbackList(uiLanguage) };

    /// <summary>A read-only fallback list whose order can be swapped atomically; readers see one consistent snapshot.</summary>
    internal sealed class CjkFallbackList : IReadOnlyList<FontFallback>
    {
        private static readonly UnicodeRange Cjk = UnicodeRange.Parse(CjkRanges);
        private FontFallback[] _items;

        internal CjkFallbackList(string? uiLanguage) => _items = Build(uiLanguage);

        internal void Reorder(string? uiLanguage) => System.Threading.Volatile.Write(ref _items, Build(uiLanguage));

        private FontFallback[] Snapshot => System.Threading.Volatile.Read(ref _items);
        public int Count => Snapshot.Length;
        public FontFallback this[int index] => Snapshot[index];
        public IEnumerator<FontFallback> GetEnumerator() => ((IEnumerable<FontFallback>)Snapshot).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private static FontFallback[] Build(string? uiLanguage) => CjkFallbackFamilies(uiLanguage)
            .Select(family => new FontFallback { FontFamily = new FontFamily(family), UnicodeRange = Cjk }).ToArray();
    }

    /// <summary>
    /// Publishes the UI and reading families for <paramref name="uiLanguage"/> as application resources. The
    /// Window and reader-body styles bind them dynamically, so a language change re-orders the CJK fallback live.
    /// Call on the UI thread.
    /// </summary>
    internal static void Apply(Application application, string? uiLanguage) => Apply(application.Resources, uiLanguage);

    internal static void Apply(IResourceDictionary resources, string? uiLanguage)
    {
        SharedFallbacks.Reorder(uiLanguage);
        FontFamily ui = new FontFamily(UiFamily(uiLanguage));
        FontFamily reading = new FontFamily(ReadingFamily(uiLanguage));
        // FontFamily.Name is only the first family (always Inter), so compare the whole fallback order.
        if (resources.TryGetValue(UiFamilyResource, out object? current) && current is FontFamily existing
            && existing.FamilyNames.SequenceEqual(ui.FamilyNames))
            return;
        resources[UiFamilyResource] = ui;
        resources[ReadingFamilyResource] = reading;
    }
}
