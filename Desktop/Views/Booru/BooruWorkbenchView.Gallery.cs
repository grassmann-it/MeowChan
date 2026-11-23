using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsTabStop = false
    };
    private readonly StackPanel _resultsArea = new() { Spacing = 8 };
    private readonly StackPanel _bottomBar = new() { Spacing = 8 };
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
        _gallery.SampleSettings = ReadGallerySampleSettings;
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
        _resultScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty) UpdateGalleryStageHeight();
        };
        _gallery.SizeChanged += (_, _) => UpdateGalleryStageHeight();
        _bottomBar.SizeChanged += (_, _) => UpdateGalleryStageHeight();
    }

    private BooruGallerySampleLimits ReadGallerySampleSettings() => BooruGallerySampleLimits.FromSettings(
        GallerySampleMiB?.Invoke(), GalleryCacheMiB?.Invoke(), GalleryMaximumPixels?.Invoke(), GalleryMaximumWidth?.Invoke(),
        GalleryTimeoutSeconds?.Invoke());

    private async Task DownloadOriginalAsync(string postId)
    {
        // Resolve the row again: a click must never act on a post the gallery has already left.
        if (_closed || _model.Search.View.Rows.FirstOrDefault(row => row.Post.PostId == postId) is not { } row) return;
        if (row.CanRetry) await _model.Search.RetryOriginalAsync(postId);
        else if (row.CanLoad) await _model.Search.LoadOriginalAsync(postId);
    }

    /// <summary>Use the measured room after the gallery controls and pager, including any wrapped translations.</summary>
    private void UpdateGalleryStageHeight()
    {
        if (_resultView != BooruResultView.Gallery || !ReferenceEquals(_gallery.Parent, _results)) return;
        double galleryChrome = _gallery.Bounds.Height - _gallery.Stage.Bounds.Height;
        _gallery.StageHeight = _resultScroll.Viewport.Height - galleryChrome - _bottomBar.Bounds.Height - _resultsArea.Spacing;
    }

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
        _cursorPostId = postId;
        SetResultView(BooruResultView.Gallery, save: false);
        ShowInGallery(_model.Search.View, requestSample: true);
        return true;
    }

    private void LeaveGallery()
    {
        if (_closed || _resultView != BooruResultView.Gallery) return;
        bool focusWasInResults = _results.IsKeyboardFocusWithin;
        SetResultView(_galleryReturn, save: false);
        if (!focusWasInResults && _cursorPostId is { } id) FocusHit(id);
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
            RebuildPanel(_results, () => new List<Control> { _gallery }, preserveFocus: false);
        UpdateGalleryStageHeight();
        ShowInGallery(view);
    }

    /// <summary>Another view than the gallery is shown: the gallery lets go of its pictures and cancels a sample that is still loading.</summary>
    private void ReleaseGallery() => _gallery.Release();

    private void FocusGalleryStage()
    {
        if (!_closed && _resultView == BooruResultView.Gallery && _gallery.CurrentPostId is not null
            && IsSearchItem(_tabs.SelectedItem) && _gallery.IsEffectivelyVisible) _gallery.FocusStage();
    }

    private async Task ChangePageAsync(int delta)
    {
        BooruSearchModel search = _model.Search;
        int tab = SearchTabs.ActiveId;
        BooruSearchView before = search.View;
        int page = before.Page + delta;
        Control pagerButton = delta < 0 ? _previous : _next;
        TopLevel? topLevel = TopLevel.GetTopLevel(this);
        IFocusManager? focus = topLevel?.FocusManager;
        IInputElement? origin = focus?.GetFocusedElement();
        bool fromPager = ReferenceEquals(origin, pagerButton);
        bool focusMoved = false;
        topLevel?.AddHandler(GotFocusEvent, TrackFocus, handledEventsToo: true);
        try { await search.GoToPageAsync(page); }
        finally { topLevel?.RemoveHandler(GotFocusEvent, TrackFocus); }
        BooruSearchView after = search.View;
        if (_closed || focusMoved || SearchTabs.ActiveId != tab || after.Page != page || after.ExecutedQuery != before.ExecutedQuery
            || _resultView != BooruResultView.Gallery || after.Rows.IsEmpty) return;
        // The pager can lose focus when the busy state hides it. Any other active focus takes priority.
        IInputElement? focused = focus?.GetFocusedElement();
        bool pagerLostFocus = fromPager && (focused is null || ReferenceEquals(focused, topLevel));
        bool stillAtOrigin = ReferenceEquals(focused, origin) && (fromPager || ReferenceEquals(origin, _gallery.Stage));
        if (topLevel is Window { IsActive: false } || !(pagerLostFocus || stillAtOrigin)) return;
        Render(force: false);
        FocusGalleryStage();

        void TrackFocus(object? sender, FocusChangedEventArgs e)
        {
            if (e.Source is IInputElement target && !ReferenceEquals(target, origin) && !ReferenceEquals(target, topLevel))
                focusMoved = true;
        }
    }
}
