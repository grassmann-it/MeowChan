using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The content of a quick settings popover (R7): a header with the title and the close button alone at the right edge, one row
/// per <see cref="QuickSettingField"/> (label and editor, the hint as tooltip and accessible description), a status line and a
/// link at the bottom left to the full settings page. The fields are the settings of the settings page under the same ids; the
/// surface keeps no value of its own. Every edit goes to a <see cref="QuickSettingsApplier"/> that saves it live through the
/// host, and what the configuration holds afterwards (clamped, normalised, or the old value after a failure) is shown again.
/// Tab cycles inside, Escape closes (an open drop-down closes first). The host frames it (<see cref="QuickSettingsPopover"/>).
/// <para>
/// RL1: a spec with a layout also has section headings, rows that exist only while the setting they depend on is on (indented under it,
/// the pattern of the settings page), the key of a setting's command at the right end of its row, and session rows: a switch or an
/// action of the open view (<see cref="QuickSettingsSession"/>), read again whenever the popover opens or the page says its state changed.
/// </para>
/// </summary>
internal sealed class QuickSettingsSurface : UserControl, IDisposable
{
    /// <summary>The width of the popover in device-independent pixels.</summary>
    internal const double PreferredWidth = 360;
    private const double EditorWidth = 168;

    private readonly QuickSettingsSpec _spec;
    private readonly QuickSettingsHost _host;
    private readonly QuickSettingsApplier _applier;
    private readonly QuickSettingsSession? _session;
    private readonly string _name;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new();
    private readonly Button _all = new() { Classes = { "subtle" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { Classes = { "workspace-error" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    // The right margin keeps the editors clear of the vertical scroll bar (typography pass: no field ran under it any more).
    private readonly StackPanel _rows = new() { Spacing = 12, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ScrollViewer _scroll;
    private readonly List<Row> _editors = new();
    // RL1: what the layout adds to the plain list of fields.
    private readonly List<(Control Container, string WhenFieldId)> _conditionals = new();
    private readonly List<SessionRow> _sessionRows = new();
    private readonly List<(TextBlock Text, string CommandId)> _shortcuts = new();
    private readonly List<SectionRow> _sections = new();
    private readonly List<Control> _focusOrder = new();
    private readonly Dictionary<string, Control> _containers = new(StringComparer.Ordinal);
    // Read out by the screen reader when a row appears under its parent (no new text: the row's own label).
    private readonly TextBlock _announcement = new() { Classes = { "visually-hidden" }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private string? _lastChanged;
    private bool _syncing, _keyboard, _disposed;

    private sealed record Row(QuickSettingField Field, Control Editor, TextBlock? Label);
    private sealed record SessionRow(string CommandId, bool IsAction, Control Container, Control Control);
    private sealed record SectionRow(TextBlock Heading, List<Control> Rows);

    /// <summary>An option of a combo box: its value in the configuration and the text shown (the default item template shows <see cref="ToString"/>).</summary>
    internal sealed record ChoiceItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    internal QuickSettingsSurface(QuickSettingsSpec spec, QuickSettingsHost host, string name = "QuickSettings", QuickSettingsApplier? applier = null,
        QuickSettingsSession? session = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _session = session;
        _name = string.IsNullOrWhiteSpace(name) ? "QuickSettings" : name;
        _applier = applier ?? new QuickSettingsApplier(host.SaveAsync, host.Debounce);
        _applier.Applied += OnApplied;
        _applier.Failed += OnFailed;
        Name = _name;
        Focusable = false;
        Width = PreferredWidth;
        Padding = new Thickness(16);
        // The flyout's logical parent is its anchor, so the styles of the toolbar it opens from reach these buttons too (ReaderToolbar:
        // ":is(Button)" with an 8-dip right margin pulled the close cross 8 dip off the right edge). The surface spaces its own buttons.
        Styles.Add(new Style(selector => selector.OfType<Button>()) { Setters = { new Setter(MarginProperty, new Thickness(0)) } });
        // The same toolbar style (":is(Button)" with Height 32 and MinWidth 32) also reached the up and down buttons inside the number
        // fields: each became 32 high, so every number field stood 64 dip tall beside 32 dip combo boxes. Give them back their own size.
        Styles.Add(new Style(selector => selector.OfType<NumericUpDown>().Template().OfType<ButtonSpinner>().Template().Is<Button>())
        {
            Setters = { new Setter(HeightProperty, double.NaN), new Setter(MinWidthProperty, 0d), new Setter(MarginProperty, new Thickness(0)) }
        });
        _title.Name = _name + "Title"; _close.Name = _name + "Close"; _all.Name = _name + "All"; _status.Name = _name + "Status";
        _rows.Name = _name + "Rows";

        // Header: the title left, the close button alone at the right edge (owner rule 14.11.), 24 dip apart at least.
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,24,Auto"), Name = _name + "Header" };
        header.Children.Add(_title);
        header.Children.Add(_announcement);
        _announcement.Name = _name + "Announcement";
        AutomationProperties.SetLiveSetting(_announcement, AutomationLiveSetting.Polite);
        header.Children.Add(_close); Grid.SetColumn(_close, 2);
        _close.VerticalAlignment = VerticalAlignment.Center;
        Header = header;

        _scroll = new ScrollViewer
        {
            Content = _rows, Name = _name + "Body",
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 12 };
        layout.Children.Add(header);
        layout.Children.Add(_scroll); Grid.SetRow(_scroll, 1);
        layout.Children.Add(_status); Grid.SetRow(_status, 2);
        layout.Children.Add(_all); Grid.SetRow(_all, 3);
        Content = layout;
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Cycle);
        // The close button is the last stop of the cycle, like the footer link before it: header content first, controls next.
        KeyboardNavigation.SetTabIndex(header, 2);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _close.Click += OnCloseClick;
        _all.Click += OnAllClick;
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPointer, RoutingStrategies.Tunnel);
        Build();
        Texts.Changed += OnTextsChanged;
        SettingsConditional.RowAppeared += OnConditionalRowAppeared;
        if (_session != null) _session.Changed += RefreshSession;
    }

    /// <summary>The surface asks its host to close it (close button, Escape).</summary>
    internal event Action? CloseRequested;
    /// <summary>An action row was chosen: the host closes the popover (the focus goes to what the action opens) and then runs it; without a host handler it runs at once.</summary>
    internal event Action<Action>? ActionRequested;
    /// <summary>"All settings": the id of the setting to reveal on the settings page (the last one changed, else the first one).</summary>
    internal event Action<string?>? AllSettingsRequested;

    internal QuickSettingsSpec Spec => _spec;
    internal QuickSettingsApplier Applier => _applier;
    internal Button CloseButton => _close;
    internal Button AllSettingsButton => _all;
    internal TextBlock Title => _title;
    internal TextBlock Status => _status;
    internal Grid Header { get; }
    internal ScrollViewer Body => _scroll;
    /// <summary>The editors in field order (smokes and tests).</summary>
    internal IReadOnlyList<(QuickSettingField Field, Control Editor)> Editors => _editors.Select(row => (row.Field, row.Editor)).ToArray();
    /// <summary>The last interaction was a key press (the focus ring and the way back to the opener follow it).</summary>
    internal bool KeyboardInteraction => _keyboard;

    internal Control? EditorOf(string fieldId) => _editors.FirstOrDefault(row => row.Field.Id == fieldId)?.Editor;
    /// <summary>The control of the session row of <paramref name="commandId"/> (a check box, or a button for an action); null without such a row.</summary>
    internal Control? SessionControlOf(string commandId) => _sessionRows.FirstOrDefault(row => row.CommandId == commandId)?.Control;
    /// <summary>Whether the row of the setting <paramref name="fieldId"/> is shown now (a conditional row only while its setting is on).</summary>
    internal bool IsRowShown(string fieldId) => _containers.TryGetValue(fieldId, out Control? container) && container.IsVisible;
    /// <summary>The section headings in order (tests and smokes).</summary>
    internal IReadOnlyList<TextBlock> SectionHeadings => _sections.Select(section => section.Heading).ToArray();
    /// <summary>The command ids of the session rows in order, shown or not (tests).</summary>
    internal IReadOnlyList<string> SessionCommandIds => _sessionRows.Select(row => row.CommandId).ToArray();
    /// <summary>The key text shown at the right end of the row of <paramref name="commandId"/> (empty without a key).</summary>
    internal string ShortcutTextOf(string commandId) => _shortcuts.FirstOrDefault(item => item.CommandId == commandId).Text?.Text ?? string.Empty;


    /// <summary>Saves what is waiting at once (the popover closed).</summary>
    internal Task FlushAsync() => _applier.FlushAsync();

    /// <summary>Puts the keyboard focus into the first editor.</summary>
    internal bool FocusFirst()
    {
        Control? first = _focusOrder.FirstOrDefault(editor => editor.IsEffectivelyVisible && editor.IsEffectivelyEnabled);
        return first?.Focus(NavigationMethod.Tab) == true;
    }

    /// <summary>Shows what the configuration holds now in every editor, without saving anything.</summary>
    internal void SyncFromConfig()
    {
        Config config = _host.CurrentConfig();
        _syncing = true;
        try
        {
            foreach (Row row in _editors) Show(row, config);
            foreach ((Control container, string whenFieldId) in _conditionals)
                SettingsConditional.SetWhen(container, _editors.FirstOrDefault(row => row.Field.Id == whenFieldId) is { } parent
                    && bool.TryParse(parent.Field.Read(config), out bool on) && on);
        }
        finally { _syncing = false; }
        RefreshShortcuts();
        RefreshSession();
    }

    /// <summary>Reads the session rows again: shown (the page offers the command), on or off, enabled, with the reason in the tip while it cannot run.</summary>
    internal void RefreshSession()
    {
        if (_disposed) return;
        IReadOnlyList<QuickSettingsSessionItem> items = _session?.Items() ?? Array.Empty<QuickSettingsSessionItem>();
        bool syncing = _syncing;
        _syncing = true;
        try
        {
            foreach (SessionRow row in _sessionRows)
            {
                QuickSettingsSessionItem? item = items.FirstOrDefault(candidate => candidate.CommandId == row.CommandId);
                row.Container.IsVisible = item != null;
                if (item == null) continue;
                bool enabled = item.IsEnabled();
                string tip = CommandBindings.Tooltip(row.CommandId, enabled ? null : item.Note?.Invoke());
                if (row.Control is CheckBox box)
                {
                    bool on = item.IsChecked?.Invoke() == true;
                    if (box.IsChecked != on) box.IsChecked = on;
                }
                row.Control.IsEnabled = enabled;
                ToolTip.SetTip(row.Control, tip);
                ToolTip.SetShowOnDisabled(row.Control, true);
                AutomationProperties.SetHelpText(row.Control, tip);
            }
        }
        finally { _syncing = syncing; }
        RefreshShortcuts();
        UpdateSections();
    }

    // The key of a row's command at its right end; read again with every look, so a rebound key shows.
    private void RefreshShortcuts()
    {
        foreach ((TextBlock text, string commandId) in _shortcuts)
        {
            string? shortcut = CommandCatalog.ShortcutDisplay(commandId);
            string value = shortcut ?? string.Empty;
            if (text.Text != value) text.Text = value;
            text.IsVisible = value.Length > 0;
        }
    }

    // A heading stands only over rows that are shown.
    private void UpdateSections()
    {
        foreach (SectionRow section in _sections)
            section.Heading.IsVisible = section.Rows.Any(row => row.IsVisible);
    }

    private void OnConditionalRowAppeared(Control row)
    {
        if (_disposed || !_conditionals.Any(item => ReferenceEquals(item.Container, row))) return;
        _announcement.Text = SettingsConditional.LabelOf(row);
    }

    private void Build()
    {
        _rows.Children.Clear();
        _editors.Clear();
        _conditionals.Clear(); _sessionRows.Clear(); _shortcuts.Clear(); _sections.Clear(); _focusOrder.Clear(); _containers.Clear();
        _announcement.Text = string.Empty;
        _title.Text = Texts.T(_spec.TitleKey);
        string close = Texts.T("quicksettings.close");
        WorkspaceIcons.MakeSquare(_close, WorkspaceIcons.Close, close, Texts.T("quicksettings.close.tip"));
        _all.Content = Texts.T(_spec.AllSettingsKey);
        string allTip = Texts.T("quicksettings.all.tip");
        ToolTip.SetTip(_all, allTip);
        AutomationProperties.SetHelpText(_all, allTip);
        AutomationProperties.SetName(this, _title.Text);
        SectionRow? section = null;
        foreach (QuickSettingsEntry entry in _spec.Layout ?? _spec.Fields.Select(field => (QuickSettingsEntry)new QuickSettingsFieldEntry(field)).ToArray())
        {
            Control container;
            switch (entry)
            {
                case QuickSettingsSection heading:
                {
                    TextBlock text = new()
                    {
                        Text = Texts.T(heading.TitleKey), Classes = { "workspace-meta" }, FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(0, _rows.Children.Count == 0 ? 0 : 4, 0, 0), Name = _name + "Section" + _sections.Count
                    };
                    AutomationProperties.SetHeadingLevel(text, 2);
                    section = new SectionRow(text, new List<Control>());
                    _sections.Add(section);
                    _rows.Children.Add(text);
                    continue;
                }
                case QuickSettingsFieldEntry setting:
                {
                    Row row = BuildRow(setting.Field);
                    _editors.Add(row);
                    _focusOrder.Add(row.Editor);
                    container = RowLayout(row, setting.ShortcutCommandId);
                    _containers[setting.Field.Id] = container;
                    if (setting.WhenFieldId != null)
                    {
                        container.Classes.Add(SettingsConditional.RowClass);
                        container.Margin = new Thickness(24, 0, 0, 0); // the settings page indents a conditional row by 24 too
                        _conditionals.Add((container, setting.WhenFieldId));
                    }
                    break;
                }
                case QuickSettingsSessionEntry session:
                {
                    (container, SessionRow sessionRow) = BuildSessionRow(session);
                    _sessionRows.Add(sessionRow);
                    _focusOrder.Add(sessionRow.Control);
                    break;
                }
                default: throw new NotSupportedException("Quick settings entry " + entry.GetType().Name + " has no row.");
            }
            section?.Rows.Add(container);
            _rows.Children.Add(container);
        }
        SyncFromConfig();
    }

    private (Control Container, SessionRow Row) BuildSessionRow(QuickSettingsSessionEntry entry)
    {
        string label = CommandCatalog.Label(entry.CommandId);
        string name = _name + "Session" + PascalCase(entry.CommandId);
        Control control;
        if (entry.IsAction)
        {
            Button button = new() { Content = label, Classes = { "subtle" }, HorizontalAlignment = HorizontalAlignment.Left };
            button.Click += (_, _) => RunSessionAction(entry.CommandId);
            control = button;
        }
        else
        {
            CheckBox box = new() { Content = label, VerticalAlignment = VerticalAlignment.Center };
            box.IsCheckedChanged += (_, _) =>
            {
                if (_syncing || _disposed) return;
                QuickSettingsSessionItem? item = _session?.Items().FirstOrDefault(candidate => candidate.CommandId == entry.CommandId);
                if (item != null && item.IsEnabled()) item.Invoke();
                // What the page made of it (a refused switch, a state that follows later) shows once its turn is over.
                RefreshSession();
                Dispatcher.UIThread.Post(RefreshSession, DispatcherPriority.Background);
            };
            control = box;
        }
        control.Name = name;
        AutomationProperties.SetName(control, label);
        Control container = control;
        if (!entry.IsAction)
        {
            Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
            grid.Children.Add(control);
            TextBlock shortcut = ShortcutText(entry.CommandId, name + "Shortcut");
            grid.Children.Add(shortcut); Grid.SetColumn(shortcut, 1);
            container = grid;
        }
        return (container, new SessionRow(entry.CommandId, entry.IsAction, container, control));
    }

    private void RunSessionAction(string commandId)
    {
        QuickSettingsSessionItem? item = _session?.Items().FirstOrDefault(candidate => candidate.CommandId == commandId);
        if (item == null || !item.IsEnabled()) return;
        if (ActionRequested is { } handler) handler(item.Invoke);
        else item.Invoke();
    }

    private TextBlock ShortcutText(string commandId, string name)
    {
        TextBlock text = new() { Classes = { "workspace-meta" }, VerticalAlignment = VerticalAlignment.Center, Name = name, TextWrapping = TextWrapping.NoWrap };
        // The key is part of the row's accessible description (tip), not read twice as its own text.
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        _shortcuts.Add((text, commandId));
        return text;
    }

    private static string PascalCase(string commandId) =>
        string.Concat(commandId.Split('.', '-').Where(part => part.Length > 0).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private Row BuildRow(QuickSettingField field)
    {
        string label = Texts.T(field.LabelKey);
        string? hint = field.HintKey == null ? null : Texts.T(field.HintKey);
        TextBlock? caption = null;
        Control editor;
        switch (field)
        {
            case QuickChoiceField choice:
            {
                ComboBox box = new()
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
                    ItemsSource = choice.Choices.Select(option => new ChoiceItem(option.Value, Texts.T(option.LabelKey))).ToArray()
                };
                box.SelectionChanged += (_, _) => { if (box.SelectedItem is ChoiceItem item) Edited(field, item.Value); };
                editor = box;
                break;
            }
            case QuickIntegerField number:
            {
                AccessibleNumberInput input = new()
                {
                    Minimum = number.Minimum, Maximum = number.Maximum, Increment = number.Step, FormatString = "0",
                    ParsingNumberStyle = NumberStyles.Integer, HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center
                };
                input.ValueChanged += (_, e) =>
                {
                    if (_syncing) return;
                    if (e.NewValue is { } value) Edited(field, ((int)value).ToString(CultureInfo.InvariantCulture));
                    else Dispatcher.UIThread.Post(SyncFromConfig, DispatcherPriority.Background); // cleared: the saved value comes back
                };
                editor = input;
                break;
            }
            case QuickToggleField:
            {
                CheckBox box = new() { Content = label, VerticalAlignment = VerticalAlignment.Center };
                box.IsCheckedChanged += (_, _) => Edited(field, box.IsChecked == true ? bool.TrueString : bool.FalseString);
                editor = box;
                break;
            }
            default:
                throw new NotSupportedException("Quick setting kind " + field.Kind + " has no editor.");
        }
        editor.Name = _name + field.Id;
        AutomationProperties.SetName(editor, label);
        if (hint != null)
        {
            AutomationProperties.SetHelpText(editor, hint);
            ToolTip.SetTip(editor, hint);
        }
        if (field is not QuickToggleField)
        {
            caption = new TextBlock { Text = label, Classes = { "workspace-field-label" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Name = _name + field.Id + "Label" };
            if (hint != null) ToolTip.SetTip(caption, hint);
            AutomationProperties.SetLabeledBy(editor, caption);
        }
        return new Row(field, editor, caption);
    }

    private Control RowLayout(Row row, string? shortcutCommandId = null)
    {
        if (row.Label == null)
        {
            if (shortcutCommandId == null) return row.Editor;
            // A switch with the key of its command at the right end of the row.
            Grid line = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
            line.Children.Add(row.Editor);
            TextBlock shortcut = ShortcutText(shortcutCommandId, _name + row.Field.Id + "Shortcut");
            line.Children.Add(shortcut); Grid.SetColumn(shortcut, 1);
            return line;
        }
        Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("*," + EditorWidth.ToString(CultureInfo.InvariantCulture)), ColumnSpacing = 16 };
        grid.Children.Add(row.Label);
        grid.Children.Add(row.Editor); Grid.SetColumn(row.Editor, 1);
        return grid;
    }

    private static void Show(Row row, Config config)
    {
        string value = row.Field.Read(config);
        switch (row.Editor)
        {
            case ComboBox box:
                box.SelectedItem = (box.ItemsSource as IEnumerable<ChoiceItem>)?.FirstOrDefault(item => item.Value == value);
                break;
            case NumericUpDown input when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number):
                if (input.Value != number) input.Value = number;
                break;
            case CheckBox box:
                box.IsChecked = bool.TryParse(value, out bool on) && on;
                break;
        }
    }

    private void Edited(QuickSettingField field, string value)
    {
        if (_syncing || _disposed) return;
        // What the configuration holds already is not an edit (the editor only moved to show it).
        if (string.Equals(field.Read(_host.CurrentConfig()), value, StringComparison.Ordinal) && _applier.PendingCount == 0) return;
        if (!_applier.Set(field, value)) return;
        _lastChanged = field.Id;
        if (_status.IsVisible) { _status.Text = string.Empty; _status.IsVisible = false; }
    }

    private void OnApplied(IReadOnlyList<string> ids)
    {
        if (_disposed) return;
        try { _host.Applied?.Invoke(ids); }
        finally { SyncFromConfig(); }
    }

    private void OnFailed(IReadOnlyList<string> ids, Exception error)
    {
        if (_disposed) return;
        _status.Text = Texts.T("quicksettings.failed", UserFacingError.Report(error, "Applying a quick setting"));
        _status.IsVisible = true;
        SyncFromConfig();
    }

    private void OnTextsChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Build();
        if (_status.IsVisible) _status.IsVisible = false;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnAllClick(object? sender, RoutedEventArgs e) =>
        AllSettingsRequested?.Invoke(_lastChanged ?? _spec.Fields.FirstOrDefault()?.Id);

    private void OnPointer(object? sender, PointerPressedEventArgs e) => _keyboard = false;

    private void OnKey(object? sender, KeyEventArgs e)
    {
        _keyboard = true;
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || e.Handled) return;
        // An open drop-down closes first; the second Escape closes the popover.
        if (_editors.Any(row => row.Editor is ComboBox { IsDropDownOpen: true })) return;
        e.Handled = true;
        CloseRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Texts.Changed -= OnTextsChanged;
        SettingsConditional.RowAppeared -= OnConditionalRowAppeared;
        if (_session != null) _session.Changed -= RefreshSession;
        _applier.Applied -= OnApplied;
        _applier.Failed -= OnFailed;
        _close.Click -= OnCloseClick;
        _all.Click -= OnAllClick;
        CloseRequested = null;
        ActionRequested = null;
        AllSettingsRequested = null;
    }
}

/// <summary>
/// The popover of a quick settings surface: a flyout under its opener (the settings gear of the reader), 360 dip wide in the
/// in-window overlay look, focus in its first editor, Escape or the close button closing it, the focus returning to the opener,
/// and anything still waiting saved when it closes. It is the reusable frame: the gallery and the media page get theirs from
/// another <see cref="QuickSettingsSpec"/>, nothing else.
/// </summary>
internal sealed class QuickSettingsPopover : IDisposable
{
    private readonly Flyout _flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly QuickSettingsSurface _surface;
    private Control? _anchor;
    private bool _hadFocus, _restoreFocus = true, _disposed;

    internal QuickSettingsPopover(QuickSettingsSpec spec, QuickSettingsHost host, string name = "QuickSettings", QuickSettingsSession? session = null)
    {
        _surface = new QuickSettingsSurface(spec, host, name, session: session);
        _flyout.FlyoutPresenterClasses.Add("quick-settings");
        _flyout.Content = _surface;
        _surface.CloseRequested += Hide;
        _surface.ActionRequested += invoke =>
        {
            _restoreFocus = false; // what the action opens takes the focus
            Hide();
            invoke();
        };
        _surface.AllSettingsRequested += id =>
        {
            _restoreFocus = false; // the settings page takes the focus
            Hide();
            host.ShowAllSettings?.Invoke(id);
        };
        _surface.GotFocus += (_, _) => _hadFocus = true;
        _flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => { if (_flyout.IsOpen) _surface.FocusFirst(); }, DispatcherPriority.Loaded);
        _flyout.Closed += OnClosed;
    }

    internal QuickSettingsSurface Surface => _surface;
    internal Flyout Flyout => _flyout;
    internal bool IsOpen => _flyout.IsOpen;

    /// <summary>Opens the popover under <paramref name="anchor"/> (a visible control); the editors show the current values.</summary>
    internal void Show(Control anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (_disposed || _flyout.IsOpen) return;
        _anchor = anchor;
        _hadFocus = false;
        _restoreFocus = true;
        // A small window scrolls the rows instead of cutting the popover off.
        double room = TopLevel.GetTopLevel(anchor)?.ClientSize.Height ?? 0;
        _surface.Body.MaxHeight = room > 0 ? Math.Max(160, room - 160) : double.PositiveInfinity;
        _surface.SyncFromConfig();
        _flyout.ShowAt(anchor);
    }

    internal void Hide()
    {
        if (_flyout.IsOpen) _flyout.Hide();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _ = _surface.FlushAsync();
        Control? anchor = _anchor;
        if (!_hadFocus || !_restoreFocus || _disposed || anchor is not { IsEffectivelyVisible: true }) return;
        _hadFocus = false;
        anchor.Focus(_surface.KeyboardInteraction ? NavigationMethod.Tab : NavigationMethod.Pointer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        _surface.Dispose();
    }
}
