using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using Avalonia.Media;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Source and file contracts of the bundled reading font; rendering is smoke/capture evidence.
public sealed class ReaderFontsTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void CjkFallbacksCoverIdeographsKanaAndHangulButNotTheBundledScripts()
    {
        FontFallback[] fallbacks = ReaderFonts.ManagerOptions().FontFallbacks!.ToArray();
        Assert.Equal(ReaderFonts.CjkFallbackFamilies(), fallbacks.Select(fallback => fallback.FontFamily.Name));
        Assert.Equal(3, fallbacks.Length);
        UnicodeRange range = fallbacks[0].UnicodeRange;
        foreach (int codepoint in new[] { 0x4E00, 0x9FFF, 0x3042, 0x30AB, 0xAC00, 0x3001, 0xFF01 })
            Assert.True(range.IsInRange(codepoint), codepoint.ToString("X4"));
        foreach (int codepoint in new[] { 0x41, 0xE9, 0x416, 0x3C9, 0x2014, 0x20AC })
            Assert.False(range.IsInRange(codepoint), codepoint.ToString("X4"));
    }

    [Fact]
    public void ReadingFontIsTheBundledNotoSansForPostBodiesOnly()
    {
        XElement styles = Source("Desktop/WorkspaceStyles.axaml");
        XElement family = Assert.Single(styles.Descendants(A + "FontFamily")
            .Where(element => (string?)element.Attribute(X + "Key") == "Workspace.Font.Reading"));
        Assert.Equal(ReaderFonts.ReadingFamily, family.Value);
        XElement style = Assert.Single(styles.Elements(A + "Style")
            .Where(element => (string?)element.Attribute("Selector") == "SelectableTextBlock.reader-body"));
        Assert.Equal("{StaticResource Workspace.Font.Reading}", (string?)style.Element(A + "Setter")!.Attribute("Value"));
        // The window (UI) font stays Inter.
        XElement window = Assert.Single(styles.Elements(A + "Style").Where(element => (string?)element.Attribute("Selector") == "Window"));
        Assert.Contains(window.Elements(A + "Setter"), setter => (string?)setter.Attribute("Property") == "FontFamily"
            && (string?)setter.Attribute("Value") == "avares://Avalonia.Fonts.Inter/Assets#Inter");
        string page = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.axaml"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, "reader-body"));
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

    private static string Path(string relative, [CallerFilePath] string sourcePath = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(sourcePath)!, "../..", relative));

    private static XElement Source(string relative, [CallerFilePath] string sourcePath = "") =>
        XDocument.Load(Path(relative, sourcePath)).Root!;
}
