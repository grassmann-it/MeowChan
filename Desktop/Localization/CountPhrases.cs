namespace MeowChan.Desktop.Localization;

/// <summary>Independent counters are closed phrases, never one shared plural category.</summary>
internal static class CountPhrases
{
    internal static string MediaSummary(long media, long threads, long missing = 0)
    {
        string mediaPhrase = Texts.Count("media.count.media", media);
        string threadPhrase = Texts.Count("media.count.threads", threads);
        return missing > 0
            ? Texts.T("media.countMissing", mediaPhrase, threadPhrase, missing)
            : Texts.T("media.count", mediaPhrase, threadPhrase);
    }

    // The total owns the noun, but the shown count owns the sentence's verb.
    internal static string MediaFilterSummary(long shown, long total) =>
        Texts.Count("media.filter.count", shown, shown, Texts.Count("media.count.media", total));

    internal static string ArchiveDeletionSummary(long threads, long files, long bytes) =>
        Texts.T("archive.delete.summary",
            Texts.Count("archive.delete.threads", threads),
            Texts.Count("archive.delete.files", files),
            bytes < 1024 ? Texts.Count("archive.delete.bytes", bytes) : SizeText.Format(bytes));
}
