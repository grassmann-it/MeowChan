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
        GotFocus += (_, _) => { _suppressed = false; _ = PopulateAsync(); };
        LostFocus += (_, e) =>
        {
            if (e.NewFocusedElement is Control next && (this.IsVisualAncestorOf(next)
                || next.DataContext is IdentitySuggestion
                || next.GetVisualAncestors().OfType<ListBoxItem>().Any(x => x.DataContext is IdentitySuggestion))) return;
            Dismiss();
        };
        TextChanged += (_, _) => { if (!_publishing) { AcceptedCandidate = null; _suppressed = false; _ = PopulateAsync(); } };
        AddHandler(KeyDownEvent, OnInputKey, RoutingStrategies.Tunnel);
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (SelectionAdapter != null) SelectionAdapter.Commit -= OnCommit;
        base.OnApplyTemplate(e);
        if (SelectionAdapter != null) SelectionAdapter.Commit += OnCommit;
    }
    private void OnCommit(object? sender, RoutedEventArgs e) => AcceptCandidate();
    private void AcceptCandidate()
    {
        if (_ended || _suppressed) return;
        if ((SelectionAdapter?.SelectedItem ?? SelectedItem) is IdentitySuggestion item)
        {
            _publishing = true;
            try { AcceptedCandidate = item; SetCurrentValue(TextProperty, item.Value); CandidateAccepted?.Invoke(item); }
            finally { _publishing = false; }
        }
        Dismiss();
    }
    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && this.GetVisualDescendants().OfType<TextPresenter>().Any(x => !string.IsNullOrEmpty(x.PreeditText)))
        { e.Handled = true; return; }
        if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; return; }
        if (e.Key == Key.Tab) { Dismiss(); return; }
        if (e.Key != Key.Enter || !IsDropDownOpen) return;
        // Consume the popup's Enter before a parent search command can observe it.
        AcceptCandidate();
        e.Handled = true;
    }
    internal void Dismiss()
    {
        ++_revision; _suppressed = true;
        _request?.Cancel(); _request?.Dispose(); _request = null;
        IsDropDownOpen = false; ItemsSource = Array.Empty<IdentitySuggestion>();
    }
    internal void EndSuggestions() { _ended = true; Dismiss(); AcceptedCandidate = null; Candidates = null; }
    internal void RestartSuggestions() { _suppressed = false; _ = PopulateAsync(); }
    internal void RefreshSuggestions() { if (!_suppressed && IsKeyboardFocusWithin) _ = PopulateAsync(); }
    internal async Task PopulateAsync()
    {
        ++_revision;
        int revision = _revision;
        _request?.Cancel(); _request?.Dispose();
        _request = new CancellationTokenSource();
        var token = _request.Token;
        IsDropDownOpen = false; ItemsSource = Array.Empty<IdentitySuggestion>();
        if (_ended || _suppressed || !IsKeyboardFocusWithin || Candidates == null) return;
        string text = Text ?? "";
        try
        {
            var items = await Candidates(text, token);
            if (_ended || token.IsCancellationRequested || revision != _revision || !IsKeyboardFocusWithin || _suppressed || Text != text) return;
            _publishing = true;
            try
            {
                ItemsSource = items.Take(IdentitySuggestions.VisibleLimit).ToArray();
                PopulateComplete();
                IsDropDownOpen = items.Length > 0;
            }
            finally { _publishing = false; }
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
