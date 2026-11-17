using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private readonly HashSet<string> _selectedResults = new(StringComparer.Ordinal);
    private BooruResultViewerWindow? _resultViewer;

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
            if (!_closed && _attached) FocusHit(viewer.CurrentPostId);
        };
        viewer.Show(owner);
        return true;
    }

    private Control CreateResultViewerActions(string postId)
    {
        StackPanel actions = new() { Spacing = 4 };
        Button open = new() { Name = "BooruView:" + postId, Content = CommandCatalog.Label("gallery.open") };
        AutomationProperties.SetName(open, CommandCatalog.Label("gallery.open") + ": " + Texts.T("booru.ui.post.title", postId));
        open.Click += (_, _) => OpenResultViewer(postId);
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
