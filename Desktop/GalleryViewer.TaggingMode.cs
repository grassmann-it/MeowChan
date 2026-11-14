using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.Desktop;

// The tagging mode (PT3, PLAN-POWER-TAGGEN §1.4/§1.6): the gallery over the open entries of the tagging queue with the tag panel (TP2)
// and its favorite/rating row (PT6) open, plus a decision strip at the bottom of the controls: Back (Backspace), Skip (Space), Done &
// next (Enter) and Stamp & next (Ctrl+Shift+Enter, the PT5 stamp). The strip is part of the controls (_chrome), below the toolbar and
// the video transport, and holds them visible while the mode runs (never auto-hidden). Its keys have their own execution context
// ("gallery-tagging") and are routed here before the gallery's own keys: in the mode Space skips and Shift+Space plays or pauses a
// video; outside the mode nothing changes. In the tag field Enter keeps taking over the typed tag and finishes the medium only when the
// field is empty; Ctrl+Enter takes over the tag and finishes. The model (TaggingWalk, TaggingUndo) is in TaggingDecisionLog.cs.
internal sealed partial class GalleryViewer
{
    private readonly Border _taggingStrip = new()
    {
        Name = "GalleryTaggingStrip", IsVisible = false, Padding = new Thickness(0, 8, 0, 0), Margin = new Thickness(0, 8, 0, 0),
        BorderThickness = new Thickness(0, 1, 0, 0)
    };
    private readonly WrapPanel _taggingActions = new() { Name = "GalleryTaggingActions", ItemSpacing = 8, LineSpacing = 8, Orientation = Orientation.Horizontal };
    private readonly TextBlock _taggingProgress = new() { Name = "GalleryTaggingProgress", Classes = { "workspace-meta" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _taggingStatus = new() { Name = "GalleryTaggingStatus", Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _taggingBack = new() { Name = "GalleryTaggingBack", MinHeight = 32 };
    private readonly Button _taggingSkip = new() { Name = "GalleryTaggingSkip", MinHeight = 32 };
    private readonly Button _taggingDone = new() { Name = "GalleryTaggingDone", MinHeight = 32, Classes = { "primary" } };
    private readonly Button _taggingStamp = new() { Name = "GalleryTaggingStamp", MinHeight = 32 };
    private TaggingWalk? _tagging;
    private IGalleryTagPanelOwner? _taggingOwner;
    private TaggingUndo? _taggingUndo;
    private QuickTagSurface? _taggingSurface;

    /// <summary>True while the gallery walks the tagging queue.</summary>
    internal bool IsTaggingMode => _tagging != null;
    internal TaggingWalk? Tagging => _tagging;
    internal Border TaggingStrip => _taggingStrip;
    internal TextBlock TaggingStatusText => _taggingStatus;
    internal TextBlock TaggingProgressText => _taggingProgress;
    internal Button TaggingDoneButton => _taggingDone;
    internal Button TaggingSkipButton => _taggingSkip;
    internal Button TaggingBackButton => _taggingBack;
    internal Button TaggingStampButton => _taggingStamp;
    /// <summary>The position text of the header ("Queue 12 of 200" in the mode).</summary>
    internal string PositionText => _position.Text ?? string.Empty;

    // The controls of the gallery with the strip below the toolbar: one parent, so the strip follows the controls into the overlay
    // footer and back without a placement of its own.
    private Control WithTaggingStrip(Control toolbar)
    {
        _taggingStrip[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
        _taggingStatus[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text");
        foreach (Button button in new[] { _taggingBack, _taggingSkip, _taggingDone, _taggingStamp }) _taggingActions.Children.Add(button);
        _taggingActions.Children.Add(_taggingProgress);
        _taggingBack.Click += (_, _) => _ = TaggingBackAsync();
        _taggingSkip.Click += (_, _) => TaggingDecide(TagQueueState.Skipped);
        _taggingDone.Click += (_, _) => TaggingDecide(TagQueueState.Done);
        _taggingStamp.Click += (_, _) => _ = TaggingStampAsync();
        // The status line speaks (politely, once); the progress is read on demand, it would repeat every decision.
        AutomationProperties.SetLiveSetting(_taggingStatus, AutomationLiveSetting.Polite);
        StackPanel content = new() { Spacing = 4 };
        content.Children.Add(_taggingActions);
        AddCardsRow(content); // the card mode's cards (PT4)
        content.Children.Add(_taggingStatus);
        _taggingStrip.Child = content;
        AutomationProperties.SetName(_taggingStrip, Texts.T("tagging.strip.name"));
        StackPanel controls = new() { Name = "GalleryControlsStack" };
        controls.Children.Add(toolbar);
        controls.Children.Add(_taggingStrip);
        return controls;
    }

    /// <summary>
    /// Starts the tagging mode over <paramref name="walk"/> (the shown list is the walk's list, in its order): the strip appears, the tag
    /// panel opens on the shown medium and one undo key covers tags, favorite, rating and the queue decisions. The window keeps the
    /// header position ("Queue 12 of 200") current.
    /// </summary>
    internal void StartTaggingMode(TaggingWalk walk, IGalleryTagPanelOwner owner)
    {
        ArgumentNullException.ThrowIfNull(walk);
        ArgumentNullException.ThrowIfNull(owner);
        if (_terminalRetirement) return;
        EndTaggingMode();
        _tagging = walk;
        _taggingOwner = owner;
        walk.Changed += OnTaggingChanged;
        ApplyTaggingLabels();
        _taggingStrip.IsVisible = true;
        _taggingStatus.Text = TaggingModeText.Start(walk);
        RefreshTaggingStrip();
        StartCards(); // the saved card setting (PT4)
        if (_tagPanelOpen && _tagSurface != null && _tagController != null) CoupleTaggingPanel(_tagSurface, _tagController);
        else ShowTagPanel(owner);
        UpdateHelp();
        RevealOverlayControls();
    }

    /// <summary>Ends the mode (the gallery closes or another list is shown): the strip goes, the panel's own undo comes back.</summary>
    internal void EndTaggingMode()
    {
        if (_tagging is not { } walk) return;
        walk.Changed -= OnTaggingChanged;
        EndCards();
        UncoupleTaggingPanel(_taggingSurface);
        _tagging = null;
        _taggingOwner = null;
        _taggingStrip.IsVisible = false;
        _taggingStatus.Text = string.Empty;
        UpdateHelp();
    }

    // The panel opened (or is open) during the mode: its undo button and key take the queue decisions too, and the stamp finishes.
    private void CoupleTaggingPanel(QuickTagSurface surface, GalleryTagPanelController controller)
    {
        if (_tagging is not { } walk) return;
        UncoupleTaggingPanel(_taggingSurface);
        _taggingUndo = new TaggingUndo(walk, controller.Decisions, () => controller.NewestTagStamp, ReturnToUndone);
        surface.ExtraUndo = _taggingUndo;
        surface.StampApplied += OnTaggingStampApplied;
        _taggingSurface = surface;
        AutomationProperties.SetLiveSetting(_taggingStatus, AutomationLiveSetting.Off); // the panel's status line says it once
    }

    private void UncoupleTaggingPanel(QuickTagSurface? surface)
    {
        if (surface == null || !ReferenceEquals(surface, _taggingSurface)) return;
        surface.StampApplied -= OnTaggingStampApplied;
        if (ReferenceEquals(surface.ExtraUndo, _taggingUndo))
            surface.ExtraUndo = ReferenceEquals(surface, _tagSurface) ? _tagController?.ExtraUndo : null;
        _taggingUndo?.Dispose();
        _taggingUndo = null;
        _taggingSurface = null;
        AutomationProperties.SetLiveSetting(_taggingStatus, AutomationLiveSetting.Polite); // without the panel the strip speaks
    }

    private void ApplyTaggingLabels()
    {
        CommandBindings.ApplyLabeled(_taggingBack, "tags.queue.back", Texts.T("tagging.back"));
        CommandBindings.ApplyLabeled(_taggingSkip, "tags.queue.skip", Texts.T("tagging.skip"));
        CommandBindings.ApplyLabeled(_taggingDone, "tags.queue.done-next", Texts.T("tagging.done"));
        CommandBindings.ApplyLabeled(_taggingStamp, QuickTagCommands.StampApply, Texts.T("tagging.stamp"));
        foreach ((Button button, string id) in new[] { (_taggingBack, "tags.queue.back"), (_taggingSkip, "tags.queue.skip"),
            (_taggingDone, "tags.queue.done-next"), (_taggingStamp, QuickTagCommands.StampApply) })
            AutomationProperties.SetAcceleratorKey(button, CommandCatalog.ShortcutDisplay(id) ?? string.Empty);
        AutomationProperties.SetName(_taggingStrip, Texts.T("tagging.strip.name"));
    }

    /// <summary>Shows the progress of the whole queue and enables the decisions for the shown medium (after every change and medium).</summary>
    internal void RefreshTaggingStrip()
    {
        if (_tagging is not { } walk) return;
        _taggingProgress.Text = TaggingModeText.Progress(walk);
        int index = _entry == null ? -1 : walk.IndexOf(_entry.EntryKey);
        bool decidable = index >= 0 && walk.StateOf(index) != null;
        _taggingDone.IsEnabled = decidable;
        _taggingSkip.IsEnabled = decidable;
        _taggingStamp.IsEnabled = decidable;
        RefreshCards();
    }

    private void OnTaggingChanged() => RefreshTaggingStrip();

    // The language or a shortcut changed: labels, keys and the progress follow (the last sentence stays until the next decision).
    private void RefreshTaggingTexts()
    {
        if (_tagging == null) return;
        ApplyTaggingLabels();
        RefreshCardTexts();
        RefreshTaggingStrip();
    }

    // One sentence for the person: in the strip, and through the panel's polite status line while it is open (never read twice).
    private void SayTagging(string text)
    {
        _taggingStatus.Text = text;
        _taggingSurface?.Announce(text);
    }

    /// <summary>
    /// Routes the keys of the tagging mode ahead of the gallery's own: Enter, Space and Backspace on the picture and the controls, the
    /// stamp and Ctrl+Enter everywhere. Inside the tag panel the keys stay the panel's (Backspace removes a focused chip, Space toggles it,
    /// typing stays typing); only an empty field's Enter finishes the medium there. Returns true when handled.
    /// </summary>
    private bool RouteTaggingKey(KeyEventArgs e)
    {
        if (_tagging == null || e.Handled) return false;
        QuickTagSurface? surface = _tagSurface;
        bool inField = surface?.Input.IsKeyboardFocusWithin == true;
        bool listOpen = inField && surface!.Input.IsDropDownOpen;
        if (surface != null && ShortcutService.Current.Matches(e, QuickTagCommands.StampApply)) return Handled(e, () => _ = TaggingStampAsync());
        if (!listOpen && ShortcutService.Current.Matches(e, QuickTagCommands.CommitNext)) return Handled(e, () => _ = TaggingCommitAsync());
        if (surface?.IsKeyboardFocusWithin == true)
        {
            if (inField && !listOpen && string.IsNullOrEmpty(surface.Input.Text) && ShortcutService.Current.Matches(e, "tags.queue.done-next"))
                return Handled(e, () => TaggingDecide(TagQueueState.Done));
            return false;
        }
        if (CommandBindings.Matches(e, "tags.queue.done-next")) return Handled(e, () => TaggingDecide(TagQueueState.Done));
        if (CommandBindings.Matches(e, "tags.queue.skip")) return Handled(e, () => TaggingDecide(TagQueueState.Skipped));
        if (CommandBindings.Matches(e, "tags.queue.back")) return Handled(e, () => _ = TaggingBackAsync());
        if (CommandBindings.Matches(e, "tags.queue.play-pause")) return Handled(e, ToggleTaggingPlayback);
        return RouteCardKey(e); // K and the card keys (PT4)

        static bool Handled(KeyEventArgs args, Action action)
        {
            args.Handled = true;
            action();
            return true;
        }
    }

    // Shift+Space in the mode: what Space does in the plain gallery (an animated GIF or a video plays or pauses).
    private void ToggleTaggingPlayback()
    {
        if (_gif?.Evidence.FrameCount > 1) _ = ToggleGifPlaybackAsync();
        else if (_entry?.Kind == MeowChan.Gallery.SavedMediaKind.Video && _video != null) _ = _video.TogglePlaybackAsync();
    }

    /// <summary>
    /// Finishes or skips the shown medium and goes to the next open one at once; the write follows in order and a refusal is said in the
    /// status line (the state then shows what the store kept). Ignored while the next medium is still being shown, so a quick second key
    /// never decides a medium nobody saw.
    /// </summary>
    private void TaggingDecide(TagQueueState state, string? prefix = null)
    {
        if (_tagging is not { } walk || _entry is not { } entry || _navigationPending || _terminalRetirement) return;
        int index = walk.IndexOf(entry.EntryKey);
        TaggingDecisionResult result = walk.Decide(index, state);
        if (result.Outcome == TaggingDecisionOutcome.NotInWalk)
        {
            SayTagging(Texts.T("tagging.status.missing", GalleryTagPanelCommands.LabelOf(entry)));
            return;
        }
        if (result.Outcome != TaggingDecisionOutcome.Decided) return;
        SayTagging(prefix == null ? TaggingModeText.Decided(walk, result, state) : prefix + " " + TaggingModeText.Decided(walk, result, state));
        if (result.Next is { } next && next != index) Move(next - index);
        _ = ObserveTaggingWriteAsync(walk, result);
    }

    private async Task ObserveTaggingWriteAsync(TaggingWalk walk, TaggingDecisionResult result)
    {
        try
        {
            TaggingWriteResult written = await result.Write;
            if (!ReferenceEquals(walk, _tagging)) return;
            if (written.Kind == TaggingWriteKind.Failed) SayTagging(Texts.T("tagging.status.failed", written.Message ?? string.Empty));
            else if (written.Kind == TaggingWriteKind.Missing) SayTagging(Texts.T("tagging.status.missing", walk.Items[result.Index].Name));
            RefreshTaggingStrip();
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "gallery.tagging")) { /* Logged and shown. */ }
    }

    /// <summary>
    /// Backspace: takes back the newest step (a queue decision returns to its medium; a favorite, rating or tag step of the panel is undone
    /// in place, newest first). With nothing to take back it only goes one medium back, without changing a state.
    /// </summary>
    private async Task TaggingBackAsync()
    {
        if (_tagging is not { } walk || _terminalRetirement) return;
        try
        {
            if (_tagSurface is { } surface && (surface.Session.UndoSteps.Count > 0 || (surface.ExtraUndo?.Steps ?? 0) > 0))
            {
                await surface.UndoAsync();
                if (ReferenceEquals(walk, _tagging)) RefreshTaggingStrip();
                return;
            }
            if (walk.Steps > 0)
            {
                TaggingUndoResult? undone;
                try { undone = await walk.UndoAsync(); }
                catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
                {
                    if (ReferenceEquals(walk, _tagging)) SayTagging(Texts.T("tagging.status.failed", TagQueueActions.DescribeFailure(error)));
                    return;
                }
                if (!ReferenceEquals(walk, _tagging)) return;
                if (undone == null) { SayTagging(Texts.T("tagging.status.undoGone")); return; }
                ReturnToUndone(undone);
                SayTagging(TaggingModeText.Undone(undone));
                return;
            }
            Move(-1);
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "gallery.tagging")) { /* Logged and shown. */ }
    }

    // An undone queue decision: back to its medium (the strip says what happened; the panel's status line says it when the panel undid it).
    private void ReturnToUndone(TaggingUndoResult undone)
    {
        if (_tagging is not { } walk || _entry is not { } entry) return;
        _taggingStatus.Text = TaggingModeText.Undone(undone);
        int index = walk.IndexOf(entry.EntryKey);
        if (index >= 0 && undone.Index != index) Move(undone.Index - index);
        RefreshTaggingStrip();
    }

    // Stamp & next: gives the stamp to the shown medium (or the marked ones) and finishes it. The stamp's own event finishes after a write
    // that left every stamp tag on the medium; a stamp that is already complete finishes too. An empty stamp, a refused or only partly
    // done write leaves the medium open with the panel's reason (the tags written stay and stay undoable; a retry writes the rest).
    private async Task TaggingStampAsync()
    {
        if (_tagging == null || _navigationPending || _terminalRetirement) return;
        if (_tagSurface == null && _taggingOwner != null) ShowTagPanel(_taggingOwner);
        if (_tagSurface is not { } surface) return;
        try
        {
            QuickTagStampResult result = await surface.ApplyStampAsync();
            // A run that wrote finished through StampApplied; one that found everything set writes nothing and finishes here.
            if (!result.Attempted && ReferenceEquals(surface, _tagSurface) && result.FinishesMedium(surface.Session.Target)) TaggingDecide(TagQueueState.Done);
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "gallery.tagging")) { /* Logged and shown. */ }
    }

    // Only a stamp that is complete on the medium still shown finishes it; a partial or refused one (or one for a medium the gallery
    // left meanwhile) decides nothing, so the queue entry stays open.
    private void OnTaggingStampApplied(QuickTagStampResult result)
    {
        if (_taggingSurface is { } surface && result.FinishesMedium(surface.Session.Target)) TaggingDecide(TagQueueState.Done);
    }

    // Ctrl+Enter in the mode: the typed tag is taken over (when there is one) and the medium is finished; a refused tag keeps the medium open.
    private async Task TaggingCommitAsync()
    {
        try
        {
            if (_tagSurface is { } surface && !string.IsNullOrWhiteSpace(surface.Input.Text))
            {
                if (!await surface.CommitTypedAsync() || !ReferenceEquals(surface, _tagSurface)) return;
            }
            TaggingDecide(TagQueueState.Done);
        }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "gallery.tagging")) { /* Logged and shown. */ }
    }

    // F1 in the mode: the decision keys under their own heading, before the gallery's way back.
    private void AddTaggingHelp()
    {
        if (_tagging == null) return;
        _helpContent.Children.Add(new TextBlock { Text = Texts.T("tagging.help.heading"), Classes = { "section-title" }, TextWrapping = TextWrapping.Wrap });
        foreach (string id in new[] { "tags.queue.done-next", "tags.queue.skip", "tags.queue.back", QuickTagCommands.StampApply, QuickTagCommands.CommitNext, "tags.queue.play-pause", TaggingCardCommands.Toggle })
            _helpContent.Children.Add(new TextBlock { Text = CommandBindings.Tooltip(id), [!TextBlock.ForegroundProperty] = new DynamicResourceExtension("Workspace.Text"), TextWrapping = TextWrapping.Wrap });
        _helpContent.Children.Add(new TextBlock { Text = TaggingModeText.HelpNote(), Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap });
        _helpContent.Children.Add(new TextBlock { Text = TaggingCardsText.HelpNote(), Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap });
    }
}
