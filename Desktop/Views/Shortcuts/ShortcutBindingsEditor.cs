using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.Desktop.Views.Shortcuts;

/// <summary>
/// The body of the shortcuts window (modals plan KS2): search, area, filters and the conflict chip on top, the grouped and
/// sortable table with keycaps, the edit bar of the selected action with the recorder, and the footer with the change
/// count, Undo, Reset all, Discard and Save. It edits a <see cref="ShortcutDraftSession"/> that the Settings card shares;
/// recording never persists or publishes, only Save does (through <see cref="SaveHandler"/>).
/// </summary>
public sealed class ShortcutBindingsEditor : UserControl
{
    internal const string SearchName = "ShortcutsSearch";
    internal const string ListName = "ShortcutsList";
    internal const string SaveName = "ShortcutsSave";
    internal const string StatusName = "ShortcutsStatus";
    internal const string ErrorsName = "ShortcutsErrors";
    internal const string HeaderName = "ShortcutsTableHeader";
    /// <summary>The columns of the header, the rows and the section rows: Action, Shortcut, Status.</summary>
    private const string TableColumns = "2*,1.4*,*";
    /// <summary>Every table row (action or section) is at least this high, so single-line rows all look alike.</summary>
    private const double TableRowHeight = 32;

    private readonly ShortcutDraftSession _session;
    private ShortcutHelpContext _context = ShortcutHelpContext.Settings;
    private ShortcutTableModel _model;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto,Auto,Auto") };
    private readonly MeowChan.Desktop.Controls.SearchBox _search = new() { Name = SearchName, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _area = new() { Name = "ShortcutsArea", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _assignedOnly = new() { Name = "ShortcutsAssignedOnly" };
    private readonly CheckBox _grouped = new() { Name = "ShortcutsGrouped", IsChecked = true };
    private readonly ToggleButton _conflicts = new() { Name = "ShortcutsConflicts" };
    // The conflict count as a status badge (the toggle around it only carries the "conflicts only" filter and its keyboard focus).
    private readonly Border _conflictBadge = new() { Classes = { "workspace-status" } };
    private readonly TextBlock _conflictText = new() { Classes = { "workspace-status" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap };
    // The context hints as one secondary-text strip: a plain line each, a leading key ("Esc  ...") as a keycap plus its text.
    private readonly StackPanel _notes = new() { Name = "ShortcutsNotes", Spacing = 4 };
    private readonly Border _notesHost = new() { Classes = { "workspace-section" }, Padding = new Thickness(12, 8), Margin = new Thickness(16, 0, 16, 12) };
    private IReadOnlyList<string> _noteLines = Array.Empty<string>();
    private readonly Grid _header = new() { ColumnDefinitions = new ColumnDefinitions(TableColumns), ColumnSpacing = 12 };
    private readonly ListBox _actions = new() { Name = ListName };
    private readonly TextBlock _empty = new() { Name = "ShortcutsEmpty", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _detail = new() { Spacing = 8 };
    private readonly Border _detailFrame = new() { IsVisible = false };
    private readonly TextBlock _noSelection = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _errors = new() { Name = ErrorsName, TextWrapping = TextWrapping.Wrap };
    private readonly KeyGestureInput _capture = new() { MinHeight = 36, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox _explicit = new();
    private readonly TextBlock _pendingLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _recording = new() { Name = "ShortcutsRecording", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _status = new() { Name = StatusName, TextWrapping = TextWrapping.Wrap };
    private readonly Border _closePrompt = new() { Name = "ShortcutsClosePrompt", IsVisible = false };
    // Refresh presentation in place: removing a focused control releases capture.
    private readonly List<Action> _frameTexts = new();
    private readonly List<Action> _rowTexts = new();
    private readonly List<Action> _detailTexts = new();
    private Button _reset = null!, _undo = null!, _discard = null!, _save = null!, _closeSave = null!;
    private Func<string>? _pendingText;
    private Func<string>? _statusText;
    private IDisposable? _captureLease;
    private ShortcutGesture? _pending;
    private bool _replaceRecording;
    private string? _selected;
    private long _validationFocusRequest;
    private long _validationViewportRequest;
    private Action? _cancelValidationViewportReveal;
    private bool _saving;
    private bool _syncingFilters;
    private Action<bool>? _closeDecision;

    public ShortcutBindingsEditor() : this(new ShortcutDraftSession(new ShortcutBindingsSection())) { }

    public ShortcutBindingsEditor(ShortcutDraftSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _model = BuildModel();
        Name = "ShortcutBindingsEditor";
        _errors.Classes.Add("workspace-error");
        _pendingLabel.Classes.Add("workspace-help");
        _empty.Classes.Add("workspace-help");
        _noSelection.Classes.Add("workspace-help");
        _status.Classes.Add("workspace-meta");
        _recording.Classes.Add("workspace-status-active");
        _detailFrame.Classes.Add("workspace-section");
        _detailFrame.Child = _detail;
        _conflicts.Classes.Add("status-toggle");
        _conflictBadge.Child = _conflictText;
        _conflicts.Content = _conflictBadge;
        _notesHost.Child = _notes;
        Content = _root;
        _search.TextChanging += (_, _) => { if (!_syncingFilters) RefreshList(); };
        _area.SelectionChanged += (_, _) => { if (!_syncingFilters) RefreshList(); };
        _assignedOnly.IsCheckedChanged += (_, _) => { if (!_syncingFilters) RefreshList(); };
        _grouped.IsCheckedChanged += (_, _) => { if (!_syncingFilters) RefreshList(); };
        _conflicts.IsCheckedChanged += (_, _) => { if (!_syncingFilters) RefreshList(); };
        _actions.SelectionChanged += (_, _) =>
        {
            _validationFocusRequest++; StopCapture(); _pending = null; _capture.Gesture = null; SetPendingText(null);
            _selected = (_actions.SelectedItem as ListBoxItem)?.Tag as string; RefreshDetail();
        };
        _actions.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Bubble);
        // Interaction audit 1.1.0: a double click on a command row records its new shortcut, like Enter (group rows fold on a single click).
        _actions.DoubleTapped += (_, e) =>
        {
            if (ShortcutService.IsCapturing || !MeowChan.Desktop.Controls.ItemActivation.IsItemDoubleClick(e, _actions)
                || MeowChan.Desktop.Controls.ItemActivation.ContainerAt(e.Source, _actions) is not ListBoxItem { Tag: string id }
                || id != _selected || !_detail.IsEnabled) return;
            e.Handled = true;
            StartRecording(replace: true);
        };
        AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Bubble);
        _capture.AcceptableKeys = Enum.GetValues<Key>().Where(k => k is not Key.Tab and not Key.Escape).ToList();
        _capture.AddHandler(KeyDownEvent, CaptureKey, RoutingStrategies.Tunnel);
        _capture.PropertyChanged += (_, e) => { if (e.Property == KeyGestureInput.GestureProperty) ReadCapture(); };
        _capture.LostFocus += (_, _) => StopCapture();
        _explicit.IsCheckedChanged += (_, _) => ReadCapture();
        _session.Changed += OnSessionChanged;
        _errors.LostFocus += (_, _) => CancelValidationViewportReveal();
        DetachedFromVisualTree += (_, _) => { _validationFocusRequest++; CancelValidationViewportReveal(); StopCapture(); };
        AttachedToVisualTree += (_, _) => { Texts.Changed += OnLanguageChanged; Localize(); };
        DetachedFromVisualTree += (_, _) => Texts.Changed -= OnLanguageChanged;
        AutomationProperties.SetLiveSetting(_errors, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(_recording, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        _syncingFilters = true;
        try { BuildFrame(); }
        finally { _syncingFilters = false; }
        RefreshList();
        Localize();
    }

    public ShortcutDraftSession Session => _session;
    public ShortcutBindingsSection Draft => _session.Draft;
    public bool IsDirty => _session.IsDirty;
    /// <summary>Saves a validated draft (the host's commit path). Without one the Save button stays disabled.</summary>
    internal Func<ShortcutBindingsSection, Task>? SaveHandler { get; set; }
    /// <summary>Whether saving is refused right now (the host is busy).</summary>
    internal Func<bool>? IsSaveBlocked { get; set; }
    internal bool IsSaving => _saving;
    internal bool IsRecording => _captureLease != null;
    internal ShortcutHelpContext Context => _context;
    internal ShortcutTableModel Model => _model;
    internal IReadOnlyList<string> CollapsedGroups => _collapsed.ToArray();
    internal string? SelectedCommandId => _selected;
    internal ListBox ActionList => _actions;
    internal TextBox SearchInput => _search;
    internal Button SaveButton => _save;
    internal TextBlock StatusText => _status;
    internal TextBlock ErrorText => _errors;
    /// <summary>The context hints shown above the table, as the localized lines (a leading key is drawn as a keycap, the line stays whole here).</summary>
    internal IReadOnlyList<string> NoteLines => _noteLines;
    /// <summary>The bound commands the opening context offers, in the words of the former F1 list (automation, tests).</summary>
    internal string PlainText => BuildPlainText();

    /// <summary>Replaces the draft from outside; recordings in progress are void.</summary>
    public void Load(ShortcutBindingsSection section) => _session.Load(section);

    /// <summary>Opens on <paramref name="context"/>: its area expanded and on top (others collapsed), its filter, no search text.</summary>
    internal void ApplyContext(ShortcutHelpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _syncingFilters = true;
        try
        {
            _search.Text = string.Empty;
            _assignedOnly.IsChecked = context.AssignedOnly;
            _conflicts.IsChecked = false;
            _grouped.IsChecked = true;
            _area.SelectedIndex = 0;
        }
        finally { _syncingFilters = false; }
        _collapsed.Clear();
        if (context.Area is { } area)
            foreach (string group in ShortcutGroups.Order.Where(g => g != area && context.ExpandedAreas?.Contains(g) != true)) _collapsed.Add(group);
        RefreshList();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Localize();

    private void OnSessionChanged(object? sender, ShortcutDraftChange change)
    {
        if (change == ShortcutDraftChange.Reloaded)
        {
            // A reload discards the recording, independently of whether rebuilding the
            // list produces SelectionChanged (there may be no selected item at all).
            _validationFocusRequest++;
            StopCapture();
            _pending = null;
            _capture.Gesture = null;
            SetPendingText(null);
        }
        else if (change == ShortcutDraftChange.Edited) SetStatus(null);
        RefreshList();
    }

    /// <summary>Unsubscribes from the shared session (the window closed).</summary>
    internal void Detach()
    {
        StopCapture();
        _session.Changed -= OnSessionChanged;
    }

    private ShortcutTableModel BuildModel() => ShortcutTableModel.Build(_session.Draft, _session.Snapshot(), _context.Area,
        _context.Preferred, _context.UnderMore);

    private ShortcutTableQuery CurrentQuery() => new(
        _search.Text ?? string.Empty,
        (_area.SelectedItem as ComboBoxItem)?.Tag as string,
        _assignedOnly.IsChecked == true,
        _conflicts.IsChecked == true,
        _grouped.IsChecked == true);

    private void Localize()
    {
        // Do not rebuild/filter rows or detail here: even a remove/re-add of the
        // same input can lose focus, selection and the existing capture lease.
        CancelValidationViewportReveal();
        long request = ++_validationFocusRequest;
        // Record ownership/visibility BEFORE changing text. A translated earlier
        // row can move the still-focused error or the active capture's invalid
        // pending explanation below the viewport without changing focus.
        QueueValidationViewportReveal(request);
        _model = BuildModel();
        _search.PlaceholderText = Texts.T("settings.shortcutsEditor.search");
        AutomationProperties.SetName(_search, _search.PlaceholderText?.ToString());
        AutomationProperties.SetName(_actions, Texts.T("settings.shortcutsEditor.actions"));
        AutomationProperties.SetHelpText(_actions, Texts.T("shortcuts.window.keysHelp"));
        AutomationProperties.SetName(_area, Texts.T("shortcuts.window.area"));
        foreach (var refresh in _frameTexts) refresh();
        foreach (var refresh in _rowTexts) refresh();
        foreach (var refresh in _detailTexts) refresh();
        RefreshConflictChip();
        RefreshNotes();
        _explicit.Content = Texts.T("settings.shortcutsEditor.keepExplicit");
        _pendingLabel.Text = _pendingText?.Invoke() ?? "";
        _status.Text = _statusText?.Invoke() ?? StatusSummary();
        _recording.Text = Texts.T("shortcuts.window.recording");
        AutomationProperties.SetHelpText(_capture, Texts.T("settings.shortcutsEditor.recordHelp"));
        // Keep the freshly localized issue help; generic recorder help must not overwrite it.
        ShowIssues(_session.Issues);
        AutomationProperties.SetName(_capture, Texts.T("settings.shortcutsEditor.record"));
    }

    private void CancelValidationViewportReveal()
    {
        ++_validationViewportRequest;
        Action? cleanup = _cancelValidationViewportReveal;
        _cancelValidationViewportReveal = null;
        cleanup?.Invoke();
    }

    private void QueueValidationViewportReveal(long validationRequest)
    {
        if (TopLevel.GetTopLevel(this) is not Window top
            || top.FocusManager?.GetFocusedElement() is not Control focus) return;
        bool pendingCapture = !OwnsValidationFocus(top);
        Control message = pendingCapture ? _pendingLabel : _errors;
        if (pendingCapture && (!OwnsInvalidCaptureFocus(top, focus) || !ControlInViewport(top, _capture))) return;
        if (!ControlInViewport(top, message)) return;
        IDisposable? captureLease = _captureLease;
        ShortcutGesture? pending = _pending;
        KeyGesture? gesture = _capture.Gesture;
        Func<string>? pendingText = _pendingText;
        bool OwnsFocus() => pendingCapture
            ? OwnsInvalidCaptureFocus(top, focus) && ReferenceEquals(_captureLease, captureLease)
                && ReferenceEquals(_pending, pending) && ReferenceEquals(_capture.Gesture, gesture)
                && ReferenceEquals(_pendingText, pendingText)
            : OwnsValidationFocus(top);
        long viewportRequest = _validationViewportRequest;
        ShortcutBindingsSection draft = _session.Draft;
        string? selected = _selected;
        object? selectedItem = _actions.SelectedItem;
        ScrollViewer[] scrolls = message.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
        Vector[] offsets = scrolls.Select(scroll => scroll.Offset).ToArray();
        // Deactivation revokes the request even if this window reactivates before
        // dispatch. LostFocus/detach cancel too, including focus-away-and-back.
        void Deactivated(object? sender, EventArgs e) => CancelValidationViewportReveal();
        void FocusLost(object? sender, RoutedEventArgs e) => CancelValidationViewportReveal();
        Action cleanup = () => { top.Deactivated -= Deactivated; focus.LostFocus -= FocusLost; };
        top.Deactivated += Deactivated;
        focus.LostFocus += FocusLost;
        _cancelValidationViewportReveal = cleanup;
        bool StillOwned()
        {
            if (validationRequest != _validationFocusRequest || viewportRequest != _validationViewportRequest
                || !ReferenceEquals(_session.Draft, draft) || _selected != selected
                || !ReferenceEquals(_actions.SelectedItem, selectedItem) || !OwnsFocus()) return false;
            var currentScrolls = message.GetVisualAncestors().OfType<ScrollViewer>().ToArray();
            // A user scroll while keeping error focus also supersedes the reveal.
            return currentScrolls.Length == scrolls.Length && currentScrolls.Select((scroll, index) =>
                ReferenceEquals(scroll, scrolls[index]) && scroll.Offset == offsets[index]).All(same => same);
        }
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!StillOwned()) return;
                // All synchronous Texts.Changed subscribers have finished. Arrange
                // their translated labels, then recheck ownership against reentry.
                top.UpdateLayout();
                if (!StillOwned()) return;
                if (pendingCapture)
                {
                    if (!ControlInViewport(top, _pendingLabel) || !ControlInViewport(top, _capture))
                    {
                        // Preserve BOTH the focused recorder and its invalid reason;
                        // never scroll the focus out merely to reveal its explanation.
                        if (_pendingLabel.TranslatePoint(default, _capture) is not Point origin
                            || VisibleViewport(top, _pendingLabel) is not Rect viewport) return;
                        Rect area = new Rect(_capture.Bounds.Size).Union(new Rect(origin, _pendingLabel.Bounds.Size));
                        if (area.Width <= viewport.Width && area.Height <= viewport.Height)
                            _capture.BringIntoView(area);
                    }
                }
                else if (!ControlInViewport(top, _errors)) _errors.BringIntoView();
                // No Focus(), recapture, retry loop or persistent LayoutUpdated hook.
            }
            finally
            {
                cleanup();
                if (ReferenceEquals(_cancelValidationViewportReveal, cleanup)) _cancelValidationViewportReveal = null;
            }
        }, DispatcherPriority.Loaded);
    }

    private bool OwnsValidationFocus(Window top) => top.IsVisible && top.IsActive
        && ReferenceEquals(TopLevel.GetTopLevel(this), top) && ReferenceEquals(TopLevel.GetTopLevel(_errors), top)
        && IsEffectivelyVisible && IsEffectivelyEnabled && _errors.IsEffectivelyVisible && _errors.IsEffectivelyEnabled
        && _errors.Focusable && _errors.IsFocused && ReferenceEquals(top.FocusManager?.GetFocusedElement(), _errors)
        && _captureLease == null && !ShortcutService.IsCapturing;

    private bool OwnsInvalidCaptureFocus(Window top, Control focus) => top.IsVisible && top.IsActive
        && ReferenceEquals(TopLevel.GetTopLevel(this), top) && ReferenceEquals(TopLevel.GetTopLevel(_capture), top)
        && ReferenceEquals(TopLevel.GetTopLevel(_pendingLabel), top) && ReferenceEquals(TopLevel.GetTopLevel(focus), top)
        && ReferenceEquals(_capture.Parent, _detail) && ReferenceEquals(_pendingLabel.Parent, _detail)
        && IsEffectivelyVisible && IsEffectivelyEnabled && _capture.IsEffectivelyVisible && _capture.IsEffectivelyEnabled
        && _pendingLabel.IsEffectivelyVisible && focus.IsEffectivelyVisible && focus.IsEffectivelyEnabled
        && focus.IsFocused && ReferenceEquals(top.FocusManager?.GetFocusedElement(), focus)
        && (ReferenceEquals(focus, _capture) || focus.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, _capture)))
        && _capture.IsKeyboardFocusWithin && _captureLease != null && ShortcutService.IsCapturing
        && _pendingText != null && _pending != null
        && EffectiveShortcutBindings.InvalidIssueReason(_pending, EffectiveShortcutBindings.CurrentPlatform) != null;

    private static bool ControlInViewport(Window top, Control target)
    {
        if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0
            || target.TranslatePoint(default, top) is not Point origin
            || VisibleViewport(top, target) is not Rect viewport) return false;
        Rect bounds = new(origin, target.Bounds.Size);
        return bounds.Left >= viewport.Left - 1 && bounds.Top >= viewport.Top - 1
            && bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;
    }

    private static Rect? VisibleViewport(Window top, Control target)
    {
        Rect viewport = new(default(Point), top.ClientSize);
        foreach (ScrollViewer scroll in target.GetVisualAncestors().OfType<ScrollViewer>())
        {
            if (scroll.TranslatePoint(default, top) is not Point point) return null;
            viewport = viewport.Intersect(new Rect(point, scroll.Bounds.Size));
        }
        return viewport;
    }

    private void BuildFrame()
    {
        // Toolbar (16 inset, 8 between its rows, 12 to what follows): search and area share the width half and half at the input
        // height; below, the filters on the left and the conflict badge on the right.
        var toolbar = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 12) };
        var find = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        _search.VerticalAlignment = VerticalAlignment.Center;
        _area.VerticalAlignment = VerticalAlignment.Center;
        find.Children.Add(_search);
        // A plain list as the source: Items.Add would bind a collection listener to the creating thread.
        _area.ItemsSource = new[] { AreaItem(null) }.Concat(ShortcutGroups.Order.Select(group => AreaItem(group))).ToArray();
        _area.SelectedIndex = 0;
        Grid.SetColumn(_area, 1);
        find.Children.Add(_area);
        toolbar.Children.Add(find);
        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 16, MinHeight = 32 };
        foreach (Control option in new Control[] { _assignedOnly, _grouped, _conflicts }) option.VerticalAlignment = VerticalAlignment.Center;
        Localized(_assignedOnly, () => Texts.T("shortcuts.window.assignedOnly"));
        options.Children.Add(_assignedOnly);
        Localized(_grouped, () => Texts.T("shortcuts.window.grouped"));
        Grid.SetColumn(_grouped, 1);
        options.Children.Add(_grouped);
        _conflicts.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_conflicts, 3);
        options.Children.Add(_conflicts);
        toolbar.Children.Add(options);
        _root.Children.Add(toolbar);

        Grid.SetRow(_notesHost, 1);
        _root.Children.Add(_notesHost);

        // Column heads: a plain header row of the table (text, no buttons; the order is fixed). Its inset is that of a row's content
        // (a list row's border and padding are 12 wide on both sides), so the labels sit above their columns.
        _header.Name = HeaderName;
        _header.MinHeight = 28;
        AutomationProperties.SetControlTypeOverride(_header, AutomationControlType.Header);
        foreach (var (index, label) in new (int, Func<string>)[]
        {
            (0, () => Texts.T("settings.shortcutsEditor.action")),
            (1, () => Texts.T("shortcuts.window.shortcut")),
            (2, () => Texts.T("shortcuts.window.status")),
        })
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Classes.Add("workspace-meta");
            AutomationProperties.SetControlTypeOverride(text, AutomationControlType.HeaderItem);
            void Refresh()
            {
                text.Text = label();
                AutomationProperties.SetName(text, text.Text);
            }
            _frameTexts.Add(Refresh);
            Refresh();
            Grid.SetColumn(text, index);
            _header.Children.Add(text);
        }
        var headerRule = new Border { Margin = new Thickness(16, 0, 16, 4), Padding = new Thickness(12, 0), Child = _header,
            BorderThickness = new Thickness(0, 0, 0, 1) };
        headerRule[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Border");
        Grid.SetRow(headerRule, 2);
        _root.Children.Add(headerRule);

        var listHost = new Grid { Margin = new Thickness(16, 0) };
        listHost.Children.Add(_actions);
        listHost.Children.Add(_empty);
        _empty.VerticalAlignment = VerticalAlignment.Top;
        _empty.Margin = new Thickness(12);
        Grid.SetRow(listHost, 3);
        _root.Children.Add(listHost);

        // Edit bar of the selected action.
        var editHost = new StackPanel { Spacing = 8 };
        editHost.Children.Add(_noSelection);
        editHost.Children.Add(_detailFrame);
        editHost.Children.Add(_recording);
        editHost.Children.Add(_errors);
        var editScroll = new ScrollViewer { Content = editHost, MaxHeight = 280, Margin = new Thickness(16, 8, 16, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(editScroll, 4);
        _root.Children.Add(editScroll);
        Localized(_noSelection, () => Texts.T("shortcuts.window.noSelection"));
        Localized(_empty, () => Texts.T("shortcuts.window.empty"));

        // Close confirmation (unsaved changes), inline above the footer.
        var promptText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Localized(promptText, () => Texts.T("shortcuts.window.closePrompt.text"));
        var keep = Button(() => Texts.T("shortcuts.window.keepEditing"), () => ResolveClose(null), _frameTexts);
        var discardClose = Button(() => Texts.T("shortcuts.window.discard"), () => ResolveClose(false), _frameTexts);
        var saveClose = _closeSave = Button(() => Texts.T("shortcuts.window.save"), () => ResolveClose(true), _frameTexts);
        saveClose.Classes.Add("primary");
        var promptRow = new DockPanel { LastChildFill = true };
        var promptButtons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        promptButtons.Children.Add(keep); promptButtons.Children.Add(discardClose); promptButtons.Children.Add(saveClose);
        DockPanel.SetDock(promptButtons, Dock.Right);
        promptRow.Children.Add(promptButtons);
        promptRow.Children.Add(promptText);
        _closePrompt.Child = promptRow;
        _closePrompt.Classes.Add("workspace-warning");
        _closePrompt.Margin = new Thickness(16, 0, 16, 8);
        Grid.SetRow(_closePrompt, 5);
        _root.Children.Add(_closePrompt);

        _undo = Button(() => Texts.T("shortcuts.window.undo"), () => _session.Undo(), _frameTexts);
        _reset = Button(() => Texts.T("settings.shortcutsEditor.resetAll"), () => _session.ResetKnown(), _frameTexts);
        _discard = Button(() => Texts.T("shortcuts.window.discard"), () => { StopCapture(); _session.Discard(); }, _frameTexts);
        _save = Button(() => Texts.T("shortcuts.window.save"), () => _ = SaveAsync(), _frameTexts);
        _save.Name = SaveName;
        _save.Classes.Add("primary");
        Border footer = DialogFooter.Build(_save, new Control[] { _undo, _reset, _discard }, _status);
        Grid.SetRow(footer, 6);
        _root.Children.Add(footer);
    }

    private ComboBoxItem AreaItem(string? group)
    {
        var item = new ComboBoxItem { Tag = group };
        void Refresh() => item.Content = group == null ? Texts.T("shortcuts.window.area.all") : ShortcutGroups.Label(group);
        _frameTexts.Add(Refresh);
        Refresh();
        return item;
    }

    private void RefreshConflictChip()
    {
        int count = _model.BlockingCount;
        _conflictText.Text = Texts.T("shortcuts.window.conflicts", count);
        AutomationProperties.SetName(_conflicts, Texts.T("shortcuts.window.conflicts", count));
        AutomationProperties.SetHelpText(_conflicts, Texts.T("shortcuts.window.conflicts.help"));
        ToolTip.SetTip(_conflicts, Texts.T("shortcuts.window.conflicts.help"));
        // Only a conflict colors the badge; with none it is the neutral status chip.
        _conflictBadge.Classes.Set("workspace-status-error", count > 0);
    }

    private void RefreshNotes()
    {
        List<string> notes = (_context.NoteKeys ?? Array.Empty<string>()).Select(key => Texts.T(key)).ToList();
        if (!ShortcutService.Current.SingleKeyShortcuts || !_session.Draft.SingleKeyShortcuts) notes.Add(Texts.T("help.singleKeysOff"));
        _noteLines = notes.Distinct().ToArray();
        _notes.Children.Clear();
        foreach (string line in _noteLines) _notes.Children.Add(NoteLine(line));
        _notesHost.IsVisible = _noteLines.Count > 0;
    }

    private static readonly System.Text.RegularExpressions.Regex KeyHint = new(@"^(?<key>\S{1,8})\s{2,}(?<text>\S.*)$",
        System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>One hint line; "Esc  Close ..." becomes a keycap (as in the table) and the rest as secondary text.</summary>
    private static Control NoteLine(string line)
    {
        var text = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Classes.Add("workspace-help");
        if (KeyHint.Match(line) is not { Success: true } match) return text;
        text.Text = match.Groups["text"].Value;
        var cap = new Border { Child = new TextBlock { Text = match.Groups["key"].Value }, VerticalAlignment = VerticalAlignment.Center };
        cap.Classes.Add(KeyCapText.CapClass);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        row.Children.Add(cap);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        AutomationProperties.SetName(row, line);
        return row;
    }

    private void RefreshList()
    {
        _rowTexts.Clear();
        _model = BuildModel();
        ShortcutTableQuery query = CurrentQuery();
        // A search or the conflict filter shows every hit, also in collapsed groups.
        bool showAll = query.Text.Trim().Length > 0 || query.ConflictsOnly || query.Area != null;
        IReadOnlyList<ShortcutTableGroup> groups = _model.Query(query);
        var items = new List<ListBoxItem>();
        foreach (ShortcutTableGroup group in groups)
        {
            bool collapsed = !showAll && query.Grouped && _collapsed.Contains(group.Key);
            if (query.Grouped) items.Add(GroupHeader(group, collapsed));
            if (collapsed) continue;
            foreach (ShortcutTableRow row in group.Rows) items.Add(Row(row));
        }
        string? selected = _selected;
        ListBoxItem? selectedItem = selected == null ? null : items.FirstOrDefault(i => i.Tag as string == selected);
        _actions.ItemsSource = items;
        // Rebuilt rows replace every previous container. Native Once navigation
        // must not retain a removed row as its next tab destination. Only restore
        // an identity that is still represented by a real item in this result.
        KeyboardNavigation.SetTabOnceActiveElement(_actions, null);
        _selected = selectedItem?.Tag as string;
        _actions.SelectedItem = selectedItem;
        _empty.IsVisible = groups.Count == 0;
        RefreshConflictChip();
        RefreshNotes();
        ShowIssues(_session.Issues);
        RefreshDetail();
        RefreshFooter();
    }

    private ListBoxItem GroupHeader(ShortcutTableGroup group, bool collapsed)
    {
        var chevron = new Border { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = collapsed ? null : new RotateTransform(90) };
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        title.Classes.Add("section-title");
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, MinHeight = TableRowHeight };
        content.Children.Add(chevron);
        content.Children.Add(title);
        var item = new ListBoxItem { Tag = new GroupTag(group.Key), Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        item.Classes.Add("shortcuts-group");
        item.AttachedToVisualTree += (_, _) => { if (chevron.Child == null) chevron.Child = CommandBindings.NamedIcon("chevron-right", 16); };
        void Refresh()
        {
            string label = ShortcutGroups.Label(group.Key);
            if (group.IsCurrent) label = Texts.T("shortcuts.group.current", label);
            title.Text = Texts.T("shortcuts.group.header", label, group.Rows.Count);
            AutomationProperties.SetName(item, title.Text + ", " + Texts.T(collapsed ? "shortcuts.group.collapsed" : "shortcuts.group.expanded"));
        }
        _rowTexts.Add(Refresh);
        Refresh();
        item.Tapped += (_, _) => ToggleGroup(group.Key);
        return item;
    }

    private sealed record GroupTag(string Key);

    private void ToggleGroup(string key)
    {
        if (!_collapsed.Remove(key)) _collapsed.Add(key);
        object? keep = _selected;
        RefreshList();
        // Keep the keyboard on the header that was toggled.
        if (keep == null && _actions.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is GroupTag tag && tag.Key == key) is { } header)
        {
            _actions.SelectedItem = header;
            Dispatcher.UIThread.Post(() => { if (header.IsAttachedToVisualTree()) header.Focus(); }, DispatcherPriority.Loaded);
        }
    }

    private ListBoxItem Row(ShortcutTableRow row)
    {
        ShortcutPlatform platform = _model.Platform;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(TableColumns), ColumnSpacing = 12, MinHeight = TableRowHeight };
        var labelColumn = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = row.Label, TextWrapping = TextWrapping.Wrap };
        labelColumn.Children.Add(label);
        var meta = new TextBlock { TextWrapping = TextWrapping.Wrap };
        meta.Classes.Add("workspace-meta");
        labelColumn.Children.Add(meta);
        grid.Children.Add(labelColumn);

        var gestures = new WrapPanel { ItemSpacing = 8, LineSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var caps = row.Gestures.Select(g => new KeyCapText(g, platform)).ToArray();
        foreach (KeyCapText cap in caps) gestures.Children.Add(cap);
        var unbound = new TextBlock { VerticalAlignment = VerticalAlignment.Center, IsVisible = !row.IsBound };
        unbound.Classes.Add("workspace-meta");
        gestures.Children.Add(unbound);
        Grid.SetColumn(gestures, 1);
        grid.Children.Add(gestures);

        var statusColumn = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var state = new TextBlock();
        state.Classes.Add("workspace-status");
        statusColumn.Children.Add(state);
        var conflict = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = row.Conflicts.Count > 0 || row.Problems.Count > 0 };
        conflict.Classes.Add(row.HasBlockingConflict ? "workspace-status-error" : "workspace-status-warning");
        statusColumn.Children.Add(conflict);
        Grid.SetColumn(statusColumn, 2);
        grid.Children.Add(statusColumn);

        var item = new ListBoxItem { Tag = row.CommandId, Content = grid, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        if (row.HasBlockingConflict) item.Classes.Add("conflict");
        string State() => row.Status switch
        {
            ShortcutRowStatus.Unavailable => Texts.T("settings.shortcutsEditor.unavailable"),
            ShortcutRowStatus.Custom => Texts.T("settings.shortcutsEditor.custom"),
            _ => Texts.T("settings.shortcutsEditor.default")
        };
        string Binding() => row.IsBound ? string.Join(" / ", row.GestureTexts) : Texts.T("settings.shortcutsEditor.unbound");
        string Conflict() => row.Conflicts.Count > 0
            ? Texts.T("shortcuts.window.conflictWith", string.Join(", ", row.Conflicts.Select(c => CommandCatalog.Label(c.OtherCommandId)).Distinct()))
            : string.Join(" ", row.Problems.Select(ShortcutIssueCatalog.Render));
        string Meta()
        {
            List<string> parts = new();
            if (!CommandCatalog.HasGenericScope(row.CommandId) && CommandCatalog.ScopeHint(row.CommandId, _context.ScopeContext) is { } hint) parts.Add(hint);
            if (row.UnderMore) parts.Add(Texts.T("workview.underMore"));
            return string.Join(" · ", parts);
        }
        void Refresh()
        {
            label.Text = CommandCatalog.Label(row.CommandId);
            meta.Text = Meta();
            meta.IsVisible = meta.Text.Length > 0;
            foreach (KeyCapText cap in caps) cap.Refresh();
            unbound.Text = Texts.T("settings.shortcutsEditor.unbound");
            state.Text = State();
            conflict.Text = Conflict();
            string accessible = string.Join(", ", new[] { label.Text, Binding(), state.Text, ShortcutGroups.Label(row.Group) });
            AutomationProperties.SetName(item, accessible);
            AutomationProperties.SetHelpText(item, string.Join(" · ", new[] { conflict.Text, meta.Text }.Where(t => t.Length > 0)));
            if (row.HasBlockingConflict || row.Conflicts.Count > 0) ToolTip.SetTip(item, conflict.Text);
        }
        _rowTexts.Add(Refresh);
        Refresh();
        return item;
    }

    private void RefreshDetail()
    {
        _detailTexts.Clear();
        _detail.Children.Clear();
        _detailFrame.IsVisible = _selected != null;
        _noSelection.IsVisible = _selected == null;
        if (_selected == null || !CommandCatalog.TryGet(_selected, out var command) || command is null) return;
        _detail.IsEnabled = _session.IsEditable && (command.IsImplemented || command.IsAwaitingHandler) && !_saving;
        var title = LocalizedText(() => command.Label, _detailTexts);
        title.Classes.Add("section-title");
        _detail.Children.Add(title);
        var context = LocalizedText(() => Texts.T("shortcuts.window.selected",
            string.Join(", ", ShortcutGroups.Areas(command).Select(ShortcutGroups.Label))), _detailTexts);
        context.Classes.Add("workspace-meta");
        _detail.Children.Add(context);
        var gestures = _session.Requested(_selected);
        ShortcutPlatform platform = EffectiveShortcutBindings.CurrentPlatform;
        for (int i = 0; i < gestures.Count; i++)
        {
            int index = i;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var caps = new KeyCapText(gestures[i], platform);
            _detailTexts.Add(caps.Refresh);
            row.Children.Add(caps);
            // The gesture as text, for search-free automation and the language checks.
            row.Children.Add(new TextBlock { Text = SafeDisplay(gestures[i], platform), IsVisible = false });
            row.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.remove"), () => _session.RemoveGesture(_selected!, index), _detailTexts));
            _detail.Children.Add(row);
        }
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        actions.Children.Add(Button(() => Texts.T("shortcuts.window.recordReplace"), () => StartRecording(replace: true), _detailTexts));
        actions.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.recordAdditional"), () => StartRecording(replace: false), _detailTexts));
        actions.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.clearBinding"), () => _session.Clear(_selected!), _detailTexts));
        actions.Children.Add(Button(() => Texts.T("settings.shortcutsEditor.restoreDefault"), () => _session.RestoreDefault(_selected!), _detailTexts));
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

    private static string SafeDisplay(ShortcutGesture gesture, ShortcutPlatform platform)
    {
        try { return gesture.Display(platform); }
        catch (FormatException) { return string.Join('+', gesture.Modifiers.Append(gesture.Key)); }
    }

    private void StartRecording(bool replace)
    {
        _validationFocusRequest++;
        StopCapture();
        _pending = null; _capture.Gesture = null; SetPendingText(null);
        _replaceRecording = replace;
        _captureLease = ShortcutService.BeginCapture();
        _capture.IsEnabled = true;
        _recording.IsVisible = true;
        _capture.Focus();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || e.KeyModifiers != KeyModifiers.None) return;
        if (_actions.SelectedItem is ListBoxItem { Tag: GroupTag tag } && e.Key is Key.Enter or Key.Space)
        { e.Handled = true; ToggleGroup(tag.Key); return; }
        if (_selected == null || !_detail.IsEnabled) return;
        if (e.Key == Key.Enter) { e.Handled = true; StartRecording(replace: true); }
        else if (e.Key == Key.Delete) { e.Handled = true; _session.Clear(_selected); }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || e.Source is TextBox) return;
        KeyModifiers primary = EffectiveShortcutBindings.CurrentPlatform == ShortcutPlatform.MacOS ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.Z && e.KeyModifiers == primary && _session.CanUndo && !_saving) { e.Handled = true; _session.Undo(); }
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

    /// <summary>Escape's local step in the window: ends a recording or the close question; false when there is none.</summary>
    internal bool TryCancelLocal()
    {
        if (_closeDecision != null) { ResolveClose(null); return true; }
        if (_captureLease == null) return false;
        CancelCapture();
        return true;
    }

    private void FocusSelectedAction()
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top == null || !IsEffectivelyVisible || !IsEffectivelyEnabled) return;
        if (_actions.SelectedItem is ListBoxItem item && item.Tag is string id && id == _selected
            && _actions.Items.OfType<ListBoxItem>().Any(current => ReferenceEquals(current, item)))
        {
            _actions.ScrollIntoView(item);
            top.UpdateLayout();
            if (ReferenceEquals(TopLevel.GetTopLevel(item), top) && item.IsEffectivelyVisible && item.IsEffectivelyEnabled)
            {
                item.BringIntoView();
                if (item.Focus(NavigationMethod.Directional)) return;
            }
        }
        if (ReferenceEquals(TopLevel.GetTopLevel(_search), top) && _search.IsEffectivelyVisible && _search.IsEffectivelyEnabled)
            _search.Focus(NavigationMethod.Directional);
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
        var reason = EffectiveShortcutBindings.InvalidIssueReason(pending, EffectiveShortcutBindings.CurrentPlatform);
        SetPendingText(() => reason != null ? ShortcutIssueCatalog.Render(reason) : pending.Display(EffectiveShortcutBindings.CurrentPlatform));
    }

    private void ApplyCapture(bool takeOver)
    {
        StopCapture();
        if (_selected == null || _pending == null) return;
        if (EffectiveShortcutBindings.InvalidIssueReason(_pending, EffectiveShortcutBindings.CurrentPlatform) is { } reason)
        { SetPendingText(() => ShortcutIssueCatalog.Render(reason)); return; }
        if (!_replaceRecording && _session.Requested(_selected).Count >= ShortcutDraftSession.GestureLimit)
        { SetPendingText(() => Texts.T("settings.shortcutsEditor.gestureLimit")); return; }
        ShortcutGesture pending = _pending;
        _pending = null;
        if (_session.TryAdd(_selected, pending, takeOver, _replaceRecording) == ShortcutAddResult.Limit)
        { _pending = pending; SetPendingText(() => Texts.T("settings.shortcutsEditor.gestureLimit")); }
    }

    private void StopCapture()
    {
        _captureLease?.Dispose(); _captureLease = null; _capture.IsEnabled = false; _recording.IsVisible = false;
    }

    /// <summary>
    /// Shows the draft's issues; with a blocking one it opens that action (search cleared, group expanded), selects it and
    /// moves the focus to the explanation once the current layout pass is done. Returns whether Save may go on.
    /// </summary>
    internal bool ValidateDraft()
    {
        StopCapture();
        IReadOnlyList<ShortcutIssue> issues = _session.Issues;
        ShowIssues(issues);
        ShortcutIssue? issue = issues.FirstOrDefault(i => i.BlocksSave);
        if (issue == null) return true;
        _syncingFilters = true;
        try { _search.Text = ""; _area.SelectedIndex = 0; }
        finally { _syncingFilters = false; }
        if (CommandCatalog.TryGet(issue.CommandId, out var command) && command != null)
            _collapsed.Remove(ShortcutGroups.For(command, _context.Area));
        _selected = CommandCatalog.TryGet(issue.CommandId, out _) ? issue.CommandId : _selected;
        RefreshList();
        long request = ++_validationFocusRequest;
        ShortcutBindingsSection draft = _session.Draft;
        string? message = _errors.Text;
        Dispatcher.UIThread.Post(() =>
        {
            TopLevel? top = TopLevel.GetTopLevel(this);
            if (request != _validationFocusRequest || !ReferenceEquals(_session.Draft, draft) || _errors.Text != message
                || top == null || !IsEffectivelyVisible || !IsEffectivelyEnabled) return;
            top.UpdateLayout();
            _errors.BringIntoView();
            _errors.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Default);
        return false;
    }

    private async Task SaveAsync()
    {
        if (_saving || SaveHandler is not { } save || IsSaveBlocked?.Invoke() == true) return;
        if (!ValidateDraft()) { RefreshFooter(); return; }
        ShortcutBindingsSection draft = _session.Draft;
        _saving = true;
        RefreshFooter();
        _detail.IsEnabled = false;
        bool saved = false;
        try
        {
            await save(draft);
            saved = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            string detail = UserFacingError.Report(exception, "Saving the shortcuts");
            SetStatus(() => Texts.T("shortcuts.window.saveFailed", detail));
        }
        finally
        {
            _saving = false;
        }
        if (saved)
        {
            // What was saved becomes the baseline; an edit made on the other surface (the Settings card) while the save ran stays
            // in the draft, stays unsaved and says so, instead of being replaced by the saved state.
            _session.CommitSaved(draft);
            SetStatus(() => Texts.T(_session.IsDirty ? "shortcuts.window.savedPending" : "shortcuts.window.saved"));
        }
        RefreshList();
        _closeDecision?.Invoke(saved);
    }

    private void RefreshFooter()
    {
        bool editable = _session.IsEditable && !_saving;
        _undo.IsEnabled = editable && _session.CanUndo;
        _reset.IsEnabled = editable;
        _discard.IsEnabled = !_saving && _session.IsDirty;
        _save.IsEnabled = !_saving && SaveHandler != null && _session.IsDirty && IsSaveBlocked?.Invoke() != true;
        if (_session.HasBlockingIssues) ToolTip.SetTip(_save, Texts.T("shortcuts.window.blocked", _session.BlockingIssues.Count));
        else ToolTip.SetTip(_save, null);
        if (_statusText == null) _status.Text = StatusSummary();
    }

    private string StatusSummary()
    {
        int blocking = _model.BlockingCount;
        string changes = _session.IsDirty ? Texts.T("shortcuts.window.changes", _session.ChangeCount) : Texts.T("shortcuts.window.noChanges");
        return blocking > 0 ? changes + " · " + Texts.T("shortcuts.window.blocked", blocking) : changes;
    }

    private void SetStatus(Func<string>? text)
    {
        _statusText = text;
        _status.Text = text?.Invoke() ?? StatusSummary();
    }

    /// <summary>
    /// The window asks before it closes with unsaved changes: the inline question above the footer offers Keep editing,
    /// Discard and Save. <paramref name="decided"/> receives true when the window may close.
    /// </summary>
    internal void AskBeforeClose(Action<bool> decided)
    {
        _closeDecision = closing => { _closeDecision = null; _closePrompt.IsVisible = false; decided(closing); };
        _closePrompt.IsVisible = true;
        _closeSave.Focus();
    }

    internal bool IsAskingBeforeClose => _closeDecision != null;

    private void ResolveClose(bool? save)
    {
        if (_closeDecision is not { } decide) return;
        if (save == null) { decide(false); return; }
        if (save == false) { StopCapture(); _session.Discard(); decide(true); return; }
        // Save: closes only when the save succeeds; a refused save keeps the window and shows why.
        _closeDecision = saved => { _closeDecision = null; _closePrompt.IsVisible = false; decide(saved); };
        if (!_session.IsDirty) { decide(true); return; }
        _ = SaveAsync();
        if (!_saving && _closeDecision != null) { _closeDecision = null; _closePrompt.IsVisible = false; decide(false); }
    }

    private void ShowIssues(IReadOnlyList<ShortcutIssue> issues)
    {
        _errors.Focusable = issues.Any(issue => issue.BlocksSave);
        _errors.Text = string.Join("\n", issues.Select(i => (CommandCatalog.TryGet(i.CommandId, out var c) ? c!.Label : i.CommandId)
            + (i.OtherCommandId != null ? " ↔ " + CommandCatalog.Label(i.OtherCommandId) : "") + ": " + ShortcutIssueCatalog.Render(i)));
        _errors.IsVisible = _errors.Text.Length > 0;
        // Advisory warnings stay visible without replacing recorder instructions.
        AutomationProperties.SetHelpText(_capture, issues.Any(issue => issue.BlocksSave)
            ? Texts.T("settings.shortcutsEditor.issuesHelp") + _errors.Text
            : Texts.T("settings.shortcutsEditor.recordHelp"));
    }

    private string BuildPlainText()
    {
        ShortcutHelpContext context = _context;
        string[] ids = context.Preferred.Distinct(StringComparer.Ordinal)
            .Where(id => CommandCatalog.TryGet(id, out _) && CommandCatalog.ShortcutDisplay(id) != null).ToArray();
        string HelpLine(string id) => CommandCatalog.Label(id) + "   " + CommandCatalog.ShortcutDisplay(id)
            + (!CommandCatalog.HasGenericScope(id) && CommandCatalog.ScopeHint(id, context.ScopeContext) is string hint ? " · " + hint : string.Empty)
            + (context.UnderMore?.Invoke(id) == true ? " · " + Texts.T("workview.underMore") : string.Empty);
        List<string> sections = new();
        foreach ((string label, bool shell) in new[] { ("help.scope.current", false), ("help.scope.application", true) })
        {
            string[] lines = ids.Where(id => CommandCatalog.Get(id).ExecutionContexts.Contains("shell") == shell).Select(HelpLine).ToArray();
            if (lines.Length > 0) sections.Add(Texts.T(label) + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }
        foreach (string key in context.NoteKeys ?? Array.Empty<string>())
            if (key != "help.scope.generalNote") sections.Insert(0, Texts.T(key));
        // The "outside inputs and overlays" qualifier of the page shortcuts is stated once, not on every line (UX round 3).
        if (ids.Any(CommandCatalog.HasGenericScope)) sections.Add(Texts.T("help.scope.generalNote"));
        return string.Join(Environment.NewLine + Environment.NewLine, sections)
            + Environment.NewLine + Environment.NewLine
            + (ShortcutService.Current.SingleKeyShortcuts ? string.Empty : Texts.T("help.singleKeysOff") + Environment.NewLine + Environment.NewLine)
            + Texts.T("help.escape");
    }

    private void SetPendingText(Func<string>? text)
    { _pendingText = text; _pendingLabel.Text = text?.Invoke() ?? ""; }

    private void Localized(TextBlock text, Func<string> label)
    {
        void Refresh() => text.Text = label();
        _frameTexts.Add(Refresh); Refresh();
    }

    private void Localized(ContentControl control, Func<string> label)
    {
        void Refresh() { string value = label(); control.Content = value; AutomationProperties.SetName(control, value); }
        _frameTexts.Add(Refresh); Refresh();
    }

    private static TextBlock LocalizedText(Func<string> label, List<Action> refreshers)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
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
