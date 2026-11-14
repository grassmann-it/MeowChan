using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

// WV3 (plan AP5 §1.5): the reader's toolbars follow WorkViewPolicy.Reader. The page receives the window's view through
// ArchiveColumnModePreference (MainWindow pushes it whenever it applies a view, the same value as MainWindow.WorkViews.Current);
// a standalone page (tests, fixtures) keeps Simple as it always did. Items keep their own IsVisible (state); the view only
// decides bar or "More", and the shortcuts of moved items keep working through the WV0 reduced-control route.
public partial class ArchivePage
{
    /// <summary>The view the reader toolbars were last laid out for (smokes compare it with the window's view).</summary>
    internal WorkView ReaderWorkView => ArchiveWorkView;

    private void ApplyReaderWorkView()
    {
        if (ReaderToolbar == null) return; // before InitializeComponent
        WorkView view = ArchiveWorkView;
        WorkViewPresenter.ApplyToolBar(ReaderToolbar, view);
        // R6: a filter that narrows the saved media, or the latest-versions-only scope, is a state and stays on the bar.
        WorkViewPresenter.ApplyToolBar(ReaderMediaTools, view, nonDefaultState: item =>
            ReferenceEquals(item, ArchiveFavoritesFilter) ? _archiveFavoritesOnly
            : ReferenceEquals(item, ArchiveRatingFilter) ? _mediaRatingFilter > 0
            : ReferenceEquals(item, ArchiveVersionScope) && !_allVersions);
        WorkViewPresenter.ApplyToolBar(ReaderMediaActions, view);
        foreach (ActionToolBar toolbar in new[] { ReaderToolbar, ReaderMediaTools, ReaderMediaActions })
            toolbar.RefreshOverflowLabel();
    }
}
