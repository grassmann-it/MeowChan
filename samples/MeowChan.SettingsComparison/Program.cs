using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using Ursa.Controls;
using AvaloniaNumeric = Avalonia.Controls.NumericUpDown;

namespace MeowChan.SettingsComparison;

internal static class Program
{
    internal static bool Automatic;
    internal static readonly List<object> Results = new();
    internal static readonly string Profile = Path.Combine(Path.GetTempPath(), "meowchan-settings-comparison-" + Guid.NewGuid().ToString("N"));
    [STAThread] public static int Main(string[] args)
    {
        Automatic = args.Contains("--numeric-matrix");
        Environment.SetEnvironmentVariable("MEOWCHAN_DISABLE_UPDATE_CHECK", "1");
        // Never discover or open the user's profile.
        Environment.SetEnvironmentVariable("MEOWCHAN_PROFILE", Profile);
        return AppBuilder.Configure<ComparisonApp>().UsePlatformDetect().WithInterFont().StartWithClassicDesktopLifetime(args);
    }
}

public sealed class ComparisonApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ComparisonWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

public sealed class ComparisonWindow : Window
{
    private readonly ContentControl _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _save = new() { Content = "Save / Speichern" };
    private readonly Button _discard = new() { Content = "Discard / Verwerfen" };
    private readonly CheckBox _storageFailure = new() { Content = "Simulate save failure" };
    private TextBlock? _numericLabel;
    private Control? _number;
    private TextBox? _editor;
    private Config _config;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private string _variant = "A";
    private string _culture = "en-US";
    private bool _numeric;
    public ComparisonWindow()
    {
        Title = "URSA 17 / 22 — isolated comparison"; Width = 900; Height = 700; MinWidth = 640; MinHeight = 480;
        _config = Config.LoadConfig(Program.Profile);
        var toolbar = new WrapPanel();
        foreach (string variant in new[] { "A", "B", "C" })
        {
            string choice = variant;
            var button = new Button { Content = choice, Margin = new Thickness(4) };
            button.Click += (_, _) => { _variant = choice; ShowCurrent(); }; toolbar.Children.Add(button);
        }
        var numbers = new Button { Content = "RecheckTimer A/B/C", Margin = new Thickness(4) };
        numbers.Click += (_, _) => { _numeric = true; ShowCurrent(); }; toolbar.Children.Add(numbers);
        var sections = new Button { Content = "Integrated Settings A/B/C", Margin = new Thickness(4) };
        sections.Click += (_, _) => { _numeric = false; ShowCurrent(); }; toolbar.Children.Add(sections);
        foreach (string culture in new[] { "en-US", "de-DE" })
        {
            string choice = culture;
            var button = new Button { Content = culture, Margin = new Thickness(4) };
            button.Click += (_, _) => { _culture = choice; ChangeCulture(); }; toolbar.Children.Add(button);
        }
        var language = new Button { Content = "EN / DE", Margin = new Thickness(4) };
        language.Click += (_, _) => { Texts.SetLanguage(Texts.Language == "en" ? "de" : "en"); ChangeCulture(); }; toolbar.Children.Add(language);
        var theme = new Button { Content = "Light / Dark", Margin = new Thickness(4) };
        theme.Click += (_, _) => RequestedThemeVariant = RequestedThemeVariant == Avalonia.Styling.ThemeVariant.Light
            ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light; toolbar.Children.Add(theme);
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(8) };
        root.Children.Add(toolbar); Grid.SetRow(_body, 1); root.Children.Add(_body); Grid.SetRow(_status, 2); root.Children.Add(_status); Content = root;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        _save.Click += (_, _) => SaveNumber();
        _discard.Click += (_, _) => ShowNumber();
        Opened += async (_, _) =>
        {
            if (!Program.Automatic) return;
            try { await RunMatrix(); Console.WriteLine(JsonSerializer.Serialize(Program.Results, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { Close(); }
        };
        Closed += (_, _) => { if (Directory.Exists(Program.Profile)) Directory.Delete(Program.Profile, true); };
        ShowCurrent();
    }
    private void ShowCurrent() { if (_numeric) ShowNumber(); else ShowSettings(); }
    private void ChangeCulture()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(_culture);
        if (_numericLabel != null) _numericLabel.Text = Texts.T("settings.recheckSeconds");
        if (_number != null) AutomationProperties.SetName(_number, Texts.T("settings.recheckSeconds"));
        if (_editor != null) AutomationProperties.SetName(_editor, Texts.T("settings.recheckSeconds"));
        if (_number is AvaloniaNumeric a) a.NumberFormat = CultureInfo.CurrentCulture.NumberFormat;
        if (_number is NumericIntUpDown b) b.NumberFormat = CultureInfo.CurrentCulture.NumberFormat;
        _status.Text = $"Variant {_variant}, {_culture}, app {Texts.Language}. Profile: {Program.Profile}";
    }
    private void ShowNumber()
    {
        if (_body.Content is SettingsEditor previous) previous.EndLifetime();
        foreach (Control reused in new Control[] { _save, _discard, _error, _storageFailure })
            if (reused.Parent is Panel parent) parent.Children.Remove(reused);
        if (_variant == "A")
            _number = new SettingsNumberInput { Minimum = 10, Maximum = 4294967, Increment = 1, Value = _config.RecheckTimer };
        else if (_variant == "C") _number = new AvaloniaNumeric { Minimum = 10, Maximum = 4294967, Increment = 1, Value = _config.RecheckTimer };
        else _number = new UrsaRecheckInput { Minimum = 10, Maximum = 4294967, Step = 1, Value = (int)_config.RecheckTimer };
        _number.Name = "ComparisonRecheckTimer";
        AutomationProperties.SetName(_number, Texts.T("settings.recheckSeconds"));
        AutomationProperties.SetHelpText(_number, "Seconds; 10 … 4294967");
        _error.Text = null;
        _numericLabel = new TextBlock { Text = Texts.T("settings.recheckSeconds"), FontWeight = FontWeight.SemiBold };
        var actions = new WrapPanel { Children = { _save, _discard } };
        _body.Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, Children = {
            _numericLabel, _number,
            new TextBlock { Text = "Seconds; 10 … 4294967" }, _error, _storageFailure, actions,
            new TextBlock { Text = "A: shipped SettingsNumberInput. B: Ursa NumericIntUpDown with bounded raw-text/focus adapter. C: native Avalonia NumericUpDown with existing raw/focus overrides removed (bounded simplification experiment).", TextWrapping = TextWrapping.Wrap } } };
        ChangeCulture();
        Dispatcher.UIThread.Post(() =>
        {
            _number.ApplyTemplate();
            _editor = _number.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
            AutomationProperties.SetName(_editor, Texts.T("settings.recheckSeconds"));
            AutomationProperties.SetHelpText(_editor, "Seconds; 10 … 4294967");
            foreach (Button button in _number.GetVisualDescendants().OfType<Button>())
                AutomationProperties.SetName(button, (button.Name?.Contains("Increase") == true ? "Increase " : "Decrease ") + Texts.T("settings.recheckSeconds"));
        }, DispatcherPriority.Loaded);
    }
    private string Raw => _number is AvaloniaNumeric a ? a.Text ?? "" : ((UrsaRecheckInput)_number!).RawText ?? "";
    private object? Value => _number is AvaloniaNumeric a ? a.Value : ((UrsaRecheckInput)_number!).Value;
    private bool SaveNumber()
    {
        bool valid = decimal.TryParse(Raw, NumberStyles.Any, CultureInfo.GetCultureInfo(_culture), out decimal value)
            && decimal.Truncate(value) == value && value >= 10 && value <= 4294967;
        if (!valid) { _error.Text = Texts.T("settings.fieldInteger", Texts.T("settings.recheckSeconds"), 10, 4294967); if (_editor != null) AutomationProperties.SetHelpText(_editor, _error.Text); _editor?.Focus(); return false; }
        try
        {
            if (_storageFailure.IsChecked == true) throw new IOException("Synthetic save failure");
            Config candidate = _config.Clone(); candidate.RecheckTimer = (int)value; candidate.SaveConfig(); _config.CopyFrom(candidate);
            _error.Text = null; return true;
        }
        catch (Exception error) { _error.Text = error.Message; return false; }
    }
    private void ShowSettings()
    {
        if (_body.Content is SettingsEditor previous) previous.EndLifetime();
        var settings = new SettingsEditor(_config, _ => Task.CompletedTask);
        SectionNavigationControl navigation = settings.FindControl<SectionNavigationControl>("SettingsSections")!;
        if (_variant == "A")
        {
            navigation.IsVisible = false;
            navigation.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && navigation.IsVisible) navigation.IsVisible = false; };
            settings.AttachedToVisualTree += (_, _) => navigation.IsVisible = false;
        }
        if (_variant == "C")
        {
            var scroll = settings.FindControl<ScrollViewer>("SettingsScrollViewer")!;
            var controller = new SectionNavigationController(scroll, () => settings.IsEditable);
            var descriptors = new List<SectionDescriptor>();
            foreach (var pair in new[] { ("settings.interface","SettingsInterfaceHeading"), ("settings.reading-media","SettingsReadingMediaHeading"),
                ("settings.downloads","SettingsDownloadsHeading"), ("settings.updates","SettingsUpdatesHeading"), ("settings.behavior","SettingsBehaviorHeading"),
                ("settings.notifications","SettingsNotificationsHeading"), ("settings.shortcuts","ShortcutsEditor") })
            {
                Control? target = settings.FindControl<Control>(pair.Item2);
                if (target == null) continue;
                Ursa.Controls.Anchor.SetId(target, pair.Item1);
                string title = target is TextBlock text ? text.Text ?? pair.Item1 : pair.Item1;
                descriptors.Add(new(pair.Item1, title, () => settings.FindControl<Control>(pair.Item2)));
            }
            controller.SetSections(descriptors);
            var anchor = new ComparisonAnchor { TargetContainer = scroll, Navigate = controller.NavigateTo };
            foreach (var section in descriptors) anchor.Items.Add(new AnchorItem { AnchorId = section.Id, Header = section.Title, Focusable = true });
            object? compactContents = navigation.Content;
            navigation.Content = anchor;
            navigation.SizeChanged += (_, _) =>
            {
                object? desired = navigation.Bounds.Width < 560 ? compactContents : anchor;
                if (!ReferenceEquals(navigation.Content, desired)) navigation.Content = desired;
                anchor.InvalidatePositions();
            };
            scroll.ScrollChanged += (_, e) => { if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0) anchor.InvalidatePositions(); };
            anchor.Loaded += (_, _) => anchor.InvalidatePositions();
            settings.DetachedFromVisualTree += (_, _) => controller.Dispose();
        }
        _body.Content = settings;
        _status.Text = $"Sections {_variant}: A headings only; B compact contents; C Ursa Anchor with public activation adapter. Actual integrated SettingsEditor, same fields and draft. {_culture}.";
    }
    private async Task RunMatrix()
    {
        _numeric = true;
        foreach (string culture in new[] { "en-US", "de-DE" })
        foreach (string variant in new[] { "A", "B", "C" })
        foreach (string fixture in new[] { "10", "4294967", "9", "4294968", "", " ", "-", "letters", new string('9', 80), "1,000", "1.000", "60.5", "60,5" })
        {
            _culture = culture; _variant = variant; ShowNumber(); await Task.Delay(60);
            _editor!.Focus(); _editor.Text = fixture; await Task.Delay(20);
            string before = Raw; object? valueBefore = Value; int configBefore = _config.RecheckTimer;
            _save.Focus(); await Task.Delay(30); string afterBlur = Raw;
            bool valid = decimal.TryParse(fixture, NumberStyles.Any, CultureInfo.GetCultureInfo(culture), out decimal expected)
                && decimal.Truncate(expected) == expected && expected >= 10 && expected <= 4294967;
            bool saved = SaveNumber();
            Program.Results.Add(new { variant, culture, fixture, before, valueBefore, afterBlur, afterSave = Raw, valueAfter = Value,
                configBefore, configAfter = _config.RecheckTimer, reloadedConfig = Config.LoadConfig(Program.Profile).RecheckTimer, saved, expectedValid = valid, error = _error.Text,
                focused = (FocusManager?.GetFocusedElement() as Control)?.Name,
                rawGate = valid || (before == fixture && afterBlur == fixture && Raw == fixture),
                saveGate = saved == valid && (valid ? _config.RecheckTimer == expected : _config.RecheckTimer == configBefore),
                nativeIme = "not run", nativeAT = "not run" });
        }
        foreach (string variant in new[] { "A", "B", "C" })
        {
            _variant = variant; _culture = "en-US"; ShowNumber(); await Task.Delay(60);
            _editor!.Focus(); _editor.Text = "invalid culture draft"; _save.Focus();
            string before = Raw;
            _culture = "de-DE"; ChangeCulture();
            Program.Results.Add(new { variant, task = "culture-during-invalid-draft", before, after = Raw, value = Value,
                rawGate = Raw == "invalid culture draft", nativeIme = "not run", nativeAT = "not run" });
            foreach (Button spinner in _number!.GetVisualDescendants().OfType<Button>())
            {
                spinner.Focus();
                Program.Results.Add(new { variant, task = "explicit-spinner-focus", button = spinner.Name,
                    requestedFocused = spinner.IsFocused, actualFocus = (FocusManager?.GetFocusedElement() as Control)?.Name,
                    raw = Raw, nativeAT = "not run" });
            }
        }
    }
}
