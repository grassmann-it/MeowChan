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
}
