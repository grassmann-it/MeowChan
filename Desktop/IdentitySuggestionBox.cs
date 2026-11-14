using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>Free text remains authoritative. Selection writes a canonical value only;
/// this control never applies a search, opens a rule scope or writes a profile.</summary>
public sealed class IdentitySuggestionBox : Ursa.Controls.AutoCompleteBox
{
    internal Func<string, CancellationToken, Task<IdentitySuggestion[]>>? Candidates { get; set; }
    internal event Action<IdentitySuggestion>? CandidateAccepted;
    internal event Action<Exception?>? LoadCompleted;
    internal IdentitySuggestion? AcceptedCandidate { get; private set; }
    /// <summary>Opt-in: Enter on an open list with no highlighted suggestion closes the list and is left
    /// unhandled for the parent, which decides what free text means. Off: Enter only closes the list.</summary>
    internal bool ReleaseUnselectedEnter { get; set; }
    /// <summary>Opt-in: Tab on an open list writes the highlighted (else the first) suggestion into the editor without
    /// accepting it, see <see cref="IdentitySuggestions.TabCompletion"/>. Off: Tab only closes the list.</summary>
    internal bool TabCompletes { get; set; }
    /// <summary>Rows shown at most; the screen reader hint still counts every match.</summary>
    internal int VisibleLimit { get; set; } = IdentitySuggestions.VisibleLimit;
    /// <summary>Catalog key of the hint announced after each list update; formatted with the match count and the limit.</summary>
    internal string CountHelpKey { get; set; } = "suggestions.count";
    /// <summary>True while the list is populated because Up/Down opened it, not because of focus or typing.</summary>
    internal bool IsExplicitRequest { get; private set; }
    /// <summary>The suggestion Up/Down (or an exact typed match) highlights in the open list, else null.</summary>
    internal IdentitySuggestion? HighlightedCandidate => IsDropDownOpen
        && (SelectionAdapter?.SelectedItem ?? SelectedItem) is IdentitySuggestion item
        && (ItemsSource?.Cast<object>().Contains(item) ?? false) ? item : null;
    private CancellationTokenSource? _request;
    private int _revision;
    private bool _publishing;
    private bool _ended;
    private bool _suppressed;
    protected override Type StyleKeyOverride => typeof(Ursa.Controls.AutoCompleteBox);
    public IdentitySuggestionBox()
    {
        MinimumPrefixLength = 0;
        MinimumPopulateDelay = TimeSpan.Zero;
        IsTextCompletionEnabled = false;
        FilterMode = AutoCompleteFilterMode.None;
        ValueMemberBinding = new Binding(nameof(IdentitySuggestion.Value));
        ItemSelector = (text, _) => text ?? string.Empty; // Navigation never changes the draft.
        ItemTemplate = new FuncDataTemplate<IdentitySuggestion>((item, _) =>
        {
            TextBlock row = new()
            {
                Text = item?.Label, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                [ToolTip.TipProperty] = item?.Description ?? item?.Label,
                [AutomationProperties.NameProperty] = item?.Description ?? item?.Label
            };
            if (item?.GroupHeader is not { } header) return row;
            // The group label sits above its first row, inside that row: it is never a selectable item of its own.
            TextBlock label = new() { Text = header, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "workspace-meta" },
                [AutomationProperties.AccessibilityViewProperty] = AccessibilityView.Raw };
            return new StackPanel { Spacing = 2, Children = { label, row },
                [ToolTip.TipProperty] = item.Description ?? item.Label,
                [AutomationProperties.NameProperty] = header + ": " + (item.Description ?? item.Label) };
        });
        Populating += (_, e) => { e.Cancel = true; };
        // A click in an already-focused editor does not raise GotFocus or TextChanged.
        // After Escape the base control may nevertheless reopen its now-empty popup.
        // Treat that explicit editor click as a fresh request, not a revival of a
        // dismissed candidate or a captured release. Keep the normal caret gesture.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (_ended || !_suppressed || !IsKeyboardFocusWithin || e.Pointer.Type != PointerType.Mouse
                || e.GetCurrentPoint(null).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
                || e.Source is not Visual visual
                || !visual.GetSelfAndVisualAncestors().OfType<TextBox>().Any(this.IsVisualAncestorOf)) return;
            int revision = _revision;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_ended || revision != _revision || !IsKeyboardFocusWithin) return;
                _suppressed = false;
                _ = PopulateAsync();
            }, Avalonia.Threading.DispatcherPriority.Input);
        }, RoutingStrategies.Tunnel);
        // Focus entering a suggestion (the popup is a logical child) is not the editor gaining focus:
        // repopulating then would clear the list under the click and fault the selection update.
        GotFocus += (_, e) =>
        {
            if (_restoringFocus || _pointerInPopup || SuggestionAt(e.Source) != null) return;
            _suppressed = false;
            int revision = _revision;
            // A cached provider can finish synchronously inside the routed focus event,
            // before the base editor finishes updating its popup/search state. Publish
            // only after that event, and never revive a request superseded by typing.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!_ended && !_suppressed && revision == _revision && IsKeyboardFocusWithin)
                    _ = PopulateAsync();
            }, Avalonia.Threading.DispatcherPriority.Input);
        };
        LostFocus += (_, e) =>
        {
            if (e.NewFocusedElement is Control next && (this.IsVisualAncestorOf(next)
                || next.DataContext is IdentitySuggestion
                || next.GetVisualAncestors().OfType<ListBoxItem>().Any(x => x.DataContext is IdentitySuggestion))) return;
            Dismiss();
        };
        TextChanged += (_, _) =>
        {
            if (_publishing) return;
            AcceptedCandidate = null; _suppressed = false;
            // The base control raises TextChanged before it records the new search text. Clearing the list
            // here would drop a list selection, which makes the base control write the OLD search text back
            // into the editor (and mutates the list inside a selection update). Close now, repopulate right
            // after this change has completed and before any further input is processed.
            CancelPointerGesture();
            int revision = ++_revision;
            _request?.Cancel();
            IsDropDownOpen = false;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (revision == _revision) _ = PopulateAsync(); },
                Avalonia.Threading.DispatcherPriority.Send);
        };
        AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (SelectionAdapter != null) SelectionAdapter.Commit -= OnCommit;
        base.OnApplyTemplate(e);
        if (SelectionAdapter != null) SelectionAdapter.Commit += OnCommit;
        AttachPopupPointer(e.NameScope.Find<SelectingItemsControl>("PART_SelectingItemsControl"));
    }
    private void OnCommit(object? sender, RoutedEventArgs e)
    {
        // The pointer gesture owns its commit; the adapter must not accept on press or twice.
        if (!_pointerInPopup) AcceptCandidate();
    }
    private void AcceptCandidate() => Accept((SelectionAdapter?.SelectedItem ?? SelectedItem) as IdentitySuggestion);
    private void Accept(IdentitySuggestion? item)
    {
        if (_ended || _suppressed) return;
        if (item != null)
        {
            _publishing = true;
            try { AcceptedCandidate = item; SetCurrentValue(TextProperty, item.Value); CandidateAccepted?.Invoke(item); }
            finally { _publishing = false; }
        }
        Dismiss();
    }
    // Own a mouse gesture before ListBox focus/selection handling can close the popup.
    // A captured release accepts only the same still-current row, never a row dismissed
    // by Escape, a scope change, a new request, retemplating or capture loss.
    private SelectingItemsControl? _popupList;
    private IPointer? _popupPointer;
    private ListBoxItem? _pointerRow;
    private IdentitySuggestion? _pointerCandidate;
    private int _pointerRevision;
    private bool _pointerInPopup => _popupPointer != null;
    private bool _restoringFocus;
    private void AttachPopupPointer(SelectingItemsControl? list)
    {
        CancelPointerGesture();
        if (_popupList != null)
        {
            _popupList.RemoveHandler(PointerPressedEvent, OnPopupPressed);
            _popupList.RemoveHandler(PointerReleasedEvent, OnPopupReleased);
            _popupList.RemoveHandler(PointerCaptureLostEvent, OnPopupCaptureLost);
        }
        _popupList = list;
        if (list == null) return;
        list.AddHandler(PointerPressedEvent, OnPopupPressed, RoutingStrategies.Tunnel);
        list.AddHandler(PointerReleasedEvent, OnPopupReleased, RoutingStrategies.Tunnel);
        list.AddHandler(PointerCaptureLostEvent, OnPopupCaptureLost);
    }
    private static ListBoxItem? SuggestionRowAt(object? source)
        => (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();
    private static IdentitySuggestion? SuggestionAt(object? source)
        => SuggestionRowAt(source)?.DataContext as IdentitySuggestion;
    private bool IsCurrentCandidate(IdentitySuggestion item)
        => ItemsSource?.Cast<object>().Any(candidate => ReferenceEquals(candidate, item)) == true;
    private void OnPopupPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse
            || e.GetCurrentPoint(null).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || _popupList == null || !IsDropDownOpen || _ended || _suppressed
            || SuggestionRowAt(e.Source) is not { DataContext: IdentitySuggestion item } row
            || !_popupList.IsVisualAncestorOf(row) || !IsCurrentCandidate(item)) return;
        CancelPointerGesture();
        _popupPointer = e.Pointer;
        _pointerRow = row;
        _pointerCandidate = item;
        _pointerRevision = _revision;
        e.Handled = true;
        e.PreventGestureRecognition();
        e.Pointer.Capture(_popupList);
        if (!ReferenceEquals(e.Pointer.Captured, _popupList)) CancelPointerGesture();
    }
    private void OnPopupReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(e.Pointer, _popupPointer)) return;
        e.Handled = true;
        IdentitySuggestion? item = _pointerCandidate;
        ListBoxItem? row = _pointerRow;
        bool accept = e.InitialPressMouseButton == MouseButton.Left
            && !_ended && !_suppressed && IsDropDownOpen && _pointerRevision == _revision
            && item != null && IsCurrentCandidate(item)
            && _popupList != null && ReferenceEquals(e.Pointer.Captured, _popupList)
            && row != null && ReferenceEquals(row.DataContext, item)
            && row.IsEffectivelyVisible && _popupList.IsVisualAncestorOf(row)
            && new Rect(row.Bounds.Size).Contains(e.GetPosition(row))
            && new Rect(_popupList.Bounds.Size).Contains(e.GetPosition(_popupList));
        CancelPointerGesture();
        if (!accept) return;
        Accept(item);
        // Suppress repopulation if a template moves focus during popup closure.
        if (this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { IsKeyboardFocusWithin: false } editor)
        {
            _restoringFocus = true;
            try { editor.Focus(); } finally { _restoringFocus = false; }
        }
    }
    private void OnPopupCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _popupPointer)) CancelPointerGesture();
    }
    private void CancelPointerGesture()
    {
        IPointer? pointer = _popupPointer;
        _popupPointer = null;
        _pointerRow = null;
        _pointerCandidate = null;
        if (pointer != null && ReferenceEquals(pointer.Captured, _popupList)) pointer.Capture(null);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Dismiss();
        base.OnDetachedFromVisualTree(e);
    }
    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && this.GetVisualDescendants().OfType<TextPresenter>().Any(x => !string.IsNullOrEmpty(x.PreeditText)))
        { e.Handled = true; return; }
        if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; return; }
        if (e.Key == Key.Tab)
        {
            if (TabCompletes && e.KeyModifiers == KeyModifiers.None && IsDropDownOpen
                && IdentitySuggestions.TabCompletion(Text, HighlightedCandidate, ShownCandidates()) is { } completion)
            { Complete(completion); e.Handled = true; return; }
            Dismiss(); return;
        }
        if ((e.Key == Key.Down || e.Key == Key.Up) && !_ended && Candidates != null
            && (!IsDropDownOpen || _suppressed || ItemsSource?.OfType<IdentitySuggestion>().Any() != true))
        {
            // Escape or a prior accept closes the list; explicit navigation reopens it
            // without changing the draft, applying a search or accepting anything.
            _suppressed = false; e.Handled = true;
            IsExplicitRequest = true;
            try { _ = PopulateAsync(); } finally { IsExplicitRequest = false; }
            return;
        }
        if (e.Key != Key.Enter || !IsDropDownOpen) return;
        if (ReleaseUnselectedEnter && !(ItemsSource?.Cast<object>().Contains(SelectionAdapter?.SelectedItem ?? SelectedItem) ?? false))
        { Dismiss(); return; }
        // Consume the popup's Enter before a parent search command can observe it.
        AcceptCandidate();
        e.Handled = true;
    }
    private IdentitySuggestion[] ShownCandidates() => ItemsSource?.OfType<IdentitySuggestion>().ToArray() ?? Array.Empty<IdentitySuggestion>();
    // Completion only edits the draft: no CandidateAccepted, nothing is applied or added.
    private void Complete(IdentitySuggestion item)
    {
        _publishing = true;
        try { SetCurrentValue(TextProperty, item.Value); }
        finally { _publishing = false; }
        Dismiss();
        if (this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } editor)
        { editor.ClearSelection(); editor.CaretIndex = item.Value.Length; }
    }
    internal void Dismiss()
    {
        ++_revision; _suppressed = true;
        CancelPointerGesture();
        _request?.Cancel(); _request?.Dispose(); _request = null;
        IsDropDownOpen = false;
        if (!ClearItems()) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_suppressed) ClearItems(); }, Avalonia.Threading.DispatcherPriority.Input);
    }
    // Scope changes invalidate the accepted pair without rewriting free/editor text.
    internal void InvalidateIdentityContext() { AcceptedCandidate = null; Dismiss(); }
    internal void EndSuggestions() { _ended = true; Dismiss(); AcceptedCandidate = null; Candidates = null; }
    internal void RestartSuggestions() { _suppressed = false; _ = PopulateAsync(); }
    internal void RefreshSuggestions() { if (!_suppressed && IsKeyboardFocusWithin) _ = PopulateAsync(); }
    /// <summary>
    /// Focuses the editor and opens the list as Up/Down would: an explicit request, so an empty field lists what the provider
    /// offers for browsing (the tag fields: the most used names). Used by "More…" of the quick tag surface.
    /// </summary>
    internal void OpenExplicitList()
    {
        if (_ended || Candidates == null) return;
        Focus();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_ended || Candidates == null || !IsKeyboardFocusWithin) return;
            _suppressed = false;
            IsExplicitRequest = true;
            try { _ = PopulateAsync(); } finally { IsExplicitRequest = false; }
        }, Avalonia.Threading.DispatcherPriority.Input);
    }
    private bool ClearItems()
    {
        try { ItemsSource = Array.Empty<IdentitySuggestion>(); return true; }
        catch (InvalidOperationException) { return false; }
    }
    private bool Publish(IdentitySuggestion[] items)
    {
        _publishing = true;
        try
        {
            ItemsSource = items.Take(VisibleLimit).ToArray();
            PopulateComplete();
            IsDropDownOpen = items.Length > 0;
            return true;
        }
        catch (InvalidOperationException) { return false; }
        finally { _publishing = false; }
    }
    internal async Task PopulateAsync()
    {
        CancelPointerGesture();
        ++_revision;
        int revision = _revision;
        _request?.Cancel(); _request?.Dispose();
        _request = new CancellationTokenSource();
        var token = _request.Token;
        IsDropDownOpen = false;
        if (!ClearItems())
        {
            // The selection model is mid-update (a click or Enter is still being processed): retry after it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (revision == _revision) _ = PopulateAsync(); },
                Avalonia.Threading.DispatcherPriority.Input);
            return;
        }
        if (_ended || _suppressed || !IsKeyboardFocusWithin || Candidates == null) return;
        string text = Text ?? "";
        try
        {
            var items = await Candidates(text, token);
            if (_ended || token.IsCancellationRequested || revision != _revision || !IsKeyboardFocusWithin || _suppressed || Text != text) return;
            if (!Publish(items))
            {
                // A selection update (click, Enter, focus hand-back) is still running and refuses a source
                // change. That is not a load failure: publish once it has finished, unless superseded.
                await Task.Yield();
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_ended || token.IsCancellationRequested || revision != _revision || _suppressed || Text != text) return;
                    if (Publish(items)) AutomationProperties.SetHelpText(this, Texts.T(CountHelpKey, items.Length, VisibleLimit));
                }, Avalonia.Threading.DispatcherPriority.Input);
            }
            AutomationProperties.SetHelpText(this, Texts.T(CountHelpKey, items.Length, VisibleLimit));
            LoadCompleted?.Invoke(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_ended && revision == _revision && !token.IsCancellationRequested) LoadCompleted?.Invoke(error);
        }
    }
}
