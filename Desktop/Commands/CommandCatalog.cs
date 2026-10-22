using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Commands;

/// <summary>Maintained runtime copy of the R09-08d action inventory. No docs filesystem dependency.</summary>
public static class CommandCatalog
{
    private static readonly CommandDefinition[] Entries =
    {
        new("navigation.downloads", "command.navigation.downloads", "Downloads", "Downloads", "download", "Primary+1", CommandScope.Sources | CommandScope.Application, new[] { "footer", "sources" }),
        new("navigation.archive", "command.navigation.archive", "Archive", "Archiv", "archive", "Primary+2", CommandScope.Application, new[] { "footer", "app-menu", "tools-menu" }),
        new("navigation.rules", "command.navigation.rules", "Rules", "Regeln", "sliders", "Primary+3", CommandScope.Application, new[] { "footer", "app-menu", "tools-menu" }),
        new("navigation.sources", "command.navigation.sources", "Sources", "Quellen", "globe", "Primary+4", CommandScope.Application, new[] { "footer", "app-menu", "tools-menu" }),
        new("navigation.diagnostics", "command.navigation.diagnostics", "Diagnostics", "Diagnose", "activity", "Primary+5", CommandScope.Sources | CommandScope.Application, new[] { "footer", "app-menu", "tools-menu", "sources" }),
        new("navigation.settings", "command.navigation.settings", "Settings", "Einstellungen", "settings", "Primary+,", CommandScope.Application, new[] { "footer", "app-menu" }),
        new("navigation.about", "command.navigation.about", "About MeowChan", "Über MeowChan", "info", null, CommandScope.Settings | CommandScope.Application, new[] { "app-menu", "settings:planned" }),
        new("navigation.tools-menu", "command.navigation.tools-menu", "Tools", "Werkzeuge", "tool", null, CommandScope.Application, new[] { "footer" }),
        new("navigation.app-menu", "command.navigation.app-menu", "App menu", "Appmenü", "more-horizontal", null, CommandScope.Application, new[] { "header" }),
        new("app.show", "command.app.show", "Show MeowChan", "MeowChan anzeigen", "monitor", null, CommandScope.Application, new[] { "tray" }),
        new("app.quit", "command.app.quit", "Quit", "Beenden", "power", null, CommandScope.Application, new[] { "app-menu", "tray", "startup-error" }),
        new("session.pause", "command.session.pause", "Pause / Resume", "Pausieren / Fortsetzen", "pause", "Primary+Shift+P", CommandScope.Application, new[] { "header", "app-menu", "workspace-keyboard", "tray:planned" }),
        new("appearance.theme", "command.appearance.theme", "Change theme", "Farbschema wechseln", "moon", null, CommandScope.Settings | CommandScope.Application, new[] { "header", "settings" }),
        new("window.minimize", "command.window.minimize", "Minimize window", "Fenster minimieren", "minus", null, CommandScope.Application, new[] { "window-caption" }),
        new("window.maximize", "command.window.maximize", "Maximize / Restore window", "Fenster maximieren / Wiederherstellen", "square", null, CommandScope.Application, new[] { "window-caption", "titlebar-double-click" }),
        new("window.close", "command.window.close", "Close window", "Fenster schließen", "x", null, CommandScope.Application, new[] { "window-caption" }),
        new("input.focus-url", "command.input.focus-url", "Focus URL input", "URL-Eingabe fokussieren", "link", "Primary+L", CommandScope.Application, new[] { "workspace" }),
        new("watch.add-url", "command.watch.add-url", "Add URL", "URL hinzufügen", "plus", null, CommandScope.Sources | CommandScope.Downloads, new[] { "downloads-url-bar", "sources-url-bar" }),
        new("watch.import-clipboard", "command.watch.import-clipboard", "Add URLs from clipboard", "URLs aus Zwischenablage hinzufügen", "clipboard", "Primary+V", CommandScope.Downloads, new[] { "tray", "downloads-list-keyboard", "empty-add" }),
        new("selection.select-all", "command.selection.select-all", "Select all", "Alle auswählen", "check-square", "Primary+A", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-list", "downloads-board-list", "archive-cards", "archive-table" }),
        new("thread.read", "command.thread.read", "Read posts", "Beiträge lesen", "file-text", "Enter", CommandScope.Archive | CommandScope.Reader | CommandScope.Gallery | CommandScope.Downloads, new[] { "downloads-thread-menu", "archive-card-menu", "archive-table-menu", "reader-toolbar", "gallery-empty-state", "thread-double-click" }),
        new("gallery.open", "command.gallery.open", "Open in gallery", "In Galerie öffnen", "image", "Shift+Enter", CommandScope.Archive | CommandScope.Reader | CommandScope.Media | CommandScope.Downloads, new[] { "downloads-thread-menu", "archive-card-menu", "archive-table-menu", "reader-toolbar", "media-list", "media-tiles", "inline-attachment" }),
        new("gallery.open-results", "command.gallery.open-results", "Media from matching threads", "Medien der Treffer", "image", null, CommandScope.Archive, new[] { "archive-results-toolbar" }),
        new("thread.details", "command.thread.details", "Show details", "Details anzeigen", "info", "Alt+Enter", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-toolbar", "archive-card-menu", "archive-table-menu" }),
        new("inspector.close", "command.inspector.close", "Close details", "Details schließen", "x", null, CommandScope.Downloads, new[] { "downloads-inspector" }),
        new("watch.check", "command.watch.check", "Check now", "Jetzt prüfen", "refresh-cw", "F5", CommandScope.Archive | CommandScope.Diagnostics | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "diagnostics" }),
        new("watch.retry-media", "command.watch.retry-media", "Retry missing media", "Fehlende Medien erneut laden", "download-cloud", "Shift+F5", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu" }),
        new("rules.edit-scope", "command.rules.edit-scope", "Media rules…", "Medienregeln…", "sliders", "Primary+R", CommandScope.Archive | CommandScope.Sources | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "archive-card-menu", "archive-table-menu", "sources" }),
        new("board.filters", "command.board.filters", "Thread filters…", "Threadfilter…", "filter", "Primary+Shift+R", CommandScope.Downloads, new[] { "downloads-board-menu" }),
        new("location.open-folder", "command.location.open-folder", "Open folder", "Ordner öffnen", "folder", "Primary+O", CommandScope.Archive | CommandScope.Media | CommandScope.Dialog | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "media-preview", "export-result", "tray" }),
        new("location.open-source", "command.location.open-source", "Open source in browser", "Quelle im Browser öffnen", "external-link", "Primary+Shift+O", CommandScope.Archive | CommandScope.Sources | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "sources" }),
        new("selection.copy-links", "command.selection.copy-links", "Copy link(s)", "Link(s) kopieren", "copy", "Primary+C", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu" }),
        new("watch.copy-all-links", "command.watch.copy-all-links", "Copy all URLs", "Alle URLs kopieren", "copy", null, CommandScope.Downloads | CommandScope.Application, new[] { "downloads-footer" }),
        new("watch.remove", "command.watch.remove", "Remove from watch list", "Aus Beobachtung entfernen", "eye-off", "Delete", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu" }),
        new("archive.delete-permanently", "command.archive.delete-permanently", "Delete permanently…", "Dauerhaft löschen …", "x-circle", "Shift+Delete", CommandScope.Archive, new[] { "archive-card-menu", "archive-table-menu" }),
        new("archive.previous-page", "command.archive.previous-page", "Previous results page", "Vorherige Ergebnisseite", "chevron-left", "Alt+PageUp", CommandScope.Archive, new[] { "archive-results-list" }),
        new("archive.next-page", "command.archive.next-page", "Next results page", "Nächste Ergebnisseite", "chevron-right", "Alt+PageDown", CommandScope.Archive, new[] { "archive-results-list" }),
        new("archive.select-page", "command.archive.select-page", "Select all on this page", "Alle auf dieser Seite auswählen", "check-square", "Primary+A", CommandScope.Archive, new[] { "archive-results-list" }),
        new("input.focus-download-search", "command.input.focus-download-search", "Search local downloads", "Lokale Downloads durchsuchen", "search", "Primary+F", CommandScope.Downloads, new[] { "downloads" }),
        new("export.open-all-results-html-dialog", "command.export.open-all-results-html-dialog", "Export all results as HTML…", "Alle Treffer als HTML exportieren…", "download", null, CommandScope.Archive, new[] { "archive-results-toolbar" }),
        new("archive.refresh", "command.archive.refresh", "Refresh archive", "Archiv aktualisieren", "refresh-cw", null, CommandScope.Archive, new[] { "archive-toolbar" }),
        new("archive.search", "command.archive.search", "Search", "Suchen", "search", null, CommandScope.Archive, new[] { "archive-toolbar", "archive-search-field" }),
        new("archive.filters-toggle", "command.archive.filters-toggle", "Filters and sorting", "Filter und Sortierung", "filter", null, CommandScope.Archive, new[] { "archive-toolbar" }),
        new("archive.thread-view", "command.archive.thread-view", "Choose archive view", "Archivansicht wählen", "columns", null, CommandScope.Archive, new[] { "archive-toolbar" }),
        new("archive.media-view", "command.archive.media-view", "Choose media view", "Medienansicht wählen", "grid", null, CommandScope.Archive | CommandScope.Media, new[] { "archive-media-toolbar" }),
        new("pagination.previous", "command.pagination.previous", "Previous page", "Vorherige Seite", "chevron-left", null, CommandScope.Archive | CommandScope.Reader, new[] { "archive-thread-pages", "reader-post-pages", "reader-reference-pages" }),
        new("pagination.next", "command.pagination.next", "Next page", "Nächste Seite", "chevron-right", null, CommandScope.Archive | CommandScope.Reader, new[] { "archive-thread-pages", "reader-post-pages", "reader-reference-pages" }),
        new("archive.back-to-list", "command.archive.back-to-list", "Back to thread list", "Zur Threadliste", "arrow-left", null, CommandScope.Reader, new[] { "embedded-reader" }),
        new("archive.capture-toggle", "command.archive.capture-toggle", "Start / stop post capture", "Posterfassung starten / stoppen", "circle", null, CommandScope.Reader | CommandScope.Application, new[] { "reader-header" }),
        new("reader.close", "command.reader.close", "Close reader", "Leser schließen", "x", "Escape", CommandScope.Reader, new[] { "large-reader-toolbar" }),
        new("reader.expand", "command.reader.expand", "Expand reader", "Leser vergrößern", "external-link", null, CommandScope.Reader, new[] { "embedded-reader-toolbar" }),
        new("reader.history-back", "command.reader.history-back", "Back to previous reading position", "Zur vorherigen Leseposition", "corner-up-left", null, CommandScope.Reader, new[] { "reader-toolbar" }),
        new("reader.previous-thread", "command.reader.previous-thread", "Previous thread", "Voriger Thread", "arrow-left", "Left", CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader.next-thread", "command.reader.next-thread", "Next thread", "Nächster Thread", "arrow-right", "Right", CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader.previous-post", "command.reader.previous-post", "Previous post", "Voriger Beitrag", "arrow-up", "Up", CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader.next-post", "command.reader.next-post", "Next post", "Nächster Beitrag", "arrow-down", "Down", CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader.open-quote", "command.reader.open-quote", "Show quoted post", "Zitierten Beitrag anzeigen", "corner-up-left", null, CommandScope.Reader | CommandScope.Gallery, new[] { "reader-post-quote", "reader-reference-list", "gallery-post-context" }),
        new("reader.show-replies", "command.reader.show-replies", "Show replies", "Antworten anzeigen", "message-square", null, CommandScope.Reader, new[] { "reader-post" }),
        new("gallery.return", "command.gallery.return", "Back to gallery", "Zurück zur Galerie", "image", null, CommandScope.Reader, new[] { "reader-toolbar" }),
        new("gallery.close", "command.gallery.close", "Close gallery", "Galerie schließen", "x", "Escape", CommandScope.Gallery, new[] { "gallery-toolbar" }),
        new("gallery.previous", "command.gallery.previous", "Previous media", "Voriges Medium", "chevron-left", "Left", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.next", "command.gallery.next", "Next media", "Nächstes Medium", "chevron-right", "Right", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.fit", "command.gallery.fit", "Fit", "Einpassen", "image", "0", CommandScope.Gallery, new[] { "gallery-toolbar", "image-double-click" }),
        new("gallery.actual-size", "command.gallery.actual-size", "1:1", "1:1", "crosshair", "1", CommandScope.Gallery, new[] { "gallery-toolbar", "image-double-click" }),
        new("gallery.zoom-in", "command.gallery.zoom-in", "Zoom in", "Vergrößern", "zoom-in", "Plus", CommandScope.Gallery, new[] { "gallery-toolbar", "image-wheel" }),
        new("gallery.zoom-out", "command.gallery.zoom-out", "Zoom out", "Verkleinern", "zoom-out", "Minus", CommandScope.Gallery, new[] { "gallery-toolbar", "image-wheel" }),
        new("gallery.context-toggle", "command.gallery.context-toggle", "Show post context", "Beitragskontext anzeigen", "file-text", "P", CommandScope.Gallery, new[] { "gallery-toolbar" }),
        new("gallery.context-close", "command.gallery.context-close", "Return to image", "Zurück zum Bild", "image", null, CommandScope.Gallery, new[] { "gallery-post-context" }),
        new("gallery.show-in-thread", "command.gallery.show-in-thread", "Show in thread", "Im Thread anzeigen", "file-text", null, CommandScope.Gallery | CommandScope.Media, new[] { "gallery-post-context", "media-context-menu:planned" }),
        new("preview.close", "command.preview.close", "Close preview", "Vorschau schließen", "x", null, CommandScope.Media, new[] { "legacy-media-preview" }),
        new("video.play", "command.video.play", "Play", "Abspielen", "play", null, CommandScope.Gallery, new[] { "gallery-video-controls" }),
        new("video.pause", "command.video.pause", "Pause", "Pausieren", "pause", null, CommandScope.Gallery, new[] { "gallery-video-controls" }),
        new("video.stop", "command.video.stop", "Stop", "Stoppen", "square", null, CommandScope.Gallery, new[] { "gallery-video-controls" }),
        new("settings.browse-folder", "command.settings.browse-folder", "Browse…", "Durchsuchen…", "folder", null, CommandScope.Settings | CommandScope.Downloads, new[] { "settings-download-path" }),
        new("settings.discard", "command.settings.discard", "Discard draft", "Entwurf verwerfen", "rotate-ccw", null, CommandScope.Settings | CommandScope.Application, new[] { "settings-footer", "legacy-settings-window" }),
        new("settings.save", "command.settings.save", "Save", "Speichern", "save", null, CommandScope.Settings | CommandScope.Application, new[] { "settings-footer", "legacy-settings-window" }),
        new("rules.open-scope", "command.rules.open-scope", "Edit scope", "Bereich bearbeiten", "edit-2", null, CommandScope.Application, new[] { "rules-saved-scope", "rules-new-scope" }),
        new("rules.open-origin", "command.rules.open-origin", "Open rule", "Regel öffnen", "corner-up-left", null, CommandScope.Application, new[] { "rules-effective-value" }),
        new("rules.open-parent", "command.rules.open-parent", "Open parent scope", "Übergeordneten Bereich öffnen", "arrow-up", null, CommandScope.Application, new[] { "rules-editor" }),
        new("rules.discard", "command.rules.discard", "Discard draft", "Entwurf verwerfen", "rotate-ccw", null, CommandScope.Application, new[] { "rules-footer" }),
        new("rules.save", "command.rules.save", "Save rule", "Regel speichern", "save", null, CommandScope.Application, new[] { "rules-footer" }),
        new("admission.reinclude", "command.admission.reinclude", "Include previously excluded matches", "Ausgeschlossene Treffer wieder aufnehmen", "plus-circle", null, CommandScope.Application, new[] { "board-admission-summary" }),
        new("admission.add-rule", "command.admission.add-rule", "Add text rule", "Textregel hinzufügen", "plus", null, CommandScope.Application, new[] { "board-admission-rule-editor" }),
        new("admission.retry-catalog", "command.admission.retry-catalog", "Retry catalog", "Katalog erneut laden", "refresh-cw", null, CommandScope.Application, new[] { "board-admission-error" }),
        new("admission.cancel", "command.admission.cancel", "Cancel", "Abbrechen", "x", null, CommandScope.Application, new[] { "board-admission-footer" }),
        new("admission.monitor-all", "command.admission.monitor-all", "Monitor all", "Alle beobachten", "eye", null, CommandScope.Application, new[] { "board-admission-footer" }),
        new("admission.apply", "command.admission.apply", "Apply selection and monitor", "Auswahl übernehmen und beobachten", "check", null, CommandScope.Application, new[] { "board-admission-footer" }),
        new("admission.reset-choice", "command.admission.reset-choice", "Use rules again", "Wieder Regeln verwenden", "rotate-ccw", null, CommandScope.Application, new[] { "board-admission-thread-row" }),
        new("admission.remove-rule", "command.admission.remove-rule", "Remove rule", "Regel entfernen", "x", null, CommandScope.Application, new[] { "board-admission-rule-row" }),
        new("export.open-html-dialog", "command.export.open-html-dialog", "Export as HTML…", "Als HTML exportieren…", "download", "Primary+E", CommandScope.Archive | CommandScope.Reader | CommandScope.Downloads, new[] { "downloads-thread-menu", "archive-card-menu", "archive-table-menu", "reader-toolbar" }),
        new("export.open-previous", "command.export.open-previous", "Open previous export", "Früheren Export öffnen", "folder", null, CommandScope.Dialog, new[] { "html-export-dialog" }),
        new("export.associate", "command.export.associate", "Associate export folder…", "Exportordner zuordnen…", "link", null, CommandScope.Dialog, new[] { "html-export-dialog" }),
        new("export.find-legacy", "command.export.find-legacy", "Find older exports", "Ältere Exporte suchen", "search", null, CommandScope.Dialog, new[] { "html-export-legacy-section" }),
        new("export.associate-legacy", "command.export.associate-legacy", "Associate older export with this thread", "Älteren Export diesem Thread zuordnen", "link", null, CommandScope.Dialog, new[] { "html-export-legacy-section" }),
        new("export.browse-parent", "command.export.browse-parent", "Choose parent folder", "Übergeordneten Ordner wählen", "folder", null, CommandScope.Dialog, new[] { "html-export-dialog", "original-export-dialog:planned" }),
        new("export.close", "command.export.close", "Close", "Schließen", "x", null, CommandScope.Dialog, new[] { "html-export-dialog" }),
        new("export.cancel", "command.export.cancel", "Cancel export", "Export abbrechen", "x-circle", null, CommandScope.Dialog, new[] { "html-export-progress", "original-export-progress:planned" }),
        new("export.start-html", "command.export.start-html", "Export", "Exportieren", "download", null, CommandScope.Dialog, new[] { "html-export-dialog" }),
        new("sources.select", "command.sources.select", "Select source", "Quelle auswählen", "globe", null, CommandScope.Sources, new[] { "sources-card" }),
        new("sources.back", "command.sources.back", "Back to sources", "Zurück zu Quellen", "arrow-left", null, CommandScope.Sources, new[] { "sources-details-narrow" }),
        new("watch.show-in-downloads", "command.watch.show-in-downloads", "Show in downloads", "In Downloads zeigen", "corner-up-right", null, CommandScope.Sources | CommandScope.Diagnostics | CommandScope.Downloads, new[] { "sources-watch-details", "diagnostics-details" }),
        new("diagnostics.copy", "command.diagnostics.copy", "Copy details", "Details kopieren", "copy", null, CommandScope.Diagnostics, new[] { "diagnostics-details" }),
        new("about.open-link", "command.about.open-link", "Open project, credit or license page", "Projekt-/Credit-/Lizenzseite öffnen", "external-link", null, CommandScope.About | CommandScope.Sources, new[] { "about-project-link", "about-author-link", "about-ychan-link", "about-gchan-link", "about-feather-link", "about-project-license-link", "credits-component-link:planned", "licenses-source-link:planned" }),
        new("about.close", "command.about.close", "Close", "Schließen", "x", null, CommandScope.About | CommandScope.Application, new[] { "about-footer" }),
        new("viewer.focus-mode", "command.viewer.focus-mode", "Expand workspace / show list", "Arbeitsbereich vergrößern / Liste zeigen", "sidebar", null, CommandScope.Reader | CommandScope.Gallery, new[] { "embedded-reader", "embedded-gallery", "large-reader", "large-gallery" }),
        new("window.fullscreen", "command.window.fullscreen", "Enter / exit fullscreen", "Vollbild einschalten / verlassen", "maximize", "F11", CommandScope.Reader | CommandScope.Gallery | CommandScope.Downloads | CommandScope.Application, new[] { "shell", "downloads-threadwatcher", "reader", "gallery" }),
        new("viewer.shortcut-help", "command.viewer.shortcut-help", "Keyboard shortcuts", "Tastaturhilfe", "help-circle", "F1", CommandScope.Reader | CommandScope.Gallery, new[] { "reader", "gallery", "workspace" }),
        new("viewer.shortcut-hints", "command.viewer.shortcut-hints", "Show / hide shortcut hints", "Kurzhilfe anzeigen / ausblenden", "help-circle", null, CommandScope.Reader | CommandScope.Gallery | CommandScope.Media | CommandScope.Settings, new[] { "reader", "gallery", "settings-reading-media" }),
        new("input.focus-search", "command.input.focus-search", "Focus search", "Suche fokussieren", "search", "Primary+F", CommandScope.Archive | CommandScope.Reader, new[] { "archive", "license-reader", "active-search-context" }),
        new("archive.clear-filter", "command.archive.clear-filter", "Remove filter", "Filter entfernen", "x", null, CommandScope.Archive, new[] { "archive-active-filter-chip" }),
        new("table.choose-columns", "command.table.choose-columns", "Choose columns…", "Spalten auswählen…", "columns", null, CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-table", "archive-table" }),
        new("table.reset-layout", "command.table.reset-layout", "Reset view layout", "Ansicht zurücksetzen", "rotate-ccw", null, CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-table", "archive-table", "split-layout" }),
        new("reader.font-size", "command.reader.font-size", "Reading font size", "Leseschriftgröße", "type", null, CommandScope.Reader | CommandScope.Media | CommandScope.Settings, new[] { "reader-toolbar", "settings-reading-media" }),
        new("watch.follow-live", "command.watch.follow-live", "Follow live", "Live folgen", "arrow-down", null, CommandScope.Reader | CommandScope.Downloads, new[] { "reader-live-watch", "threadwatcher" }),
        new("reader.show-new-posts", "command.reader.show-new-posts", "Show new posts", "Neue Beiträge anzeigen", "arrow-down", null, CommandScope.Reader, new[] { "reader-new-posts-notice" }),
        new("favorites.toggle", "command.favorites.toggle", "Add / remove favorite", "Als Favorit markieren / entfernen", "star", "F", CommandScope.Reader | CommandScope.Gallery | CommandScope.Media, new[] { "reader-inline-media", "gallery-toolbar", "media-tile", "media-context-menu" }),
        new("favorites.filter", "command.favorites.filter", "Favorites only", "Nur Favoriten", "star", null, CommandScope.Archive | CommandScope.Media, new[] { "thread-media-filter", "archive-media-filter" }),
        new("favorites.open", "command.favorites.open", "Show favorites", "Favoriten anzeigen", "star", null, CommandScope.Archive, new[] { "archive-favorites-overview" }),
        new("favorites.export-originals", "command.favorites.export-originals", "Export favorite originals…", "Favorisierte Originale exportieren…", "download", null, CommandScope.Archive | CommandScope.Gallery | CommandScope.Media, new[] { "gallery", "thread-media", "selected-threads", "archive-results", "favorites-overview" }),
        new("media.export-original", "command.media.export-original", "Export original…", "Original exportieren…", "download", null, CommandScope.Gallery | CommandScope.Media, new[] { "media-context-menu", "gallery-menu" }),
        new("media.show-in-folder", "command.media.show-in-folder", "Show in folder", "Im Ordner anzeigen", "folder", null, CommandScope.Gallery | CommandScope.Media, new[] { "media-context-menu", "gallery-menu" }),
        new("media.details", "command.media.details", "Show media details", "Mediendetails anzeigen", "info", null, CommandScope.Gallery | CommandScope.Media, new[] { "media-context-menu", "gallery-menu" }),
        new("post.copy-link", "command.post.copy-link", "Copy post link", "Beitragslink kopieren", "link", null, CommandScope.Reader, new[] { "reader-post-menu" }),
        new("post.open-source", "command.post.open-source", "Open post on source", "Beitrag auf Quelle öffnen", "external-link", null, CommandScope.Reader, new[] { "reader-post-menu" }),
        new("video.play-pause", "command.video.play-pause", "Play / pause", "Wiedergabe / Pause", "play", "Space", CommandScope.Gallery, new[] { "gallery-video-surface" }),
        new("video.mute", "command.video.mute", "Mute / unmute", "Ton aus / an", "volume-2", null, CommandScope.Gallery | CommandScope.Media | CommandScope.Settings, new[] { "gallery-video-controls", "settings-reading-media" }),
        new("about.copy-version", "command.about.copy-version", "Copy version", "Version kopieren", "copy", null, CommandScope.About, new[] { "about-overview" }),
        new("about.open-license", "command.about.open-license", "Open license text", "Lizenztext öffnen", "file-text", null, CommandScope.About, new[] { "about-licenses", "component-license-list" }),
        new("about.show-credits", "command.about.show-credits", "Acknowledgements", "Danksagungen", "heart", null, CommandScope.About, new[] { "about-navigation" }),
        new("about.show-licenses", "command.about.show-licenses", "Licenses", "Lizenzen", "file-text", null, CommandScope.About, new[] { "about-navigation" }),
        new("export.start-originals", "command.export.start-originals", "Export originals", "Originale exportieren", "download", null, CommandScope.Dialog, new[] { "original-export-dialog" }),
        new("menu.context-open", "command.menu.context-open", "Open context menu", "Kontextmenü öffnen", "more-horizontal", "Shift+F10", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-list", "downloads-board-list", "archive-card-list", "archive-table" }),
    };
    private static readonly IReadOnlyDictionary<string, CommandDefinition> ById =
        new ReadOnlyDictionary<string, CommandDefinition>(Entries.ToDictionary(x => x.Id, StringComparer.Ordinal));

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StateIcons =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
    {
        { "session.pause", new Dictionary<string, string>(StringComparer.Ordinal) { { "running", "pause" }, { "paused", "play" } } },
        { "appearance.theme", new Dictionary<string, string>(StringComparer.Ordinal) { { "choose_dark", "moon" }, { "choose_light", "sun" }, { "choose_system", "monitor" } } },
        { "window.maximize", new Dictionary<string, string>(StringComparer.Ordinal) { { "maximize", "square" }, { "restore", "copy" } } },
        { "archive.filters-toggle", new Dictionary<string, string>(StringComparer.Ordinal) { { "closed", "filter" }, { "open", "filter" } } },
        { "archive.thread-view", new Dictionary<string, string>(StringComparer.Ordinal) { { "list", "list" }, { "tiles", "grid" }, { "table", "columns" } } },
        { "archive.media-view", new Dictionary<string, string>(StringComparer.Ordinal) { { "tiles", "grid" }, { "list", "list" } } },
        { "archive.capture-toggle", new Dictionary<string, string>(StringComparer.Ordinal) { { "stopped", "circle" }, { "capturing", "square" } } },
        { "gallery.context-toggle", new Dictionary<string, string>(StringComparer.Ordinal) { { "hidden", "file-text" }, { "shown", "file-text" } } },
        { "viewer.focus-mode", new Dictionary<string, string>(StringComparer.Ordinal) { { "embedded", "sidebar" }, { "expanded", "sidebar" } } },
        { "window.fullscreen", new Dictionary<string, string>(StringComparer.Ordinal) { { "windowed", "maximize" }, { "fullscreen", "minimize" } } },
        { "viewer.shortcut-hints", new Dictionary<string, string>(StringComparer.Ordinal) { { "hidden", "help-circle" }, { "shown", "help-circle" } } },
        { "watch.follow-live", new Dictionary<string, string>(StringComparer.Ordinal) { { "off", "arrow-down" }, { "on", "arrow-down" } } },
        { "favorites.toggle", new Dictionary<string, string>(StringComparer.Ordinal) { { "unmarked", "star" }, { "marked", "check" } } },
        { "favorites.filter", new Dictionary<string, string>(StringComparer.Ordinal) { { "all", "star" }, { "favorites_only", "star" } } },
        { "video.play-pause", new Dictionary<string, string>(StringComparer.Ordinal) { { "paused", "play" }, { "playing", "pause" } } },
        { "video.mute", new Dictionary<string, string>(StringComparer.Ordinal) { { "audible", "volume-2" }, { "muted", "volume-x" } } },
    };

    public static string IconForState(string id, string? state)
    {
        CommandDefinition definition = Get(id);
        return state is not null && StateIcons.TryGetValue(id, out var states) && states.TryGetValue(state, out string? icon)
            ? icon : definition.Icon;
    }

    public static IReadOnlyList<CommandDefinition> All { get; } = Array.AsReadOnly(Entries);
    public static bool TryGet(string id, out CommandDefinition? definition) => ById.TryGetValue(id, out definition);
    public static CommandDefinition Get(string id) => ById.TryGetValue(id, out CommandDefinition? definition)
        ? definition : throw new KeyNotFoundException($"Unknown command: {id}");

    public static string Label(string id)
    {
        CommandDefinition definition = Get(id);
        string translated = Texts.T(definition.LabelKey);
        if (translated != definition.LabelKey) return translated;
        return Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase)
            ? definition.LabelDe : definition.LabelEn;
    }

    public static string? ShortcutDisplay(string id) => ShortcutDisplay(id, OperatingSystem.IsMacOS());

    internal static string? ShortcutDisplay(string id, bool macOS)
    {
        string? shortcut = Get(id).Shortcut;
        if (shortcut is null) return null;
        return shortcut.Replace("Primary", macOS ? "⌘" : "Ctrl", StringComparison.Ordinal)
            .Replace("+Left", "+←", StringComparison.Ordinal)
            .Replace("+Right", "+→", StringComparison.Ordinal);
    }

    public static string? ScopeHint(string id)
    {
        bool german = Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase);
        return id switch
        {
            "archive.select-page" => german ? "Ergebnisliste · nur diese Seite" : "Results list · this page only",
            "archive.previous-page" or "archive.next-page" => german ? "Fokus in der Ergebnisliste · innerhalb der Seitengrenzen" : "Results-list focus · within page boundaries",
            "selection.select-all" => german ? "Fokussierte Downloadliste" : "Focused downloads list",
            "menu.context-open" => german ? "Fokussierte Download- oder Archivliste" : "Focused downloads or archive list",
            "watch.import-clipboard" => german ? "Downloads · URL-Eingabe fügt Text ein" : "Downloads · URL input pastes text",
            "input.focus-download-search" => german ? "Downloads · lokale Liste" : "Downloads · local list",
            "input.focus-search" => german ? "Archiv · Treffer durchsuchen" : "Archive · search results",
            "export.open-all-results-html-dialog" => german ? "Eigener Button · alle Treffer der aktuellen Suche" : "Separate button · all results of the current search",
            "navigation.downloads" or "navigation.archive" or "navigation.rules" or "navigation.sources" or "navigation.diagnostics" or "session.pause"
                => german ? "Außerhalb von Eingaben und Overlays · bei verfügbarer Aktion" : "Outside inputs and overlays · when available",
            _ => null
        };
    }

    public static IEnumerable<CommandDefinition> Available(CommandScope scope, Func<CommandDefinition, bool> isAvailable) =>
        Entries.Where(x => (x.Scope & scope) != 0 && isAvailable(x));
}
