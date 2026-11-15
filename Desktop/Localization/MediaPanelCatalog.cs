namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the Media page's media panel (a large medium beside the grid, the reader's media sidebar in the Media page): its layout rows,
/// the header and empty state, the two arrow commands, the drag handle and the two settings (width, page width below which it hides).
/// </summary>
internal static class MediaPanelCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("command.media.panel.previous", "Previous medium", "Voriges Medium"),
        ("command.media.panel.next", "Next medium", "Nächstes Medium"),
        ("media.layout.panel", "Media panel", "Medienleiste"),
        ("media.layout.panel.collapsed", "Hidden while the page is narrower than {0} dip. Your choice stays.", "Ausgeblendet, solange die Seite schmaler als {0} dip ist. Deine Wahl bleibt erhalten."),
        ("media.layout.panelSide.left", "Media panel on the left", "Medienleiste links"),
        ("media.layout.panelSide.right", "Media panel on the right", "Medienleiste rechts"),
        ("media.panel.title", "Selected medium", "Ausgewähltes Medium"),
        ("media.panel.empty", "Select a medium to see it large here.", "Wähle ein Medium aus, um es hier groß zu sehen."),
        ("media.panel.close", "Close media panel", "Medienleiste schließen"),
        ("media.panel.region", "Medium {0} of {1}: {2}", "Medium {0} von {1}: {2}"),
        ("media.panel.regionEmpty", "Media panel: no medium selected", "Medienleiste: kein Medium ausgewählt"),
        ("media.panel.resize", "Drag to change the width of the media panel, or use the arrow keys when this edge has focus", "Ziehe, um die Breite der Medienleiste zu ändern, oder nutze die Pfeiltasten, wenn dieser Rand den Fokus hat"),
        ("settings.limits.mediaPanelWidth", "Media panel: width (dip)", "Medienleiste: Breite (dip)"),
        ("settings.limits.mediaPanelWidth.hint", "Width of the media panel beside the grid on the Media page. You can also change it by dragging the panel's edge.", "Breite der Medienleiste neben dem Raster auf der Medienseite. Du kannst sie auch ändern, indem du den Rand der Leiste ziehst."),
        ("settings.limits.mediaPanelMinimumPageWidth", "Hide the media panel below this page width (dip)", "Medienleiste unter dieser Seitenbreite ausblenden (dip)"),
        ("settings.limits.mediaPanelMinimumPageWidth.hint", "A narrower Media page shows only the grid; the panel returns as soon as the page is wide enough again. Your choice in the Layout menu stays.", "Eine schmalere Medienseite zeigt nur das Raster; die Leiste kommt zurück, sobald die Seite wieder breit genug ist. Deine Wahl im Layout-Menü bleibt erhalten."),
    };
}
