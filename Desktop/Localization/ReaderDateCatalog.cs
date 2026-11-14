namespace MeowChan.Desktop.Localization;

/// <summary>Reader date units and relative-date labels. Packs are registered by the parent integration.</summary>
internal static class ReaderDateCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("reader.date.justNow", "just now", "gerade eben"),
        ("reader.date.ago", "{0} ago", "vor {0}"),
        ("reader.date.sinceOp", "+{0}", "+{0}"),
        ("reader.date.unit.d", "{0} d", "{0} Tg."),
        ("reader.date.unit.h", "{0} h", "{0} Std."),
        ("reader.date.unit.min", "{0} min", "{0} Min."),
        ("reader.date.unit.s", "{0} s", "{0} Sek."),
    };
}
