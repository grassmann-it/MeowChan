using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.Desktop;

// The quick tag panel (PLAN-SCHNELL-TAGGEN §1.1, decision E1): the Tags segment of the gallery side panel host (V2,
// GalleryViewer.SidePanel.cs). It is the same bordered slot (_contextPanel), so its placement (docked from 900 dip, over the picture
// below), the auto-hide hold of the controls while it is open, the work-view policy row "gallery.post-panel" and the focus handling
// are shared and not copied; only the shown segment changes. Post and tags exclude each other (P and the Post segment switch to the
// post, Ctrl+Shift+T and the Tags segment to the tags); the host's × closes both.
// The viewer shows state and raises requests; everything that touches the tag store lives in the owner (IGalleryTagPanelOwner).
internal sealed partial class GalleryViewer
{
    private const string TagPanelName = "GalleryTagPanel";
    private GalleryTagPanelController? _tagController;
    private QuickTagSurface? _tagSurface;
    private GalleryThreadTagSection? _threadSection;
    private StackPanel? _tagExtra;
    private TextBlock? _tagReason;
    // Favorite and rating of the panel's targets (PT6): the row above the field; null when the owner offers none.
    private GalleryDecisionRow? _decisionRow;
    private IGalleryTagPanelOwner? _tagOwner;
    // The media marked in the gallery (Shift+Page Down, E4): gallery state, kept while the panel closes and opens again.
    private GalleryMarks? _marks;
    private bool _tagPanelOpen;

    /// <summary>True while the tag panel (not the post panel) holds the slot.</summary>
    internal bool IsTagPanelOpen => _tagPanelOpen;
    internal QuickTagSurface? TagSurface => _tagSurface;
    internal GalleryTagPanelController? TagPanelController => _tagController;
    /// <summary>The favorite and rating row above the tag field; null while the panel is closed or its owner offers no decisions.</summary>
    internal GalleryDecisionRow? DecisionRow => _decisionRow;
    /// <summary>The media marked for one tag change; null until the tag panel was opened once.</summary>
    internal GalleryMarks? Marks => _marks;

    /// <summary>
    /// True while a favorite and rating row is on the screen: the Tags segment's row (PT6) or the Post segment's (review 15.11.). The row then
    /// shows the rating, the info line does not (QW5); with no panel open the info line is the only place that shows it.
    /// </summary>
    internal bool DecisionRowVisible => _tagPanelOpen ? _decisionRow != null : PostContextVisible && _entry != null;

    // While the tag panel holds the slot the post context is neither built nor current.
    private bool PostContextVisible => _contextOpen && !_tagPanelOpen;

    /// <summary>
    /// Opens the tag panel for the medium shown now (or focuses its field when it is open): it takes the slot of the post panel, follows
    /// the gallery from medium to medium and stays open. No-op without a medium.
    /// </summary>
    internal void ShowTagPanel(IGalleryTagPanelOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (_terminalRetirement || _entry is not { } entry) return;
        _tagOwner = owner;
        if (_tagPanelOpen)
        {
            _tagSurface?.FocusField();
            return;
        }
        _marks ??= new GalleryMarks(owner.Options.MarkedMediaLimit);
        GalleryTagPanelController controller = GalleryTagPanelController.Create(owner, entry, _marks);
        QuickTagSurface surface = new(controller.Session, owner.Options, TagPanelName) { NavigationEnabled = true, FillSuggestions = true, ReadSourceTags = owner.ReadSourceTags };
        surface.UseSectionInset();
        surface.Suggestions = controller.Suggestions;
        surface.TargetLabel = GalleryTagPanelCommands.LabelOf(entry);
        surface.CounterText = PanelCounter(controller.CounterFor(entry));
        surface.MarkedCount = _marks.Count;
        AnnounceCounter(surface);
        // UX-LEDGER-14: keep the same live badge, but beside Post/Tags in the host header, not beside the file title below it.
        _sidePanel.HeaderCounter = surface.DetachCounter();
        _tagExtra = new StackPanel { Spacing = 8, Name = TagPanelName + "Sections" };
        _tagReason = new TextBlock { Name = TagPanelName + "Reason", Classes = { "workspace-help" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
        _threadSection = new GalleryThreadTagSection(TagPanelName + "Thread") { IsVisible = false };
        _tagExtra.Children.Add(_tagReason);
        _tagExtra.Children.Add(_threadSection);
        surface.ExtraContent = _tagExtra;
        surface.CloseRequested += CloseTagPanel;
        surface.AdvancedRequested += OnTagAdvancedRequested;
        surface.NavigationRequested += OnTagNavigationRequested;
        surface.MarksClearRequested += OnTagMarksClearRequested;
        surface.ThreadSectionRequested += OnTagThreadSectionRequested;
        controller.Suggestions.Ready += OnTagSuggestionsReady;
        if (controller.Decisions is { } decisions)
        {
            _decisionRow = new GalleryDecisionRow(TagPanelName + "Decision")
            { FieldFocused = () => surface.Input.IsKeyboardFocusWithin, ReturnFocus = () => surface.FocusField(), KeyContext = "gallery" };
            _decisionRow.FavoriteRequested += OnDecisionFavoriteRequested;
            _decisionRow.RatingRequested += RequestDecisionRating;
            surface.DecisionContent = _decisionRow;
            surface.ExtraUndo = controller.ExtraUndo;
            decisions.Updated += OnDecisionsUpdated;
        }
        _tagController = controller;
        _tagSurface = surface;
        if (_tagging != null) CoupleTaggingPanel(surface, controller); // the tagging mode's undo and stamp (PT3)
        surface.CloseButton.IsVisible = false; // the host's × at the right edge of the header closes the panel
        ShowUnavailableReason(controller.UnavailableReason);
        surface.LoadSourceTags();
        _tagPanelOpen = true;
        _filmstrip.SetTaggingViewOpen(true); // the filmstrip's "no tags yet" mark follows the tagging view
        _hoverContextOpen = false; // an edge-hover opened post panel would otherwise close the slot behind the tags
        RefreshDecisionRow();
        ApplyChrome(); // the info line's rating yields to the row (QW5)
        _sidePanel.TagContent = surface;
        _sidePanel.Segment = GallerySidePanelSegment.Tags;
        if (_contextOpen)
        {
            LayoutContext();
            UpdateContextButton();
            RefreshPlaybackControls();
        }
        else SetContextOpen(true, notify: false);
    }

    /// <summary>Closes the tag panel and gives the focus back to the picture.</summary>
    internal void CloseTagPanel()
    {
        if (_tagPanelOpen) SetContextOpen(false, notify: false);
    }

    // Called when the slot closes or the viewer stops: gives the slot back to the post panel and ends every background step.
    // True when a tag panel was released, so closing it does not rewrite the post panel's "open" preference.
    private bool ReleaseTagPanel()
    {
        if (!_tagPanelOpen) return false;
        _tagPanelOpen = false;
        _filmstrip.SetTaggingViewOpen(false);
        QuickTagSurface? surface = _tagSurface;
        GalleryTagPanelController? controller = _tagController;
        _tagSurface = null; _tagController = null; _tagOwner = null;
        if (surface != null)
        {
            UncoupleTaggingPanel(surface);
            surface.CloseRequested -= CloseTagPanel;
            surface.AdvancedRequested -= OnTagAdvancedRequested;
            surface.NavigationRequested -= OnTagNavigationRequested;
            surface.MarksClearRequested -= OnTagMarksClearRequested;
            surface.ThreadSectionRequested -= OnTagThreadSectionRequested;
        }
        if (controller != null) controller.Suggestions.Ready -= OnTagSuggestionsReady;
        if (controller?.Decisions != null) controller.Decisions.Updated -= OnDecisionsUpdated;
        if (_decisionRow != null)
        {
            _decisionRow.FavoriteRequested -= OnDecisionFavoriteRequested;
            _decisionRow.RatingRequested -= RequestDecisionRating;
            if (surface != null) { surface.ExtraUndo = null; surface.DecisionContent = null; }
            _decisionRow = null;
        }
        _sidePanel.HeaderCounter = null;
        if (ReferenceEquals(_sidePanel.TagContent, surface)) _sidePanel.TagContent = null;
        _sidePanel.Segment = GallerySidePanelSegment.Post;
        _threadSection?.Dispose();
        surface?.Dispose();
        controller?.Dispose();
        _threadSection = null; _tagExtra = null; _tagReason = null;
        ApplyChrome(); // the row is gone: the info line shows the rating again (QW5)
        // The post context was not kept current meanwhile.
        _contextDirty = true;
        EndFreeTagModeWithPanel(); // M1: a Tag mode without a walk is the panel; it ends with it
        return true;
    }

    // P: while the tag panel holds the slot the post takes it over (and is remembered as open); otherwise the plain toggle.
    private void ToggleContext()
    {
        if (!_tagPanelOpen) { SetContextOpen(!_contextOpen); return; }
        ShowPostSegment();
        FocusGallerySurface();
    }

    /// <summary>
    /// The owner calls this after every medium change (and association switch): the open panel moves to the shown medium with a fresh
    /// target frozen to exactly that entry, its file name and "Medium 4 of 27"; the undo stack and the field's text stay.
    /// </summary>
    internal void RetargetTagPanel(GalleryEntry entry)
    {
        if (!_tagPanelOpen || _tagController is not { } controller || _tagSurface is not { } surface || _tagOwner is not { } owner) return;
        bool sameMedium = controller.Entry.EntryKey == entry.EntryKey;
        if (sameMedium)
        {
            // Only the position may have changed (more threads resolved).
            surface.CounterText = PanelCounter(controller.CounterFor(entry));
            RefreshDecisionRow();
            return;
        }
        bool hadFocus = surface.IsKeyboardFocusWithin;
        GalleryTagPanelFrame frame = controller.Frame(entry);
        surface.Retarget(frame.Target, frame.Label);
        surface.CounterText = PanelCounter(frame.Counter);
        surface.MarkedCount = controller.Marks.Count;
        ShowUnavailableReason(frame.UnavailableReason);
        if (_threadSection is { IsVisible: true } section) section.Model = frame.UnavailableReason == null ? controller.OpenThreadTags() : null;
        RefreshDecisionRow();
        // The field stays focused while paging: a focus lost with the replaced chips returns to it.
        if (hadFocus && !surface.IsKeyboardFocusWithin) surface.FocusField();
    }

    /// <summary>Reloads the open panel from the store (after the full dialog changed tags of the same medium).</summary>
    internal void ReloadTagPanel()
    {
        if (!_tagPanelOpen || _tagSurface is not { } surface) return;
        surface.Session.Target.Reload();
        surface.Refresh();
        if (_threadSection is { Model: { } model } section) { model.Reload(); section.Refresh(); }
    }

    private void ShowUnavailableReason(string? reason)
    {
        if (_tagReason == null) return;
        _tagReason.Text = reason ?? string.Empty;
        _tagReason.IsVisible = !string.IsNullOrEmpty(reason);
    }

    private void OnTagAdvancedRequested() => _tagController?.OpenAdvanced();

    private void OnTagNavigationRequested(QuickTagNavigation navigation)
    {
        if (_tagController is { } controller && _tagSurface != null)
        {
            // Shift+Page Down marks the shown medium and goes on; Shift+Page Up goes back and takes the mark off the medium it reaches.
            // The shown medium is read from the owner's position, which is current at once, so quick repeats mark each medium once.
            if (navigation == QuickTagNavigation.MarkNext)
            {
                ApplyMarkResult(controller.Mark(controller.ShownEntry()));
                Move(1);
                return;
            }
            if (navigation == QuickTagNavigation.UnmarkPrevious)
            {
                if (controller.PreviousEntry() is not { } previous) return;
                ApplyMarkResult(controller.Unmark(previous));
                Move(-1);
                return;
            }
        }
        int delta = GalleryTagPanelCommands.Delta(navigation);
        if (delta != 0) Move(delta);
    }

    // The counter chip's clear button: every mark goes and the panel writes to the shown medium again.
    private void OnTagMarksClearRequested()
    {
        if (_tagController is not { } controller || _tagSurface is not { } surface) return;
        if (controller.ClearMarks() == 0) return;
        RetargetForMarks(controller, surface);
        surface.Announce(GalleryTagPanelCommands.ClearedAnnouncement());
        surface.FocusField();
    }

    // A mark request changed (or refused to change) the marks: the target is made again, the chips, the counter and the status say so.
    private void ApplyMarkResult(GalleryMarkResult result)
    {
        if (_tagController is not { } controller || _tagSurface is not { } surface) return;
        if (result.Changed) RetargetForMarks(controller, surface);
        surface.Announce(GalleryTagPanelCommands.MarkAnnouncement(result));
    }

    private void RetargetForMarks(GalleryTagPanelController controller, QuickTagSurface surface)
    {
        GalleryTagPanelFrame frame = controller.Frame(controller.Entry);
        surface.Retarget(frame.Target, frame.Label);
        // The owner's position may already be on the next medium (a move was requested): the counter names that one.
        surface.CounterText = PanelCounter(controller.CounterFor(controller.ShownEntry()));
        surface.MarkedCount = controller.Marks.Count;
        ShowUnavailableReason(frame.UnavailableReason);
        RefreshDecisionRow();
    }

    // QW1: in the tagging mode the position stands once, between the arrows ("Queue 3 of 27"); the panel head shows no second counter.
    private string? PanelCounter(string? counter) => GalleryChromePlan.PanelCounter(counter, IsTagMode);

    // The tagging mode started or ended under an open panel: its counter chip follows (shown outside the mode, away inside it).
    private void RefreshPanelCounter()
    {
        if (_tagController is { } controller && _tagSurface is { } surface)
            surface.CounterText = PanelCounter(controller.CounterFor(controller.ShownEntry()));
    }

    // ---- favorite and rating (PT6) ------------------------------------------------------------------------------------------

    /// <summary>Shows what the panel's targets (every marked medium, else the shown one) carry now; the gallery calls it when a favorite or rating changed elsewhere too.</summary>
    internal void RefreshDecisionRow()
    {
        if (_decisionRow is { } row && _tagController?.DecisionView() is { } view) row.Show(view);
    }

    private void OnDecisionsUpdated() => RefreshDecisionRow();

    private void OnDecisionFavoriteRequested(bool selected) =>
        RunDecision((decisions, targets) => decisions.SetFavoriteAsync(targets, selected));

    private void RequestDecisionRating(int? value) =>
        RunDecision((decisions, targets) => decisions.SetRatingAsync(targets, value));

    /// <summary>
    /// A rating key or button: with the tag panel open it rates the panel's targets (the marked media, else the shown one) as one undoable
    /// step and says what it did in the status line; without the panel it rates the shown medium as before.
    /// </summary>
    private void RequestRating(int? value)
    {
        if (_entry == null) return;
        RevealForRatingFeedback();
        if (_tagPanelOpen && _tagController?.Decisions != null) { RequestDecisionRating(value); return; }
        RatingSetRequested?.Invoke(_entry, value);
    }

    // The targets are read when the request is made: paging or marking afterwards never redirects the write.
    private void RunDecision(Func<GalleryDecisionSession, IReadOnlyList<GalleryEntry>, Task<GalleryDecisionStatus>> run)
    {
        if (_tagController is not { Decisions: { } decisions } controller || _tagSurface is not { } surface) return;
        _ = RunDecisionAsync(surface, run(decisions, controller.DecisionTargets()));
    }

    private async Task RunDecisionAsync(QuickTagSurface surface, Task<GalleryDecisionStatus> request)
    {
        try
        {
            GalleryDecisionStatus status = await request;
            if (!ReferenceEquals(surface, _tagSurface)) return;
            if (status.Kind != GalleryDecisionStatusKind.None) surface.Announce(status.Text());
            RefreshDecisionRow();
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "gallery.decision")) { /* Logged and shown. */ }
    }

    // Shift+1…5 and Shift+0 also rate from an empty tag field: a typed "!" or ")" never starts a tag name. With text in the field (or its
    // suggestion list open) the keys stay typing. Matches reads the effective bindings directly: CommandBindings.Matches leaves text fields.
    private bool RouteDecisionKeyInField(KeyEventArgs e, QuickTagSurface surface)
    {
        if (_tagController?.Decisions == null || !surface.Input.IsKeyboardFocusWithin
            || !string.IsNullOrEmpty(surface.Input.Text) || surface.Input.IsDropDownOpen) return false;
        for (int value = 1; value <= 5; value++)
            if (ShortcutService.Current.Matches(e, "media.rating.set-" + value)) { RequestRating(value); e.Handled = true; return true; }
        if (!ShortcutService.Current.Matches(e, "media.rating.clear")) return false;
        RequestRating(null);
        e.Handled = true;
        return true;
    }

    // Ctrl+T: opens the thread tags section and focuses its field; again, with the focus inside it, collapses it back to the field.
    private void OnTagThreadSectionRequested()
    {
        if (_threadSection is not { } section || _tagSurface is not { } surface || _tagController is not { } controller) return;
        if (section.IsVisible && section.IsKeyboardFocusWithin)
        {
            section.IsVisible = false;
            surface.FocusField();
            return;
        }
        if (!section.IsVisible)
        {
            section.Model = controller.UnavailableReason == null ? controller.OpenThreadTags() : null;
            section.IsVisible = true;
        }
        section.BringIntoView();
        section.FocusInput();
    }

    // The strip is asked again once a warm-up or the neighbour mapping finished (worker thread).
    private void OnTagSuggestionsReady()
    {
        QuickTagSurface? surface = _tagSurface;
        if (surface == null) return;
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(surface, _tagSurface)) surface.Refresh(); });
    }

    // "Medium 4 of 27" is announced politely when it changes.
    private static void AnnounceCounter(QuickTagSurface surface)
    {
        foreach (Border counter in surface.Header.Children.OfType<Border>().Where(item => item.Name == TagPanelName + "Counter"))
        {
            AutomationProperties.SetLiveSetting(counter, AutomationLiveSetting.Polite);
            if (counter.Child is TextBlock label) AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Polite);
        }
    }

    /// <summary>
    /// Routes the keys of the tag panel's commands (rebindable) while it is open, wherever the focus is. A key in a text field stays
    /// with the field (CommandBindings.Matches leaves it), so the surface's own routing handles those. Returns true when handled.
    /// </summary>
    private bool RouteTagPanelKey(KeyEventArgs e)
    {
        if (!_tagPanelOpen || _tagSurface is not { } surface || e.Handled) return false;
        // Escape closes an open suggestion list of the thread field before it closes the panel.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && _threadSection is { Input: { IsDropDownOpen: true } input } && input.IsKeyboardFocusWithin)
        {
            input.Dismiss();
            e.Handled = true;
            return true;
        }
        if (RouteDecisionKeyInField(e, surface)) return true;
        // The command that opened the panel closes it again, also from inside the field.
        if (CommandBindings.Matches(e, "media.tags.edit")) { CloseTagPanel(); e.Handled = true; return true; }
        foreach (string id in GalleryTagPanelCommands.Handled)
        {
            if (!CommandBindings.Matches(e, id) || GalleryTagPanelCommands.RouteOf(id) is not { } route) continue;
            if (surface.Execute(route)) e.Handled = true;
            return e.Handled;
        }
        return false;
    }
}
