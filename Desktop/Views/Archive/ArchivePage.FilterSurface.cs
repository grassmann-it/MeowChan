using Avalonia.Interactivity;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// UX103 P2, S2: the archive filter drafts open as a local surface over the result
// list (an overlay until the full-width header, L1). Presentation only: the filter toggle
// (a FilterToggleButton) keeps the toggle command, IsChecked and the draft marker; drafts,
// Search and the applied chips keep their contracts.
public partial class ArchivePage
{
    private FilterSurfaceController _filterSurface = null!;

    private void InitializeFilterSurface() => _filterSurface = new FilterSurfaceController(this, ArchiveFilters, ArchiveFilterSurface, () => _ended, drivesVisibility: false, overlay: true);

    // The surface names itself and carries its cross (MD4 overlay header); both follow the language and the cross keeps its name.
    private void LocalizeFilterSurface()
    {
        ArchiveFilterSurfaceTitle.Text = Texts.T("archive.filters");
        OverlayFrame.MakeClose(ArchiveFilterSurfaceClose, Texts.T("action.close"));
    }

    internal bool IsFilterSurfaceOpen => ArchiveFilters.IsChecked == true;

    /// <summary>The filters shortcut: opens the filter surface and moves into it, or closes it and returns to the toggle.</summary>
    internal void ToggleFilterSurfaceByShortcut()
    {
        if (_ended) return;
        if (IsFilterSurfaceOpen) { CloseFilterSurface(restoreFocus: true); return; }
        ShowThreadListForListCommand();
        ArchiveFilters.IsChecked = true;
        _filterSurface.FocusFirstField();
    }

    internal void CloseFilterSurface(bool restoreFocus) => _filterSurface.Close(restoreFocus);

    private void OnFilterCloseClick(object? sender, RoutedEventArgs e) => CloseFilterSurface(restoreFocus: true);

    // Same apply path as the main Search button; the surface closes only when a
    // new request was actually applied, so invalid drafts stay visible for repair.
    private async void OnFilterApplyClick(object? sender, RoutedEventArgs e)
    {
        var before = _appliedSearch;
        await ApplySearchAsync();
        if (!_ended && _invalidSearchEditor == null && !ReferenceEquals(before, _appliedSearch))
            CloseFilterSurface(restoreFocus: true);
    }
}
