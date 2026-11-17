using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class IndependentPluralLocalizationTests : IDisposable
{
    public IndependentPluralLocalizationTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("de", 1, 1, "1 Medium in 1 Thread")]
    [InlineData("de", 1, 2, "1 Medium in 2 Threads")]
    [InlineData("de", 2, 1, "2 Medien in 1 Thread")]
    [InlineData("de", 2, 2, "2 Medien in 2 Threads")]
    [InlineData("en", 1, 1, "1 medium in 1 thread")]
    [InlineData("en", 1, 2, "1 medium in 2 threads")]
    [InlineData("en", 2, 1, "2 media in 1 thread")]
    [InlineData("en", 2, 2, "2 media in 2 threads")]
    public void MediaAndThreadsHaveIndependentPluralDecisions(string language, int media, int threads, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, CountPhrases.MediaSummary(media, threads));
        foreach (int missing in new[] { 1, 2 })
            Assert.Equal(expected + (language == "de" ? $", davon {missing} fehlend" : $", {missing} missing"),
                CountPhrases.MediaSummary(media, threads, missing));
    }
}
