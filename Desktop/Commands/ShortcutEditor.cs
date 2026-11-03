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
    private readonly StackPanel _root = new() { Spacing = 12 };
    private readonly TextBlock _heading = new() { Name = "SettingsShortcutsHeading" };
    public Control SectionHeading => _heading;
    private readonly TextBox _search = new();
    private readonly ListBox _actions = new() { Height = 210 };
    private readonly StackPanel _detail = new() { Spacing = 8 };
    private readonly Border _detailFrame = new() { IsVisible = false };
    private readonly TextBlock _errors = new() { TextWrapping = TextWrapping.Wrap };
    private readonly KeyGestureInput _capture = new() { MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _explicit = new();
    private readonly TextBlock _pendingLabel = new() { TextWrapping = TextWrapping.Wrap };
    // Refresh presentation in place: removing a focused control releases capture.
    private readonly List<Action> _frameTexts = new();
    private readonly List<Action> _rowTexts = new();
    private readonly List<Action> _detailTexts = new();
    private Button _reset = null!;
    private Func<string>? _pendingText;
    private IDisposable? _captureLease;
    private ShortcutGesture? _pending;
    private string? _selected;
    private long _validationFocusRequest;
    public ShortcutBindingsSection Draft { get; private set; } = new();
    public bool IsDirty { get; private set; }
    public ShortcutEditor()
    {
        Name = "ShortcutEditor";
        _heading.Classes.Add("section-title");
        _errors.Classes.Add("workspace-error");
        _pendingLabel.Classes.Add("workspace-help");
        _detailFrame.Classes.Add("workspace-section");
        _detailFrame.Child = _detail;
        Content = _root;
        // TextChanged is posted asynchronously. A validation search reset must
        // finish rebuilding selection before its guarded error-focus request.
        _search.TextChanging += (_, _) => RefreshList();
        _actions.SelectionChanged += (_, _) => { _validationFocusRequest++; StopCapture(); _pending = null; _capture.Gesture = null; SetPendingText(null); _selected = (_actions.SelectedItem as ListBoxItem)?.Tag as string; RefreshDetail(); };
        _capture.AcceptableKeys = Enum.GetValues<Key>().Where(k => k is not Key.Tab and not Key.Escape).ToList();
        _capture.AddHandler(KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel);
        _capture.PropertyChanged += (_, e) => { if (e.Property == KeyGestureInput.GestureProperty) ReadCapture(); };
        _capture.LostFocus += (_, _) => StopCapture();
        _explicit.IsCheckedChanged += (_, _) => ReadCapture();
        DetachedFromVisualTree += (_, _) => { _validationFocusRequest++; StopCapture(); };
        AttachedToVisualTree += (_, _) => { Texts.Changed += OnLanguageChanged; Localize(); };
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
        AutomationProperties.SetLiveSetting(_errors, AutomationLiveSetting.Assertive);
        BuildFrame();
        RefreshList();
        Localize();
    }
    private void OnLanguageChanged(object? sender, EventArgs e) => Localize();
    public void Load(ShortcutBindingsSection section)
    {
        _validationFocusRequest++;
        StopCapture();
        // Reload discards the recording, independently of whether rebuilding the
        // list produces SelectionChanged (there may be no selected item at all).
        _pending = null;
        _capture.Gesture = null;
        SetPendingText(null);
        Draft = section.Clone();
        IsDirty = false;
        RefreshList();
        Localize();
    }
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
        // Do not rebuild/filter rows or detail here: even a remove/re-add of the
        // same input can lose focus, selection and the existing capture lease.
        _validationFocusRequest++;
        _heading.Text = Texts.T("shortcuts.title");
        _search.PlaceholderText = Texts.T("settings.shortcutsEditor.search");
        AutomationProperties.SetName(_search, _search.PlaceholderText?.ToString());
        AutomationProperties.SetName(_actions, Texts.T("settings.shortcutsEditor.actions"));
        foreach (var refresh in _frameTexts) refresh();
        foreach (var refresh in _rowTexts) refresh();
        foreach (var refresh in _detailTexts) refresh();
        _explicit.Content = Texts.T("settings.shortcutsEditor.keepExplicit");
        _pendingLabel.Text = _pendingText?.Invoke() ?? "";
        ShowIssues(EffectiveShortcutBindings.Build(Draft).Issues);
        AutomationProperties.SetName(_capture, Texts.T("settings.shortcutsEditor.record"));
        AutomationProperties.SetHelpText(_capture, Texts.T("settings.shortcutsEditor.recordHelp"));
    }
    private void BuildFrame()
    {
        _root.Children.Add(_heading);
        var hint = LocalizedText(() => Texts.T("settings.shortcutsEditor.hint"), _frameTexts);
        hint.Classes.Add("workspace-help");
        _root.Children.Add(hint);
        _root.Children.Add(_search);
        var tableHeader = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,*"), ColumnSpacing = 12, Margin = new Thickness(12, 0) };
        foreach (var (label, column) in new (Func<string>, int)[] { (() => Texts.T("settings.shortcutsEditor.action"), 0), (() => Texts.T("settings.shortcutsEditor.context"), 1), (() => Texts.T("settings.shortcutsEditor.effectiveBinding"), 2) })
        {
            var text = LocalizedText(label, _frameTexts);
            text.Classes.Add("workspace-field-label");
            Grid.SetColumn(text, column);
            tableHeader.Children.Add(text);
        }
        _root.Children.Add(tableHeader);
        _root.Children.Add(_actions);
        _root.Children.Add(_detailFrame);
        _root.Children.Add(_errors);
        _reset = Button(() => Texts.T("settings.shortcutsEditor.resetAll"), () => { Draft = Draft.ResetKnown(); Changed(); }, _frameTexts);
        _root.Children.Add(_reset);
    }
    private void RefreshList()
    {
        _rowTexts.Clear();
        _reset.IsEnabled = Draft.IsEditable;
        string query = _search.Text?.Trim() ?? "";
        var snapshot = EffectiveShortcutBindings.Build(Draft);
        var items = CommandCatalog.All.Select(command =>
        {
            string context = string.Join(", ", command.ExecutionContexts);
            string Binding() => snapshot.Display(command.Id) ?? Texts.T("settings.shortcutsEditor.unbound");
            string State() => !command.IsImplemented || command.Contexts.All(x => x.EndsWith(":planned", StringComparison.Ordinal))
                ? Texts.T("settings.shortcutsEditor.unavailable") : Draft.HasOverride(command.Id) ? Texts.T("settings.shortcutsEditor.custom") : Texts.T("settings.shortcutsEditor.default");
            string Accessible() => command.Label + " · " + context + " · " + Binding() + " · " + State();
            string accessible = Accessible();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,*"), ColumnSpacing = 12 };
            var label = new TextBlock { Text = command.Label, TextWrapping = TextWrapping.Wrap };
            row.Children.Add(label);
            var contextText = new TextBlock { Text = context };
            contextText.Classes.Add("workspace-meta");
            Grid.SetColumn(contextText, 1);
            row.Children.Add(contextText);
            var bindingColumn = new StackPanel { Spacing = 4 };
            var binding = new TextBlock { Text = Binding(), TextWrapping = TextWrapping.Wrap };
            bindingColumn.Children.Add(binding);
            var stateText = new TextBlock { Text = State() };
            stateText.Classes.Add("workspace-status");
            bindingColumn.Children.Add(stateText);
            Grid.SetColumn(bindingColumn, 2);
            row.Children.Add(bindingColumn);
            var item = new ListBoxItem { Tag = command.Id, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(item, accessible);
            void RefreshTexts()
            {
                label.Text = command.Label;
                binding.Text = Binding();
                stateText.Text = State();
                AutomationProperties.SetName(item, Accessible());
            }
            return (Item: item, Search: accessible + " · " + command.Id, Refresh: (Action)RefreshTexts);
        }).Where(row => query.Length == 0 || row.Search.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(row => { _rowTexts.Add(row.Refresh); return row.Item; }).ToArray();
        string? selected = _selected;
        ListBoxItem? selectedItem = items.FirstOrDefault(i => (string)i.Tag! == selected);
        _actions.ItemsSource = items;
        // Rebuilt rows replace every previous container. Native Once navigation
        // must not retain a removed row as its next tab destination. Only restore
        // an identity that is still represented by a real item in this result.
        KeyboardNavigation.SetTabOnceActiveElement(_actions, null);
        _selected = selectedItem?.Tag as string;
        _actions.SelectedItem = selectedItem;
        ShowIssues(snapshot.Issues);
        RefreshDetail();
    }
    private IReadOnlyList<ShortcutGesture> DraftGestures(string id)
    {
        return EffectiveShortcutBindings.Build(Draft).RequestedGestures.TryGetValue(id, out var gestures) ? gestures : Array.Empty<ShortcutGesture>();
    }

    private void RefreshDetail()
    {
        _detailTexts.Clear();
        _detail.Children.Clear();
        _detailFrame.IsVisible = _selected != null;
        if (_selected == null || !CommandCatalog.TryGet(_selected, out var command)) return;
        _detail.IsEnabled = Draft.IsEditable && command!.IsImplemented;
        var title = LocalizedText(() => command!.Label, _detailTexts);
        title.Classes.Add("section-title");
        _detail.Children.Add(title);
        var context = new TextBlock { Text = string.Join(", ", command.ExecutionContexts) };
        context.Classes.Add("workspace-meta");
        _detail.Children.Add(context);
        var gestures = DraftGestures(_selected);
        for (int i = 0; i < gestures.Count; i++)
        {
            int index = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = gestures[i].Display(EffectiveShortcutBindings.CurrentPlatform), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.remove"), () => { Draft = Draft.Set(_selected!, gestures.Where((_, n) => n != index).ToArray()); Changed(); }, _detailTexts));
            _detail.Children.Add(row);
        }
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.recordAdditional"), () =>
        { _validationFocusRequest++; StopCapture(); _pending = null; _capture.Gesture = null; SetPendingText(null); _captureLease = ShortcutService.BeginCapture(); _capture.IsEnabled = true; _capture.Focus(); }, _detailTexts));
        actions.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.clearBinding"), () => { Draft = Draft.Set(_selected!, Array.Empty<ShortcutGesture>()); Changed(); }, _detailTexts));
        actions.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.restoreDefault"), () => { Draft = Draft.Set(_selected!, null); Changed(); }, _detailTexts));
        _detail.Children.Add(actions);
        _capture.IsEnabled = false;
        AutomationProperties.SetName(_capture, Texts.T("settings.shortcutsEditor.record"));
        AutomationProperties.SetHelpText(_capture, Texts.T("settings.shortcutsEditor.recordHelp"));
        _detail.Children.Add(_capture);
        _explicit.Content = Texts.T("settings.shortcutsEditor.keepExplicit");
        _detail.Children.Add(_explicit); _detail.Children.Add(_pendingLabel);
        var commit = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        commit.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.applyRecording"), () => ApplyCapture(false), _detailTexts));
        commit.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.takeOver"), () => ApplyCapture(true), _detailTexts));
        commit.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.cancelRecording"), CancelCapture, _detailTexts));
        _detail.Children.Add(commit);
    }
    private void CaptureKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; CancelCapture(); }
        else if (e.Key == Key.Tab) StopCapture();
        else if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == (KeyModifiers.Control | KeyModifiers.Alt))
        {
            e.Handled = true;
            _pending = null;
            // Clear the source gesture as well: a later modifier-checkbox change
            // must not read the rejected AltGr gesture back into _pending.
            _capture.Gesture = null;
            SetPendingText(() => Texts.T("settings.shortcutsEditor.altGrText"));
        }
    }
    private void CancelCapture()
    {
        _validationFocusRequest++;
        _pending = null; _capture.Gesture = null; SetPendingText(null);
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
        var pending = new ShortcutGesture(g.Key.ToString(), modifiers.AsReadOnly());
        _pending = pending;
        SetPendingText(() => EffectiveShortcutBindings.InvalidReason(pending, EffectiveShortcutBindings.CurrentPlatform) ?? pending.Display(EffectiveShortcutBindings.CurrentPlatform));
    }
    private void ApplyCapture(bool takeOver)
    {
        StopCapture();
        if (_selected == null || _pending == null) return;
        if (EffectiveShortcutBindings.InvalidReason(_pending, EffectiveShortcutBindings.CurrentPlatform) is string error)
        { SetPendingText(() => error); return; }
        var list = DraftGestures(_selected).ToList();
        if (list.Count >= 8) { SetPendingText(() => Texts.T("settings.shortcutsEditor.gestureLimit")); return; }
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
        AutomationProperties.SetHelpText(_capture, Texts.T("settings.shortcutsEditor.issuesHelp") + _errors.Text);
    }
    private void SetPendingText(Func<string>? text)
    { _pendingText = text; _pendingLabel.Text = text?.Invoke() ?? ""; }
    private static TextBlock LocalizedText(Func<string> label, List<Action> refreshers)
    {
        var text = new TextBlock();
        void Refresh() => text.Text = label();
        refreshers.Add(Refresh); Refresh();
        return text;
    }
    private static Button Button(Func<string> label, Action execute, List<Action> refreshers)
    {
        var button = new Button();
        void Refresh() { string text = label(); button.Content = text; AutomationProperties.SetName(button, text); }
        refreshers.Add(Refresh); Refresh();
        button.Click += (_, _) => execute();
        return button;
    }
}
