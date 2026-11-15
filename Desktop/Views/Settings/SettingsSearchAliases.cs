using System;
using System.Collections.Generic;

namespace MeowChan;

/// <summary>
/// Synonyms for the settings search: words people use for a setting that its label does not contain ("dark mode" for the
/// colour scheme, "speicher" for a cache size). They are search data, not shown text, so they are kept in English and German
/// for every interface language; the English texts of the labels are searched as well. A setting is named by its input
/// ("ClipboardUrlLimitInput" has the key "ClipboardUrlLimit").
/// </summary>
internal static class SettingsSearchAliases
{
    private static readonly Dictionary<string, string> ByKey = new(StringComparer.Ordinal)
    {
        ["WorkspaceMode"] = "view mode minimal simple advanced barebones ansicht modus einfach erweitert arbeitsansicht",
        ["Language"] = "locale translation sprache übersetzung deutsch english",
        ["Theme"] = "dark mode light mode dark light dunkel hell dunkelmodus nachtmodus farbschema appearance design color scheme darstellung",
        ["ThemeColors"] = "accent color colour customize palette presets contrast orange blue green violet pink grey akzent akzentfarbe farben anpassen vorlagen kontrast orange blau grün violett rosa grau",
        ["Animations"] = "motion effects transitions reduce motion bewegung effekte animation weniger bewegung",
        ["CompactDensity"] = "density spacing dense kompakt dichte abstand zeilenabstand",
        ["ReadingFontSize"] = "font text size zoom schrift schriftgröße lesen lesbarkeit",
        ["ReaderPostWidth"] = "width breite layout columns spalten",
        ["GalleryPlaybackMode"] = "slideshow autoplay diashow wiedergabe",
        ["GallerySlideshowSeconds"] = "slideshow interval delay diashow intervall sekunden",
        ["GalleryMediaFocus"] = "focus fullscreen fokus vollbild",
        ["GalleryOverlayControls"] = "controls buttons bedienelemente steuerung",
        ["GalleryAutoHideControls"] = "hide idle auto ausblenden",
        ["GalleryOverlayPostContext"] = "post context kontext text",
        ["MediaFocusHidesHeader"] = "media focus mode fullscreen header title bar hide immersive medien fokusmodus vollbild kopfzeile titelleiste ausblenden",
        ["GalleryFilmstrip"] = "filmstrip thumbnails strip thumbnail preview filmstreifen vorschau vorschaubilder miniaturen layout reset gallery layout blocks galerie-layout zurücksetzen blöcke",
        ["MediaBadgeCollapseBelow"] = "badge badges marks flags dot dots collapse thumbnail size markierungen punkte abzeichen vorschau",
        ["MediaBadgeHideBelow"] = "badge badges marks flags hide thumbnail size markierungen ausblenden abzeichen vorschau",
        ["MediaBadgeMaxPerCorner"] = "badge badges marks flags corner thumbnail markierungen ecke abzeichen vorschau",
        ["VideoVolume"] = "audio sound volume lautstärke ton",
        ["RememberReadingPosition"] = "resume continue bookmark position weiterlesen lesestand",
        ["ReadingHistoryLimit"] = "history recent threads continue resume earlier places verlauf zuletzt gelesen weiterlesen frühere stellen",
        ["ReadingHistoryMenuEntries"] = "history menu recent threads continue resume right-click list verlauf menü zuletzt gelesen weiterlesen rechtsklick",
        ["ReaderTextlessLargeImages"] = "images pictures bigger larger no text image only media bilder größer ohne text nur bild medien",
        ["ReaderTextlessMediaWidth"] = "images pictures width bigger larger no text image only bilder breite größer ohne text nur bild",
        ["ReaderFocusedMediaEnlarge"] = "focus focused selected active post media enlarge zoom bigger fokus markiert vergrößern medien",
        ["ReaderFocusedMediaScalePercent"] = "focus focused selected active post media enlarge zoom percent fokus markiert vergrößern prozent",
        ["ReaderHoverEnlarge"] = "hover pointer mouse preview zoom magnify enlarge überfahren zeiger maus vorschau vergrößern lupe",
        ["ReaderHoverEnlargeSize"] = "hover pointer mouse preview zoom size überfahren zeiger maus vorschau größe",
        ["ReaderHoverEnlargeDelayMs"] = "hover pointer mouse preview delay überfahren zeiger maus vorschau verzögerung",
        ["ReaderNavigationMode"] = "navigation arrows next previous thread post bar hide always show off keyboard only pfeile weiter zurück nächster voriger navigationsleiste ausblenden immer anzeigen aus nur tastatur",
        ["ReaderNavigationShowPosition"] = "navigation counter position number thread post bar x of y zähler position nummer navigationsleiste von",
        ["ReaderMediaSidebar"] = "sidebar side panel column media large full height focused post leiste seitenleiste medien groß volle höhe markiert",
        ["ReaderMediaSidebarScope"] = "sidebar archive view large reader always where scope leiste seitenleiste archivansicht großer reader immer wo",
        ["ReaderMediaSidebarSide"] = "sidebar side left right position leiste seitenleiste links rechts seite",
        ["ReaderMediaSidebarWidth"] = "sidebar width splitter divider column leiste seitenleiste breite trennlinie",
        ["ReaderMediaSidebarMinimumWidth"] = "sidebar narrow window hide collapse width leiste seitenleiste schmal fenster ausblenden breite",
        ["ReaderMediaClick"] = "click medium image video inline play expand open in post sidebar gallery webm mp4 movie player in place klick medium bild video abspielen aufklappen vergrößern im post seitenleiste galerie wiedergabe",
        ["ReaderInlineVideoAutoplayMuted"] = "video autoplay automatic muted sound silent start automatisch stumm ton start",
        ["ReaderInlineVideoMaxPlayers"] = "video players memory limit count instances player speicher grenze anzahl",
        ["ReaderInlineVideoMaxFileMiB"] = "video size large file limit mib megabytes größe groß datei grenze",
        ["MediaFolders"] = "folder folders directory directories library local files scan rescan hashing add folder ordner medienordner verzeichnis verzeichnisse bibliothek lokale dateien durchsuchen hinzufügen",
        ["ReaderMediaStripLimit"] = "sidebar thumbnails strip files several images count limit leiste vorschaubilder streifen dateien mehrere bilder anzahl grenze",
        ["ReaderPreviewPrefetchPosts"] = "preload prefetch previews thumbnails ahead scrolling jump stable posts vorausladen vorschaubilder voraus scrollen springen stabil posts",
        ["ReaderPreviewSizeMemory"] = "remembered preview sizes memory aspect jump layout gemerkte vorschaugrößen speicher seitenverhältnis springen",
        ["ReaderExpandReplies"] = "replies answers comments expand open automatically auto unfold nested cards antworten kommentare aufklappen automatisch öffnen karten",
        ["ReaderMediaGrid"] = "grid tiles several images columns post layout raster kacheln mehrere bilder spalten anordnung",
        ["ReaderMediaGridTextLimit"] = "grid short text characters limit several images raster kurzer text zeichen grenze mehrere bilder",
        ["ShowShortcutHelp"] = "shortcuts keyboard hints tastenkürzel tastatur hilfe",
        ["DownloadPath"] = "folder directory location target ordner verzeichnis speicherort ziel pfad",
        ["RecheckTimer"] = "interval refresh poll check timer intervall prüfen aktualisieren",
        ["Bandwidth"] = "speed throttle mbit geschwindigkeit tempo drosseln",
        ["ParallelDownloads"] = "concurrent threads connections gleichzeitig verbindungen",
        ["ParallelDownloadsSafety"] = "concurrent safety maximum sicherheit",
        ["Reserve"] = "disk space free space harddisk festplatte speicherplatz frei",
        ["ClipboardUrlLimit"] = "clipboard paste zwischenablage einfügen",
        ["ClipboardTextLimitKiB"] = "clipboard paste zwischenablage einfügen",
        ["MonitorClipboardUrls"] = "clipboard paste watch automatic zwischenablage einfügen automatisch",
        ["AutomaticUpdates"] = "update upgrade version aktualisierung",
        ["PrereleaseUpdates"] = "beta preview vorabversion testversion",
        ["InformationNotifications"] = "notifications toast alerts messages benachrichtigungen meldungen",
        ["WarningNotifications"] = "notifications toast alerts messages benachrichtigungen meldungen",
        ["ErrorNotifications"] = "notifications toast alerts messages benachrichtigungen meldungen",
        ["BoardAdmissionWizard"] = "board assistant wizard assistent",
        ["RestoreSession"] = "session restore startup wiederherstellen sitzung start",
        ["CloseToTray"] = "tray system tray taskbar background infobereich schließen hintergrund",
        ["MinimizeToTray"] = "tray system tray taskbar background infobereich minimieren hintergrund",
        ["StatsHeaderIcons"] = "statistics header icons symbols words network disk graph statistik kopfleiste symbole wörter netz platte",
        ["FooterLayoutSection"] = "footer navigation bar buttons pages order reorder show hide visible taskbar fußleiste fusszeile navigationsleiste knöpfe seiten reihenfolge ausblenden einblenden sichtbar",
        ["ShowBooruActions"] = "booru danbooru actions tools page show hide footer master aktionen anzeigen ausblenden fußleiste",
        ["HeaderTagline"] = "tagline slogan quip fortune motto header title bar spruch kopfleiste titelleiste kopfzeile",
        ["HeaderTaglineFixedId"] = "tagline slogan quip fortune motto fixed spruch fest",
        ["StatsHeaderGraph"] = "statistics graph network disk traffic statistik diagramm netz platte",
        ["StatsPersist"] = "statistics history save verlauf speichern statistik",
        ["TagStampSize"] = "stamp stempel quick tagging shortcut schnell taggen kürzel tags set",
        ["TagQueueWalkWindow"] = "walk window batch round queue durchgang runde fenster warteschlange tagging-warteschlange",
        ["TagQueueUndoDepth"] = "undo back history steps queue rückgängig zurücknehmen schritte verlauf warteschlange tagging-warteschlange",
        ["MediaTagListTop"] = "booru mode tag list sidebar tag counts top show all booru-modus tag-liste seitenleiste anzahl alle anzeigen",
        ["MediaTagListWidth"] = "tag list width sidebar handle resize wide narrow booru layout breite tag-liste seitenleiste layout schmal breit",
        ["MediaPanelWidth"] = "media panel width sidebar preview large medium split view handle resize wide narrow medienleiste breite vorschau groß seitenleiste schmal breit",
        ["MediaPanelMinimumPageWidth"] = "media panel hide narrow page width threshold auto split view medienleiste ausblenden schmal seitenbreite schwelle automatisch",
        ["TaggingCardCount"] = "cards tinder karten kartenmodus card mode",
        ["TaggingCardsDefault"] = "cards tinder karten kartenmodus card mode start default beginnen standard",
    };

    public static IReadOnlyList<string> For(string? key, string categoryId)
    {
        var result = new List<string>();
        if (key != null && ByKey.TryGetValue(key, out string? own)) result.Add(own);
        // A setting a quick settings popover offers is also found by the popover's words.
        if (key != null && MeowChan.Desktop.QuickSettingsSurfaces.Contains(key))
            result.Add("popover quick settings schnelleinstellungen");
        if (key != null)
        {
            if (key.Contains("Cache", StringComparison.Ordinal)) result.Add("cache puffer zwischenspeicher speicher memory ram");
            if (key.EndsWith("MiB", StringComparison.Ordinal) || key.EndsWith("GiB", StringComparison.Ordinal)
                || key.EndsWith("KiB", StringComparison.Ordinal) || key.Contains("Heap", StringComparison.Ordinal))
                result.Add("size memory storage megabytes gigabytes speicher größe");
            if (key.Contains("Limit", StringComparison.Ordinal) || key.Contains("Max", StringComparison.Ordinal))
                result.Add("limit limits maximum budget quota grenze grenzen höchstens");
            if (key.Contains("Heap", StringComparison.Ordinal) || key.Contains("Workers", StringComparison.Ordinal))
                result.Add("memory ram performance process worker arbeitsspeicher prozess");
            if (key.StartsWith("FolderSource", StringComparison.Ordinal)) result.Add("folder folders media folders directory scan hashing ordner medienordner verzeichnis durchsuchen");
            if (key.StartsWith("Import", StringComparison.Ordinal)) result.Add("import einlesen");
            if (key.StartsWith("Export", StringComparison.Ordinal)) result.Add("export ausgabe");
            if (key.Contains("Tag", StringComparison.Ordinal)) result.Add("tags labels schlagwörter");
            if (key.StartsWith("TagQueue", StringComparison.Ordinal)) result.Add("tagging queue warteschlange vormerken tagging-warteschlange");
            if (key.StartsWith("TaggingCard", StringComparison.Ordinal)) result.Add("cards tinder decision entscheidung karten kartenmodus card mode");
            if (key.Contains("Favorite", StringComparison.Ordinal)) result.Add("favorites favoriten");
            if (key.Contains("Bookmark", StringComparison.Ordinal)) result.Add("bookmarks lesezeichen");
            if (key.StartsWith("Stats", StringComparison.Ordinal)) result.Add("statistics graph statistik diagramm");
        }
        if (categoryId == "settings.worker-budgets") result.Add("storage resources limits speicher ressourcen grenzen");
        if (categoryId == "settings.shortcuts") result.Add("keyboard shortcuts hotkeys keybindings keys tastatur tastenkürzel kürzel belegung");
        return result;
    }
}
