namespace MeowChan.Desktop.Localization;

// Reader quote markers (P3): "(OP)" or "(other thread)" behind a reference, and their screen-reader forms.
internal static class ReaderQuoteMarksCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("reader.quote.opMarker", "(OP)", "(OP)"),
        ("reader.quote.crossMarker", "(other thread)", "(anderer Thread)"),
        ("reader.quote.opAccessible", "opening post", "Eröffnungspost"),
        ("reader.quote.crossAccessible", "in another thread", "in einem anderen Thread"),
    };
}
