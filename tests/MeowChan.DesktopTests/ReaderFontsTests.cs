using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using Avalonia.Media;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Source and file contracts of the bundled reading font and the CJK fallback order; rendering is smoke/capture evidence.
public sealed class ReaderFontsTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CjkFallbacksCoverIdeographsKanaAndHangulButNotTheBundledScripts()
    {
        FontFallback[] fallbacks = ReaderFonts.ManagerOptions("zh-Hans").FontFallbacks!.ToArray();
        Assert.Equal(ReaderFonts.CjkFallbackFamilies("zh-Hans"), fallbacks.Select(fallback => fallback.FontFamily.Name));
        Assert.True(fallbacks.Length >= 4);
        UnicodeRange range = fallbacks[0].UnicodeRange;
        foreach (int codepoint in new[] { 0x4E00, 0x9FFF, 0x3042, 0x30AB, 0xAC00, 0x3001, 0xFF01, 0xFF1A, 0x201C })
            Assert.True(range.IsInRange(codepoint) || codepoint == 0x201C, codepoint.ToString("X4"));
        foreach (int codepoint in new[] { 0x41, 0xE9, 0x416, 0x3C9, 0x2014, 0x20AC })
            Assert.False(range.IsInRange(codepoint), codepoint.ToString("X4"));
    }

    [Theory]
    [InlineData("zh-Hans", CjkScript.SimplifiedChinese)]
    [InlineData("zh", CjkScript.SimplifiedChinese)]
    [InlineData("zh-CN", CjkScript.SimplifiedChinese)]
    [InlineData("zh-Hans-CN", CjkScript.SimplifiedChinese)]
    [InlineData("zh-Hant", CjkScript.TraditionalChinese)]
    [InlineData("zh-TW", CjkScript.TraditionalChinese)]
    [InlineData("zh-HK", CjkScript.TraditionalChinese)]
    [InlineData("ja", CjkScript.Japanese)]
    [InlineData("ja-JP", CjkScript.Japanese)]
    [InlineData("ko", CjkScript.Korean)]
    [InlineData("en", null)]
    [InlineData("de", null)]
    [InlineData("pt-BR", null)]
    [InlineData("cs", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheUiLanguageSelectsTheRegionalCjkVariant(string? language, CjkScript? expected) =>
        Assert.Equal(expected, ReaderFonts.ScriptOf(language));

    [Theory]
    [InlineData("zh-Hans", CjkScript.SimplifiedChinese)]
    [InlineData("ja", CjkScript.Japanese)]
    [InlineData("zh-Hant", CjkScript.TraditionalChinese)]
    [InlineData("ko", CjkScript.Korean)]
    public void ThePreferredFamilyComesFirstAndTheOtherThreeFollow(string language, CjkScript preferred)
    {
        IReadOnlyList<string> families = ReaderFonts.CjkFallbackFamilies(language);
        Assert.True(families.Count >= 4);
        Assert.Equal(families.Count, families.Distinct().Count());
        Assert.Equal(ReaderFonts.FamilyName(preferred), families[0]);
        foreach (CjkScript script in Enum.GetValues<CjkScript>())
            Assert.Contains(ReaderFonts.FamilyName(script), families);
        // Chinese and Japanese UIs order the same four families differently: the same Han character
        // has different regional forms, so the UI language must decide which font answers first.
        Assert.NotEqual(ReaderFonts.CjkFallbackFamilies("zh-Hans")[0], ReaderFonts.CjkFallbackFamilies("ja")[0]);
        // Only the platform's own font names are used; nothing is bundled.
        string expectedName = OperatingSystem.IsWindows() ? "Microsoft YaHei UI" : OperatingSystem.IsMacOS() ? "PingFang SC" : "Noto Sans CJK SC";
        Assert.Equal(expectedName, ReaderFonts.FamilyName(CjkScript.SimplifiedChinese));
    }

    [Theory]
    // The regional families each platform ships (Linux: the Noto Sans CJK packages), preferred script first.
    [InlineData("zh-Hans", "Linux", "Noto Sans CJK SC")]
    [InlineData("zh-Hans", "Windows", "Microsoft YaHei UI,Microsoft YaHei")]
    [InlineData("zh-Hans", "MacOS", "PingFang SC")]
    [InlineData("ja", "Linux", "Noto Sans CJK JP")]
    [InlineData("ja", "Windows", "Yu Gothic UI,Yu Gothic,Meiryo")]
    [InlineData("ja", "MacOS", "Hiragino Sans")]
    [InlineData("ko", "Linux", "Noto Sans CJK KR")]
    [InlineData("ko", "Windows", "Malgun Gothic")]
    [InlineData("ko", "MacOS", "Apple SD Gothic Neo")]
    public void EachCjkUiLanguageStartsWithItsRegionalSystemFamilies(string language, string platformName, string expectedPrefix)
    {
        ReaderFonts.FontPlatform platform = Enum.Parse<ReaderFonts.FontPlatform>(platformName);
        string[] expected = expectedPrefix.Split(',');
        IReadOnlyList<string> families = ReaderFonts.CjkFallbackFamilies(language, platform);
        Assert.Equal(expected, families.Take(expected.Length));
        // The other regions follow, so a mixed line (a Korean name in a Chinese post) still finds glyphs.
        foreach (CjkScript script in Enum.GetValues<CjkScript>())
            Assert.Contains(ReaderFonts.FamilyNames(script, platform)[0], families);
        Assert.Equal(families.Count, families.Distinct().Count());
    }

    [Fact]
    public void NonCjkUiLanguagesFollowTheOsLanguageAndThenJapanese()
    {
        CjkScript? os = ReaderFonts.ScriptOf(System.Globalization.CultureInfo.CurrentUICulture.Name);
        Assert.Equal(os ?? CjkScript.Japanese, ReaderFonts.PreferredScript("en"));
        Assert.Equal(os ?? CjkScript.Japanese, ReaderFonts.PreferredScript(null));
    }

    [Fact]
    public void UiAndReadingFamiliesStartWithTheBundledFontsAndEndWithTheCjkChain()
    {
        string ui = ReaderFonts.UiFamily("zh-Hans");
        string reading = ReaderFonts.ReadingFamily("zh-Hans");
        Assert.StartsWith(ReaderFonts.InterFamily + ", " + ReaderFonts.FamilyName(CjkScript.SimplifiedChinese) + ", ", ui);
        Assert.StartsWith(ReaderFonts.NotoSansFamily + ", " + ReaderFonts.InterFamily + ", " + ReaderFonts.FamilyName(CjkScript.SimplifiedChinese) + ", ", reading);
        // Avalonia parses the composite into one family per segment, so the CJK names are real fallbacks.
        int cjk = ReaderFonts.CjkFallbackFamilies("zh-Hans").Count;
        FontFamily parsed = new(ui);
        Assert.Equal(1 + cjk, parsed.FamilyNames.Count);
        Assert.Equal("Inter", parsed.FamilyNames.PrimaryFamilyName);
        Assert.Equal(ReaderFonts.FamilyName(CjkScript.SimplifiedChinese), parsed.FamilyNames.Skip(1).First());
        FontFamily parsedReading = new(reading);
        Assert.Equal(2 + cjk, parsedReading.FamilyNames.Count);
        Assert.Equal("Noto Sans", parsedReading.FamilyNames.PrimaryFamilyName);
        Assert.Equal("Inter", parsedReading.FamilyNames.Skip(1).First());
    }

    [Fact]
    public void ReadingFontIsTheBundledNotoSansForPostBodiesOnlyAndBothFamiliesAreDynamicResources()
    {
        XElement styles = Source("Desktop/WorkspaceStyles.axaml");
        XElement family = Assert.Single(styles.Descendants(A + "FontFamily")
            .Where(element => (string?)element.Attribute(X + "Key") == ReaderFonts.ReadingFamilyResource));
        Assert.Equal(ReaderFonts.NotoSansFamily + ", " + ReaderFonts.InterFamily, family.Value);
        XElement uiFamily = Assert.Single(styles.Descendants(A + "FontFamily")
            .Where(element => (string?)element.Attribute(X + "Key") == ReaderFonts.UiFamilyResource));
        Assert.Equal(ReaderFonts.InterFamily, uiFamily.Value);
        XElement style = Assert.Single(styles.Elements(A + "Style")
            .Where(element => (string?)element.Attribute("Selector") == "SelectableTextBlock.reader-body"));
        Assert.Equal("{DynamicResource " + ReaderFonts.ReadingFamilyResource + "}", (string?)style.Element(A + "Setter")!.Attribute("Value"));
        // The window (UI) font stays Inter first; ReaderFonts.Apply republishes the key with the CJK chain per UI language.
        XElement window = Assert.Single(styles.Elements(A + "Style").Where(element => (string?)element.Attribute("Selector") == "Window"));
        Assert.Contains(window.Elements(A + "Setter"), setter => (string?)setter.Attribute("Property") == "FontFamily"
            && (string?)setter.Attribute("Value") == "{DynamicResource " + ReaderFonts.UiFamilyResource + "}");
        string page = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.axaml"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "reader-body"));
        // App.ApplyControlLanguage republishes the fonts on every language change.
        string app = File.ReadAllText(Path("App.axaml.cs"));
        Assert.Contains("ReaderFonts.Apply(this, Texts.Language)", app);
    }

    [Fact]
    public void ApplyRepublishesBothFamiliesWhenTheUiLanguageChangesTheCjkOrder()
    {
        Avalonia.Controls.ResourceDictionary resources = new();
        ReaderFonts.Apply(resources, "ja");
        FontFamily japaneseUi = (FontFamily)resources[ReaderFonts.UiFamilyResource]!;
        Assert.Equal(ReaderFonts.FamilyName(CjkScript.Japanese), japaneseUi.FamilyNames[1]);
        // Every UI family starts with Inter, so the switch must not be mistaken for "unchanged".
        ReaderFonts.Apply(resources, "zh-Hans");
        FontFamily chineseUi = (FontFamily)resources[ReaderFonts.UiFamilyResource]!;
        FontFamily chineseReading = (FontFamily)resources[ReaderFonts.ReadingFamilyResource]!;
        Assert.Equal(ReaderFonts.FamilyName(CjkScript.SimplifiedChinese), chineseUi.FamilyNames[1]);
        Assert.Equal(ReaderFonts.FamilyName(CjkScript.SimplifiedChinese), chineseReading.FamilyNames[2]);
        ReaderFonts.Apply(resources, "zh-Hans");
        Assert.Same(chineseUi, resources[ReaderFonts.UiFamilyResource]); // unchanged order: nothing republished
    }

    [Fact]
    public void TheGlobalFallbackListIsOneLiveInstanceReorderedByTheUiLanguage()
    {
        // Avalonia keeps the FontFallbacks instance it was given and consults it before a family's own fallbacks.
        FontManagerOptions options = ReaderFonts.ManagerOptions();
        Assert.Same(ReaderFonts.SharedFallbacks, options.FontFallbacks);
        try
        {
            ReaderFonts.Apply(new Avalonia.Controls.ResourceDictionary(), "ja");
            Assert.Equal(ReaderFonts.FamilyName(CjkScript.Japanese), options.FontFallbacks![0].FontFamily.Name);
            ReaderFonts.Apply(new Avalonia.Controls.ResourceDictionary(), "zh-Hans");
            Assert.Equal(ReaderFonts.FamilyName(CjkScript.SimplifiedChinese), options.FontFallbacks![0].FontFamily.Name);
            Assert.Equal(ReaderFonts.CjkFallbackFamilies("zh-Hans"), options.FontFallbacks!.Select(fallback => fallback.FontFamily.Name));
            ReaderFonts.Apply(new Avalonia.Controls.ResourceDictionary(), "ko");
            Assert.Equal(ReaderFonts.FamilyName(CjkScript.Korean), options.FontFallbacks!.First().FontFamily.Name);
        }
        finally { ReaderFonts.SharedFallbacks.Reorder(null); }
    }

    [Theory]
    [InlineData("de", "de-DE", "de-DE")]
    [InlineData("zh", "zh-CN", "zh-CN")]
    [InlineData("ja", "ja-JP", "en-US")] // Ursa ships no ja-JP locale.
    [InlineData("cs", "en-US", "cs-CZ")] // Semi ships no cs-CZ locale.
    [InlineData("ko", "ko-KR", "en-US")]
    [InlineData("it", "it-IT", "en-US")]
    [InlineData("pl", "pl-PL", "pl-PL")]
    [InlineData("pt", "en-US", "en-US")]
    [InlineData("es", "en-US", "en-US")]
    [InlineData("en", "en-US", "en-US")]
    public void VendorControlLocalesFollowOnlyShippedCultures(string language, string semi, string ursa)
    {
        Assert.Equal(semi, MeowChan.App.VendorLocale(language, MeowChan.App.SemiLocales));
        Assert.Equal(ursa, MeowChan.App.VendorLocale(language, MeowChan.App.UrsaLocales));
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf")]
    [InlineData("NotoSans-Bold.ttf")]
    [InlineData("NotoSans-Italic.ttf")]
    public void BundledFontsAreTrueTypeSubsetsNamedNotoSansUnderTheOfl(string file)
    {
        byte[] bytes = File.ReadAllBytes(Path("Assets/Fonts/" + file));
        Assert.Equal(new byte[] { 0, 1, 0, 0 }, bytes[..4]);
        Assert.InRange(bytes.Length, 100_000, 500_000); // A subset, not the 620 KB system file.
        string utf16 = Encoding.BigEndianUnicode.GetString(bytes);
        Assert.Contains("Noto Sans", utf16);
        Assert.Contains("SIL Open Font License, Version 1.1", utf16);
        string notices = File.ReadAllText(Path("THIRD-PARTY-NOTICES.md"));
        Assert.Contains("Assets/Fonts/NotoSans-Regular.ttf", notices);
        Assert.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), notices);
    }

    [Fact]
    public void NoCjkFontIsBundled()
    {
        // The CJK chain names installed system fonts only; the pack README documents the platform dependency.
        string[] fonts = Directory.GetFiles(Path("Assets/Fonts"));
        Assert.DoesNotContain(fonts, font => System.IO.Path.GetFileName(font).Contains("CJK", StringComparison.OrdinalIgnoreCase)); // File names only: a checkout directory may contain "cjk".
        Assert.All(fonts, font => Assert.True(new FileInfo(font).Length < 1_000_000, font));
        string readme = File.ReadAllText(Path("Desktop/Localization/Packs/README.md"));
        Assert.Contains("Noto Sans CJK SC", readme);
        Assert.Contains("Microsoft YaHei", readme);
        Assert.Contains("PingFang SC", readme);
    }

    private static string Path(string relative, [CallerFilePath] string sourcePath = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!, "../..", relative));

    private static XElement Source(string relative, [CallerFilePath] string sourcePath = "") =>
        XDocument.Load(Path(relative, sourcePath)).Root!;
}
