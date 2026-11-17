using Avalonia.Controls;

namespace MeowChan;

// The head of the settings page (SP1, UX-LEDGER-02): the title row, then the search field and the "More" button on a row over the content column only.
// The head row has the same two columns as the form below it (navigation column, content column), so the field's left edge and the button's right edge
// are the edges of the Interface section. Without the category column (narrow window) the first column collapses and the row spans the page.
public partial class SettingsEditor
{
    private void ApplyHeaderLayout(bool categories)
    {
        ColumnDefinition navigation = SettingsHeaderRow.ColumnDefinitions[0];
        double width = categories ? SettingsHeaderLayout.NavigationWidth : 0;
        double spacing = categories ? SettingsHeaderLayout.NavigationGap : 0;
        if (navigation.Width.Value == width && SettingsHeaderRow.ColumnSpacing == spacing) return;
        navigation.Width = new GridLength(width);
        SettingsHeaderRow.ColumnSpacing = spacing;
        PositionSearchPanelLater();
    }

    private void PositionSearchPanelLater() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(PositionSearchPanel, Avalonia.Threading.DispatcherPriority.Loaded);
}
