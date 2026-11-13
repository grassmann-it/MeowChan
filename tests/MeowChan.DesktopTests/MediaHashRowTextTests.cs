using MeowChan.Desktop.Localization;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

// The result row names the version flag in plain words in every language and still tells
// a current asset from a historical one; no internal field name leaks into the visible text.
[Collection("Localization")]
public sealed class MediaHashRowTextTests
{
    private static MediaAssetReference Asset(bool current) => new("asset-1", "media-1", "preview", "file.bin", "/never-opened/asset-1",
        123, new string('a', 64), "https://offline.invalid/x", "original.bin", current);

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void RowDoesNotExposeInternalNamesAndKeepsCurrentVersusHistoricalApart(string language)
    {
        string before = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            string current = MediaHashSearchDialog.RowLabel(Asset(true));
            string historical = MediaHashSearchDialog.RowLabel(Asset(false));
            foreach (string row in new[] { current, historical })
            {
                Assert.DoesNotContain("IsCurrent", row, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("captured", row, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("asset-1", row); Assert.Contains("media-1", row); Assert.Contains("preview", row);
            }
            Assert.NotEqual(current, historical);
        }
        finally { Texts.SetLanguage(before); }
    }
}
