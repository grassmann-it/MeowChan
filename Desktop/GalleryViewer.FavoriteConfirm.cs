using System;
using System.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

internal enum GalleryFavoriteKeyAction { Add, Confirm, Ignore }

internal static class GalleryFavoriteKeyPolicy
{
    // Keyboard F never inverts a stored or pending state. Not a favorite (or still unknown):
    // the idempotent set-true path. Persisted favorite: ask first. A write in flight for a
    // favorite is ambiguous from here (remove or re-add), so it is neither confirmed nor taken back.
    internal static GalleryFavoriteKeyAction Decide(bool known, bool selected, bool pending) =>
        !selected || !known ? GalleryFavoriteKeyAction.Add
        : pending ? GalleryFavoriteKeyAction.Ignore : GalleryFavoriteKeyAction.Confirm;
}

internal sealed partial class GalleryViewer
{
    private readonly Border _favoriteConfirmPanel = new()
    {
        Name = "GalleryFavoriteConfirm", Classes = { "workspace-section" }, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12), MaxWidth = 380
    };
    private readonly TextBlock _favoriteConfirmTitle = new() { Classes = { "section-title" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _favoriteConfirmHint = new() { Classes = { "workspace-help" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly Button _favoriteConfirmKeep = new() { Name = "GalleryFavoriteConfirmKeep", IsDefault = true };
    private readonly Button _favoriteConfirmRemove = new() { Name = "GalleryFavoriteConfirmRemove" };
    // The confirmation belongs to one exact medium in one viewer generation (_request + session token).
    // It holds no writer token: nothing is queued until the user explicitly confirms.
    private GalleryAssetKey? _favoriteConfirmKey;
    private int _favoriteConfirmRequest;
    private CancellationToken _favoriteConfirmSession;
    private Key? _favoriteGestureKey;

    internal bool IsFavoriteConfirmOpen => _favoriteConfirmKey != null;

    private void InitializeFavoriteConfirm()
    {
        _favoriteConfirmKeep.Click += (_, _) => KeepFavoriteFromConfirm();
        _favoriteConfirmRemove.Click += (_, _) => ConfirmFavoriteRemove();
        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        actions.Children.Add(_favoriteConfirmKeep);
        actions.Children.Add(_favoriteConfirmRemove);
        _favoriteConfirmPanel.Child = new StackPanel
        {
            Spacing = 8, Children = { _favoriteConfirmTitle, _favoriteConfirmHint, actions }
        };
        _favoriteConfirmPanel.ZIndex = 40;
        _visualRoot.Children.Add(_favoriteConfirmPanel);
    }

    private void UpdateFavoriteConfirmText()
    {
        string title = Texts.T("viewer.favorite.confirm.title");
        _favoriteConfirmTitle.Text = title;
        AutomationProperties.SetName(_favoriteConfirmPanel, title);
        string? toggle = CommandCatalog.ShortcutDisplay("favorites.toggle");
        string? remove = CommandCatalog.ShortcutDisplay("favorites.remove");
        _favoriteConfirmHint.Text = toggle != null && remove != null
            ? Texts.T("viewer.favorite.confirm.hint", toggle, remove) : string.Empty;
        _favoriteConfirmHint.IsVisible = !string.IsNullOrEmpty(_favoriteConfirmHint.Text);
        _favoriteConfirmKeep.Content = Texts.T("viewer.favorite.confirm.keep");
        _favoriteConfirmRemove.Content = Texts.T("viewer.favorite.confirm.remove");
        AutomationProperties.SetName(_favoriteConfirmKeep, Texts.T("viewer.favorite.confirm.keep"));
        AutomationProperties.SetName(_favoriteConfirmRemove, Texts.T("viewer.favorite.confirm.remove"));
    }

    // Still the exact medium, viewer generation and session this confirmation was opened for.
    private bool IsFavoriteConfirmCurrent => _favoriteConfirmKey is { } key && _entry?.Key == key
        && _request == _favoriteConfirmRequest && _favoriteConfirmSession == _sessionCancellation
        && !_terminalRetirement && !_viewerInteractionStopped && !_sessionCancellation.IsCancellationRequested;

    private void OpenFavoriteConfirm(GalleryEntry entry)
    {
        if (IsFavoriteConfirmOpen) return;
        _favoriteConfirmKey = entry.Key;
        _favoriteConfirmRequest = _request;
        _favoriteConfirmSession = _sessionCancellation;
        UpdateFavoriteConfirmText();
        _favoriteConfirmPanel.IsVisible = true;
        // Same hold as the shortcut help: no automatic advance while the question is open.
        HoldSlideshowAdvance();
        RefreshPlaybackControls();
        if (!this.IsAttachedToVisualTree()) return;
        UpdateLayout();
        _favoriteConfirmKeep.Focus(NavigationMethod.Tab);
    }

    // Called with every settled/pending state report of the shown medium: a question is only
    // meaningful for a persisted favorite without a write in flight.
    private void ReconcileFavoriteConfirm(bool selected, bool pending)
    {
        if (!selected || pending) CloseFavoriteConfirm();
    }

    // Discards the question without any write. Focus is only restored when it lived in the panel,
    // otherwise a hidden focused button would leave keys without a gallery route.
    private void CloseFavoriteConfirm()
    {
        if (!IsFavoriteConfirmOpen) return;
        bool hadFocus = _favoriteConfirmPanel.IsKeyboardFocusWithin;
        _favoriteConfirmKey = null;
        _favoriteConfirmPanel.IsVisible = false;
        RefreshPlaybackControls();
        if (hadFocus && !_terminalRetirement) FocusGallerySurface();
    }

    private void KeepFavoriteFromConfirm() => CloseFavoriteConfirm();

    private void ConfirmFavoriteRemove()
    {
        if (!IsFavoriteConfirmOpen) return;
        bool current = IsFavoriteConfirmCurrent;
        CloseFavoriteConfirm();
        // Explicit set-false for the confirmed medium only; a stale confirmation changes nothing.
        if (current) RequestFavorite(false);
    }

    // Keyboard entry for favorites.toggle. Toolbar and context-menu clicks keep ToggleFavorite.
    private void RequestFavoriteFromKeyboard()
    {
        if (!CanRequestFavorite || _entry is not { } entry) return;
        switch (GalleryFavoriteKeyPolicy.Decide(_favoriteKnown, _favoriteSelected, _favoritePending))
        {
            case GalleryFavoriteKeyAction.Add: RequestFavorite(true); break;
            case GalleryFavoriteKeyAction.Confirm: OpenFavoriteConfirm(entry); break;
        }
    }

    // True when the key is consumed by the favorite gesture (a held key repeats KeyDown without
    // KeyUp: the first press acts, the repeats are swallowed).
    private bool ConsumeFavoriteRepeat(KeyEventArgs e)
    {
        bool repeat = _favoriteGestureKey == e.Key;
        _favoriteGestureKey = e.Key;
        if (repeat) e.Handled = true;
        return repeat;
    }

    // Routes keys while the question is open; nothing else (navigation, zoom, ...) may act on it.
    internal void RouteFavoriteConfirmKey(KeyEventArgs e)
    {
        if (!IsFavoriteConfirmCurrent) { CloseFavoriteConfirm(); return; }
        if (CommandBindings.Matches(e, "favorites.remove"))
        { e.Handled = true; ConfirmFavoriteRemove(); }
        else if (CommandBindings.Matches(e, "favorites.toggle"))
        { if (!ConsumeFavoriteRepeat(e)) { e.Handled = true; KeepFavoriteFromConfirm(); } }
        // Enter is Keep even when the Remove control has focus; Remove needs U or a real activation.
        else if (e.Key is Key.Enter && e.KeyModifiers == KeyModifiers.None)
        { e.Handled = true; KeepFavoriteFromConfirm(); }
    }

    internal void RouteFavoriteToggleKey(KeyEventArgs e)
    {
        e.Handled = true;
        if (!ConsumeFavoriteRepeat(e)) RequestFavoriteFromKeyboard();
    }

    private void OnFavoriteKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == _favoriteGestureKey) _favoriteGestureKey = null;
    }

    // A window that lost the activation never sees the key-up: the next F is a new press.
    private void OnHostWindowDeactivated(object? sender, EventArgs e) => _favoriteGestureKey = null;

    /// <summary>Whether the held-key latch of the favourite keys is set (tests).</summary>
    internal bool FavoriteGestureLatched => _favoriteGestureKey != null;
}
