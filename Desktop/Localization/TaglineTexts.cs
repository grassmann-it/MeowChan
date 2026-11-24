namespace MeowChan.Desktop.Localization;

/// <summary>
/// The header tagline texts: the fixed lead sentence, the rotating quips (<c>shell.tagline.quip.&lt;id&gt;</c>, one per
/// <see cref="MeowChan.Desktop.TaglineCatalog"/> entry, freely translated rather than word for word), the tagline's context
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
        ("shell.tagline.quip.flashbang", "light mode now less flashbang", "heller Modus, weniger Blendgranaten-Effekt"),
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
        ("shell.tagline.quip.jdownloader", "almost as bloated as JDownloader", "fast so aufgebläht wie JDownloader"),
        ("shell.tagline.quip.worksonmine", "works on my machine", "läuft auf meinem Rechner"),
        ("shell.tagline.quip.noai", "wanted to bundle AI tagging, but the download would explode", "wollte KI-Tagging mitliefern, aber der Download würde explodieren"),
        ("shell.tagline.quip.whatbooru", "what even is a booru?", "was ist eigentlich ein Booru?"),
        ("shell.tagline.quip.features", "80% more features, 20% more bugs", "80 % mehr Funktionen, 20 % mehr Bugs"),
        ("shell.tagline.quip.settingsforsettings", "now with settings for your settings", "jetzt mit Einstellungen für deine Einstellungen"),
        ("shell.tagline.quip.tooltipflicker", "tooltips no longer flicker (mostly)", "Tooltips flackern nicht mehr (meistens)"),
        ("shell.tagline.quip.bugfeature", "not a bug, an undocumented feature", "kein Bug, ein undokumentiertes Feature"),
        ("shell.tagline.quip.greenhere", "all tests green, on at least one machine", "alle Tests grün, auf mindestens einem Rechner"),
        ("shell.tagline.quip.toolbar14", "toolbar rebuilt 14 times, now it's perfect", "Werkzeugleiste 14-mal umgebaut, jetzt ist sie perfekt"),
        ("shell.tagline.quip.pixelsbyhand", "every pixel moved by hand, some twice", "jedes Pixel von Hand verschoben, manche zweimal"),
        ("shell.tagline.quip.scopecreep", "started as a thread saver, got a bit out of hand", "fing als Thread-Retter an, ist etwas ausgeufert"),
        ("shell.tagline.quip.bugshide", "bugs hide best right before a release", "Bugs verstecken sich am besten kurz vor dem Release"),
        ("shell.tagline.quip.tidytoolbars", "tidy toolbars, tidier thoughts", "aufgeräumte Werkzeugleisten, aufgeräumtere Gedanken"),
        ("shell.tagline.quip.keyboardfirst", "keyboard first, mouse welcome too", "erst die Tastatur, die Maus ist auch willkommen"),
        ("shell.tagline.quip.galleryreader", "gallery for browsing, reader for reading", "Galerie zum Stöbern, Reader zum Lesen"),
        ("shell.tagline.quip.evenspacing", "equal spacing everywhere, give or take a pixel", "überall gleiche Abstände, plus minus ein Pixel"),
        ("shell.tagline.quip.lightdark", "light or dark, equally well dressed", "hell oder dunkel, immer gut angezogen"),
        ("shell.tagline.quip.fewerclicks", "fewer clicks to your media, ideally", "weniger Klicks bis zu deinen Medien, idealerweise"),
        ("shell.tagline.quip.boorutabs", "now with booru tabs, because one tab is never enough", "jetzt mit Booru-Tabs, denn ein Tab reicht nie"),
        ("shell.tagline.quip.downloadall", "download all now, sort it out later", "erst alles laden, später sortieren"),
        ("shell.tagline.quip.threadswitch", "a switch for the thread list, finally", "endlich ein Schalter für die Thread-Liste"),
        ("shell.tagline.quip.tagquickfix", "tagging quick fixes for the tidy-minded", "Tagging-Schnellkorrekturen für Ordnungsliebende"),
        ("shell.tagline.quip.taglinesfeature", "these taglines are a feature now", "diese Taglines sind jetzt ein Feature"),
        ("shell.tagline.quip.yourarchive", "an archive that belongs to you", "ein Archiv, das dir gehört"),
        ("shell.tagline.quip.sortnothoard", "sorting your media instead of just hoarding it", "Medien ordnen statt nur horten"),
        ("shell.tagline.quip.fetchthem", "Gonna fetch them all", "Ich schnapp sie mir alle"),
        ("shell.tagline.quip.tabsbreed", "the tabs are multiplying", "die Tabs vermehren sich"),
        ("shell.tagline.quip.pawprint", "every archive needs a paw print", "jedes Archiv braucht einen Pfotenabdruck"),
        ("shell.tagline.quip.sortedquips", "quip by quip, neatly sorted", "Spruch für Spruch, schön sortiert"),
        ("shell.tagline.quip.snowed", "snowed in with the archive", "eingeschneit mit dem Archiv"),
        ("shell.tagline.quip.trickorthread", "trick or thread?", "Thread oder Saures?"),
        ("settings.headerTagline.category.general", "General", "Allgemein"),
        ("settings.headerTagline.category.release", "By release", "Nach Version"),
        ("settings.headerTagline.category.seasonal", "Seasonal", "Saisonal"),
        ("shell.tagline.menuAnother", "Another tagline", "Andere Tagline"),
        ("shell.tagline.menuHide", "Hide tagline", "Tagline ausblenden"),
        ("settings.headerTagline.label", "Header tagline", "Tagline in der Kopfleiste"),
        ("settings.headerTagline.hint", "A short line beside the MeowChan name. The first start after an update shows the line of the new release (not with Off or Always the same). When the window is too narrow, the line shortens or disappears and is never cut off.", "Eine kurze Zeile neben dem MeowChan-Namen. Der erste Start nach einem Update zeigt die Zeile der neuen Version (nicht bei „Aus“ oder „Immer dieselbe“). Ist das Fenster zu schmal, wird die Zeile kürzer oder verschwindet; abgeschnitten wird sie nie."),
        ("settings.headerTagline.off", "Off", "Aus"),
        ("settings.headerTagline.fixed", "Always the same", "Immer dieselbe"),
        ("settings.headerTagline.daily", "New every day", "Täglich neu"),
        ("settings.headerTagline.perStart", "New at every start", "Bei jedem Start neu"),
        ("settings.headerTagline.interval", "New every N minutes", "Neu alle N Minuten"),
        ("settings.headerTagline.fixedLabel", "Tagline for “Always the same”", "Tagline für „Immer dieselbe“"),
        ("settings.headerTagline.fixedHint", "Only used with “Always the same”.", "Wird nur bei „Immer dieselbe“ verwendet."),
        ("settings.headerTagline.intervalLabel", "Minutes between changes", "Minuten zwischen den Wechseln"),
        ("settings.headerTagline.intervalHint", "Only used with “New every N minutes”: the line changes while MeowChan runs. Default {0}, from {1} to {2}.", "Wird nur bei „Neu alle N Minuten“ verwendet: die Zeile wechselt, solange MeowChan läuft. Standard {0}, von {1} bis {2}.")
    ];
}
