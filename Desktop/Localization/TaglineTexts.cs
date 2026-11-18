namespace MeowChan.Desktop.Localization;

/// <summary>
/// The header tagline texts: the fixed lead sentence, the rotating quips (<c>shell.tagline.quip.&lt;id&gt;</c>, one per
/// <see cref="MeowChan.Desktop.Shell.TaglineCatalog"/> entry, freely translated rather than word for word), the tagline's context
/// menu and the "Header tagline" setting. English and German are built in; the other ten languages are in the bundled packs.
/// <c>lead + " — " + quip.cats</c> is <c>shell.subtitle</c> (the About window keeps using that key).
/// </summary>
internal static class TaglineTexts
{
    internal static readonly (string Key, string English, string German)[] Entries =
    [
        ("shell.tagline.quip.cats", "now with 100% more cats", "jetzt mit 100 % mehr Katzen"),
        ("shell.tagline.lead", "Open-source imageboard downloader", "Open-Source-Imageboard-Downloader"),
        ("shell.tagline.quip.plain", "archives threads, keeps the media", "archiviert Threads und behält die Medien"),
        ("shell.tagline.quip.booru", "now with booru support", "jetzt mit Booru-Unterstützung"),
        ("shell.tagline.quip.flashbang", "light mode now less flashbang", "heller Modus, jetzt mit weniger Blendgranate"),
        ("shell.tagline.quip.workviews", "now with work views for every taste", "jetzt mit Arbeitsansichten für jeden Geschmack"),
        ("shell.tagline.quip.offline", "your archive stays on your computer", "dein Archiv bleibt auf deinem Rechner"),
        ("shell.tagline.quip.notfound", "saving threads before they 404", "rettet Threads vor dem 404"),
        ("shell.tagline.quip.hoard", "hoarding made tidy", "Horten, aber ordentlich"),
        ("shell.tagline.quip.tags", "now with tags for your inner librarian", "jetzt mit Tags für die innere Bibliothekarin"),
        ("shell.tagline.quip.vibetested", "vibe coded, hard tested", "nach Gefühl programmiert, hart getestet"),
        ("shell.tagline.quip.pink", "have you tried the pink theme in dark mode?", "schon das pinke Theme im Dark Mode probiert?"),
        ("shell.tagline.quip.workflow", "tag it, rate it, export it", "taggen, bewerten, exportieren"),
        ("shell.tagline.quip.reader", "reads like a book, works offline", "liest sich wie ein Buch, funktioniert offline"),
        ("shell.tagline.quip.units", "now with proper KiB, MiB and GiB", "jetzt mit richtigen KiB, MiB und GiB"),
        ("shell.tagline.quip.tray", "quietly working away in the tray", "arbeitet leise im Infobereich vor sich hin"),
        ("shell.tagline.menuAnother", "Another tagline", "Andere Tagline"),
        ("shell.tagline.menuHide", "Hide tagline", "Tagline ausblenden"),
        ("settings.headerTagline.label", "Header tagline", "Tagline in der Kopfleiste"),
        ("settings.headerTagline.hint", "A short line beside the MeowChan name. The first start after an update shows the line of the new release (not with Off or Always the same). When the window is too narrow, the line shortens or disappears and is never cut off.", "Eine kurze Zeile neben dem MeowChan-Namen. Der erste Start nach einem Update zeigt die Zeile der neuen Version (nicht bei „Aus“ oder „Immer dieselbe“). Ist das Fenster zu schmal, wird die Zeile kürzer oder verschwindet; abgeschnitten wird sie nie."),
        ("settings.headerTagline.off", "Off", "Aus"),
        ("settings.headerTagline.fixed", "Always the same", "Immer dieselbe"),
        ("settings.headerTagline.daily", "New every day", "Täglich neu"),
        ("settings.headerTagline.perStart", "New at every start", "Bei jedem Start neu"),
        ("settings.headerTagline.fixedLabel", "Tagline for “Always the same”", "Tagline für „Immer dieselbe“"),
        ("settings.headerTagline.fixedHint", "Only used with “Always the same”.", "Wird nur bei „Immer dieselbe“ verwendet.")
    ];
}
