using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// UX round 3 (13.11.2026): wording fixes that must stay true in every language.
[Collection("Localization")]
public sealed class UxRound3TextTests : IDisposable
{
    private readonly string _language = Texts.Language;

    public UxRound3TextTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_language); }

    [Theory]
    [InlineData("en", "any link")]
    [InlineData("de", "keinem Link")]
    public void TheLinkDialogSaysThatNotAskingAgainCoversEveryLink(string language, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Contains(expected, Texts.T("reader.link.remember"));
    }

    [Fact]
    public void EveryBundledLanguageWordsTheLinkChoiceDifferentlyFromTheOldSessionOnlyText()
    {
        string[] old =
        [
            "Não perguntar de novo nesta sessão", "이 세션에서는 다시 묻지 않기", "Больше не спрашивать в этом сеансе",
            "本次会话中不再询问", "Ne plus demander pendant cette session", "V této relaci se už neptat",
            "No volver a preguntar en esta sesión", "Non chiedere più in questa sessione", "このセッション中は再度確認しない",
            "Nie pytaj ponownie w tej sesji"
        ];
        foreach (string language in new[] { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(language);
            Assert.DoesNotContain(Texts.T("reader.link.remember"), old);
        }
    }

    [Fact]
    public void TheGalleryShowsTheBinarySizeFirstAndTheExactByteCountInBrackets()
    {
        Texts.SetLanguage("en");
        Assert.Equal("Saved file: 63 KiB (64,624 bytes)", MediaProvenanceText.LocalSize(64_624));
        Texts.SetLanguage("de");
        Assert.Equal("Gespeicherte Datei: 2,4 MiB (2.500.000 Bytes)", MediaProvenanceText.LocalSize(2_500_000));
        foreach (string language in new[] { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" })
        {
            Texts.SetLanguage(language);
            string line = MediaProvenanceText.LocalSize(2_500_000);
            Assert.Contains(SizeText.Format(2_500_000), line);
            Assert.Contains(2_500_000L.ToString("N0", Texts.Culture), line);
        }
    }
}
