using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;

namespace MeowChan.Desktop;

// R6 (reader study 2026-11-13): Noto Sans is the reading font of post bodies only; the UI stays Inter.
// The bundled files are Latin/Greek/Cyrillic subsets (Assets/Fonts, THIRD-PARTY-NOTICES.md). Glyphs outside
// them fall back to Inter first (arrows, symbols), CJK to the explicit platform list below.
internal static class ReaderFonts
{
    internal const string ReadingFamily = "avares://MeowChan/Assets/Fonts#Noto Sans, avares://Avalonia.Fonts.Inter/Assets#Inter";

    // CJK radicals, symbols and punctuation, kana, bopomofo, hangul, enclosed/compatibility forms,
    // unified ideographs (incl. extension A), compatibility ideographs, vertical and fullwidth forms.
    internal const string CjkRanges = "U+2E80-2FDF,U+3000-30FF,U+3100-312F,U+3130-318F,U+31A0-31FF,U+3200-33FF,"
        + "U+3400-4DBF,U+4E00-9FFF,U+A960-A97F,U+AC00-D7FF,U+F900-FAFF,U+FE10-FE1F,U+FE30-FE4F,U+FF00-FFEF";

    // Japanese, Simplified Chinese, Korean, in that order, so that one family answers a mixed line
    // and the line height/baseline do not jump between per-glyph system picks.
    internal static IReadOnlyList<string> CjkFallbackFamilies() =>
        OperatingSystem.IsWindows() ? ["Yu Gothic UI", "Microsoft YaHei UI", "Malgun Gothic"]
        : OperatingSystem.IsMacOS() ? ["Hiragino Sans", "PingFang SC", "Apple SD Gothic Neo"]
        : ["Noto Sans CJK JP", "Noto Sans CJK SC", "Noto Sans CJK KR"];

    internal static FontManagerOptions ManagerOptions()
    {
        UnicodeRange cjk = UnicodeRange.Parse(CjkRanges);
        return new FontManagerOptions
        {
            FontFallbacks = CjkFallbackFamilies()
                .Select(family => new FontFallback { FontFamily = new FontFamily(family), UnicodeRange = cjk }).ToArray()
        };
    }
}
