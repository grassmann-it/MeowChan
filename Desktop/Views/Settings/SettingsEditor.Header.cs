using Avalonia.Controls;

namespace MeowChan;

// The head row of the settings page (SP1): title at the start, the search field and the "More" button at the end. In a narrow window the field
// takes a row of its own under the title and the results lie under it, as wide as the page.
public partial class SettingsEditor
{
    private bool _headerStacked;

    private void ApplyHeaderLayout(bool stacked)
    {
        if (stacked == _headerStacked && SettingsHeaderRow.ColumnDefinitions[1].MinWidth == (stacked ? 0 : SettingsHeaderLayout.SearchMinimumWidth)) return;
        _headerStacked = stacked;
        ColumnDefinition search = SettingsHeaderRow.ColumnDefinitions[1];
        search.MinWidth = stacked ? 0 : SettingsHeaderLayout.SearchMinimumWidth;
        search.MaxWidth = stacked ? double.PositiveInfinity : SettingsHeaderLayout.SearchMaximumWidth;
        search.Width = stacked ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        Grid.SetRow(SettingsSearchHost, stacked ? 1 : 0);
        Grid.SetColumn(SettingsSearchHost, stacked ? 0 : 1);
        Grid.SetColumnSpan(SettingsSearchHost, stacked ? 3 : 1);
        PositionSearchPanelLater();
    }

    private void PositionSearchPanelLater() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(PositionSearchPanel, Avalonia.Threading.DispatcherPriority.Loaded);
}
