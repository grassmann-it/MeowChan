using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The inline gallery (third result view) and the cursor of the list and the grid. The cursor is the hit the person is on: the one with the
/// keyboard focus, or the one last clicked, drawn with an accent edge (<c>booru-cursor</c>) that stays while the focus is elsewhere on the page.
/// Arrow keys move it (Left/Right and Up/Down by a row in the grid, Up/Down in the list: BooruWorkbenchView.Hits.cs), Space marks the hit, Enter opens the
/// gallery on it (or shows a saved original in Media, decision 95). Keys act only while the focus is on a hit; nothing here is a global shortcut.
/// </summary>
internal sealed partial class BooruWorkbenchView
{
    private readonly BooruGalleryPanel _gallery = new();
    private readonly ScrollViewer _resultScroll = new()
    {
        Name = "BooruResultScroll", HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false
    };
    private string? _cursorPostId;
    private BooruResultView _galleryReturn = BooruResultView.List;

    /// <summary>The setting "largest gallery image" in MiB (read at each load); the default without a host.</summary>
    internal Func<int>? GallerySampleMiB { get; set; }
    /// <summary>The setting "gallery image memory" in MiB (read at each load); the default without a host.</summary>
    internal Func<int>? GalleryCacheMiB { get; set; }
    internal Func<int>? GalleryMaximumPixels { get; set; }
    internal Func<int>? GalleryMaximumWidth { get; set; }
    internal Func<int>? GalleryTimeoutSeconds { get; set; }

    /// <summary>The hit the cursor is on (null before the person moved or clicked, or when it is not on this page).</summary>
    internal string? CursorPostId => _cursorPostId;
    internal BooruGalleryPanel Gallery => _gallery;

    /// <summary>Called once from <see cref="InitializeHitList"/>: wires the panel to this page's selection, links, original and popup.</summary>
    private void InitializeGallery()
    {
        _gallery.Samples = () => _model.Search.Samples;
        _gallery.SampleLimitBytes = () => Math.Clamp(GallerySampleMiB?.Invoke() ?? MeowChan.Config.BooruGallerySampleMiBDefault,
            MeowChan.Config.BooruGallerySampleMiBMinimum, MeowChan.Config.BooruGallerySampleMiBMaximum) * 1024 * 1024;
        _gallery.CacheBytes = () => Math.Clamp(GalleryCacheMiB?.Invoke() ?? MeowChan.Config.BooruGalleryCacheMiBDefault,
            MeowChan.Config.BooruGalleryCacheMiBMinimum, MeowChan.Config.BooruGalleryCacheMiBMaximum) * 1024L * 1024;
        _gallery.MaximumPixels = () => GalleryMaximumPixels?.Invoke() ?? Config.BooruGalleryMaximumPixelsDefault;
        _gallery.MaximumWidth = () => GalleryMaximumWidth?.Invoke() ?? Config.BooruGalleryMaximumWidthDefault;
        _gallery.TimeoutSeconds = () => GalleryTimeoutSeconds?.Invoke() ?? Config.BooruGalleryTimeoutSecondsDefault;
        _gallery.IsSelected = postId => _selectedResults.Contains(postId);
        _gallery.SelectionToggled = postId =>
        {
            if (!_selectedResults.Add(postId)) _selectedResults.Remove(postId);
            Render(force: true);
        };
        _gallery.Moved = postId => { SetCursor(postId); ShowInGallery(_model.Search.View, requestSample: true); };
        _gallery.DownloadRequested = postId => OpenActions.Start(() => DownloadOriginalAsync(postId), "booru.gallery.download");
        _gallery.OpenPostRequested = postId =>
        {
            if (_model.Search.View.Rows.FirstOrDefault(row => row.Post.PostId == postId) is { } row
                && BooruPostLinks.TryCanonical(row.Post, out string url))
                OpenActions.Start(() => OpenLinkAsync(url), "booru.gallery.openPost");
        };
        _gallery.OpenWindowRequested = postId => OpenResultViewer(postId);
        _gallery.LeaveRequested = LeaveGallery;
        _gallery.FullscreenRequested = ToggleGalleryFullscreen;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_resultView == BooruResultView.Gallery && IsSearchItem(_tabs.SelectedItem))
                _gallery.RouteKey(e);
        }, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => UpdateGalleryStageHeight();
    }

    private WindowState _beforeFullscreen;

    private void ToggleGalleryFullscreen()
    {
        if (TopLevel.GetTopLevel(this) is MainWindow shell) shell.ToggleFullscreen();
        else if (TopLevel.GetTopLevel(this) is Window window)
        {
            if (window.WindowState == WindowState.FullScreen) window.WindowState = _beforeFullscreen;
            else
            {
                _beforeFullscreen = window.WindowState;
                window.WindowState = WindowState.FullScreen;
            }
        }
    }

    private void UpdateGalleryScroll()
    {
        bool gallery = _resultView == BooruResultView.Gallery && _viewBar.IsVisible && IsSearchItem(_tabs.SelectedItem);
        // One scroll owner at a time. The gallery can scroll its stage and pager below the fixed view switch.
        if (Body.FindAncestorOfType<ScrollViewer>() is { Name: "BooruPageScroll" or "BooruWorkbenchScroll" } outer)
        {
            var visibility = gallery ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            if (outer.VerticalScrollBarVisibility != visibility)
            {
                outer.Offset = default;
                outer.VerticalScrollBarVisibility = visibility;
            }
        }
        _resultScroll.VerticalScrollBarVisibility = gallery ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    private async Task DownloadOriginalAsync(string postId)
    {
        // Resolve the row again: a click must never act on a post the gallery has already left.
        if (_closed || _model.Search.View.Rows.FirstOrDefault(row => row.Post.PostId == postId) is not { } row) return;
        if (row.CanRetry) await _model.Search.RetryOriginalAsync(postId);
        else if (row.CanLoad) await _model.Search.LoadOriginalAsync(postId);
    }

    /// <summary>The stage fills most of the window below the controls of the page (never less than its minimum).</summary>
    private void UpdateGalleryStageHeight()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _gallery.StageHeight = top.ClientSize.Height - GalleryReservedHeight;
    }

    /// <summary>What the page keeps for its own controls above and below the stage, in dip (title, search row, view bar, gallery bar, status, pager).</summary>
    private const double GalleryReservedHeight = 420;

    // ---- cursor ---------------------------------------------------------------------------------------------

    /// <summary>Moves the cursor to <paramref name="postId"/> and draws it; nothing is requested or changed otherwise.</summary>
    internal void SetCursor(string? postId)
    {
        if (_closed || _cursorPostId == postId) return;
        _cursorPostId = postId;
        ApplyCursor();
    }

    private void ApplyCursor()
    {
        foreach (Control child in ResultCards)
            if (child is Border card && card.Name?.StartsWith("BooruResult:", StringComparison.Ordinal) == true)
                card.Classes.Set("booru-cursor", _cursorPostId is not null && card.Name == "BooruResult:" + _cursorPostId);
    }

    /// <summary>A card or tile is a stop of the cursor: focus and a click put the cursor on it, Space marks the hit while the card itself holds the focus.</summary>
    private void ConfigureHitCursor(Border card, string postId)
    {
        card.GotFocus += (_, _) => SetCursor(postId);
        card.AddHandler(InputElement.PointerPressedEvent, (object? _, PointerPressedEventArgs e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) SetCursor(postId);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        card.Classes.Set("booru-cursor", _cursorPostId == postId);
    }

    /// <summary>Space on a focused hit marks or unmarks it like its check box (a button, a link or text inside the card keeps its own keys).</summary>
    private bool TryToggleSelectionKey(KeyEventArgs e, Border card, string postId)
    {
        if (e.Handled || e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None || ShortcutServiceCapturing()
            || !ReferenceEquals(e.Source, card)) return false;
        if (!_selectedResults.Add(postId)) _selectedResults.Remove(postId);
        Render(force: true);
        return true;
    }

    // ---- gallery ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens the gallery on <paramref name="postId"/> (Enter or a double click on a hit, "Open in gallery"). The view is not saved as the person's
    /// choice (only the switch saves it); Esc returns to the view the person came from.
    /// </summary>
    internal bool OpenGallery(string postId)
    {
        if (_closed || !_model.Search.View.Rows.Any(row => row.Post.PostId == postId)) return false;
        if (_resultView != BooruResultView.Gallery) _galleryReturn = _resultView;
        _cursorPostId = postId;
        SetResultView(BooruResultView.Gallery, save: false);
        ShowInGallery(_model.Search.View, requestSample: true);
        return true;
    }

    private void LeaveGallery()
    {
        if (_closed || _resultView != BooruResultView.Gallery) return;
        SetResultView(_galleryReturn == BooruResultView.Gallery ? BooruResultView.List : _galleryReturn, save: false);
    }

    /// <summary>The post the gallery shows: the cursor when it is on this page, else the first hit (the cursor follows).</summary>
    private string? ResolveGalleryPost(BooruSearchView view)
    {
        if (_cursorPostId is { } id && view.Rows.Any(row => row.Post.PostId == id)) return id;
        _cursorPostId = view.Rows.IsEmpty ? null : view.Rows[0].Post.PostId;
        return _cursorPostId;
    }

    private void ShowInGallery(BooruSearchView view, bool requestSample = false) =>
        _gallery.Show(view, ResolveGalleryPost(view), requestSample);

    /// <summary>Builds the gallery view of <paramref name="view"/>: the one persistent panel, put under the results host once and then only updated.</summary>
    private void RebuildGallery(BooruSearchView view)
    {
        _tiles = null;
        if (!ReferenceEquals(_gallery.Parent, _results) || _results.Children.Count != 1)
            RebuildPanel(_results, () => new List<Control> { _gallery }, fallback: () => null);
        UpdateGalleryStageHeight();
        ShowInGallery(view);
    }

    /// <summary>Another view than the gallery is shown: the gallery lets go of its pictures and cancels a sample that is still loading.</summary>
    private void ReleaseGallery() => _gallery.Release();

    private void FocusGalleryStage() => Dispatcher.UIThread.Post(() =>
    {
        if (!_closed && _resultView == BooruResultView.Gallery && _gallery.CurrentPostId is not null
            && IsSearchItem(_tabs.SelectedItem)) _gallery.FocusStage();
    }, DispatcherPriority.Loaded);
}
