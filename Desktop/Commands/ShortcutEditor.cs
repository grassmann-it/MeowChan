using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.Desktop.Commands;

/// <summary>Settings-owned draft. Capture never persists or publishes bindings.</summary>
public sealed class ShortcutEditor : UserControl
{
    private readonly StackPanel _root = new() { Spacing = 8 };
    private readonly TextBlock _heading = new() { Name = "SettingsShortcutsHeading", FontSize = 18, FontWeight = FontWeight.SemiBold };
    public Control SectionHeading => _heading;
    private readonly TextBox _search = new();
    private readonly ListBox _actions = new() { Height = 210 };
    private readonly StackPanel _detail = new() { Spacing = 8 };
    private readonly TextBlock _errors = new() { TextWrapping = TextWrapping.Wrap };
    private readonly KeyGestureInput _capture = new() { MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _explicit = new();
    private readonly TextBlock _pendingLabel = new() { TextWrapping = TextWrapping.Wrap };
    private IDisposable? _captureLease;
    private ShortcutGesture? _pending;
    private string? _selected;
    private long _validationFocusRequest;
    public ShortcutBindingsSection Draft { get; private set; } = new();
    public bool IsDirty { get; private set; }
    private static string T(string en, string de) => Texts.Language.StartsWith("de", StringComparison.OrdinalIgnoreCase) ? de : en;
    public ShortcutEditor()
    {
        Name = "ShortcutEditor";
        Content = _root;
        // TextChanged is posted asynchronously. A validation search reset must
        // finish rebuilding selection before its guarded error-focus request.
        _search.TextChanging += (_, _) => RefreshList();
        _actions.SelectionChanged += (_, _) => { _validationFocusRequest++; StopCapture(); _pending = null; _capture.Gesture = null; _pendingLabel.Text = ""; _selected = (_actions.SelectedItem as ListBoxItem)?.Tag as string; RefreshDetail(); };
        _capture.AcceptableKeys = Enum.GetValues<Key>().Where(k => k is not Key.Tab and not Key.Escape).ToList();
        _capture.AddHandler(KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel);
        _capture.PropertyChanged += (_, e) => { if (e.Property == KeyGestureInput.GestureProperty) ReadCapture(); };
        _capture.LostFocus += (_, _) => StopCapture();
        _explicit.IsCheckedChanged += (_, _) => ReadCapture();
        DetachedFromVisualTree += (_, _) => { _validationFocusRequest++; StopCapture(); };
        AttachedToVisualTree += (_, _) => { Texts.Changed += OnLanguageChanged; Localize(); };
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
        AutomationProperties.SetLiveSetting(_errors, AutomationLiveSetting.Assertive);
        Localize();
    }
    private void OnLanguageChanged(object? sender, EventArgs e) => Localize();
    public void Load(ShortcutBindingsSection section)
    { StopCapture(); Draft = section.Clone(); IsDirty = false; Localize(); }
    public bool ValidateDraft()
    {
        StopCapture();
        var snapshot = EffectiveShortcutBindings.Build(Draft);
        ShowIssues(snapshot.Issues);
        var issue = snapshot.Issues.FirstOrDefault(i => i.BlocksSave);
        if (issue == null) return true;
        _search.Text = "";
        _selected = issue.CommandId;
        RefreshList();
        // Settings disables the editor while validating. Reveal and focus the
        // actual error after that synchronous busy scope ends, not a disabled list.
        long request = ++_validationFocusRequest;
        ShortcutBindingsSection draft = Draft;
        string? selected = _selected;
        string? message = _errors.Text;
        TopLevel? top = TopLevel.GetTopLevel(this);
        var focusAtRequest = top?.FocusManager?.GetFocusedElement();
        TraceFocus("queued");
        // SelectingItemsControl posts its selected-row ScrollIntoView at Default
        // during RefreshList. Run after those queued reveals so they cannot scroll
        // the Settings viewport back to the row after the error receives focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _validationFocusRequest || !ReferenceEquals(Draft, draft) || _selected != selected
                || _errors.Text != message || top == null || !ReferenceEquals(TopLevel.GetTopLevel(this), top)
                || !IsEffectivelyVisible || !IsEffectivelyEnabled || top is Window { IsActive: false }
                || !ReferenceEquals(top.FocusManager?.GetFocusedElement(), focusAtRequest))
            {
                TraceFocus("skipped");
                return;
            }
            top.UpdateLayout();
            _errors.BringIntoView();
            bool accepted = _errors.Focus(NavigationMethod.Directional);
            TraceFocus("attempted", accepted);
        }, DispatcherPriority.Default);
        return false;

        void TraceFocus(string phase, bool? accepted = null)
        {
            var currentFocus = top?.FocusManager?.GetFocusedElement();
            Serilog.Log.Debug("Shortcut validation focus {@State}", new
            {
                phase, request, currentRequest = _validationFocusRequest,
                busy = this.GetVisualAncestors().OfType<SettingsEditor>().FirstOrDefault()?.IsBusy,
                sameDraft = ReferenceEquals(Draft, draft), selected, currentSelected = _selected,
                sameMessage = _errors.Text == message,
                attached = top != null, sameOwner = ReferenceEquals(TopLevel.GetTopLevel(this), top),
                visible = IsEffectivelyVisible, enabled = IsEnabled, effectivelyEnabled = IsEffectivelyEnabled,
                active = (top as Window)?.IsActive,
                sameFocus = ReferenceEquals(currentFocus, focusAtRequest),
                requestedFocus = focusAtRequest?.GetType().Name, currentFocus = currentFocus?.GetType().Name,
                errorAttached = TopLevel.GetTopLevel(_errors) != null,
                errorVisible = _errors.IsEffectivelyVisible, errorEnabled = _errors.IsEffectivelyEnabled,
                errorFocusable = _errors.Focusable, errorBounds = _errors.Bounds,
                accepted, errorFocused = _errors.IsFocused
            });
        }
    }
    private void Localize()
    {
        _validationFocusRequest++;
        _root.Children.Clear();
        _heading.Text = Texts.T("shortcuts.title");
        _root.Children.Add(_heading);
        _root.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = T("Bindings apply after Save in every view. Text editing, Tab and Escape remain available. System shortcuts may intercept keys.", "Belegungen gelten nach Speichern in jeder Ansicht. Texteingabe, Tab und Escape bleiben verfügbar. Systemkürzel können Tasten abfangen.") });
        _search.PlaceholderText = T("Search action, context or shortcut", "Aktion, Bereich oder Kürzel suchen");
        AutomationProperties.SetName(_search, _search.PlaceholderText?.ToString());
        AutomationProperties.SetName(_actions, T("Actions and shortcuts", "Aktionen und Tastenkürzel"));
        _root.Children.Add(_search); _root.Children.Add(_actions); _root.Children.Add(_detail); _root.Children.Add(_errors);
        var reset = Button(T("Reset all known actions", "Alle bekannten Aktionen zurücksetzen"), () => { Draft = Draft.ResetKnown(); Changed(); });
        reset.IsEnabled = Draft.IsEditable;
        _root.Children.Add(reset);
        RefreshList();
    }
    private void RefreshList()
    {
        string query = _search.Text?.Trim() ?? "";
        var snapshot = EffectiveShortcutBindings.Build(Draft);
        var items = CommandCatalog.All.Select(c => new ListBoxItem
        {
            Tag = c.Id,
            Content = new TextBlock { Text = c.Label + " · " + string.Join(", ", c.ExecutionContexts) + " · " + (snapshot.Display(c.Id) ?? T("Unbound", "Unbelegt")) + " · " + (!c.IsImplemented || c.Contexts.All(x => x.EndsWith(":planned", StringComparison.Ordinal)) ? T("Unavailable", "Nicht verfügbar") : Draft.HasOverride(c.Id) ? T("Custom", "Benutzer") : T("Default", "Standard")), TextWrapping = TextWrapping.Wrap }
        }).Where(i => query.Length == 0 || ((TextBlock)i.Content!).Text!.Contains(query, StringComparison.OrdinalIgnoreCase) || ((string)i.Tag!).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        string? selected = _selected;
        _actions.ItemsSource = items;
        _selected = selected;
        _actions.SelectedItem = items.FirstOrDefault(i => (string)i.Tag! == selected);
        ShowIssues(snapshot.Issues);
        RefreshDetail();
    }
    private IReadOnlyList<ShortcutGesture> DraftGestures(string id)
    {
        return EffectiveShortcutBindings.Build(Draft).RequestedGestures.TryGetValue(id, out var gestures) ? gestures : Array.Empty<ShortcutGesture>();
    }

    private void RefreshDetail()
    {
        _detail.Children.Clear();
        if (_selected == null || !CommandCatalog.TryGet(_selected, out var command)) return;
        _detail.IsEnabled = Draft.IsEditable && command!.IsImplemented;
        _detail.Children.Add(new TextBlock { Text = command!.Label + " · " + string.Join(", ", command.ExecutionContexts), TextWrapping = TextWrapping.Wrap });
        var gestures = DraftGestures(_selected);
        for (int i = 0; i < gestures.Count; i++)
        {
            int index = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = gestures[i].Display(EffectiveShortcutBindings.CurrentPlatform), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Button(T("Remove", "Entfernen"), () => { Draft = Draft.Set(_selected!, gestures.Where((_, n) => n != index).ToArray()); Changed(); }));
            _detail.Children.Add(row);
        }
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(Button(T("Record additional shortcut", "Weiteres Kürzel aufnehmen"), () =>
        { _validationFocusRequest++; StopCapture(); _pending = null; _capture.Gesture = null; _captureLease = ShortcutService.BeginCapture(); _capture.IsEnabled = true; _capture.Focus(); }));
        actions.Children.Add(Button(T("Clear binding", "Belegung entfernen"), () => { Draft = Draft.Set(_selected!, Array.Empty<ShortcutGesture>()); Changed(); }));
        actions.Children.Add(Button(T("Restore default", "Standard wiederherstellen"), () => { Draft = Draft.Set(_selected!, null); Changed(); }));
        _detail.Children.Add(actions);
        _capture.IsEnabled = false;
        AutomationProperties.SetName(_capture, T("Record shortcut", "Tastenkürzel aufnehmen"));
        AutomationProperties.SetHelpText(_capture, T("Escape cancels. Tab moves focus. Apply adds the recorded gesture to the draft.", "Escape bricht ab. Tab wechselt den Fokus. Übernehmen ergänzt die Geste im Entwurf."));
        _detail.Children.Add(_capture);
        _explicit.Content = T("Keep explicit Ctrl/Meta instead of Primary", "Ctrl/Meta ausdrücklich statt Primärtaste speichern");
        _detail.Children.Add(_explicit); _detail.Children.Add(_pendingLabel);
        var commit = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        commit.Children.Add(Button(T("Apply recording", "Aufnahme übernehmen"), () => ApplyCapture(false)));
        commit.Children.Add(Button(T("Take over conflicting gesture", "Kollidierende Geste übernehmen"), () => ApplyCapture(true)));
        commit.Children.Add(Button(T("Cancel recording", "Aufnahme abbrechen"), CancelCapture));
        _detail.Children.Add(commit);
    }
    private void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelCapture(); }
        else if (e.Key == Key.Tab) StopCapture();
        else if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt))
        { e.Handled = true; _pending = null; _pendingLabel.Text = T("AltGr / Ctrl+Alt text is not a shortcut.", "AltGr-/Ctrl+Alt-Text ist kein Tastenkürzel."); }
    }
    private void CancelCapture()
    {
        _validationFocusRequest++;
        _pending = null; _capture.Gesture = null; _pendingLabel.Text = "";
        // Transfer focus while capture still owns it. LostFocus also stops the
        // lease; the final call covers a detached/disabled editor with no target.
        FocusSelectedAction();
        StopCapture();
    }
    private void FocusSelectedAction()
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top == null || !IsEffectivelyVisible || !IsEffectivelyEnabled) return;
        if (_actions.SelectedItem is ListBoxItem item && item.Tag is string id && id == _selected
            && _actions.Items.OfType<ListBoxItem>().Any(current => ReferenceEquals(current, item)))
        {
            // The ListBox is not focusable; its selected real item is. It may be
            // virtualized or above the capture area in the outer settings scroll.
            _actions.ScrollIntoView(item);
            top.UpdateLayout();
            if (ReferenceEquals(TopLevel.GetTopLevel(item), top) && item.IsEffectivelyVisible && item.IsEffectivelyEnabled)
            {
                item.BringIntoView();
                if (item.Focus(NavigationMethod.Directional)) return;
            }
        }
        if (ReferenceEquals(TopLevel.GetTopLevel(_search), top) && _search.IsEffectivelyVisible && _search.IsEffectivelyEnabled)
        {
            _search.BringIntoView();
            _search.Focus(NavigationMethod.Directional);
        }
    }
    private void ReadCapture()
    {
        if (_capture.Gesture is not { } g) return;
        var modifiers = new List<string>();
        var primary = EffectiveShortcutBindings.CurrentPlatform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;
        foreach (var m in new[] { KeyModifiers.Control, KeyModifiers.Meta, KeyModifiers.Alt, KeyModifiers.Shift })
            if (g.KeyModifiers.HasFlag(m)) modifiers.Add(m == primary && _explicit.IsChecked != true ? "Primary" : m.ToString());
        _pending = new(g.Key.ToString(), modifiers.AsReadOnly());
        _pendingLabel.Text = EffectiveShortcutBindings.InvalidReason(_pending, EffectiveShortcutBindings.CurrentPlatform) ?? _pending.Display(EffectiveShortcutBindings.CurrentPlatform);
    }
    private void ApplyCapture(bool takeOver)
    {
        StopCapture();
        if (_selected == null || _pending == null) return;
        if (EffectiveShortcutBindings.InvalidReason(_pending, EffectiveShortcutBindings.CurrentPlatform) is string error)
        { _pendingLabel.Text = error; return; }
        var list = DraftGestures(_selected).ToList();
        if (list.Count >= 8) { _pendingLabel.Text = T("At most eight gestures per action.", "Höchstens acht Gesten pro Aktion."); return; }
        if (!list.Any(g => EffectiveShortcutBindings.Equivalent(g, _pending, EffectiveShortcutBindings.CurrentPlatform))) list.Add(_pending);
        if (takeOver)
            foreach (var command in CommandCatalog.All.Where(c => c.Id != _selected && EffectiveShortcutBindings.ContextsOverlap(c.Id, _selected)))
            {
                var other = DraftGestures(command.Id);
                var remaining = other.Where(g => !EffectiveShortcutBindings.Equivalent(g, _pending, EffectiveShortcutBindings.CurrentPlatform)).ToArray();
                if (remaining.Length != other.Count) Draft = Draft.Set(command.Id, remaining);
            }
        Draft = Draft.Set(_selected, list); _pending = null; Changed();
    }
    private void Changed() { IsDirty = true; RefreshList(); }
    private void StopCapture() { _captureLease?.Dispose(); _captureLease = null; _capture.IsEnabled = false; }
    private void ShowIssues(IReadOnlyList<ShortcutIssue> issues)
    {
        _errors.Focusable = issues.Any(issue => issue.BlocksSave);
        _errors.Text = string.Join("\n", issues.Select(i => (CommandCatalog.TryGet(i.CommandId, out var c) ? c!.Label : i.CommandId) + (i.OtherCommandId != null ? " ↔ " + CommandCatalog.Label(i.OtherCommandId) : "") + ": " + i.Message));
        AutomationProperties.SetHelpText(_capture, T("Escape cancels recording. Tab moves focus. ", "Escape bricht die Aufnahme ab. Tab wechselt den Fokus. ") + _errors.Text);
    }
    private static Button Button(string label, Action execute)
    { var b = new Button { Content = label }; AutomationProperties.SetName(b, label); b.Click += (_, _) => execute(); return b; }
}
