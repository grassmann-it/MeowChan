namespace MeowChan.Desktop.Localization;

/// <summary>
/// Texts of the Media page's media panel (a large medium beside the grid, the reader's media sidebar in the Media page; in English, as in the
/// reader, it is the "media sidebar", in German the "Medienleiste": one word per thing): its layout rows,
/// the header and empty state, the switch, the drag handle and the two settings (width, page width below which it hides).
/// </summary>
internal static class MediaPanelCatalog
{
    internal static readonly (string Key, string English, string German)[] Entries =
    {
        ("command.media.panel.toggle", "Media sidebar", "Medienleiste"),
        ("media.layout.panel", "Media sidebar", "Medienleiste"),
        ("media.layout.panel.collapsed", "Hidden while the page is narrower than {0} dip. Your choice stays.", "Ausgeblendet, solange die Seite schmaler als {0} dip ist. Deine Wahl bleibt erhalten."),
        ("media.layout.panelSide.left", "Media sidebar on the left", "Medienleiste links"),
        ("media.layout.panelSide.right", "Media sidebar on the right", "Medienleiste rechts"),
        ("media.panel.title", "Selected medium", "Ausgewähltes Medium"),
        ("media.panel.empty", "Select a medium to see it large here.", "Wähle ein Medium aus, um es hier groß zu sehen."),
        ("media.panel.close", "Close media sidebar", "Medienleiste schließen"),
        ("media.panel.region", "Medium {0} of {1}: {2}", "Medium {0} von {1}: {2}"),
        ("media.panel.regionEmpty", "Media sidebar: no medium selected", "Medienleiste: kein Medium ausgewählt"),
        ("media.panel.resize", "Drag to change the width of the media sidebar, or use the arrow keys when this edge has focus", "Ziehe, um die Breite der Medienleiste zu ändern, oder nutze die Pfeiltasten, wenn dieser Rand den Fokus hat"),
        ("settings.limits.mediaPanelWidth", "Media sidebar: width (dip)", "Medienleiste: Breite (dip)"),
        ("settings.limits.mediaPanelWidth.hint", "Width of the media sidebar beside the grid on the Media page. You can also change it by dragging the sidebar's edge.", "Breite der Medienleiste neben dem Raster auf der Medienseite. Du kannst sie auch ändern, indem du den Rand der Leiste ziehst."),
        ("settings.limits.mediaPanelMinimumPageWidth", "Hide the media sidebar below this page width (dip)", "Medienleiste unter dieser Seitenbreite ausblenden (dip)"),
        ("settings.limits.mediaPanelMinimumPageWidth.hint", "A narrower Media page shows only the grid; the sidebar returns as soon as the page is wide enough again. Your choice in the Layout menu stays.", "Eine schmalere Medienseite zeigt nur das Raster; die Leiste kommt zurück, sobald die Seite wieder breit genug ist. Deine Wahl im Layout-Menü bleibt erhalten."),
    };
}
