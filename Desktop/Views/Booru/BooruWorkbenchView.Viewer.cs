using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView : IAsyncDisposable
{
    public async System.Threading.Tasks.ValueTask DisposeAsync()
    {
        System.Threading.Tasks.Task cleanup = _resultViewer?.DisposeAsync().AsTask()
            ?? System.Threading.Tasks.Task.CompletedTask;
        Dispose();
        await cleanup;
    }

    private readonly HashSet<string> _selectedResults = new(StringComparer.Ordinal);
    private BooruResultViewerWindow? _resultViewer;

    private void DismissResultViewer() => _resultViewer?.Close();

    internal bool OpenResultViewer(string postId)
    {
        if (_closed || TopLevel.GetTopLevel(this) is not Window owner
            || !_model.Search.View.Rows.Any(row => row.Post.PostId == postId)) return false;
        _resultViewer?.Close();
        BooruResultViewerWindow viewer = new(_model.Search, postId, _selectedResults, () => Render(force: true));
        _resultViewer = viewer;
        viewer.Closed += (_, _) =>
        {
            if (ReferenceEquals(_resultViewer, viewer)) _resultViewer = null;
            if (_closed) return;
            // The cursor follows the post the popup ended on; the inline gallery (when shown) moves there too.
            BooruSearchView current = _model.Search.View;
            if (!current.Rows.Any(row => row.Post.PostId == viewer.CurrentPostId)) return;
            SetCursor(viewer.CurrentPostId);
            if (_resultView == BooruResultView.Gallery) ShowInGallery(current);
            if (_attached && IsEffectivelyVisible && IsSearchItem(_tabs.SelectedItem)) FocusHit(viewer.CurrentPostId);
        };
        viewer.Show(owner);
        return true;
    }

    private Control CreateResultViewerActions(string postId)
    {
        WrapPanel actions = new() { Name = "BooruViewerActions:" + postId, Classes = { "workspace-toolbar" } };
        Button open = new() { Name = "BooruView:" + postId, Content = CommandCatalog.Label("gallery.open"), HorizontalAlignment = HorizontalAlignment.Left };
        open.Classes.Add("ghost");
        CommandBindings.ShowIconBeforeText(open, CommandCatalog.Get("gallery.open").Icon);
        AutomationProperties.SetName(open, CommandCatalog.Label("gallery.open") + ": " + Texts.T("booru.ui.post.title", postId));
        open.Click += (_, _) => OpenGallery(postId);
        CheckBox select = new() { Name = "BooruSelect:" + postId, Content = Texts.T("media.tile.selected"), IsChecked = _selectedResults.Contains(postId) };
        AutomationProperties.SetName(select, Texts.T("media.tile.selected") + ": " + Texts.T("booru.ui.post.title", postId));
        select.Click += (_, _) =>
        {
            if (!_selectedResults.Add(postId)) _selectedResults.Remove(postId);
            Render(force: true);
        };
        actions.Children.Add(open);
        actions.Children.Add(select);
        return actions;
    }
}
