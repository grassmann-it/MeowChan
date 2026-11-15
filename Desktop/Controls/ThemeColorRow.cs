using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// TH3: one role of the colour dialog: a swatch button that opens the picker, the role's name with "Default" or "Changed", the contrast
/// of its displayed pair written out as text ("4.8 : 1 – good", "3.1 : 1 – too weak for text"), the hex field, and the reset button
/// (rotate-ccw). Colour is never the only carrier: the state and the contrast are sentences, and a refused hex entry says so in text.
/// The row holds no colour of its own; <see cref="Refresh"/> shows what the model says.
/// </summary>
internal sealed class ThemeColorRow : Border
{
    private readonly ThemeUserColor _role;
    private readonly Button _swatch;
    private readonly Border _swatchFill = new();
    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _state = new();
    private readonly TextBlock _contrast = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _extra = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _hexError = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBox _hex;
    private readonly Button _reset;
    private readonly Flyout _flyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly ThemeColorPicker _picker = new();
    private ThemeRoleView? _view;
    private bool _syncing;

    /// <summary>The picked colour (the picker or a valid hex entry), raised after the user chose it.</summary>
    internal event Action<ThemeColorRow, Color>? ColorChosen;

    /// <summary>The user pressed reset on this row.</summary>
    internal event Action<ThemeColorRow>? ResetRequested;

    internal ThemeColorRow(ThemeUserColor role)
    {
        _role = role ?? throw new ArgumentNullException(nameof(role));
        Name = "ThemeColorRow_" + role.Id;
        Padding = new Thickness(0, 8);
        BorderThickness = new Thickness(0, 0, 0, 1);
        this[!BorderBrushProperty] = new DynamicResourceExtension("Workspace.Border");
        _swatch = new Button { Name = "ThemeColorSwatch_" + role.Id, Padding = new Thickness(0), Width = ThemeColorsMetrics.Swatch, Height = ThemeColorsMetrics.Swatch,
            VerticalAlignment = VerticalAlignment.Top, Content = _swatchFill };
        _swatchFill.Margin = new Thickness(4);
        _swatchFill.BorderThickness = new Thickness(1);
        _swatchFill[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Badge");
        _swatchFill[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
        _hex = new TextBox { Name = "ThemeColorHex_" + role.Id, Width = ThemeColorsMetrics.HexField, MaxLength = 7, VerticalAlignment = VerticalAlignment.Top };
        _reset = new Button { Name = "ThemeColorReset_" + role.Id, VerticalAlignment = VerticalAlignment.Top };
        _name.Classes.Add("workspace-field-label");
        _state.Classes.Add("muted");
        _extra.Classes.Add("muted");
        _hexError.Classes.Add("workspace-error");
        AutomationProperties.SetLiveSetting(_hexError, AutomationLiveSetting.Assertive);
        AutomationProperties.SetLiveSetting(_contrast, AutomationLiveSetting.Polite);

        Grid layout = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        StackPanel texts = new() { Spacing = 2, VerticalAlignment = VerticalAlignment.Top,
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _name, _state } }, _contrast, _extra, _hexError } };
        layout.Children.Add(_swatch);
        Grid.SetColumn(texts, 1); layout.Children.Add(texts);
        Grid.SetColumn(_hex, 2); layout.Children.Add(_hex);
        Grid.SetColumn(_reset, 3); layout.Children.Add(_reset);
        Child = layout;

        _picker.ColorChanged += (_, _) => ColorChosen?.Invoke(this, _picker.Color);
        _picker.DoneRequested += (_, _) => _flyout.Hide();
        _flyout.Content = new Border { Padding = new Thickness(4), Child = _picker };
        _flyout.Opened += (_, _) => _picker.HueSlider.Focus();
        _flyout.Closed += (_, _) => { if (_swatch.IsEffectivelyVisible) _swatch.Focus(); };
        _swatch.Flyout = _flyout;
        _flyout.Opening += (_, _) => { if (_view != null) _picker.Color = _view.Color; };
        _hex.TextChanged += (_, _) => OnHexTyped();
        _hex.LostFocus += (_, _) => RestoreHex();
        _hex.KeyDown += OnHexKeyDown;
        _reset.Click += (_, _) => ResetRequested?.Invoke(this);
        IconButtons.Apply(_reset, "rotate-ccw", string.Empty);
    }

    internal ThemeUserColor Role => _role;
    internal Button Swatch => _swatch;
    internal TextBox HexBox => _hex;
    internal Button ResetButton => _reset;
    internal Flyout PickerFlyout => _flyout;
    internal ThemeColorPicker Picker => _picker;
    internal TextBlock ContrastText => _contrast;
    internal TextBlock StateText => _state;
    internal TextBlock HexError => _hexError;
    internal TextBlock ExtraText => _extra;
    internal bool IsPickerOpen => _flyout.IsOpen;
    /// <summary>The last view <see cref="Refresh"/> showed.</summary>
    internal ThemeRoleView? View => _view;

    /// <summary>Whether the hex field holds text the user typed that is not (yet) a valid colour.</summary>
    internal bool HasPendingInvalidText => _hexError.IsVisible;

    /// <summary>Shows <paramref name="view"/>: swatch, hex, state, contrast sentence, reset button and the accessible names.</summary>
    internal void Refresh(ThemeRoleView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        _view = view;
        string name = ThemeColorsText.RoleName(_role.Id);
        _name.Text = name;
        _state.Text = ThemeColorsText.State(view);
        _swatchFill.Background = new SolidColorBrush(view.Color);
        // Typing keeps its own text while the field has the focus and holds the colour that is shown.
        if (!(_hex.IsFocused && ThemeColorMath.TryParse(_hex.Text, out Color typed) && typed == view.Color) && !HasPendingInvalidText)
        {
            _syncing = true;
            try { _hex.Text = view.Hex; }
            finally { _syncing = false; }
        }
        if (_picker.Color != view.Color && !_flyout.IsOpen) _picker.Color = view.Color;
        _contrast.Text = ThemeColorsText.Contrast(view);
        // Weak pairs read as an error line; a good one stays muted. The text says it either way.
        SetClass(_contrast, "workspace-error", !view.PairPasses);
        SetClass(_contrast, "muted", view.PairPasses);
        string against = ThemeColorsText.Against(view);
        AutomationProperties.SetHelpText(_contrast, against);
        ToolTip.SetTip(_contrast, against);
        _extra.Text = ThemeColorsText.Extra(view);
        _extra.IsVisible = _extra.Text.Length > 0;
        string swatchName = Texts.T("theme.colors.choose", name, view.Hex);
        AutomationProperties.SetName(_swatch, swatchName);
        ToolTip.SetTip(_swatch, swatchName);
        AutomationProperties.SetName(_hex, Texts.T("theme.colors.hex", name));
        ToolTip.SetTip(_hex, Texts.T("theme.colors.hex", name));
        string resetName = Texts.T("theme.colors.reset", name);
        IconButtons.Apply(_reset, "rotate-ccw", resetName);
        _reset.IsEnabled = view.IsCustom;
        AutomationProperties.SetName(_picker, Texts.T("theme.colors.picker.name", name));
        _picker.ApplyTexts();
        if (HasPendingInvalidText) _hexError.Text = Texts.T("theme.colors.hexInvalid", view.Hex);
    }

    private static void SetClass(Control control, string name, bool on)
    {
        if (on && !control.Classes.Contains(name)) control.Classes.Add(name);
        else if (!on) control.Classes.Remove(name);
    }

    private void OnHexTyped()
    {
        if (_syncing || _view == null) return;
        string? text = _hex.Text;
        if (ThemeColorMath.TryParse(text, out Color color))
        {
            _hexError.IsVisible = false;
            _hexError.Text = string.Empty;
            ColorChosen?.Invoke(this, color);
        }
        else
        {
            _hexError.Text = Texts.T("theme.colors.hexInvalid", _view.Hex); // the colour shown now doubles as the example
            _hexError.IsVisible = true;
        }
    }

    /// <summary>Puts the colour that is shown back into the field and clears the error (focus left the field, or Escape).</summary>
    internal void RestoreHex()
    {
        _hexError.IsVisible = false;
        _hexError.Text = string.Empty;
        if (_view == null) return;
        _syncing = true;
        try { _hex.Text = _view.Hex; }
        finally { _syncing = false; }
    }

    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        // Escape first takes back an entry that is not a colour; with a valid field it belongs to the dialog.
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || !HasPendingInvalidText) return;
        RestoreHex();
        _hex.SelectAll();
        e.Handled = true;
    }

    /// <summary>Opens or closes the picker without a pointer (tests, smokes).</summary>
    internal void SetPickerOpen(bool open)
    {
        if (open == _flyout.IsOpen) return;
        if (open) _flyout.ShowAt(_swatch);
        else _flyout.Hide();
    }
}
