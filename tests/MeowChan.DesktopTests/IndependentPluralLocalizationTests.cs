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

    [Theory]
    [InlineData("de", 1, 1, "1 Thread und 1 lokale Datei (2.048 Bytes) endgültig löschen?")]
    [InlineData("de", 1, 2, "1 Thread und 2 lokale Dateien (2.048 Bytes) endgültig löschen?")]
    [InlineData("de", 2, 1, "2 Threads und 1 lokale Datei (2.048 Bytes) endgültig löschen?")]
    [InlineData("de", 2, 2, "2 Threads und 2 lokale Dateien (2.048 Bytes) endgültig löschen?")]
    [InlineData("en", 1, 1, "Permanently delete 1 thread and 1 local file (2,048 bytes)?")]
    [InlineData("en", 1, 2, "Permanently delete 1 thread and 2 local files (2,048 bytes)?")]
    [InlineData("en", 2, 1, "Permanently delete 2 threads and 1 local file (2,048 bytes)?")]
    [InlineData("en", 2, 2, "Permanently delete 2 threads and 2 local files (2,048 bytes)?")]
    public void DeletionThreadsAndFilesHaveIndependentPluralDecisions(string language, int threads, int files, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, CountPhrases.ArchiveDeletionSummary(threads, files, 2048));
    }

    [Theory]
    [InlineData("de", 1, "1 Thread und 1 lokale Datei (1 Byte) endgültig löschen?")]
    [InlineData("de", 2, "1 Thread und 1 lokale Datei (2 Bytes) endgültig löschen?")]
    [InlineData("en", 1, "Permanently delete 1 thread and 1 local file (1 byte)?")]
    [InlineData("en", 2, "Permanently delete 1 thread and 1 local file (2 bytes)?")]
    public void DeletionByteUnitAlsoOwnsItsPluralDecision(string language, int bytes, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, CountPhrases.ArchiveDeletionSummary(1, 1, bytes));
    }

    [Theory]
    [InlineData("de", 1, "Tag vergeben: 1.")]
    [InlineData("de", 2, "Tags vergeben: 2.")]
    [InlineData("en", 1, "Tag added: 1.")]
    [InlineData("en", 2, "Tags added: 2.")]
    public void ImportTagNounFollowsTheTagCount(string language, int tags, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, Texts.Count("import.done.tags", tags));
    }

    [Theory]
    [InlineData("de", 1, "1 ausgewählten Thread gemeinsam exportieren")]
    [InlineData("de", 2, "2 ausgewählte Threads gemeinsam exportieren")]
    [InlineData("en", 1, "Export 1 selected thread together")]
    [InlineData("en", 2, "Export 2 selected threads together")]
    public void CollectionSelectionNounFollowsTheMemberCount(string language, int members, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, Texts.Count("export.collectionSelection", members));
    }
}
