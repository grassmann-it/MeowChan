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
        ItemTemplate = new FuncDataTemplate<IdentitySuggestion>((item, _) => new TextBlock
        {
            Text = item?.Label, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            [ToolTip.TipProperty] = item?.Label,
            [AutomationProperties.NameProperty] = item?.Label
        });
        Populating += (_, e) => { e.Cancel = true; };
        // Focus entering a suggestion (the popup is a logical child) is not the editor gaining focus:
        // repopulating then would clear the list under the click and fault the selection update.
        GotFocus += (_, e) =>
        {
            if (_restoringFocus || _pointerInPopup || SuggestionAt(e.Source) != null) return;
            _suppressed = false; _ = PopulateAsync();
        };
        LostFocus += (_, e) =>
        {
            if (_pointerInPopup) return;
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
    private void OnCommit(object? sender, RoutedEventArgs e) => AcceptCandidate();
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
    // A mouse click must not depend on the popup adapter's Commit or on the editor keeping focus:
    // the popup may take focus on press, which would otherwise dismiss the list before the release.
    private SelectingItemsControl? _popupList;
    private bool _pointerInPopup;
    private bool _restoringFocus;
    private void AttachPopupPointer(SelectingItemsControl? list)
    {
        if (_popupList != null)
        {
            _popupList.RemoveHandler(PointerPressedEvent, OnPopupPressed);
            _popupList.RemoveHandler(PointerReleasedEvent, OnPopupReleased);
        }
        _popupList = list;
        if (list == null) return;
        list.AddHandler(PointerPressedEvent, OnPopupPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(PointerReleasedEvent, OnPopupReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
    }
    private static IdentitySuggestion? SuggestionAt(object? source)
        => (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as IdentitySuggestion;
    private void OnPopupPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(null).Properties.IsLeftButtonPressed && SuggestionAt(e.Source) != null) _pointerInPopup = true;
    }
    private void OnPopupReleased(object? sender, PointerReleasedEventArgs e)
    {
        bool pressedHere = _pointerInPopup;
        _pointerInPopup = false;
        if (!pressedHere || e.InitialPressMouseButton != MouseButton.Left) return;
        if (SuggestionAt(e.Source) is not { } item) return;
        Accept(item);
        // The press moved focus into the popup, which Accept just closed; hand it back to the editor
        // without letting the focus event reopen the list for the accepted value.
        if (this.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { IsKeyboardFocusWithin: false } editor)
        {
            _restoringFocus = true;
            try { editor.Focus(); } finally { _restoringFocus = false; }
        }
    }
    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && this.GetVisualDescendants().OfType<TextPresenter>().Any(x => !string.IsNullOrEmpty(x.PreeditText)))
        { e.Handled = true; return; }
        if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; return; }
        if (e.Key == Key.Tab) { Dismiss(); return; }
        if ((e.Key == Key.Down || e.Key == Key.Up) && !IsDropDownOpen && !_ended && Candidates != null)
        {
            // Escape or a prior accept closes the list; explicit navigation reopens it
            // without changing the draft, applying a search or accepting anything.
            _suppressed = false; _ = PopulateAsync(); e.Handled = true; return;
        }
        if (e.Key != Key.Enter || !IsDropDownOpen) return;
        if (ReleaseUnselectedEnter && !(ItemsSource?.Cast<object>().Contains(SelectionAdapter?.SelectedItem ?? SelectedItem) ?? false))
        { Dismiss(); return; }
        // Consume the popup's Enter before a parent search command can observe it.
        AcceptCandidate();
        e.Handled = true;
    }
    internal void Dismiss()
    {
        ++_revision; _suppressed = true; _pointerInPopup = false;
        _request?.Cancel(); _request?.Dispose(); _request = null;
        IsDropDownOpen = false;
        if (!ClearItems()) Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_suppressed) ClearItems(); }, Avalonia.Threading.DispatcherPriority.Input);
    }
    // Scope changes invalidate the accepted pair without rewriting free/editor text.
    internal void InvalidateIdentityContext() { AcceptedCandidate = null; Dismiss(); }
    internal void EndSuggestions() { _ended = true; Dismiss(); AcceptedCandidate = null; Candidates = null; }
    internal void RestartSuggestions() { _suppressed = false; _ = PopulateAsync(); }
    internal void RefreshSuggestions() { if (!_suppressed && IsKeyboardFocusWithin) _ = PopulateAsync(); }
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
            ItemsSource = items.Take(IdentitySuggestions.VisibleLimit).ToArray();
            PopulateComplete();
            IsDropDownOpen = items.Length > 0;
            return true;
        }
        catch (InvalidOperationException) { return false; }
        finally { _publishing = false; }
    }
    internal async Task PopulateAsync()
    {
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
                    if (Publish(items)) AutomationProperties.SetHelpText(this, Texts.T("suggestions.count", items.Length));
                }, Avalonia.Threading.DispatcherPriority.Input);
            }
            AutomationProperties.SetHelpText(this, Texts.T("suggestions.count", items.Length));
            LoadCompleted?.Invoke(null);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!_ended && revision == _revision && !token.IsCancellationRequested) LoadCompleted?.Invoke(error);
        }
    }
}
