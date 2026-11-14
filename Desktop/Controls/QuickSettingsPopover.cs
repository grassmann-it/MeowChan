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
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The content of a quick settings popover (R7): a header with the title and the close button alone at the right edge, one row
/// per <see cref="QuickSettingField"/> (label and editor, the hint as tooltip and accessible description), a status line and a
/// link at the bottom left to the full settings page. The fields are the settings of the settings page under the same ids; the
/// surface keeps no value of its own. Every edit goes to a <see cref="QuickSettingsApplier"/> that saves it live through the
/// host, and what the configuration holds afterwards (clamped, normalised, or the old value after a failure) is shown again.
/// Tab cycles inside, Escape closes (an open drop-down closes first). The host frames it (<see cref="QuickSettingsPopover"/>).
/// </summary>
internal sealed class QuickSettingsSurface : UserControl, IDisposable
{
    /// <summary>The width of the popover in device-independent pixels.</summary>
    internal const double PreferredWidth = 360;
    private const double EditorWidth = 168;

    private readonly QuickSettingsSpec _spec;
    private readonly QuickSettingsHost _host;
    private readonly QuickSettingsApplier _applier;
    private readonly string _name;
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _close = new();
    private readonly Button _all = new() { Classes = { "subtle" }, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _status = new() { Classes = { "workspace-error" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _rows = new() { Spacing = 12 };
    private readonly ScrollViewer _scroll;
    private readonly List<Row> _editors = new();
    private string? _lastChanged;
    private bool _syncing, _keyboard, _disposed;

    private sealed record Row(QuickSettingField Field, Control Editor, TextBlock? Label);

    /// <summary>An option of a combo box: its value in the configuration and the text shown (the default item template shows <see cref="ToString"/>).</summary>
    internal sealed record ChoiceItem(string Value, string Label)
    {
        public override string ToString() => Label;
    }

    internal QuickSettingsSurface(QuickSettingsSpec spec, QuickSettingsHost host, string name = "QuickSettings", QuickSettingsApplier? applier = null)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _name = string.IsNullOrWhiteSpace(name) ? "QuickSettings" : name;
        _applier = applier ?? new QuickSettingsApplier(host.SaveAsync, host.Debounce);
        _applier.Applied += OnApplied;
        _applier.Failed += OnFailed;
        Name = _name;
        Focusable = false;
        Width = PreferredWidth;
        Padding = new Thickness(16);
        _title.Name = _name + "Title"; _close.Name = _name + "Close"; _all.Name = _name + "All"; _status.Name = _name + "Status";
        _rows.Name = _name + "Rows";

        // Header: the title left, the close button alone at the right edge (owner rule 14.11.), 24 dip apart at least.
        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("*,24,Auto"), Name = _name + "Header" };
        header.Children.Add(_title);
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
    }

    /// <summary>The surface asks its host to close it (close button, Escape).</summary>
    internal event Action? CloseRequested;
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

    /// <summary>Saves what is waiting at once (the popover closed).</summary>
    internal Task FlushAsync() => _applier.FlushAsync();

    /// <summary>Puts the keyboard focus into the first editor.</summary>
    internal bool FocusFirst()
    {
        Control? first = _editors.Select(row => row.Editor).FirstOrDefault(editor => editor.IsEffectivelyVisible && editor.IsEffectivelyEnabled);
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
        }
        finally { _syncing = false; }
    }

    private void Build()
    {
        _rows.Children.Clear();
        _editors.Clear();
        _title.Text = Texts.T(_spec.TitleKey);
        string close = Texts.T("quicksettings.close");
        WorkspaceIcons.MakeSquare(_close, WorkspaceIcons.Close, close, Texts.T("quicksettings.close.tip"));
        _all.Content = Texts.T(_spec.AllSettingsKey);
        string allTip = Texts.T("quicksettings.all.tip");
        ToolTip.SetTip(_all, allTip);
        AutomationProperties.SetHelpText(_all, allTip);
        AutomationProperties.SetName(this, _title.Text);
        foreach (QuickSettingField field in _spec.Fields)
        {
            Row row = BuildRow(field);
            _editors.Add(row);
            _rows.Children.Add(RowLayout(row));
        }
        SyncFromConfig();
    }

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

    private static Control RowLayout(Row row)
    {
        if (row.Label == null) return row.Editor;
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
        _applier.Applied -= OnApplied;
        _applier.Failed -= OnFailed;
        _close.Click -= OnCloseClick;
        _all.Click -= OnAllClick;
        CloseRequested = null;
        AllSettingsRequested = null;
    }
}

/// <summary>
/// The popover of a quick settings surface: a flyout under its opener (the "View" button of the reader), 360 dip wide in the
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

    internal QuickSettingsPopover(QuickSettingsSpec spec, QuickSettingsHost host, string name = "QuickSettings")
    {
        _surface = new QuickSettingsSurface(spec, host, name);
        _flyout.FlyoutPresenterClasses.Add("quick-settings");
        _flyout.Content = _surface;
        _surface.CloseRequested += Hide;
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
