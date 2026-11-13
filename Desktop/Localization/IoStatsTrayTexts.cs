namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the tray tooltip; keys start with "stats.tray.". The counted text has a
/// <c>.one</c> form. The Spanish, French and Russian texts live in the bundled packs.
/// </summary>
internal static class IoStatsTrayTexts
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("stats.tray.rates", "↓ {0} · Disk {1}", "↓ {0} · Platte {1}"),
        ("stats.tray.activeCount", "{0} downloads active", "{0} Downloads aktiv"),
        ("stats.tray.activeCount.one", "{0} download active", "{0} Download aktiv"),
        ("stats.tray.today", "Today ↓ {0}", "Heute ↓ {0}"),
        ("stats.tray.idle", "idle", "Leerlauf"),
    ];
}
