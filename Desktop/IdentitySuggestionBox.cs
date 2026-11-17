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
    /// <summary>Opt-in: Escape with no open list is left unhandled for the owner (a dialog's Escape policy, <c>DialogEscape</c>); an open
    /// list is still closed by it. Off: Escape is always consumed here.</summary>
    internal bool ReleaseEscape { get; set; }
    /// <summary>Rows shown at most; the screen reader hint still counts every match.</summary>
    internal int VisibleLimit { get; set; } = IdentitySuggestions.VisibleLimit;
    /// <summary>Catalog key of the hint announced after each list update; formatted with the match count and the limit.</summary>
    internal string CountHelpKey { get; set; } = "suggestions.count";
    /// <summary>True while the list is populated because Up/Down opened it, not because of focus or typing.</summary>
    internal bool IsExplicitRequest { get; private set; }
    /// <summary>The suggestion Up/Down (or an exact typed match, or a preselected row) highlights in the open list, else null.</summary>
    internal IdentitySuggestion? HighlightedCandidate => IsDropDownOpen
        && (SelectionAdapter?.SelectedItem ?? SelectedItem) is IdentitySuggestion item
        && (ItemsSource?.Cast<object>().Contains(item) ?? false) ? item : null;
    /// <summary>True while the highlighted row is the one the list preselected (<see cref="IdentitySuggestion.Preselect"/>), not one
    /// the user chose with Up/Down.</summary>
    /// <summary>True while an input method composes in the editor (Enter then belongs to the composition).</summary>
    internal bool IsComposing => this.GetVisualDescendants().OfType<TextPresenter>().Any(x => !string.IsNullOrEmpty(x.PreeditText));
    internal bool IsPreselected => _preselected != null && ReferenceEquals(HighlightedCandidate, _preselected);
    /// <summary>
    /// Opt-in: the list follows the caret. Given the text and the editor's selection it returns the token the list concerns (any
    /// value with equality); a caret move (keys, a click) that changes it asks <see cref="Candidates"/> again. Null: the list only
    /// follows the text.
    /// </summary>
    internal Func<string, int, int, object?>? CaretToken { get; set; }
    /// <summary>The editor's selection (start ≤ end; both the caret without a selection), the end of the text without an editor.</summary>
    internal (int Start, int End) EditorSelection
    {
        get
        {
            int length = Text?.Length ?? 0;
            if (Editor() is not { } editor) return (length, length);
            int start = Math.Min(editor.SelectionStart, editor.SelectionEnd), end = Math.Max(editor.SelectionStart, editor.SelectionEnd);
            if (start == end) start = end = editor.CaretIndex;
            return (Math.Clamp(start, 0, length), Math.Clamp(end, 0, length));
        }
    }
    private IdentitySuggestion? _preselected;
    private object? _populatedToken;
    private (int Start, int End)? _navigationCaret;
    private TextBox? _editor;
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
        // After the list's own Up/Down handling: moving the highlight never moves the caret (it says which token is completed).
        AddHandler(KeyDownEvent, (_, _) =>
        {
            if (_navigationCaret is not { } caret) return;
            _navigationCaret = null;
            if (EditorSelection != caret) PlaceCaret(caret.Start, caret.End);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (SelectionAdapter != null) SelectionAdapter.Commit -= OnCommit;
        base.OnApplyTemplate(e);
        if (SelectionAdapter != null) SelectionAdapter.Commit += OnCommit;
        AttachPopupPointer(e.NameScope.Find<SelectingItemsControl>("PART_SelectingItemsControl"));
        if (_editor != null) _editor.PropertyChanged -= OnEditorProperty;
        _editor = e.NameScope.Find<TextBox>("PART_TextBox");
        if (_editor != null) _editor.PropertyChanged += OnEditorProperty;
    }
    private TextBox? Editor() => _editor ?? this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
    private bool _placingCaret;
    private void PlaceCaret(int start, int end)
    {
        if (Editor() is not { } editor) return;
        int length = editor.Text?.Length ?? 0;
        start = Math.Clamp(start, 0, length); end = Math.Clamp(end, start, length);
        _placingCaret = true;
        try
        {
            if (start == end) { editor.ClearSelection(); editor.CaretIndex = start; }
            else { editor.SelectionStart = start; editor.SelectionEnd = end; }
        }
        finally { _placingCaret = false; }
    }
    // The caret moved (arrow keys, Home/End, a click): when it is now in another token, the list is asked again for that one.
    // Typing is not this path: TextChanged repopulates first, so the token then is the one just listed.
    private void OnEditorProperty(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.CaretIndexProperty && e.Property != TextBox.SelectionStartProperty && e.Property != TextBox.SelectionEndProperty) return;
        if (CaretToken == null || _publishing || _placingCaret || _navigationCaret != null || _ended || _suppressed || Candidates == null || !IsKeyboardFocusWithin) return;
        int revision = _revision;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CaretToken is not { } token || _ended || _suppressed || revision != _revision || !IsKeyboardFocusWithin) return;
            (int start, int end) = EditorSelection;
            if (!Equals(token(Text ?? "", start, end), _populatedToken)) _ = PopulateAsync();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }
    private void OnCommit(object? sender, RoutedEventArgs e)
    {
        // The pointer gesture owns its commit; the adapter must not accept on press or twice.
        if (!_pointerInPopup) AcceptCandidate();
    }
    private void AcceptCandidate() => Accept(IdentitySuggestions.Resolve(Text, HighlightedCandidate, ShownCandidates()));
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
        // A completed token in the middle of the draft keeps the caret right behind it.
        if (item != null) PlaceCaret(item.Caret ?? item.Value.Length, item.Caret ?? item.Value.Length);
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
        if (e.Key == Key.Enter && IsComposing) { e.Handled = true; return; }
        if (e.Key == Key.Escape)
        {
            bool listWasOpen = IsDropDownOpen;
            Dismiss();
            if (listWasOpen || !ReleaseEscape) e.Handled = true;
            return;
        }
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
        if ((e.Key == Key.Down || e.Key == Key.Up) && IsDropDownOpen)
        {
            // The user chooses now: the preselection is over, and the caret stays where it is (restored after the list's handling).
            _preselected = null;
            _navigationCaret = EditorSelection;
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
        PlaceCaret(item.Caret ?? item.Value.Length, item.Caret ?? item.Value.Length);
    }
    internal void Dismiss()
    {
        ++_revision; _suppressed = true; _preselected = null;
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
            IdentitySuggestion[] shown = items.Take(VisibleLimit).ToArray();
            ItemsSource = shown;
            PopulateComplete();
            IsDropDownOpen = items.Length > 0;
            _preselected = null;
            // The one real match is highlighted at once (the list's selected item, so a screen reader names it); the caret stays.
            if (IsDropDownOpen && SelectionAdapter != null && shown.FirstOrDefault(item => item.Preselect) is { } preselect)
            {
                (int start, int end) = EditorSelection;
                _preselected = preselect;
                SelectionAdapter.SelectedItem = preselect;
                if (EditorSelection != (start, end)) PlaceCaret(start, end);
            }
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
        (int caretStart, int caretEnd) = EditorSelection;
        _populatedToken = CaretToken?.Invoke(text, caretStart, caretEnd);
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
                    if (Publish(items)) AutomationProperties.SetHelpText(this, Texts.Count(CountHelpKey, items.Length, items.Length, VisibleLimit));
                }, Avalonia.Threading.DispatcherPriority.Input);
            }
            AutomationProperties.SetHelpText(this, Texts.Count(CountHelpKey, items.Length, items.Length, VisibleLimit));
            LoadCompleted?.Invoke(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_ended && revision == _revision && !token.IsCancellationRequested) LoadCompleted?.Invoke(error);
        }
    }
}
