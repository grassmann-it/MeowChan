using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Theming;

namespace MeowChan.Desktop.Controls;

/// <summary>Sizes of the colour dialog and its picker, all on the 8 dip grid (TH3).</summary>
internal static class ThemeColorsMetrics
{
    /// <summary>The square swatch button of a row and of a preset (the input height).</summary>
    internal const double Swatch = 40;
    /// <summary>Height of the two-tone swatch of an accent preset (the width is <see cref="Swatch"/>).</summary>
    internal const double PresetSwatchHeight = 24;
    /// <summary>The hex field of a row.</summary>
    internal const double HexField = 104;
    /// <summary>The picker's width: three sliders and the sample.</summary>
    internal const double PickerWidth = 280;
    /// <summary>The decorative gradient under a slider.</summary>
    internal const double ScaleStrip = 8;
    /// <summary>The sample of the chosen colour in the picker.</summary>
    internal const double PickerSample = 32;
    /// <summary>The width of the live preview card beside the role list.</summary>
    internal const double PreviewWidth = 280;
    /// <summary>Space between the role list and the preview card.</summary>
    internal const double ColumnGap = 16;
    /// <summary>Width of the readout of a slider value ("360°", "100 %").</summary>
    internal const double ReadoutWidth = 48;
    /// <summary>One slider step moves the value by this much; Page Up/Down by the large step.</summary>
    internal const double SliderStep = 1, SliderLargeStep = 8;
}

/// <summary>
/// TH3: our own small colour control, no package: a hue slider, a saturation slider and a brightness slider (each moves with the arrow keys,
/// Page Up/Down and Home/End like any slider), a sample of the chosen colour with its <c>#RRGGBB</c> and a "Done" action. The thin gradient under
/// each slider shows what it will do to the colour and is decoration only. <see cref="ColorChanged"/> follows every change the
/// user makes; setting <see cref="Color"/> from code never raises it. The picker keeps its own hue while the user drags, so a grey
/// or black colour does not throw the hue away.
/// </summary>
internal sealed class ThemeColorPicker : StackPanel
{
    private readonly Slider _hue = new() { Name = "ThemeColorPickerHue", Minimum = 0, Maximum = 359, SmallChange = ThemeColorsMetrics.SliderStep, LargeChange = ThemeColorsMetrics.SliderLargeStep };
    private readonly Slider _saturation = new() { Name = "ThemeColorPickerSaturation", Minimum = 0, Maximum = 100, SmallChange = ThemeColorsMetrics.SliderStep, LargeChange = ThemeColorsMetrics.SliderLargeStep * 2 };
    private readonly Slider _brightness = new() { Name = "ThemeColorPickerBrightness", Minimum = 0, Maximum = 100, SmallChange = ThemeColorsMetrics.SliderStep, LargeChange = ThemeColorsMetrics.SliderLargeStep * 2 };
    private readonly TextBlock _hueLabel = new(), _saturationLabel = new(), _brightnessLabel = new();
    private readonly TextBlock _hueReadout = new(), _saturationReadout = new(), _brightnessReadout = new(), _hexText = new() { Name = "ThemeColorPickerHex" };
    private readonly Border _hueStrip = Strip(), _saturationStrip = Strip(), _brightnessStrip = Strip();
    private readonly Border _sample = new() { Name = "ThemeColorPickerSample", Width = ThemeColorsMetrics.PickerSample, Height = ThemeColorsMetrics.PickerSample, BorderThickness = new Thickness(1) };
    private readonly Button _done = new() { Name = "ThemeColorPickerDone", HorizontalAlignment = HorizontalAlignment.Right };
    private bool _updating;
    private Color _color;

    internal ThemeColorPicker()
    {
        Spacing = 12;
        Width = ThemeColorsMetrics.PickerWidth;
        _sample[!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Control");
        _sample[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.ControlBorder");
        AutomationProperties.SetAccessibilityView(_hueStrip, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_saturationStrip, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_brightnessStrip, AccessibilityView.Raw);
        Grid sampleRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        sampleRow.Children.Add(_sample);
        _hexText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_hexText, 1); sampleRow.Children.Add(_hexText);
        Grid.SetColumn(_done, 2); sampleRow.Children.Add(_done);
        Children.Add(sampleRow);
        Children.Add(Row(_hueLabel, _hueReadout, _hue, _hueStrip));
        Children.Add(Row(_saturationLabel, _saturationReadout, _saturation, _saturationStrip));
        Children.Add(Row(_brightnessLabel, _brightnessReadout, _brightness, _brightnessStrip));
        _hue.ValueChanged += (_, _) => OnSlider();
        _saturation.ValueChanged += (_, _) => OnSlider();
        _brightness.ValueChanged += (_, _) => OnSlider();
        _done.Click += (_, _) => DoneRequested?.Invoke(this, EventArgs.Empty);
        ApplyTexts();
        Show(Opaque(default));
    }

    /// <summary>Raised after the user changed the colour with a slider.</summary>
    internal event EventHandler? ColorChanged;

    /// <summary>The user asked to close the picker (the Done button).</summary>
    internal event EventHandler? DoneRequested;

    internal Slider HueSlider => _hue;
    internal Slider SaturationSlider => _saturation;
    internal Slider BrightnessSlider => _brightness;
    internal Button DoneButton => _done;
    internal TextBlock HexText => _hexText;

    /// <summary>The chosen colour (opaque). Setting it moves the sliders without raising <see cref="ColorChanged"/>.</summary>
    internal Color Color
    {
        get => _color;
        set
        {
            Color opaque = Opaque(value);
            // The sliders already describe this colour (a grey keeps its hue): leave them where the user put them.
            Show(opaque, keepSliders: ThemeColorMath.FromHsv(CurrentHsv()) == opaque);
        }
    }

    /// <summary>The sliders as one HSV reading (hue 0 to 359 whole degrees, saturation and brightness in whole percent).</summary>
    internal PickerHsv CurrentHsv() => new(_hue.Value, _saturation.Value / 100, _brightness.Value / 100);

    /// <summary>Sets the language-dependent texts; called again after a language change.</summary>
    internal void ApplyTexts()
    {
        _hueLabel.Text = Texts.T("theme.colors.picker.hue");
        _saturationLabel.Text = Texts.T("theme.colors.picker.saturation");
        _brightnessLabel.Text = Texts.T("theme.colors.picker.brightness");
        AutomationProperties.SetName(_hue, _hueLabel.Text);
        AutomationProperties.SetName(_saturation, _saturationLabel.Text);
        AutomationProperties.SetName(_brightness, _brightnessLabel.Text);
        _done.Content = Texts.T("theme.colors.picker.done");
        AutomationProperties.SetName(_done, _done.Content.ToString());
        UpdateReadouts();
        AutomationProperties.SetName(_sample, Texts.T("theme.colors.picker.sample") + " " + ThemeColorMath.Format(_color));
    }

    private void Show(Color color, bool keepSliders = false)
    {
        _color = color;
        _updating = true;
        try
        {
            if (!keepSliders)
            {
                PickerHsv hsv = ThemeColorMath.ToHsv(color);
                // A grey or black colour has no hue of its own: keep the one the slider shows.
                _hue.Value = hsv.Saturation > 0 && hsv.Value > 0 ? Math.Round(hsv.Hue) % 360 : _hue.Value;
                _saturation.Value = Math.Round(hsv.Saturation * 100);
                _brightness.Value = Math.Round(hsv.Value * 100);
            }
            Redraw();
        }
        finally { _updating = false; }
    }

    private void OnSlider()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            _color = ThemeColorMath.FromHsv(CurrentHsv());
            Redraw();
        }
        finally { _updating = false; }
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Redraw()
    {
        _sample.Background = new SolidColorBrush(_color);
        _hexText.Text = ThemeColorMath.Format(_color);
        AutomationProperties.SetName(_sample, Texts.T("theme.colors.picker.sample") + " " + _hexText.Text);
        PickerHsv hsv = CurrentHsv();
        _hueStrip.Background = Gradient(Enumerable.Range(0, 7).Select(step => ThemeColorMath.FromHsv(new PickerHsv(step * 60, 1, 1))).ToArray());
        _saturationStrip.Background = Gradient(ThemeColorMath.FromHsv(hsv with { Saturation = 0 }), ThemeColorMath.FromHsv(hsv with { Saturation = 1 }));
        _brightnessStrip.Background = Gradient(ThemeColorMath.FromHsv(hsv with { Value = 0 }), ThemeColorMath.FromHsv(hsv with { Value = 1 }));
        UpdateReadouts();
    }

    private void UpdateReadouts()
    {
        _hueReadout.Text = Texts.T("theme.colors.picker.degrees", (int)_hue.Value);
        _saturationReadout.Text = Texts.T("theme.colors.picker.percent", (int)_saturation.Value);
        _brightnessReadout.Text = Texts.T("theme.colors.picker.percent", (int)_brightness.Value);
    }

    private static Color Opaque(Color color) => ThemeCustomization.ParseHex(ThemeColorMath.Format(color));

    private static Border Strip() => new()
    {
        Height = ThemeColorsMetrics.ScaleStrip, Margin = new Thickness(8, 0), IsHitTestVisible = false, Focusable = false,
        [!Border.CornerRadiusProperty] = new DynamicResourceExtension("Workspace.Radius.Badge")
    };

    private static IBrush Gradient(params Color[] colors)
    {
        LinearGradientBrush brush = new() { StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
        for (int index = 0; index < colors.Length; index++)
            brush.GradientStops.Add(new GradientStop(colors[index], colors.Length == 1 ? 0 : index / (double)(colors.Length - 1)));
        return brush;
    }

    private static Control Row(TextBlock label, TextBlock readout, Slider slider, Border strip)
    {
        label.Classes.Add("workspace-field-label");
        readout.Classes.Add("muted");
        readout.Width = ThemeColorsMetrics.ReadoutWidth;
        readout.TextAlignment = TextAlignment.Right;
        Grid head = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(label);
        Grid.SetColumn(readout, 1); head.Children.Add(readout);
        return new StackPanel { Spacing = 2, Children = { head, slider, strip } };
    }
}
