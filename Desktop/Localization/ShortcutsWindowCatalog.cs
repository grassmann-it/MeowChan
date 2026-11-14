namespace MeowChan.Desktop.Localization;

/// <summary>Texts of the shortcuts window and its Settings card (modals plan KS2). Reached through
/// <see cref="WorkspaceHelpCatalog"/>, the catalog of the F1 help that the window replaces.</summary>
internal static class ShortcutsWindowCatalog
{
    public static readonly (string Key, string English, string German)[] Entries =
    {
        ("shortcuts.group.reader", "Reader", "Reader"),
        ("shortcuts.group.gallery", "Gallery", "Galerie"),
        ("shortcuts.group.tagPanel", "Tag panel", "Tag-Panel"),
        ("shortcuts.group.dialogs", "Dialogs", "Dialoge"),
        ("shortcuts.group.application", "Application and navigation", "Anwendung und Navigation"),
        ("shortcuts.group.current", "{0} (current area)", "{0} (aktueller Bereich)"),
        ("shortcuts.group.header", "{0} ({1})", "{0} ({1})"),
        ("shortcuts.group.expanded", "expanded", "aufgeklappt"),
        ("shortcuts.group.collapsed", "collapsed", "zugeklappt"),
        ("shortcuts.window.area", "Area", "Bereich"),
        ("shortcuts.window.area.all", "All areas", "Alle Bereiche"),
        ("shortcuts.window.assignedOnly", "Assigned only", "Nur belegte"),
        ("shortcuts.window.grouped", "Group by area", "Nach Bereich gruppieren"),
        ("shortcuts.window.conflicts", "Conflicts: {0}", "Konflikte: {0}"),
        ("shortcuts.window.conflicts.help", "Show only actions whose shortcut collides with another action", "Nur Aktionen zeigen, deren Kürzel mit einer anderen Aktion kollidiert"),
        ("shortcuts.window.shortcut", "Shortcut", "Kürzel"),
        ("shortcuts.window.status", "Status", "Status"),
        ("shortcuts.window.sortBy", "Sort by {0}", "Nach {0} sortieren"),
        ("shortcuts.window.sortedAscending", "{0}, sorted ascending", "{0}, aufsteigend sortiert"),
        ("shortcuts.window.sortedDescending", "{0}, sorted descending", "{0}, absteigend sortiert"),
        ("shortcuts.window.conflictWith", "Collides with: {0}", "Kollidiert mit: {0}"),
        ("shortcuts.window.selected", "Applies in: {0}", "Wirkt in: {0}"),
        ("shortcuts.window.noSelection", "Select an action to change its shortcuts.", "Wähle eine Aktion, um ihre Kürzel zu ändern."),
        ("shortcuts.window.recordReplace", "Record new shortcut", "Neues Kürzel aufnehmen"),
        ("shortcuts.window.empty", "No action matches the search and filters.", "Keine Aktion passt zu Suche und Filtern."),
        ("shortcuts.window.changes", "Unsaved changes: {0}", "Ungespeicherte Änderungen: {0}"),
        ("shortcuts.window.noChanges", "No unsaved changes", "Keine ungespeicherten Änderungen"),
        ("shortcuts.window.blocked", "Collisions prevent saving: {0}", "Kollisionen verhindern das Speichern: {0}"),
        ("shortcuts.window.undo", "Undo", "Rückgängig"),
        ("shortcuts.window.discard", "Discard", "Verwerfen"),
        ("shortcuts.window.save", "Save", "Speichern"),
        ("shortcuts.window.saved", "Shortcuts saved.", "Tastenkürzel gespeichert."),
        ("shortcuts.window.savedPending", "Shortcuts saved. Changes made while saving are still unsaved.", "Tastenkürzel gespeichert. Änderungen, die während des Speicherns erfolgten, sind noch nicht gespeichert."),
        ("shortcuts.window.saveFailed", "The shortcuts could not be saved: {0}", "Die Tastenkürzel konnten nicht gespeichert werden: {0}"),
        ("shortcuts.window.recording", "Recording: press the new shortcut. Escape cancels.", "Aufnahme läuft: Drücke das neue Kürzel. Escape bricht ab."),
        ("shortcuts.window.keysHelp", "Enter records a shortcut, Delete clears it, F1 closes the window.", "Enter nimmt ein Kürzel auf, Entf entfernt es, F1 schließt das Fenster."),
        ("shortcuts.window.closePrompt.title", "Unsaved shortcuts", "Ungespeicherte Tastenkürzel"),
        ("shortcuts.window.closePrompt.text", "Save the changed shortcuts before closing the window?", "Geänderte Tastenkürzel vor dem Schließen des Fensters speichern?"),
        ("shortcuts.window.keepEditing", "Keep editing", "Weiter bearbeiten"),
        ("shortcuts.card.hint", "View, search and change every shortcut in one window. F1 opens it from anywhere, on the current area.", "Alle Kürzel in einem Fenster ansehen, suchen und ändern. F1 öffnet es überall, beim aktuellen Bereich."),
        ("shortcuts.card.custom", "Custom bindings: {0}", "Eigene Belegungen: {0}"),
        ("shortcuts.card.open", "Open keyboard shortcuts…", "Tastenkürzel öffnen…"),
    };
}
