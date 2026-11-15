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
        new("sections.open-navigation", "command.sections.open-navigation", "Navigate sections", "Abschnitte öffnen", "list", null, CommandScope.Settings, new[] { "settings" }),
        new("sections.go-to", "command.sections.go-to", "Go to selected section", "Ausgewählten Abschnitt öffnen", "arrow-right", null, CommandScope.Settings, new[] { "settings-sections" }),
        new("reader.skip-unavailable-group-member", "command.reader.skip-unavailable-group-member", "Skip unavailable member", "Fehlendes Mitglied überspringen", "arrow-right", null, CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader-groups.open-manager", "command.reader-groups.open-manager", "Reader groups", "Readergruppen", "list", null, CommandScope.Application, new[] { "shell" }),
        new("reader-groups.create", "command.reader-groups.create", "Create group", "Gruppe erstellen", "plus", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("reader-groups.rename", "command.reader-groups.rename", "Rename group", "Gruppe umbenennen", "edit-2", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("reader-groups.delete", "command.reader-groups.delete", "Delete group", "Gruppe löschen", "x-circle", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("reader-groups.add-members", "command.reader-groups.add-members", "Add threads", "Threads hinzufügen", "plus-circle", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("reader-groups.remove-member", "command.reader-groups.remove-member", "Remove thread", "Thread entfernen", "minus", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("reader-groups.move-up", "command.reader-groups.move-up", "Move thread up", "Thread nach oben", "arrow-up", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("reader-groups.move-down", "command.reader-groups.move-down", "Move thread down", "Thread nach unten", "arrow-down", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("reader-groups.save", "command.reader-groups.save", "Save group", "Gruppe speichern", "save", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("reader-groups.read", "command.reader-groups.read", "Read group", "Gruppe lesen", "book-open", null, CommandScope.Dialog, new[] { "reader-groups-dialog" }),
        new("media.tags.edit", "media.tags.edit", "Edit tags…", "Tags bearbeiten…", "edit-2", "Primary+Shift+T", CommandScope.Media | CommandScope.Reader | CommandScope.Gallery, new[] { "media-context-menu", "reader-attachment-menu" }, new[] { "media", "gallery" }),
        new("media.queue.add", "command.media.queue.add", "Add to tagging queue", "Zur Tagging-Warteschlange hinzufügen", "plus", "Q", CommandScope.Media | CommandScope.Reader | CommandScope.Gallery | CommandScope.Archive, new[] { "media-context-menu", "reader-attachment-menu", "gallery-menu" }, new[] { "media", "gallery" }),
        new("media.queue.open", "command.media.queue.open", "Walk through tagging queue", "Tagging-Warteschlange durchgehen", "list", null, CommandScope.Application, new[] { "media-page" }),
        // PT2: the tagging page and the commands of its queue list (execution context "tagging"). The page has a footer button behind
        // Media, a More entry and a ⋯ entry like every main page. The main pages carry no fixed digit here: Ctrl/Cmd+1, 2, 3 ... follow the
        // footer buttons the person chose (MainPageOrder.Numbering, EffectiveShortcutBindings.Defaults).
        new("navigation.tagging", "command.navigation.tagging", "Tagging", "Taggen", "tag", null, CommandScope.Application, new[] { "footer", "tools-menu" }),
        new("tagging.start", "command.tagging.start", "Start tagging", "Taggen starten", "play", null, CommandScope.Media, new[] { "tagging-page" }, new[] { "tagging" }),
        new("tagging.continue-here", "command.tagging.continue-here", "Continue here", "Hier fortsetzen", "play", "Enter", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        // Revision 19 (decision #125): D marks the selected entries done, S skipped, Alt+M shows them in Media (the thread's "Show in Media" key);
        // single letters only act in the queue list, never in a text field (TaggingPage.OnListKeyDown, MainWindow.IsListCommandSource).
        new("tagging.mark-done", "command.tagging.mark-done", "Mark as finished", "Als erledigt markieren", "check", "D", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.mark-open", "command.tagging.mark-open", "Mark as open", "Als offen markieren", "circle", null, CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.mark-skipped", "command.tagging.mark-skipped", "Mark as skipped", "Als übersprungen markieren", "chevron-right", "S", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.move-up", "command.tagging.move-up", "Move up", "Nach oben", "arrow-up", "Alt+Up", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.move-down", "command.tagging.move-down", "Move down", "Nach unten", "arrow-down", "Alt+Down", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.remove", "command.tagging.remove", "Remove from queue", "Aus der Warteschlange entfernen", "x", "Delete", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        // TG1: the rest of the entry menu (list and grid). Tag…, the stamp, the gallery, the thread, the folder and the file path are the
        // existing commands of the media page and the gallery (one id, one key, one binding); these have no counterpart there.
        new("tagging.tag-together", "command.tagging.tag-together", "Tag together…", "Gemeinsam taggen…", "tag", null, CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.show-in-media", "command.tagging.show-in-media", "Show in Media", "In Medien zeigen", "grid", "Alt+M", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.move-first", "command.tagging.move-first", "Move to the start", "An den Anfang", "chevrons-up", "Alt+Home", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.move-last", "command.tagging.move-last", "Move to the end", "Ans Ende", "chevrons-down", "Alt+End", CommandScope.Media, new[] { "tagging-list-menu" }, new[] { "tagging" }),
        new("tagging.add", "command.tagging.add", "Add…", "Hinzufügen…", "plus", null, CommandScope.Media, new[] { "tagging-page" }, new[] { "tagging" }),
        new("tagging.clear-done", "command.tagging.clear-done", "Remove finished", "Erledigte entfernen", "check-square", null, CommandScope.Media, new[] { "tagging-page" }, new[] { "tagging" }),
        new("tagging.clear", "command.tagging.clear", "Clear queue…", "Warteschlange leeren…", "x-circle", null, CommandScope.Media, new[] { "tagging-page" }, new[] { "tagging" }),
        // The rating family has its own mask (star-half, decision #126 of 15.11.2026): the plain star stays the favourite's, so the two neighbouring menu
        // entries no longer look alike.
        new("media.rating.edit", "command.media.rating.edit", "Rating", "Bewertung", "star-half", "R", CommandScope.Media | CommandScope.Reader | CommandScope.Gallery, new[] { "media-rating" }),
        new("media.rating.clear", "command.media.rating.clear", "Remove rating", "Bewertung entfernen", "star-half", "Shift+0", CommandScope.Media | CommandScope.Gallery | CommandScope.Dialog, new[] { "media-rating" }),
        new("media.rating.manage", "command.media.rating.manage", "Manage saved ratings…", "Gespeicherte Bewertungen verwalten…", "star-half", null, CommandScope.Media | CommandScope.Archive | CommandScope.Gallery, new[] { "media-page", "archive-media-rating", "gallery-overflow" }),
        new("media.rating.remove-selected", "command.media.rating.remove-selected", "Remove selected rating…", "Ausgewählte Bewertung entfernen…", "star-half", null, CommandScope.Dialog, new[] { "media-ratings-manager" }),
        new("media.rating.cleanup-retry", "command.media.rating.cleanup-retry", "Retry rating cleanup", "Bewertungsbereinigung wiederholen", "star-half", null, CommandScope.Dialog, new[] { "archive-deletion-result" }),
        new("media.rating.set-1", "command.media.rating.set-1", "Rating: 1 of 5", "Bewertung: 1 von 5", "star-half", "Shift+1", CommandScope.Media | CommandScope.Gallery | CommandScope.Dialog, new[] { "media-rating" }),
        new("media.rating.set-2", "command.media.rating.set-2", "Rating: 2 of 5", "Bewertung: 2 von 5", "star-half", "Shift+2", CommandScope.Media | CommandScope.Gallery | CommandScope.Dialog, new[] { "media-rating" }),
        new("media.rating.set-3", "command.media.rating.set-3", "Rating: 3 of 5", "Bewertung: 3 von 5", "star-half", "Shift+3", CommandScope.Media | CommandScope.Gallery | CommandScope.Dialog, new[] { "media-rating" }),
        new("media.rating.set-4", "command.media.rating.set-4", "Rating: 4 of 5", "Bewertung: 4 von 5", "star-half", "Shift+4", CommandScope.Media | CommandScope.Gallery | CommandScope.Dialog, new[] { "media-rating" }),
        new("media.rating.set-5", "command.media.rating.set-5", "Rating: 5 of 5", "Bewertung: 5 von 5", "star-half", "Shift+5", CommandScope.Media | CommandScope.Gallery | CommandScope.Dialog, new[] { "media-rating" }),
        new("post-bookmarks.open-manager", "bookmarks.title", "Post bookmarks", "Post-Lesezeichen", "message-square", null, CommandScope.Application, new[] { "app-menu", "archive-results-toolbar", "reader-thread-menu" }),
        new("post-bookmarks.edit-post", "bookmarks.edit", "Add / edit post bookmark…", "Post-Lesezeichen hinzufügen / bearbeiten…", "edit-2", null, CommandScope.Reader, new[] { "reader-post-menu" }),
        new("post-bookmarks.open-target", "bookmarks.open", "Open exact local post", "Exakten lokalen Post öffnen", "corner-up-right", null, CommandScope.Dialog, new[] { "post-bookmarks" }),
        new("post-bookmarks.save", "bookmarks.save", "Save bookmark", "Lesezeichen speichern", "save", null, CommandScope.Dialog, new[] { "post-bookmarks" }),
        new("post-bookmarks.remove", "bookmarks.remove", "Remove bookmark…", "Lesezeichen entfernen…", "x", null, CommandScope.Dialog, new[] { "post-bookmarks" }),
        new("navigation.downloads", "command.navigation.downloads", "Downloads", "Downloads", "download", null, CommandScope.Application, new[] { "footer" }),
        // RH1: the one command of "continue reading" with one icon (book-open). The header button, the app menu entry, the reading history
        // menu and window, and the archive list's thread menu all run it, each with the label of its place; only the shortcut is the last place's.
        new("navigation.continue-reading", "resume.action", "Continue reading", "Weiterlesen", "book-open", "Primary+Shift+R", CommandScope.Application, new[] { "header", "app-menu", "workspace-keyboard", "header-reading-menu", "reading-history-window", "archive-thread-menu" }),
        new("navigation.reading-history", "resume.history.showAll", "Show full history…", "Ganzen Verlauf zeigen…", "list", null, CommandScope.Application, new[] { "header-reading-menu", "app-menu" }),
        new("import.open", "import.menu", "Import files…", "Dateien importieren…", "download-cloud", "Primary+Shift+I", CommandScope.Application, new[] { "app-menu", "workspace-keyboard" }),
        // PR1 (profiles): the "⋯ › Profile" submenu. No default gestures; each opens or starts a window of its own.
        new("profile.new", "command.profile.new", "New profile…", "Neues Profil…", "plus-circle", null, CommandScope.Application, new[] { "app-menu" }),
        new("profile.switch", "command.profile.switch", "Switch profile…", "Profil wechseln…", "external-link", null, CommandScope.Application, new[] { "app-menu" }),
        new("profile.open-folder", "command.profile.open-folder", "Open profile folder", "Profilordner öffnen", "folder", null, CommandScope.Application, new[] { "app-menu" }),
        new("navigation.archive", "command.navigation.archive", "Archive", "Archiv", "archive", null, CommandScope.Application, new[] { "footer", "tools-menu" }),
        new("navigation.media", "command.navigation.media", "Media", "Medien", "grid", null, CommandScope.Application, new[] { "footer", "tools-menu" }),
        new("navigation.booru", "command.navigation.booru", "Booru", "Booru", "search", null, CommandScope.Application, new[] { "footer", "tools-menu" }),
        new("navigation.rules", "command.navigation.rules", "Rules", "Regeln", "sliders", null, CommandScope.Application, new[] { "footer", "tools-menu" }),
        new("navigation.sources", "command.navigation.sources", "Plugins", "Plugins", "globe", null, CommandScope.Application, new[] { "footer", "tools-menu" }),
        // The Diagnostics segment of Status: no main page, no default gesture (MainPageOrder.StatusSegmentCommandIds); the Sources page opens it.
        new("navigation.diagnostics", "command.navigation.diagnostics", "Diagnostics", "Diagnose", "activity", null, CommandScope.Sources | CommandScope.Application, new[] { "sources", "status-segment" }),
        new("plugins.refresh", "plugins.refresh", "Refresh status", "Status aktualisieren", "refresh-cw", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.folder", "plugins.folder", "Open plugin folder", "Pluginordner öffnen", "folder", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.back", "plugins.back", "Back to plugins", "Zurück zur Pluginliste", "arrow-left", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.save", "plugins.save", "Save for next start", "Für nächsten Start speichern", "save", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.discard", "plugins.discard", "Discard draft and reload", "Entwurf verwerfen und neu laden", "rotate-ccw", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.edit", "plugins.edit", "Open in editor", "Im Editor öffnen", "file-text", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.build", "plugins.build", "Build and test", "Bauen und testen", "tool", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.cancel", "plugins.cancel", "Cancel operation", "Vorgang abbrechen", "x", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.probe", "plugins.probe", "Fetch test URL", "Test-URL abrufen", "download", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.enable", "plugins.enable", "Enable plugin", "Plugin aktivieren", "power", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.disable", "plugins.disable", "Disable plugin", "Plugin deaktivieren", "power", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.details", "plugins.details", "Show details", "Details anzeigen", "info", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.show-settings", "plugins.showSettings", "Open settings", "Einstellungen öffnen", "sliders", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.watches", "plugins.watches", "Show in Plugins", "In Plugins zeigen", "list", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.select-candidate", "plugins.selectCandidate", "Use this version next start", "Diese Fassung beim nächsten Start verwenden", "check", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.rollback", "plugins.rollback", "Use last good saved build next start", "Letzte gute gespeicherte Fassung beim nächsten Start verwenden", "rotate-ccw", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.show-grants", "plugins.showGrants", "View permissions", "Zugriffsrechte ansehen", "sliders", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.show-developer-tools", "plugins.showDeveloperTools", "Show developer details", "Entwicklerdetails anzeigen", "tool", null, CommandScope.Settings, new[] { "plugins" }),
        new("plugins.actions", "plugins.actions", "Actions", "Aktionen", "more-horizontal", null, CommandScope.Settings, new[] { "plugins" }),
        new("updates.check", "updates.check", "Check for updates", "Nach Updates suchen", "refresh-cw", null, CommandScope.Application | CommandScope.Settings, new[] { "app-menu", "about" }),
        new("navigation.plugins", "command.navigation.plugins", "Show all plugins", "Alle Plugins anzeigen", "tool", null, CommandScope.Application, new[] { "sources" }),
        new("navigation.status", "command.navigation.status", "Status", "Status", "activity", null, CommandScope.Application, new[] { "footer", "tools-menu" }),
        // The Statistics segment of Status: no default gesture; the header graph and the tray open it.
        new("navigation.statistics", "command.navigation.statistics", "Statistics", "Statistik", "monitor", null, CommandScope.Application, new[] { "status-segment", "header-graph", "tray" }),
        new("booru.open", "booru.ui.command.open", "Danbooru search and watchers…", "Danbooru-Suche und Suchbeobachtungen…", "search", null, CommandScope.Application, new[] { "app-menu", "tools-menu" }),
        new("navigation.settings", "command.navigation.settings", "Settings", "Einstellungen", "settings", "Primary+,", CommandScope.Application, new[] { "footer", "app-menu" }),
        new("navigation.about", "command.navigation.about", "About MeowChan", "Über MeowChan", "info", null, CommandScope.Settings | CommandScope.Application, new[] { "app-menu", "settings:planned" }),
        new("navigation.tools-menu", "command.navigation.tools-menu", "More", "Mehr", "more-horizontal", null, CommandScope.Application, new[] { "footer" }),
        // R8 of the work-view plan: switches Minimal -> Simple -> Advanced -> Minimal. No default gesture (it would collide with a
        // profile's own bindings); the user assigns one under Shortcuts. The app menu carries it in every view.
        new("workspace.view-cycle", "workview.viewCycle", "Switch work view", "Arbeitsansicht wechseln", "eye", null, CommandScope.Application, new[] { "app-menu" }),
        new("navigation.app-menu", "command.navigation.app-menu", "App menu", "Appmenü", "more-horizontal", null, CommandScope.Application, new[] { "header" }),
        new("app.show", "command.app.show", "Show MeowChan", "MeowChan anzeigen", "monitor", null, CommandScope.Application, new[] { "tray" }),
        new("app.quit", "command.app.quit", "Quit", "Beenden", "power", null, CommandScope.Application, new[] { "app-menu", "tray", "startup-error" }),
        new("session.pause", "command.session.pause", "Pause / resume downloads and checks", "Downloads und Prüfungen pausieren / fortsetzen", "pause", "Primary+Shift+P", CommandScope.Application, new[] { "header", "app-menu", "workspace-keyboard", "tray" }),
        new("appearance.theme", "command.appearance.theme", "Change theme", "Farbschema wechseln", "moon", null, CommandScope.Settings | CommandScope.Application, new[] { "header", "settings", "app-menu" }),
        new("window.minimize", "command.window.minimize", "Minimize window", "Fenster minimieren", "minus", null, CommandScope.Application, new[] { "window-caption" }),
        new("window.maximize", "command.window.maximize", "Maximize / restore window", "Fenster maximieren / wiederherstellen", "square", null, CommandScope.Application, new[] { "window-caption", "titlebar-double-click" }),
        new("window.close", "command.window.close", "Close window", "Fenster schließen", "x", null, CommandScope.Application, new[] { "window-caption" }),
        new("input.focus-url", "command.input.focus-url", "Focus URL input", "URL-Eingabe fokussieren", "link", "Primary+L", CommandScope.Application, new[] { "workspace", "sources-details" }),
        new("watch.add-url", "command.watch.add-url", "Add URL", "URL hinzufügen", "plus", null, CommandScope.Downloads, new[] { "downloads-url-bar" }),
        new("watch.import-clipboard", "command.watch.import-clipboard", "Add URLs from clipboard", "URLs aus Zwischenablage hinzufügen", "clipboard", "Primary+V", CommandScope.Downloads, new[] { "tray", "downloads-list-keyboard", "empty-add" }),
        new("selection.select-all", "command.selection.select-all", "Select all", "Alle auswählen", "check-square", "Primary+A", CommandScope.Downloads, new[] { "downloads-thread-list", "downloads-board-list" }),
        new("thread.read", "command.thread.read", "Read posts", "Posts lesen", "eye", "Enter", CommandScope.Archive | CommandScope.Reader | CommandScope.Gallery | CommandScope.Downloads, new[] { "downloads-thread-menu", "archive-card-menu", "archive-table-menu", "reader-toolbar", "gallery-empty-state", "thread-double-click" }),
        new("gallery.open", "command.gallery.open", "Open in gallery", "In der Galerie öffnen", "image", "Shift+Enter", CommandScope.Archive | CommandScope.Reader | CommandScope.Media | CommandScope.Downloads, new[] { "downloads-thread-menu", "archive-card-menu", "archive-table-menu", "reader-toolbar", "media-list", "media-tiles", "inline-attachment" }),
        new("media.open", "command.media.open", "Open selected media", "Ausgewähltes Medium öffnen", "image", "Enter", CommandScope.Media, new[] { "media-list", "media-tiles" }),
        // The empty Media page opens the read-only folder-source flow; keep its presentation in the shared command catalog.
        // QW-A #1: the empty Media page offers "Link folder…"; the page's More menu offers
        // the settings group that manages all linked folders.
        new("media.link-folder", "command.media.link-folder", "Link folder…", "Ordner verknüpfen…", "folder", null, CommandScope.Media, new[] { "media-empty-state" }),
        new("media.folders", "command.media.folders", "Media folders…", "Medienordner…", "folder", null, CommandScope.Media, new[] { "media-page" }),
        new("media.focus-mode", "command.media.focus-mode", "Focus mode", "Fokusmodus", "maximize", "Shift+F", CommandScope.Media, new[] { "media-toolbar", "media-tiles" }, new[] { "media" }),
        new("media.select-all", "command.media.select-all", "Select all media", "Alle Medien auswählen", "check-square", "Primary+A", CommandScope.Media, new[] { "media-tiles" }),
        // Interaction audit 1.1.0: the Layout menu tile-size steps also have direct Ctrl/Cmd bindings.
        new("media.tile-size.larger", "command.media.tile-size.larger", "Larger tiles", "Größere Kacheln", "zoom-in", "Primary+Plus", CommandScope.Media, new[] { "media-tiles" }),
        new("media.tile-size.smaller", "command.media.tile-size.smaller", "Smaller tiles", "Kleinere Kacheln", "zoom-out", "Primary+Minus", CommandScope.Media, new[] { "media-tiles" }),
        new("gallery.open-results", "command.gallery.open-results", "Media from matching threads", "Medien der Treffer", "image", null, CommandScope.Archive, new[] { "archive-results-toolbar" }),
        new("thread.show-in-media", "command.thread.show-in-media", "Show in Media", "In Medien zeigen", "grid", "Alt+M", CommandScope.Archive | CommandScope.Reader | CommandScope.Downloads, new[] { "downloads-thread-menu", "archive-card-menu", "archive-table-menu", "reader-thread-menu" }),
        new("thread.details", "command.thread.details", "Show details", "Details anzeigen", "info", "Alt+Enter", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-toolbar", "archive-card-menu", "archive-table-menu", "reader-thread-menu" }),
        new("inspector.close", "command.inspector.close", "Close details", "Details schließen", "x", null, CommandScope.Downloads, new[] { "downloads-inspector" }),
        new("watch.check", "command.watch.check", "Check now", "Jetzt prüfen", "refresh-cw", "F5", CommandScope.Archive | CommandScope.Diagnostics | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "diagnostics", "reader-thread-menu" }),
        new("watch.retry-media", "command.watch.retry-media", "Retry missing media", "Fehlende Medien erneut versuchen", "download-cloud", "Shift+F5", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "reader-thread-menu" }),
        new("rules.edit-scope", "command.rules.edit-scope", "Media rules…", "Medienregeln…", "sliders", "Primary+R", CommandScope.Archive | CommandScope.Sources | CommandScope.Downloads | CommandScope.Reader | CommandScope.Media, new[] { "downloads-thread-menu", "downloads-board-menu", "archive-card-menu", "archive-table-menu", "sources", "reader-thread-menu" }),
        new("board.filters", "command.board.filters", "Thread filters…", "Threadfilter…", "filter", "Primary+Shift+F", CommandScope.Downloads, new[] { "downloads-board-menu" }),
        new("location.open-folder", "command.location.open-folder", "Open folder", "Ordner öffnen", "folder", "Primary+O", CommandScope.Archive | CommandScope.Media | CommandScope.Dialog | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "media-preview", "export-result", "tray", "reader-thread-menu" }),
        new("location.open-source", "command.location.open-source", "Open source in browser", "Quelle im Browser öffnen", "external-link", "Primary+Shift+O", CommandScope.Archive | CommandScope.Sources | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "sources", "reader-thread-menu" }),
        new("selection.copy-links", "command.selection.copy-links", "Copy link(s)", "Link(s) kopieren", "link", "Primary+C", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "reader-thread-menu" }),
        new("watch.copy-all-links", "command.watch.copy-all-links", "Copy all URLs", "Alle URLs kopieren", "copy", null, CommandScope.Downloads | CommandScope.Application, new[] { "downloads-footer" }),
        new("watch.remove", "command.watch.remove", "Remove from watch list", "Aus Beobachtung entfernen", "eye-off", "Delete", CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-thread-menu", "downloads-board-menu", "downloads-inspector", "archive-card-menu", "archive-table-menu", "reader-thread-menu" }),
        new("archive.delete-permanently", "command.archive.delete-permanently", "Delete permanently…", "Dauerhaft löschen…", "x-circle", "Shift+Delete", CommandScope.Archive, new[] { "archive-card-menu", "archive-table-menu" }),
        new("archive.previous-page", "command.archive.previous-page", "Previous results page", "Vorherige Ergebnisseite", "chevron-left", "Alt+PageUp", CommandScope.Archive, new[] { "archive-results-list" }),
        new("archive.next-page", "command.archive.next-page", "Next results page", "Nächste Ergebnisseite", "chevron-right", "Alt+PageDown", CommandScope.Archive, new[] { "archive-results-list" }),
        new("archive.select-page", "command.archive.select-page", "Select all on this page", "Alle auf dieser Seite auswählen", "check-square", "Primary+A", CommandScope.Archive, new[] { "archive-results-list" }),
        new("input.focus-download-search", "command.input.focus-download-search", "Search local downloads", "Lokale Downloads durchsuchen", "search", "Primary+F", CommandScope.Downloads, new[] { "downloads" }),
        new("export.open-all-results-html-dialog", "command.export.open-all-results-html-dialog", "Export all results", "Alle Treffer exportieren", "download", null, CommandScope.Archive, new[] { "archive-results-toolbar" }),
        new("archive.refresh", "command.archive.refresh", "Refresh archive", "Archiv aktualisieren", "refresh-cw", null, CommandScope.Archive, new[] { "archive-toolbar" }),
        new("archive.transfer-preview", "transfer.preview.open", "Inspect transfer package…", "Transferpaket prüfen…", "archive", null, CommandScope.Archive, new[] { "archive-header" }),
        new("archive.cleanup", "command.archive.cleanup", "Clean up archive…", "Archiv aufräumen…", "archive", null, CommandScope.Archive, new[] { "archive-toolbar" }),
        new("archive.saved-searches.open-manager", "savedSearch.openManager", "Saved searches…", "Gespeicherte Suchen…", "folder", null, CommandScope.Archive, new[] { "archive-results-toolbar" }),
        new("archive.saved-searches.save-current", "savedSearch.saveCurrent", "Save applied search…", "Angewandte Suche speichern…", "save", null, CommandScope.Archive | CommandScope.Dialog, new[] { "archive-results-toolbar" }),
        new("archive.saved-searches.apply", "savedSearch.apply", "Apply search", "Suche anwenden", "search", null, CommandScope.Dialog, new[] { "saved-searches-dialog" }),
        new("archive.saved-searches.rename", "savedSearch.rename", "Rename", "Umbenennen", "edit-2", null, CommandScope.Dialog, new[] { "saved-searches-dialog" }),
        new("archive.saved-searches.replace", "savedSearch.replace", "Replace with applied search…", "Durch angewandte Suche ersetzen…", "refresh-cw", null, CommandScope.Dialog, new[] { "saved-searches-dialog" }),
        new("archive.saved-searches.delete", "savedSearch.delete", "Delete search…", "Suche löschen…", "x-circle", null, CommandScope.Dialog, new[] { "saved-searches-dialog" }),
        new("archive.search", "command.archive.search", "Search", "Suchen", "search", null, CommandScope.Archive, new[] { "archive-toolbar", "archive-search-field" }),
        new("archive.filters-toggle", "command.archive.filters-toggle", "Filters and sorting", "Filter und Sortierung", "filter", "Primary+Shift+F", CommandScope.Archive, new[] { "archive-toolbar" }),
        new("archive.thread-view", "command.archive.thread-view", "Choose archive view", "Archivansicht wählen", "columns", null, CommandScope.Archive, new[] { "archive-toolbar" }),
        new("archive.media-view", "command.archive.media-view", "Choose media view", "Medienansicht wählen", "grid", null, CommandScope.Archive | CommandScope.Media, new[] { "archive-media-toolbar" }),
        new("pagination.previous", "command.pagination.previous", "Previous page", "Vorherige Seite", "chevron-left", "Alt+PageUp", CommandScope.Archive | CommandScope.Reader, new[] { "archive-thread-pages", "reader-post-pages", "reader-reference-pages" }, new[] { "reader-pages" }),
        new("pagination.next", "command.pagination.next", "Next page", "Nächste Seite", "chevron-right", "Alt+PageDown", CommandScope.Archive | CommandScope.Reader, new[] { "archive-thread-pages", "reader-post-pages", "reader-reference-pages" }, new[] { "reader-pages" }),
        new("archive.back-to-list", "command.archive.back-to-list", "Back to thread list", "Zurück zur Threadliste", "arrow-left", null, CommandScope.Reader, new[] { "embedded-reader" }),
        new("archive.capture-toggle", "command.archive.capture-toggle", "Start / stop post capture", "Post-Erfassung starten / stoppen", "circle", null, CommandScope.Reader | CommandScope.Application, new[] { "reader-thread-menu" }),
        new("reader.close", "command.reader.close", "Close reader", "Reader schließen", "arrow-left", "Escape", CommandScope.Reader, new[] { "large-reader-toolbar" }),
        new("reader.expand", "command.reader.expand", "Large reader", "Großer Reader", "external-link", "Primary+Enter", CommandScope.Reader, new[] { "embedded-reader-toolbar" }),
        new("reader.history-back", "command.reader.history-back", "Back to last read", "Zurück zum Lesestand", "arrow-left", "Alt+Left", CommandScope.Reader, new[] { "reader-toolbar" }, new[] { "reader-history" }),
        new("reader.previous-thread", "command.reader.previous-thread", "Previous thread", "Voriger Thread", "arrow-left", "Left", CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader.next-thread", "command.reader.next-thread", "Next thread", "Nächster Thread", "arrow-right", "Right", CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader.previous-post", "command.reader.previous-post", "Previous post", "Voriger Post", "arrow-up", "Up", CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader.find", "command.reader.find", "Find in thread", "Im Thread suchen", "search", "Primary+F", CommandScope.Reader, new[] { "reader-find" }),
        new("reader.find-next", "command.reader.find-next", "Find next", "Weitersuchen", "arrow-down", "F3", CommandScope.Reader, new[] { "reader-find" }),
        new("reader.find-previous", "command.reader.find-previous", "Find previous", "Zurücksuchen", "arrow-up", "Shift+F3", CommandScope.Reader, new[] { "reader-find" }),
        new("reader.next-post", "command.reader.next-post", "Next post", "Nächster Post", "arrow-down", "Down", CommandScope.Reader, new[] { "large-reader-navigation" }),
        new("reader.open-quote", "command.reader.open-quote", "Show quoted post", "Zitierten Post anzeigen", "corner-up-left", null, CommandScope.Reader | CommandScope.Gallery, new[] { "reader-post-quote", "reader-reference-list", "gallery-post-context" }),
        new("reader.show-replies", "command.reader.show-replies", "Show replies", "Antworten anzeigen", "message-square", null, CommandScope.Reader, new[] { "reader-post" }),
        new("gallery.return", "command.gallery.return", "Back to gallery", "Zurück zur Galerie", "arrow-left", "Alt+Left", CommandScope.Reader, new[] { "reader-toolbar" }, new[] { "reader-gallery" }),
        new("media.return", "command.media.return", "Back to media", "Zurück zu Medien", "arrow-left", "Alt+Left", CommandScope.Reader, new[] { "reader-toolbar" }),
        new("gallery.close", "command.gallery.close", "Close gallery", "Galerie schließen", "x", "Escape", CommandScope.Gallery, new[] { "gallery-toolbar" }),
        new("gallery.previous", "command.gallery.previous", "Previous media", "Voriges Medium", "chevron-left", "Left", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.next", "command.gallery.next", "Next media", "Nächstes Medium", "chevron-right", "Right", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.fit", "command.gallery.fit", "Fit", "Einpassen", "image", "0", CommandScope.Gallery, new[] { "gallery-toolbar", "image-double-click" }),
        new("gallery.actual-size", "command.gallery.actual-size", "1:1", "1:1", "crosshair", "1", CommandScope.Gallery, new[] { "gallery-toolbar", "image-double-click" }),
        new("gallery.zoom-in", "command.gallery.zoom-in", "Zoom in", "Vergrößern", "zoom-in", "Plus", CommandScope.Gallery, new[] { "gallery-toolbar", "image-wheel" }),
        new("gallery.zoom-out", "command.gallery.zoom-out", "Zoom out", "Verkleinern", "zoom-out", "Minus", CommandScope.Gallery, new[] { "gallery-toolbar", "image-wheel" }),
        // Turns the shown picture or video clockwise by 90° (a view transform, never a file change; upright again with every new medium).
        new("gallery.rotate", "command.gallery.rotate", "Rotate 90°", "90° drehen", "rotate-cw", "Shift+R", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        // G1: the gallery's mode switch "View | Tag" (enters and leaves the tagging mode of a walkable list).
        new("gallery.mode-toggle", "command.gallery.mode-toggle", "Gallery mode: view / tag", "Galeriemodus: Ansehen / Taggen", "edit-2", "Shift+T", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.tools-toggle", "command.gallery.tools-toggle", "Show / hide gallery tools", "Galeriewerkzeuge anzeigen / ausblenden", "sliders", "Shift+H", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.slideshow-toggle", "command.gallery.slideshow-toggle", "Start / pause slideshow", "Diashow starten / pausieren", "play", "D", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.overlay-toggle", "command.gallery.overlay-toggle", "Toggle controls overlay", "Overlay-Bedienelemente umschalten", "sliders", "Shift+O", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.overlay-autohide-toggle", "command.gallery.overlay-autohide-toggle", "Toggle overlay auto-hide", "Automatisches Ausblenden umschalten", "sliders", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.post-overlay-toggle", "command.gallery.post-overlay-toggle", "Toggle post overlay", "Post-Overlay umschalten", "file-text", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.filmstrip-toggle", "command.gallery.filmstrip-toggle", "Show / hide filmstrip", "Filmstreifen anzeigen / ausblenden", "columns", "Shift+L", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        // GL3: the layout menu. All unbound (rebindable); the filmstrip row of the menu is the Shift+L command above.
        new("gallery.layout-menu", "command.gallery.layout-menu", "Open layout menu", "Layout-Menü öffnen", "columns", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.layout.tools", "command.gallery.layout.tools", "Layout: show / hide tools", "Layout: Werkzeuge anzeigen / ausblenden", "tool", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.layout.info", "command.gallery.layout.info", "Layout: show / hide info line", "Layout: Infozeile anzeigen / ausblenden", "info", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.layout.hints", "command.gallery.layout.hints", "Layout: show / hide hints", "Layout: Hinweise anzeigen / ausblenden", "help", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.layout.filmstrip-top", "command.gallery.layout.filmstrip-top", "Layout: filmstrip at the top", "Layout: Filmstreifen oben", "columns", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.layout.filmstrip-bottom", "command.gallery.layout.filmstrip-bottom", "Layout: filmstrip at the bottom", "Layout: Filmstreifen unten", "columns", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.layout.reset", "command.gallery.layout.reset", "Reset gallery layout", "Galerie-Layout zurücksetzen", "rotate-ccw", null, CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("gallery.context-toggle", "command.gallery.context-toggle", "Show post context", "Post-Kontext anzeigen", "file-text", "Shift+P", CommandScope.Gallery, new[] { "gallery-toolbar" }),
        new("gallery.context-close", "command.gallery.context-close", "Back to image", "Zurück zum Bild", "image", null, CommandScope.Gallery, new[] { "gallery-post-context" }),
        new("gallery.show-in-thread", "command.gallery.show-in-thread", "Show in thread", "Im Thread anzeigen", "file-text", "T", CommandScope.Gallery | CommandScope.Media, new[] { "gallery-post-context", "media-page" }),
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
        new("rules.template-create", "templates.create", "Create template", "Vorlage erstellen", "plus", null, CommandScope.Application, new[] { "rules-page" }),
        new("rules.template-edit", "templates.edit", "Edit template", "Vorlage bearbeiten", "edit-2", null, CommandScope.Application, new[] { "rules-page" }),
        new("rules.template-delete", "templates.delete", "Delete template", "Vorlage löschen", "x-circle", null, CommandScope.Application, new[] { "rules-page" }),
        new("rules.template-preview", "templates.preview", "Preview template", "Vorlage ansehen", "eye", null, CommandScope.Application, new[] { "rules-page" }),
        new("rules.template-apply", "templates.apply", "Copy into draft", "In Entwurf übernehmen", "copy", null, CommandScope.Dialog, new[] { "rules-page" }),
        new("rules.template-undo-apply", "templates.undo-apply", "Undo template copy", "Übernahme rückgängig", "rotate-ccw", null, CommandScope.Application, new[] { "rules-page" }),
        new("rules.save", "command.rules.save", "Save rule", "Regel speichern", "save", null, CommandScope.Application, new[] { "rules-footer" }),
        new("admission.reinclude", "command.admission.reinclude", "Include previously excluded matches", "Ausgeschlossene Treffer wieder aufnehmen", "plus-circle", null, CommandScope.Application, new[] { "board-admission-summary" }),
        new("admission.add-rule", "command.admission.add-rule", "Add text rule", "Textregel hinzufügen", "plus", null, CommandScope.Application, new[] { "board-admission-rule-editor" }),
        new("admission.retry-catalog", "command.admission.retry-catalog", "Retry catalog", "Katalog erneut laden", "refresh-cw", null, CommandScope.Application, new[] { "board-admission-error" }),
        new("admission.cancel", "command.admission.cancel", "Cancel", "Abbrechen", "x", null, CommandScope.Application, new[] { "board-admission-footer" }),
        new("admission.monitor-all", "command.admission.monitor-all", "Watch all", "Alle beobachten", "eye", null, CommandScope.Application, new[] { "board-admission-footer" }),
        new("admission.apply", "command.admission.apply", "Apply selection and watch", "Auswahl übernehmen und beobachten", "check", null, CommandScope.Application, new[] { "board-admission-footer" }),
        new("admission.reset-choice", "command.admission.reset-choice", "Use rules again", "Wieder Regeln verwenden", "rotate-ccw", null, CommandScope.Application, new[] { "board-admission-thread-row" }),
        new("admission.remove-rule", "command.admission.remove-rule", "Remove rule", "Regel entfernen", "x", null, CommandScope.Application, new[] { "board-admission-rule-row" }),
        new("export.open-urltext-dialog", "urltext.export.open", "Export link list", "Linkliste exportieren", "download", null, CommandScope.Archive, new[] { "archive-card-menu", "archive-table-menu" }),
        new("export.open-html-dialog", "command.export.open-html-dialog", "Export", "Exportieren", "download", "Primary+E", CommandScope.Archive | CommandScope.Reader | CommandScope.Downloads, new[] { "downloads-thread-menu", "archive-card-menu", "archive-table-menu", "reader-toolbar" }),
        new("export.open-previous", "command.export.open-previous", "Open previous export", "Früheren Export öffnen", "folder", null, CommandScope.Dialog, new[] { "html-export-dialog" }),
        new("export.associate", "command.export.associate", "Associate export folder…", "Exportordner zuordnen…", "link", null, CommandScope.Dialog, new[] { "html-export-dialog" }),
        new("export.find-legacy", "command.export.find-legacy", "Find older exports", "Ältere Exporte suchen", "search", null, CommandScope.Dialog, new[] { "html-export-legacy-section" }),
        new("export.associate-legacy", "command.export.associate-legacy", "Associate older export with this thread", "Älteren Export diesem Thread zuordnen", "link", null, CommandScope.Dialog, new[] { "html-export-legacy-section" }),
        new("export.browse-parent", "command.export.browse-parent", "Choose parent folder", "Übergeordneten Ordner wählen", "folder", null, CommandScope.Dialog, new[] { "html-export-dialog", "original-export-dialog:planned" }),
        new("export.close", "command.export.close", "Close", "Schließen", "x", null, CommandScope.Dialog, new[] { "html-export-dialog" }),
        new("export.cancel", "command.export.cancel", "Cancel export", "Export abbrechen", "x-circle", null, CommandScope.Dialog, new[] { "html-export-progress", "original-export-progress:planned" }),
        new("export.start-html", "command.export.start-html", "Export", "Exportieren", "download", null, CommandScope.Dialog, new[] { "html-export-dialog" }),
        new("sources.select", "command.sources.select", "Select source", "Quelle auswählen", "globe", null, CommandScope.Sources, new[] { "sources-card" }),
        new("sources.back", "command.sources.back", "Back to Plugins", "Zurück zu Plugins", "arrow-left", null, CommandScope.Sources, new[] { "sources-details-narrow" }),
        new("watch.show-in-downloads", "command.watch.show-in-downloads", "Show in downloads", "In Downloads zeigen", "corner-up-right", null, CommandScope.Sources | CommandScope.Diagnostics | CommandScope.Downloads, new[] { "sources-watch-details", "diagnostics-details" }),
        new("diagnostics.retry-selection", "command.diagnostics.retry-selection", "Retry selected targets", "Auswahl wiederholen", "refresh-cw", "Shift+F5", CommandScope.Diagnostics, new[] { "diagnostics" }),
        new("diagnostics.retry-filtered", "command.diagnostics.retry-filtered", "Retry filtered targets", "Gefilterte wiederholen", "refresh-cw", null, CommandScope.Diagnostics, new[] { "diagnostics" }),
        new("diagnostics.copy", "command.diagnostics.copy", "Copy details", "Details kopieren", "copy", "Primary+C", CommandScope.Diagnostics, new[] { "diagnostics-details" }),
        new("about.open-link", "command.about.open-link", "Open project, credit or license page", "Projekt-/Credit-/Lizenzseite öffnen", "external-link", null, CommandScope.About | CommandScope.Sources, new[] { "about-project-link", "about-author-link", "about-ychan-link", "about-gchan-link", "about-feather-link", "about-project-license-link", "credits-component-link:planned", "licenses-source-link:planned" }),
        new("about.close", "command.about.close", "Close", "Schließen", "x", null, CommandScope.About | CommandScope.Application, new[] { "about-footer" }),
        new("viewer.focus-mode", "command.viewer.focus-mode", "Expand workspace / show list", "Arbeitsbereich vergrößern / Liste zeigen", "sidebar", null, CommandScope.Reader | CommandScope.Gallery, new[] { "embedded-reader", "embedded-gallery", "large-reader", "large-gallery" }),
        new("window.fullscreen", "command.window.fullscreen", "Enter / exit fullscreen", "Vollbild einschalten / verlassen", "maximize", "F11", CommandScope.Reader | CommandScope.Gallery | CommandScope.Downloads | CommandScope.Application, new[] { "shell", "downloads-threadwatcher", "reader", "gallery", "app-menu" }),
        new("viewer.shortcut-help", "command.viewer.shortcut-help", "Keyboard shortcuts", "Tastaturhilfe", "help", "F1", CommandScope.Reader | CommandScope.Gallery, new[] { "reader", "gallery", "workspace" }),
        new("viewer.shortcut-hints", "command.viewer.shortcut-hints", "Show / hide shortcut hints", "Kurzhilfe anzeigen / ausblenden", "help", null, CommandScope.Reader | CommandScope.Gallery | CommandScope.Media | CommandScope.Settings, new[] { "reader", "gallery", "settings-reading-media" }),
        new("input.focus-search", "command.input.focus-search", "Focus search", "Suche fokussieren", "search", "Primary+F", CommandScope.Archive | CommandScope.Reader, new[] { "archive", "license-reader", "active-search-context" }),
        new("input.focus-media-search", "command.input.focus-media-search", "Search media", "Medien durchsuchen", "search", "Primary+F", CommandScope.Media, new[] { "media" }),
        new("input.focus-settings-search", "command.input.focus-settings-search", "Search settings", "Einstellungen durchsuchen", "search", "Primary+F", CommandScope.Settings, new[] { "settings" }, new[] { "settings" }),
        new("input.focus-diagnostics-search", "command.input.focus-diagnostics-search", "Search diagnostics", "Diagnose durchsuchen", "search", "Primary+F", CommandScope.Diagnostics, new[] { "diagnostics" }),
        new("archive.clear-filter", "command.archive.clear-filter", "Remove filter", "Filter entfernen", "x", null, CommandScope.Archive, new[] { "archive-active-filter-chip" }),
        new("table.choose-columns", "command.table.choose-columns", "Choose columns…", "Spalten auswählen…", "columns", null, CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-table", "archive-table" }),
        new("table.reset-layout", "command.table.reset-layout", "Reset table layout", "Tabellenlayout zurücksetzen", "rotate-ccw", null, CommandScope.Archive | CommandScope.Downloads, new[] { "downloads-table", "archive-table", "split-layout" }),
        new("reader.font-size", "command.reader.font-size", "Reading font size", "Leseschriftgröße", "type", null, CommandScope.Reader | CommandScope.Media | CommandScope.Settings, new[] { "reader-toolbar", "settings-reading-media" }),
        new("watch.follow-live", "command.watch.follow-live", "Show new posts automatically", "Neue Posts automatisch anzeigen", "arrow-down", "Primary+Shift+L", CommandScope.Reader | CommandScope.Downloads, new[] { "reader-settings", "threadwatcher" }),
        new("reader.show-new-posts", "command.reader.show-new-posts", "Show new posts", "Neue Posts anzeigen", "arrow-down", "Primary+Shift+N", CommandScope.Reader, new[] { "reader-new-posts-notice" }),
        // The reader's view options (plan P0; the settings popover's session rows since RL1). No default gesture; each one can be given a shortcut. The conversation view
        // (P4) and the post filters (P2) appear in the menu once their package registers them.
        new("reader.view.hide-media", "command.reader.view.hide-media", "Hide media", "Medien ausblenden", "eye-off", null, CommandScope.Reader, new[] { "reader-settings" }),
        new("reader.view.media-only", "command.reader.view.media-only", "Only posts with media", "Nur Posts mit Medien", "image", null, CommandScope.Reader, new[] { "reader-settings" }),
        new("reader.view.conversation", "command.reader.view.conversation", "Show as conversation", "Als Gespräch zeigen", "message-square", null, CommandScope.Reader, new[] { "reader-settings" }),
        new("reader.view.textless-large", "command.reader.view.textless-large", "Larger images in posts without text", "Größere Bilder in Posts ohne Text", "image", null, CommandScope.Reader, new[] { "reader-settings" }),
        new("reader.view.focused-enlarge", "command.reader.view.focused-enlarge", "Enlarge focused post's media", "Medien des markierten Posts vergrößern", "zoom-in", null, CommandScope.Reader, new[] { "reader-settings" }),
        new("reader.view.hover-enlarge", "command.reader.view.hover-enlarge", "Enlarge image on hover", "Bild beim Überfahren vergrößern", "zoom-in", null, CommandScope.Reader, new[] { "reader-settings" }),
        new("reader.post-filters", "command.reader.post-filters", "Post filters…", "Postfilter…", "filter", null, CommandScope.Reader, new[] { "reader-settings" }),
        // The reader's settings popover (R7, the toolbar's gear since RL1); a user shortcut opens it too.
        new("reader.quick-settings", "command.reader.quick-settings", "Reader settings…", "Reader-Einstellungen…", "settings", null, CommandScope.Reader, new[] { "reader-settings" }),
        // R8: the media sidebar of the large reader (a settings-popover switch) and its steps through the focused post's media.
        new("reader.sidebar.toggle", "command.reader.sidebar.toggle", "Media sidebar", "Medienleiste", "sidebar", "Primary+Shift+M", CommandScope.Reader, new[] { "reader-settings", "reader-media-sidebar" }),
        new("reader.sidebar.previous-media", "command.reader.sidebar.previous-media", "Previous media of the post", "Voriges Medium des Posts", "chevron-left", "Alt+Shift+Left", CommandScope.Reader, new[] { "reader-media-sidebar" }),
        new("reader.sidebar.next-media", "command.reader.sidebar.next-media", "Next media of the post", "Nächstes Medium des Posts", "chevron-right", "Alt+Shift+Right", CommandScope.Reader, new[] { "reader-media-sidebar" }),
        // The Media page's media panel (a large medium beside the grid).
        new("media.panel.toggle", "command.media.panel.toggle", "Media sidebar", "Medienleiste", "sidebar", "Primary+Shift+M", CommandScope.Media, new[] { "media-layout-menu", "media-panel" }),
        // Revision 19: H hides the selected post (ArchivePage.OnPostFilterKeyDown; a text field keeps its letters).
        new("post.hide", "command.post.hide", "Hide post", "Post ausblenden", "eye-off", "H", CommandScope.Reader, new[] { "reader-post-menu" }),
        new("post.unhide", "command.post.unhide", "Show post again", "Post wieder anzeigen", "eye", null, CommandScope.Reader, new[] { "reader-post-menu" }),
        new("thread.favorite-toggle", "command.thread.favorite-toggle", "Favorite / unfavorite thread", "Threadfavorit markieren / entfernen", "star", "Primary+D", CommandScope.Archive | CommandScope.Reader, new[] { "archive-card", "archive-table", "archive-thread-menu", "reader-toolbar" }),
        new("thread.tags", "command.thread.tags", "Thread tags…", "Thread-Tags…", "tag", "Primary+T", CommandScope.Archive | CommandScope.Reader, new[] { "archive-thread-menu" }, new[] { "archive", "reader" }),
        // Quick tagging (tag panel in the gallery, tag popover elsewhere). Registered ahead of the handlers: QuickTagCommands.AwaitingHandler keeps them inactive and unlisted until
        // the packages that wire them say so. The panel commands act in the gallery only (Ctrl+T there is the thread tags section; thread.tags stays in the archive and the reader);
        // undo and the suggestion toggles also act in the popover on the media page, the reader attachment and the archive tile.
        new("tags.quick.next", "command.tags.quick.next", "Next media (tag panel)", "Nächstes Medium (Tag-Panel)", "chevron-right", "PageDown", CommandScope.Gallery, new[] { "gallery-tag-panel" }, new[] { "gallery" }),
        new("tags.quick.previous", "command.tags.quick.previous", "Previous media (tag panel)", "Voriges Medium (Tag-Panel)", "chevron-left", "PageUp", CommandScope.Gallery, new[] { "gallery-tag-panel" }, new[] { "gallery" }),
        new("tags.quick.commit-next", "command.tags.quick.commit-next", "Apply tags and go to next media", "Tags übernehmen und zum nächsten Medium", "check", "Primary+Enter", CommandScope.Gallery, new[] { "gallery-tag-panel" }, new[] { "gallery" }),
        new("tags.quick.mark-next", "command.tags.quick.mark-next", "Mark media for batch and go on", "Medium für Stapel markieren und weiter", "check-square", "Shift+PageDown", CommandScope.Gallery, new[] { "gallery-tag-panel" }, new[] { "gallery" }),
        new("tags.quick.mark-previous", "command.tags.quick.mark-previous", "Go back and unmark media", "Zurück und Markierung aufheben", "rotate-ccw", "Shift+PageUp", CommandScope.Gallery, new[] { "gallery-tag-panel" }, new[] { "gallery" }),
        new("tags.quick.thread-section", "command.tags.quick.thread-section", "Thread tags section", "Abschnitt Thread-Tags", "edit-2", "Primary+T", CommandScope.Gallery, new[] { "gallery-tag-panel" }, new[] { "gallery" }),
        new("tags.quick.undo", "command.tags.quick.undo", "Undo last tag change", "Letzte Tagänderung rückgängig machen", "rotate-ccw", "Primary+Z", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.stamp-apply", "command.tags.quick.stamp-apply", "Apply stamp", "Stempel setzen", "check-square", "Primary+Shift+Enter", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-1", "command.tags.quick.suggestion-1", "Toggle tag suggestion 1", "Tagvorschlag 1 umschalten", "check", "Alt+1", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-2", "command.tags.quick.suggestion-2", "Toggle tag suggestion 2", "Tagvorschlag 2 umschalten", "check", "Alt+2", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-3", "command.tags.quick.suggestion-3", "Toggle tag suggestion 3", "Tagvorschlag 3 umschalten", "check", "Alt+3", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-4", "command.tags.quick.suggestion-4", "Toggle tag suggestion 4", "Tagvorschlag 4 umschalten", "check", "Alt+4", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-5", "command.tags.quick.suggestion-5", "Toggle tag suggestion 5", "Tagvorschlag 5 umschalten", "check", "Alt+5", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-6", "command.tags.quick.suggestion-6", "Toggle tag suggestion 6", "Tagvorschlag 6 umschalten", "check", "Alt+6", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-7", "command.tags.quick.suggestion-7", "Toggle tag suggestion 7", "Tagvorschlag 7 umschalten", "check", "Alt+7", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-8", "command.tags.quick.suggestion-8", "Toggle tag suggestion 8", "Tagvorschlag 8 umschalten", "check", "Alt+8", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        new("tags.quick.suggestion-9", "command.tags.quick.suggestion-9", "Toggle tag suggestion 9", "Tagvorschlag 9 umschalten", "check", "Alt+9", CommandScope.Gallery | CommandScope.Media | CommandScope.Reader | CommandScope.Archive, new[] { "gallery-tag-panel", "quick-tag-popover" }, new[] { "gallery", "media", "reader", "archive" }),
        // Tagging mode (PT3): the gallery walking the tagging queue. Own execution context "gallery-tagging": the keys mean something
        // else there than in the plain gallery (Space skips instead of playing, Backspace goes back instead of resetting the speed),
        // and only the tagging mode routes them, ahead of the gallery's own keys.
        new("tags.queue.done-next", "command.tags.queue.done-next", "Done and next", "Erledigt und weiter", "check", "Enter", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("tags.queue.skip", "command.tags.queue.skip", "Skip and next", "Überspringen und weiter", "arrow-right", "Space", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("tags.queue.back", "command.tags.queue.back", "Back: undo the last decision", "Zurück: letzte Entscheidung zurücknehmen", "corner-up-left", "Back", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("tags.queue.play-pause", "command.tags.queue.play-pause", "Play / pause in tagging mode", "Wiedergabe / Pause im Tagging-Modus", "play", "Shift+Space", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        // Card mode (PT4): Shift+K switches it on and off inside the tagging mode (the cards are a block, so Shift plus a letter since revision 13); the five card keys (1 to 5) act only while the cards are shown.
        // Same context as the keys above, so 1 to 5 never compete with the plain gallery's 1 (actual size) and 0 outside the mode.
        new("tags.queue.cards", "command.tags.queue.cards", "Card mode on / off", "Kartenmodus ein / aus", "columns", "Shift+K", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("tags.queue.card-1", "command.tags.queue.card-1", "Choose card 1", "Karte 1 wählen", "check", "D1", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("tags.queue.card-2", "command.tags.queue.card-2", "Choose card 2", "Karte 2 wählen", "check", "D2", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("tags.queue.card-3", "command.tags.queue.card-3", "Choose card 3", "Karte 3 wählen", "check", "D3", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("tags.queue.card-4", "command.tags.queue.card-4", "Choose card 4", "Karte 4 wählen", "check", "D4", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("tags.queue.card-5", "command.tags.queue.card-5", "Choose card 5", "Karte 5 wählen", "check", "D5", CommandScope.Gallery, new[] { "gallery-tagging" }, new[] { "gallery-tagging" }),
        new("favorites.toggle", "command.favorites.toggle", "Add / remove favorite", "Als Favorit markieren / entfernen", "star", "F", CommandScope.Reader | CommandScope.Gallery | CommandScope.Media, new[] { "reader-inline-media", "gallery-toolbar", "media-tile", "media-context-menu" }),
        new("favorites.add", "command.favorites.add", "Add favorite", "Favorit hinzufügen", "star", "B", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("favorites.remove", "command.favorites.remove", "Remove favorite", "Favorit entfernen", "x", "U", CommandScope.Gallery, new[] { "gallery-toolbar", "gallery-keyboard" }),
        new("favorites.filter", "command.favorites.filter", "Favorites only", "Nur Favoriten", "star", null, CommandScope.Archive | CommandScope.Media, new[] { "thread-media-filter", "archive-media-filter" }),
        new("favorites.open", "command.favorites.open", "Show favorites", "Favoriten anzeigen", "star", null, CommandScope.Archive, new[] { "archive-favorites-overview" }),
        new("favorites.export-originals", "command.favorites.export-originals", "Export favorite originals", "Favorisierte Originale exportieren", "download", null, CommandScope.Archive | CommandScope.Gallery | CommandScope.Media, new[] { "gallery", "thread-media", "selected-threads", "archive-results", "favorites-overview" }),
        new("media.export-original", "command.media.export-original", "Export", "Exportieren", "download", "Primary+E", CommandScope.Gallery | CommandScope.Media, new[] { "media-context-menu", "gallery-menu" }),
        new("media.show-in-folder", "command.media.show-in-folder", "Show in folder", "Im Ordner anzeigen", "folder", "Primary+O", CommandScope.Gallery | CommandScope.Media, new[] { "media-context-menu", "gallery-menu" }),
        new("media.details", "command.media.details", "Show media details", "Mediendetails anzeigen", "info", "I", CommandScope.Gallery | CommandScope.Media, new[] { "media-context-menu", "gallery-menu" }),
        new("media.copy-path", "command.media.copy-path", "Copy file path", "Dateipfad kopieren", "copy", "Primary+Shift+C", CommandScope.Gallery | CommandScope.Media, new[] { "media-context-menu", "gallery-menu" }),
        new("media.open-external", "command.media.open-external", "Open with default program", "Mit Standardprogramm öffnen", "external-link", "Primary+Shift+O", CommandScope.Gallery, new[] { "gallery-file-only" }),
        // Revision 19: Primary+Shift+C copies the selected post's link, like the file path of a medium (Ctrl/Cmd+C stays text copy).
        new("post.copy-link", "command.post.copy-link", "Copy post link", "Post-Link kopieren", "link", "Primary+Shift+C", CommandScope.Reader, new[] { "reader-post-menu" }),
        new("post.open-source", "command.post.open-source", "Open post on source", "Post auf Quelle öffnen", "external-link", null, CommandScope.Reader, new[] { "reader-post-menu" }),
        new("post.copy-text", "command.post.copy-text", "Copy post text", "Post-Text kopieren", "copy", null, CommandScope.Reader, new[] { "reader-post-menu" }),
        new("post.copy-number", "command.post.copy-number", "Copy post number", "Post-Nummer kopieren", "copy", null, CommandScope.Reader, new[] { "reader-post-menu" }),
        new("video.play-pause", "command.video.play-pause", "Play / pause", "Wiedergabe / Pause", "play", "Space", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-surface", "gallery-video-controls", "reader-video-keys", "media-video-keys" }),
        new("video.volume-up", "command.video.volume-up", "Increase video volume", "Videolautstärke erhöhen", "volume-2", "Up", CommandScope.Gallery, new[] { "gallery-video-controls", "gallery-keyboard" }),
        new("video.volume-down", "command.video.volume-down", "Decrease video volume", "Videolautstärke verringern", "volume-2", "Down", CommandScope.Gallery, new[] { "gallery-video-controls", "gallery-keyboard" }),
        // Gallery key scheme (revision 13, GalleryShortcutScheme): plain letters are playback actions, so M mutes (it was Shift+M); "Add favorite" moved from M to B (Bookmark; F still toggles, U removes).
        new("video.mute", "command.video.mute", "Mute / unmute", "Ton aus / an", "volume-2", "M", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-controls", "reader-video-keys", "media-video-keys" }),
        // L repeats the shown video (the filmstrip toggle moved to Shift+L for it; both are gallery commands, so they may not share a key).
        new("video.loop", "command.video.loop", "Repeat video", "Video wiederholen", "repeat", "L", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-controls", "gallery-keyboard", "reader-video-keys", "media-video-keys" }),
        // Left/Right stay previous/next medium; seeking takes Ctrl (⌘) with the arrows, far seeking adds Shift.
        new("video.seek-back", "command.video.seek-back", "Back 5 seconds", "5 Sekunden zurück", "rotate-ccw", "Primary+Left", CommandScope.Gallery, new[] { "gallery-video-controls", "gallery-keyboard" }),
        new("video.seek-forward", "command.video.seek-forward", "Forward 5 seconds", "5 Sekunden vor", "rotate-cw", "Primary+Right", CommandScope.Gallery, new[] { "gallery-video-controls", "gallery-keyboard" }),
        new("video.seek-back-far", "command.video.seek-back-far", "Back 30 seconds", "30 Sekunden zurück", "chevron-left", "Primary+Shift+Left", CommandScope.Gallery, new[] { "gallery-video-controls", "gallery-keyboard" }),
        new("video.seek-forward-far", "command.video.seek-forward-far", "Forward 30 seconds", "30 Sekunden vor", "chevron-right", "Primary+Shift+Right", CommandScope.Gallery, new[] { "gallery-video-controls", "gallery-keyboard" }),
        // "," and "." jump back and forward by the seek step setting (5 seconds by default, 1 to 60); they act only while a video is shown.
        new("video.step-back", "command.video.step-back", "Back by the seek step", "Um die Sprungweite zurück", "rotate-ccw", "OemComma", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-controls", "gallery-keyboard", "reader-video-keys", "media-video-keys" }),
        new("video.step-forward", "command.video.step-forward", "Forward by the seek step", "Um die Sprungweite vor", "rotate-cw", "OemPeriod", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-controls", "gallery-keyboard", "reader-video-keys", "media-video-keys" }),
        // Speed: the catalog key [ / ] plus the extra defaults X (slower) and C (faster) of EffectiveShortcutBindings.Defaults (revision 14; [ ] need AltGr on DE/FR layouts).
        new("video.speed-down", "command.video.speed-down", "Slower", "Langsamer", "minus", "OemOpenBrackets", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-controls", "gallery-keyboard", "reader-video-keys", "media-video-keys" }),
        new("video.speed-up", "command.video.speed-up", "Faster", "Schneller", "plus", "OemCloseBrackets", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-controls", "gallery-keyboard", "reader-video-keys", "media-video-keys" }),
        new("video.speed-reset", "command.video.speed-reset", "Normal speed", "Normale Geschwindigkeit", "activity", "Back", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-controls", "gallery-keyboard", "reader-video-keys", "media-video-keys" }),
        new("video.snapshot", "command.video.snapshot", "Save frame as image", "Einzelbild speichern", "image", "S", CommandScope.Gallery, new[] { "gallery-menu", "gallery-keyboard" }),
        // Transport row (plan V1): A sets A, then B, then turns the loop off; Shift+. shows the next frame (LibVLC 3 has no step back,
        // so Shift+, stays free: there is no "previous frame" command). Audio track and the speed choices have no default key: the player menu and rebinding reach them.
        new("video.ab-loop", "command.video.ab-loop", "A–B loop", "A–B-Schleife", "repeat", "A", CommandScope.Gallery, new[] { "gallery-video-controls", "gallery-keyboard" }),
        new("video.frame-step", "command.video.frame-step", "Next frame", "Nächstes Einzelbild", "skip-forward", "Shift+OemPeriod", CommandScope.Gallery | CommandScope.Reader | CommandScope.Media, new[] { "gallery-video-controls", "gallery-keyboard", "reader-video-keys", "media-video-keys" }),
        new("video.audio-track", "command.video.audio-track", "Next audio track", "Nächste Tonspur", "volume-2", null, CommandScope.Gallery, new[] { "gallery-video-menu" }),
        new("video.speed-choose", "command.video.speed-choose", "Choose speed…", "Tempo wählen…", "sliders", null, CommandScope.Gallery, new[] { "gallery-video-controls", "gallery-video-menu" }),
        new("about.copy-version", "command.about.copy-version", "Copy version", "Version kopieren", "copy", null, CommandScope.About, new[] { "about-overview" }),
        new("about.open-license", "command.about.open-license", "Open license text", "Lizenztext öffnen", "file-text", null, CommandScope.About, new[] { "about-licenses", "component-license-list" }),
        new("about.show-credits", "command.about.show-credits", "Acknowledgements", "Danksagungen", "heart", null, CommandScope.About, new[] { "about-navigation" }),
        new("about.show-licenses", "command.about.show-licenses", "Licenses", "Lizenzen", "file-text", null, CommandScope.About, new[] { "about-navigation" }),
        new("export.start-originals", "command.export.start-originals", "Export originals", "Originale exportieren", "download", null, CommandScope.Dialog, new[] { "original-export-dialog" }),
        new("rules.toggle-enabled", "command.rules.toggle-enabled", "Enable or disable rule", "Regel aktivieren oder deaktivieren", "power", null, CommandScope.Application, new[] { "rules-overview-row" }),
        new("rules.delete", "command.rules.delete", "Delete rule…", "Regel löschen…", "x-circle", null, CommandScope.Application, new[] { "rules-overview-row" }),
        new("rules.copy-summary", "command.rules.copy-summary", "Copy rule summary", "Regelzusammenfassung kopieren", "copy", null, CommandScope.Application, new[] { "rules-overview-row" }),
        new("statistics.copy-row", "command.statistics.copy-row", "Copy row", "Zeile kopieren", "copy", "Primary+C", CommandScope.Application, new[] { "statistics-table" }, new[] { "statistics" }),
        new("statistics.copy-source", "command.statistics.copy-source", "Copy source name", "Quellennamen kopieren", "copy", null, CommandScope.Application, new[] { "statistics-table" }, new[] { "statistics" }),
        new("statistics.reset", "command.statistics.reset", "Reset statistics…", "Statistik zurücksetzen…", "rotate-ccw", null, CommandScope.Application, new[] { "statistics-table" }, new[] { "statistics" }),
        new("archive.copy-filter", "command.archive.copy-filter", "Copy filter text", "Filtertext kopieren", "copy", null, CommandScope.Archive, new[] { "archive-active-filter-chip" }),
        new("tags.filter-toggle", "command.tags.filter-toggle", "Switch include / exclude", "Zwischen Einschließen und Ausschließen wechseln", "refresh-cw", null, CommandScope.Archive | CommandScope.Media, new[] { "archive-active-filter-chip" }),
        new("tags.filter-include", "tags.filter.include", "Include", "Einschließen", "plus", null, CommandScope.Archive | CommandScope.Media, new[] { "tag-filter-picker" }),
        new("tags.filter-exclude", "tags.filter.exclude", "Exclude", "Ausschließen", "minus", null, CommandScope.Archive | CommandScope.Media, new[] { "tag-filter-picker" }),
        new("tags.copy-name", "command.tags.copy-name", "Copy tag name", "Tagnamen kopieren", "copy", null, CommandScope.Archive | CommandScope.Media, new[] { "archive-active-filter-chip", "tag-filter-picker" }),
        // The chips of the media filter row (and the row itself): the shared chip menu of the media page and the archive.
        new("tags.filter-only", "command.tags.filter-only", "Show only this", "Nur diesen anzeigen", "crosshair", null, CommandScope.Media, new[] { "media-filter-chip" }),
        new("media.filter.edit", "media.filter.chip.edit", "Edit…", "Bearbeiten…", "edit-2", null, CommandScope.Media, new[] { "media-filter-chip" }),
        new("media.filter.copy-query", "media.filter.chip.copyText", "Copy filters as text", "Filter als Text kopieren", "clipboard", null, CommandScope.Media, new[] { "media-filter-chip", "media-filter-row" }),
        new("media.filter.reset-all", "media.filter.resetAll", "Reset all", "Alle zurücksetzen", "rotate-ccw", null, CommandScope.Archive | CommandScope.Media, new[] { "media-filter-chip", "media-filter-row", "archive-active-filter-chip" }),
        new("media.queue.all", "media.queue.all", "Add all shown to the tagging queue…", "Alle angezeigten zur Tagging-Warteschlange hinzufügen…", "plus", null, CommandScope.Media, new[] { "media-filter-chip" }),
        new("feedback.copy-text", "command.feedback.copy-text", "Copy message text", "Meldungstext kopieren", "copy", null, CommandScope.Application, new[] { "notification" }),
        new("feedback.dismiss", "feedback.dismiss", "Dismiss message", "Meldung ausblenden", "x", null, CommandScope.Application, new[] { "notification" }),
        new("feedback.dismiss-all", "command.feedback.dismiss-all", "Close all notifications", "Alle Benachrichtigungen schließen", "x", null, CommandScope.Application, new[] { "notification" }),
        new("settings.reset-default", "command.settings.reset-default", "Reset setting", "Einstellung zurücksetzen", "rotate-ccw", null, CommandScope.Settings, new[] { "settings-row" }),
        new("settings.copy-id", "command.settings.copy-id", "Copy setting id", "Einstellungs-ID kopieren", "copy", null, CommandScope.Settings, new[] { "settings-row" }),
        new("menu.context-open", "command.menu.context-open", "Open context menu", "Kontextmenü öffnen", "more-horizontal", "Shift+F10", CommandScope.Archive | CommandScope.Downloads | CommandScope.Settings, new[] { "downloads-thread-list", "downloads-board-list", "archive-card-list", "archive-table", "plugins-list", "header-io-monitor", "rules-overview-row", "statistics-table", "settings-row", "archive-active-filter-chip", "tag-filter-picker", "notification", "media-filter-chip", "media-filter-row" }, new[] { "downloads", "archive", "plugins", "header-io-monitor", "rules", "statistics", "settings", "notification" }),
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
        { "viewer.shortcut-hints", new Dictionary<string, string>(StringComparer.Ordinal) { { "hidden", "help" }, { "shown", "help" } } },
        { "watch.follow-live", new Dictionary<string, string>(StringComparer.Ordinal) { { "off", "arrow-down" }, { "on", "arrow-down" } } },
        { "favorites.toggle", new Dictionary<string, string>(StringComparer.Ordinal) { { "unmarked", "star" }, { "marked", "star-filled" } } },
        { "favorites.filter", new Dictionary<string, string>(StringComparer.Ordinal) { { "all", "star" }, { "favorites_only", "star" } } },
        { "video.play-pause", new Dictionary<string, string>(StringComparer.Ordinal) { { "paused", "play" }, { "playing", "pause" } } },
        { "gallery.slideshow-toggle", new Dictionary<string, string>(StringComparer.Ordinal) { { "stopped", "play" }, { "running", "pause" } } },
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
        var snapshot = ShortcutService.Current;
        var platform = macOS ? ShortcutPlatform.MacOS : ShortcutPlatform.Windows;
        var gestures = snapshot.For(id);
        return gestures.Count == 0 ? null : string.Join(" / ", gestures.Select(g => g.Display(platform)));
    }

    /// <summary>The shortcut works outside inputs and overlays, when the action is available: the common case that the shortcut help states once.</summary>
    internal static bool HasGenericScope(string id) => id is "session.pause" or "navigation.plugins" || MainPageOrder.Contains(id)
        || MainPageOrder.StatusSegmentCommandIds.Contains(id, StringComparer.Ordinal);

    public static string? ScopeHint(string id, CommandScope context = CommandScope.None)
    {
        if (context == CommandScope.Reader && id == "gallery.open")
            return Texts.T("command.scope.gallery-open.reader");
        if (context == CommandScope.Reader && id == "export.open-html-dialog")
            return Texts.T("command.scope.export-open-html.reader");
        return id switch
        {
            "archive.select-page" => Texts.T("command.scope.archive.select-page"),
            "archive.previous-page" or "archive.next-page" => Texts.T("command.scope.archive.page-navigation"),
            "selection.select-all" => Texts.T("command.scope.selection.select-all"),
            "menu.context-open" => Texts.T("command.scope.menu.context-open"),
            "watch.import-clipboard" => Texts.T("command.scope.watch.import-clipboard"),
            "input.focus-download-search" => Texts.T("command.scope.input.focus-download-search"),
            "input.focus-search" => Texts.T("command.scope.input.focus-search"),
            "input.focus-media-search" => Texts.T("command.scope.input.focus-media-search"),
            "input.focus-diagnostics-search" => Texts.T("command.scope.input.focus-diagnostics-search"),
            "export.open-all-results-html-dialog" => Texts.T("command.scope.export.open-all-results-html-dialog"),
            "reader.expand" => Texts.T("command.scope.reader.expand"),
            _ when HasGenericScope(id) => Texts.T("command.scope.navigation.generic"),
            _ => null
        };
    }

    /// <summary>
    /// Export labels carry no trailing ellipsis, so the tooltip says instead that the command opens the export dialog.
    /// </summary>
    public static string? DialogHint(string id) => id switch
    {
        "export.open-html-dialog" or "export.open-all-results-html-dialog" or "export.open-urltext-dialog"
            or "favorites.export-originals" or "media.export-original" => Texts.T("command.hint.opens-export-dialog"),
        _ => null
    };

    public static IEnumerable<CommandDefinition> Available(CommandScope scope, Func<CommandDefinition, bool> isAvailable) =>
        Entries.Where(x => (x.Scope & scope) != 0 && isAvailable(x));
}
