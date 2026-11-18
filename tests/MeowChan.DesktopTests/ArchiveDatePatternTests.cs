using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// The date field shows its pattern in the user's language; the accepted input stays ISO yyyy-MM-dd.
[Collection("Localization")]
public sealed class ArchiveDatePatternTests
{
    [Theory]
    [InlineData("en", "YYYY-MM-DD")]
    [InlineData("de", "JJJJ-MM-TT")]
    [InlineData("es", "AAAA-MM-DD")]
    [InlineData("fr", "AAAA-MM-JJ")]
    [InlineData("ru", "ГГГГ-ММ-ДД")]
    public void PatternAndTheTextsThatNameItFollowTheLanguage(string language, string pattern)
    {
        string before = Texts.Language;
        try
        {
            Texts.Initialize(); // the bundled packs; without it the result depends on which test ran first
            Texts.SetLanguage(language);
            Assert.Equal(pattern, Texts.T("archive.datePattern"));
            Assert.Contains(pattern, Texts.T("archive.invalidDate", "x"));
            Assert.Contains(pattern, Texts.T("archive.dateHint"));
            if (language != "en")
            {
                Assert.DoesNotContain("YYYY-MM-DD", Texts.T("archive.invalidDate", "x"));
                Assert.DoesNotContain("YYYY-MM-DD", Texts.T("archive.dateHint"));
            }
        }
        finally { Texts.SetLanguage(before); }
    }
}
