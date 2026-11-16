using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed class ArchiveFavoriteButton : ToggleButton
{
    /// <summary>
    /// The style class that gives the button its own hit surface (WorkspaceStyles: a transparent fill at rest, Hover under the pointer, muted and
    /// without a fill while unavailable). The star drawing is no hit target (<see cref="MeowChan.Desktop.Controls.FavoriteStar"/>), and the
    /// toggle theme leaves the fill unset: without the class a real pointer went through the star to the post card, so the star of a post's
    /// file line and of the sidebar's header did nothing (review 15.11.2026); only a routed Click (the old smokes) reached it.
    /// </summary>
    internal const string StarClass = "favorite-star";

    /// <summary>
    /// Styled as a ToggleButton: a style selector names the exact style key, so without this neither "ToggleButton.favorite-star" nor the tiles'
    /// "ToggleButton.tile-favorite" ever matched this subclass, and its fill stayed unset (no hit surface, see <see cref="StarClass"/>).
    /// </summary>
    protected override Type StyleKeyOverride => typeof(ToggleButton);
    private Func<bool>? _available;
    private Func<bool>? _marked;
    private Func<Task>? _toggle;
    private bool _running;
    private Func<string?>? _errorSource;
    private string? _error;
    private bool _failed;
    private string? _shownError;

    /// <summary>
    /// The execution context whose keys the tooltip names (EffectiveShortcutBindings.ExecutionContexts): every star of this kind sits in the reader (a post's
    /// file line, the media sidebar, the Media tab's tiles), whose keys run in "reader".
    /// </summary>
    internal string KeyContext { get; init; } = "reader";

    internal ArchiveFavoriteButton()
    {
        Name = "ArchiveMediaFavorite";
        Padding = new Avalonia.Thickness(8);
        CommandBindings.Apply(this, "favorites.toggle", iconOnly: true);
        // One tooltip source (Refresh): a rebinding re-runs Refresh instead of the catalog's "Add / remove favorite · F", which used to replace the
        // star's own tip (and its name) until the next refresh.
        CommandBindings.ObservePresentation(this, Refresh);
        Classes.Add(StarClass);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
        Click += async (_, e) =>
        {
            e.Handled = true;
            if (_running || _available?.Invoke() != true || _toggle == null) return;
            _error = null;
            IsChecked = _marked?.Invoke() == true;
            _running = true; IsEnabled = false;
            Refresh(); // pending: disabled, named "… · saving" until the write returns
            try { await _toggle(); }
            catch (Exception error) { _error = error.Message; }
            finally { _running = false; Refresh(); }
        };
    }

    internal void Configure(Func<bool>? available, Func<bool>? marked, Func<Task>? toggle, Func<string?>? error = null)
    { _available = available; _marked = marked; _toggle = toggle; _errorSource = error; Refresh(); }

    /// <summary>
    /// The star of a reader attachment. It asks the row each time instead of keeping the callbacks the row has now: the page wires (and rewires) a row's
    /// favourite once its saved files are known, often after the star was shown, and a star with copied callbacks then stayed disabled or pointed at
    /// an earlier scan (the "star that does nothing" of the media sidebar, review 15.11.2026).
    /// </summary>
    internal void Configure(ArchiveAttachmentRow? row)
    {
        if (row == null) { Configure(null, null, null); return; }
        var (available, marked, toggle, error) = Callbacks(row);
        Configure(available, marked, toggle, error);
    }

    /// <summary>The star of an archive media tile, read from the row the same way as <see cref="Configure(ArchiveAttachmentRow?)"/>.</summary>
    internal void Configure(ArchiveAssetRow? row)
    {
        if (row == null) { Configure(null, null, null); return; }
        Configure(() => row.FavoriteAvailable?.Invoke() == true, () => row.IsFavorite?.Invoke() == true,
            () => row.ToggleFavoriteAsync?.Invoke() ?? Task.CompletedTask, () => row.FavoriteError?.Invoke());
    }

    /// <summary>The star's callbacks for a reader attachment: each asks the row's callback of the moment (tests read them without a control).</summary>
    internal static (Func<bool> Available, Func<bool> Marked, Func<Task> Toggle, Func<string?> Error) Callbacks(ArchiveAttachmentRow row) =>
        (() => row.FavoriteAvailable?.Invoke() == true, () => row.IsFavorite?.Invoke() == true,
            () => row.ToggleFavoriteAsync?.Invoke() ?? Task.CompletedTask, () => row.FavoriteError?.Invoke());

    // Availability also owns the callbacks' lifetime: a queued layout or toggle completion
    // may refresh a control after its page has ended and released the session.
    internal static (bool Available, bool Marked, string? Error) ReadState(
        Func<bool>? available, Func<bool>? marked, Func<string?>? error)
    {
        if (available?.Invoke() != true) return (false, false, null);
        return (true, marked?.Invoke() == true, error?.Invoke());
    }

    internal void Refresh()
    {
        var state = ReadState(_available, _marked, _errorSource);
        bool marked = state.Marked;
        if (!state.Available || _errorSource != null) _error = state.Error;
        IsEnabled = !_running && state.Available;
        IsChecked = marked;
        // ★ filled when marked, ☆ outline otherwise: the same pair as the thread list and the gallery badge.
        Content = MeowChan.Desktop.Controls.FavoriteStar.Create(marked, tinted: _error == null);
        // A failed write keeps the star in place: an error edge plus the reason in the tooltip, which
        // opens once for a new error, instead of a text block that pushes the card layout apart.
        bool failed = _error != null;
        if (failed != _failed)
        {
            _failed = failed;
            if (failed)
            {
                this[!BorderBrushProperty] = new DynamicResourceExtension("Workspace.Error");
                BorderThickness = new Avalonia.Thickness(1.5);
            }
            else { ClearValue(BorderBrushProperty); ClearValue(BorderThicknessProperty); }
        }
        // The name says what a click does; the tooltip adds the keys that do the same here (FavoriteKeys.Tooltip, one line). A write in flight
        // ("saving") and a medium that is not saved say so instead of the keys.
        string label = Texts.T(marked ? "viewer.favorite.remove" : "viewer.favorite.add");
        string description = _error != null ? label + ": " + _error : _running ? label + " · " + Texts.T("gallery.saving")
            : IsEnabled ? label : Texts.T("archive.notSaved");
        string tip = _error == null && IsEnabled ? FavoriteKeys.Tooltip(marked, KeyContext) : description;
        ToolTip.SetShowOnDisabled(this, true);
        ToolTip.SetTip(this, tip);
        AutomationProperties.SetName(this, description);
        AutomationProperties.SetHelpText(this, tip);
        if (_error != null && !string.Equals(_error, _shownError, StringComparison.Ordinal))
        {
            _shownError = _error;
            Dispatcher.UIThread.Post(() =>
            {
                if (_error == _shownError && this.IsAttachedToVisualTree() && IsEffectivelyVisible) ToolTip.SetIsOpen(this, true);
            }, DispatcherPriority.Loaded);
        }
        else if (_error == null) _shownError = null;
    }
}
