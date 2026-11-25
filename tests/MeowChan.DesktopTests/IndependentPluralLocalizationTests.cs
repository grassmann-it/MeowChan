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
    [InlineData("de", 1, 1, "1 Thread und 1 lokale Datei (2 KiB) endgültig löschen?")]
    [InlineData("de", 1, 2, "1 Thread und 2 lokale Dateien (2 KiB) endgültig löschen?")]
    [InlineData("de", 2, 1, "2 Threads und 1 lokale Datei (2 KiB) endgültig löschen?")]
    [InlineData("de", 2, 2, "2 Threads und 2 lokale Dateien (2 KiB) endgültig löschen?")]
    [InlineData("en", 1, 1, "Permanently delete 1 thread and 1 local file (2 KiB)?")]
    [InlineData("en", 1, 2, "Permanently delete 1 thread and 2 local files (2 KiB)?")]
    [InlineData("en", 2, 1, "Permanently delete 2 threads and 1 local file (2 KiB)?")]
    [InlineData("en", 2, 2, "Permanently delete 2 threads and 2 local files (2 KiB)?")]
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
    [InlineData("de", 0, 0, "0 von 0 Medien passen zum Filter")]
    [InlineData("de", 0, 1, "0 von 1 Medium passen zum Filter")]
    [InlineData("de", 0, 2, "0 von 2 Medien passen zum Filter")]
    [InlineData("de", 1, 1, "1 von 1 Medium passt zum Filter")]
    [InlineData("de", 1, 2, "1 von 2 Medien passt zum Filter")]
    [InlineData("de", 2, 1, "2 von 1 Medium passen zum Filter")]
    [InlineData("de", 2, 2, "2 von 2 Medien passen zum Filter")]
    [InlineData("en", 0, 0, "0 of 0 media match the filter")]
    [InlineData("en", 0, 1, "0 of 1 medium match the filter")]
    [InlineData("en", 0, 2, "0 of 2 media match the filter")]
    [InlineData("en", 1, 1, "1 of 1 medium matches the filter")]
    [InlineData("en", 1, 2, "1 of 2 media matches the filter")]
    [InlineData("en", 2, 1, "2 of 1 medium match the filter")]
    [InlineData("en", 2, 2, "2 of 2 media match the filter")]
    public void FilterNounFollowsTotalAndVerbFollowsShownCount(string language, int shown, int total, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, CountPhrases.MediaFilterSummary(shown, total));
    }

    [Theory]
    [InlineData("cs", "Filtr", "Média")]
    [InlineData("es", "Filtro", "Medios")]
    [InlineData("fr", "Filtre", "Médias")]
    [InlineData("it", "Filtro", "Media")]
    [InlineData("pl", "Filtr", "Media")]
    [InlineData("pt-BR", "Filtro", "Mídias")]
    [InlineData("ru", "Фильтр", "Медиа")]
    [InlineData("ja", "フィルター", "メディア")]
    [InlineData("ko", "필터", "미디어")]
    [InlineData("zh-Hans", "过滤条件", "媒体")]
    public void BundledFilterTemplatesAcceptTheClosedTotalPhrase(string language, string filterLabel, string mediaLabel)
    {
        Texts.SetLanguage(language);
        foreach (int shown in new[] { 0, 1, 2 })
        foreach (int total in new[] { 0, 1, 2 })
            Assert.Equal($"{filterLabel}: {shown} / ({mediaLabel}: {total})", CountPhrases.MediaFilterSummary(shown, total));
    }

    [Theory]
    [InlineData("de", 0, "Nur die 0 Medien ohne Medien-Tags zeigen: ein Startpunkt fürs Taggen")]
    [InlineData("de", 1, "Nur das 1 Medium ohne Medien-Tags zeigen: ein Startpunkt fürs Taggen")]
    [InlineData("de", 2, "Nur die 2 Medien ohne Medien-Tags zeigen: ein Startpunkt fürs Taggen")]
    [InlineData("en", 0, "Show only the 0 media without media tags: a place to start tagging")]
    [InlineData("en", 1, "Show only the 1 medium without media tags: a place to start tagging")]
    [InlineData("en", 2, "Show only the 2 media without media tags: a place to start tagging")]
    public void UntaggedHintFollowsTheMediaCount(string language, int count, string expected)
    {
        Texts.SetLanguage(language);
        Assert.Equal(expected, Texts.Count("media.untagged.hint", count));
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
