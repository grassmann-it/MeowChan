using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView : IAsyncDisposable
{
    public async System.Threading.Tasks.ValueTask DisposeAsync()
    {
        System.Threading.Tasks.Task cleanup = _resultViewer?.DisposeAsync().AsTask()
            ?? System.Threading.Tasks.Task.CompletedTask;
        Dispose();
        await System.Threading.Tasks.Task.WhenAll(cleanup, _hostedGalleryCleanup);
    }

    private HashSet<string> _selectedResults = new(StringComparer.Ordinal);
    private BooruResultViewerWindow? _resultViewer;
    private BooruResultViewer? _hostedGallery;
    private System.Threading.Tasks.Task _hostedGalleryCleanup = System.Threading.Tasks.Task.CompletedTask;
    private (Size Size, PixelPoint Position)? _viewerBounds;
    internal (Size Size, PixelPoint Position)? OpenViewerBounds => _resultViewer?.NormalBounds;
    internal Func<(Size Size, PixelPoint Position)?>? RestoreViewerBounds { get; set; }
    internal Action<(Size Size, PixelPoint Position)>? SaveViewerBounds { get; set; }

    private void DismissResultViewer() => _resultViewer?.Close();

    internal bool OpenHostedGallery(string postId)
    {
        if (_closed || OverlayLayer.GetOverlayLayer(this) is not { } layer
            || !_model.Search.View.Rows.Any(row => row.Post.PostId == postId)) return false;
        if (_hostedGallery?.CurrentPostId == postId) return true;
        CloseHostedGallery(restoreFocus: false);
        int tab = SearchTabs.ActiveId;
        BooruResultViewer gallery = new(_model.Search, postId, _selectedResults, () => Render(force: true),
            () => CloseHostedGallery(restoreFocus: SearchTabs.ActiveId == tab),
            openPost: url => OpenActions.Start(() => OpenLinkAsync(url), "booru.viewer.openPost"),
            sampleSettings: ReadGallerySampleSettings, composeMenu: ComposeResultMenuAt);
        _hostedGallery = gallery;
        _gallery.SuspendSamples(true);
        layer.Children.Add(gallery);
        SetCursor(postId);
        return true;
    }

    private void CloseHostedGallery(bool restoreFocus)
    {
        if (_hostedGallery == null) return;
        BooruResultViewer gallery = _hostedGallery;
        System.Threading.Tasks.Task cleanup = _hostedGallery.DisposeAsync().AsTask();
        _hostedGallery = null;
        if (gallery.Parent is Panel layer) layer.Children.Remove(gallery);
        _hostedGalleryCleanup = System.Threading.Tasks.Task.WhenAll(_hostedGalleryCleanup, cleanup);
        OpenActions.Start(() => _hostedGalleryCleanup, "booru.gallery.dispose");
        _gallery.SuspendSamples(false);
        if (!restoreFocus || _closed || !_attached || !IsEffectivelyVisible || !IsSearchItem(_tabs.SelectedItem)) return;
        SetCursor(gallery.CurrentPostId);
        if (_resultView == BooruResultView.Gallery)
        {
            ShowInGallery(_model.Search.View, requestSample: true);
            FocusGalleryStage();
        }
        else FocusHit(gallery.CurrentPostId);
    }

    private ContextMenuBuilder? ComposeResultMenuAt(string postId)
    {
        BooruSearchView view = _model.Search.View;
        return view.Rows.FirstOrDefault(row => row.Post.PostId == postId) is { } hit
            ? ComposeResultMenu(new ResultActions(this, BooruResultTarget.From(view, hit)))
            : null;
    }

    internal bool OpenResultViewer(string postId)
    {
        if (_closed || TopLevel.GetTopLevel(this) is not Window owner
            || !_model.Search.View.Rows.Any(row => row.Post.PostId == postId)) return false;
        int tab = SearchTabs.ActiveId;
        _resultViewer?.Close();
        BooruResultViewerWindow viewer = new(_model.Search, postId, _selectedResults, () => Render(force: true),
            openPost: url => OpenActions.Start(() => OpenLinkAsync(url), "booru.viewer.openPost"),
            sampleSettings: ReadGallerySampleSettings, composeMenu: ComposeResultMenuAt);
        if ((_viewerBounds ??= RestoreViewerBounds?.Invoke()) is { } bounds) viewer.RestoreNormalBounds(owner, bounds);
        _resultViewer = viewer;
        _gallery.SuspendSamples(true);
        viewer.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_resultViewer, viewer)) return;
            _resultViewer = null;
            if (viewer.NormalBounds is { } saved && _viewerBounds != saved)
            {
                _viewerBounds = saved;
                SaveViewerBounds?.Invoke(saved);
            }
            if (_closed) return;
            // The cursor follows the post the popup ended on; the inline gallery (when shown) moves there too.
            BooruSearchView current = _model.Search.View;
            if (SearchTabs.ActiveId != tab || !current.Rows.Any(row => row.Post.PostId == viewer.CurrentPostId))
            {
                _gallery.SuspendSamples(false);
                return;
            }
            SetCursor(viewer.CurrentPostId);
            if (_resultView == BooruResultView.Gallery) ShowInGallery(current);
            _gallery.SuspendSamples(false);
            if (_attached && IsEffectivelyVisible && IsSearchItem(_tabs.SelectedItem)) FocusHit(viewer.CurrentPostId);
        };
        viewer.Show(owner);
        return true;
    }

    private WrapPanel CreateResultViewerActions(ResultActions result, bool compact = false)
    {
        string postId = result.PostId;
        WrapPanel actions = new() { Name = "BooruViewerActions:" + postId, Classes = { "workspace-toolbar" } };
        if (compact)
        {
            actions.Classes.Clear();
            actions.ItemSpacing = 4;
            actions.LineSpacing = 4;
        }
        Button open = new() { Name = "BooruView:" + postId, Content = CommandCatalog.Label("gallery.open"), HorizontalAlignment = HorizontalAlignment.Left };
        open.Classes.Add("ghost");
        string icon = CommandCatalog.Get("gallery.open").Icon;
        if (compact) CompactAction(open, icon);
        else CommandBindings.ShowIconBeforeText(open, icon);
        AutomationProperties.SetName(open, CommandCatalog.Label("gallery.open") + ": " + Texts.T("booru.ui.post.title", postId));
        if (compact) ToolTip.SetTip(open, AutomationProperties.GetName(open));
        open.Click += (_, _) => { if (result.IsCurrent()) OpenHostedGallery(postId); };
        CheckBox select = new() { Name = "BooruSelect:" + postId, Content = Texts.T("media.tile.selected"), IsChecked = _selectedResults.Contains(postId) };
        AutomationProperties.SetName(select, Texts.T("media.tile.selected") + ": " + Texts.T("booru.ui.post.title", postId));
        if (compact)
        {
            select.Content = null;
            SizeCompactAction(select);
            ToolTip.SetTip(select, AutomationProperties.GetName(select));
        }
        select.Click += (_, _) =>
        {
            if (!result.IsCurrent()) return;
            if (!_selectedResults.Add(postId)) _selectedResults.Remove(postId);
            Render(force: true);
        };
        actions.Children.Add(open);
        actions.Children.Add(select);
        return actions;
    }
}
