using System.Runtime.CompilerServices;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Every bundled font is credited in the About dialog, the README and the third-party notices with its licence.</summary>
[Collection("Localization")]
public sealed class FontCreditsTests
{
    [Fact]
    public void TheAboutCreditsNameBothBundledFontsInEveryBuiltInLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
            {
                Texts.SetLanguage(language);
                string credits = Texts.T("about.uiCredits");
                Assert.Contains("Inter", credits);
                Assert.Contains("Noto Sans", credits);
                Assert.Contains("SIL Open Font License", credits);
            }
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void EveryBundledFontFileIsListedInTheReadmeAndTheNoticesUnderTheOfl()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "../.."));
        string[] fonts = Directory.EnumerateFiles(Path.Combine(root, "Assets"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName).Select(name => name!).ToArray();
        Assert.NotEmpty(fonts);
        string notices = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES.md"));
        string readme = File.ReadAllText(Path.Combine(root, "README.md"));
        foreach (string font in fonts) Assert.Contains(font, notices);
        Assert.Contains("Noto Sans", readme);
        Assert.Contains("Inter", readme);
        Assert.Contains("SIL Open Font License", readme);
        Assert.Contains("## Noto Sans fonts — SIL Open Font License 1.1", notices);
        Assert.Contains("## Inter fonts — SIL Open Font License 1.1", notices);
    }

    private static string Here([CallerFilePath] string path = "") => path;
}
