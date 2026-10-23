using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.ImageViewerComparison;

internal sealed record Options(string Fixtures, string Output, string Language, string Theme,
    string Mode, bool Auto, bool ChecksOnly, string Backend)
{
    internal static Options Parse(string[] args)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        bool auto = false, checksOnly = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--auto") { auto = true; continue; }
            if (args[i] == "--checks-only") { auto = true; checksOnly = true; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 == args.Length)
                throw new ArgumentException("Expected --fixtures PATH --output PATH [--auto] [--language en|de] [--theme dark|light] [--mode b|s|a] [--backend-description TEXT]");
            values.Add(args[i], args[++i]);
        }
        string Get(string key, string fallback) => values.GetValueOrDefault(key, fallback);
        string fixtures = Get("--fixtures", "");
        string output = Get("--output", "");
        string language = Get("--language", "en"), theme = Get("--theme", "dark"), mode = Get("--mode", "b");
        if (fixtures.Length == 0 || output.Length == 0 || language is not ("en" or "de")
            || theme is not ("dark" or "light") || mode is not ("b" or "s" or "a"))
            throw new ArgumentException("Explicit fixture/output directories and valid language/theme/mode are required.");
        if (!Directory.Exists(fixtures)) throw new DirectoryNotFoundException(fixtures);
        if (Directory.Exists(output)) throw new IOException("Output must be a new directory; existing evidence is never overwritten.");
        return new(Path.GetFullPath(fixtures), Path.GetFullPath(output), language, theme, mode, auto, checksOnly,
            Get("--backend-description", "UNRECORDED: supply GPU/driver/software backend before decision"));
    }
}

internal static class Program
{
    internal static Options Options { get; private set; } = null!;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Options = Options.Parse(args);
            Directory.CreateDirectory(Options.Output);
            MeowChan.Desktop.Localization.Texts.Language = Options.Language;
            return AppBuilder.Configure<ComparisonApp>().UsePlatformDetect().LogToTrace()
                .StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
    }
}

public sealed class ComparisonApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = Program.Options.Theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ComparisonWindow window = new(Program.Options);
            desktop.MainWindow = window;
            if (Program.Options.Auto)
                window.Opened += async (_, _) =>
                {
                    int result = 1;
                    try { await window.LoadFixturesAsync(); result = await new ComparisonRun(window, Program.Options).RunAsync(); }
                    catch (Exception error)
                    {
                        Console.Error.WriteLine(error);
                        await File.WriteAllTextAsync(Path.Combine(Program.Options.Output, "fatal.txt"), error.ToString());
                    }
                    finally { window.ClosePreview(); desktop.Shutdown(result); }
                };
            else window.Opened += async (_, _) => await window.LoadFixturesAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed record FixtureCase(string Name, bool ExpectedError, string Sha256, long Length);

internal sealed class BitmapOwner : IDisposable
{
    internal static int Count { get; private set; }
    internal static long Bytes { get; private set; }
    private bool _disposed;
    internal Bitmap Bitmap { get; }
    internal long ByteCount => (long)Bitmap.PixelSize.Width * Bitmap.PixelSize.Height * 4;
    internal BitmapOwner(Bitmap bitmap) { Bitmap = bitmap; Count++; Bytes += ByteCount; }
    public void Dispose()
    {
        if (_disposed) throw new InvalidOperationException("Bitmap disposed twice.");
        _disposed = true; Count--; Bytes -= ByteCount; Bitmap.Dispose();
    }
}

internal sealed class ComparisonWindow : Window
{
    private readonly Options _options;
    private readonly IComparisonView[] _views = { new ExistingView(), new UrsaView() };
    private readonly ComboBox _variant = new() { ItemsSource = new[] { "A · Image/Transform", "B · Ursa ImageViewer 2.2.0" }, SelectedIndex = 0, MinWidth = 190 };
    private readonly ComboBox _fixture = new() { MinWidth = 150 };
    internal readonly Button OpenButton = new() { Name = "ComparisonOpen" };
    internal readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap };
    internal readonly Grid Viewport = new() { Name = "ComparisonViewport", ClipToBounds = true, Background = Brushes.Transparent };
    private readonly List<Button> _actions = new();
    private CancellationTokenSource? _pending;
    private BitmapOwner? _manualOwner;
    private int _generation;
    internal IReadOnlyList<FixtureCase> Cases { get; private set; } = Array.Empty<FixtureCase>();
    private MediaAssetSnapshot _manifest = null!;
    internal IComparisonView? Active { get; private set; }
    internal int Variant => _variant.SelectedIndex;
    internal string T(string en, string de) => _options.Language == "de" ? de : en;
    internal ComparisonWindow(Options options)
    {
        _options = options;
        Title = "URSA-21 · A/B · " + options.Language + " · " + options.Mode;
        Width = options.Mode == "b" ? 640 : 760; Height = options.Mode == "b" ? 480 : 500;
        MinWidth = 420; MinHeight = 320;
        Grid root = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(8), RowSpacing = 6 };
        WrapPanel source = new() { ItemSpacing = 6, LineSpacing = 4 };
        OpenButton.Content = T("Open", "Öffnen");
        AutomationProperties.SetName(_variant, T("Comparison variant", "Vergleichsvariante"));
        AutomationProperties.SetName(_fixture, T("Local fixture", "Lokales Prüfbild"));
        AutomationProperties.SetName(OpenButton, T("Open local preview", "Lokale Vorschau öffnen"));
        source.Children.Add(_variant); source.Children.Add(_fixture); source.Children.Add(OpenButton);
        root.Children.Add(source);
        WrapPanel toolbar = new() { ItemSpacing = 6, LineSpacing = 4 };
        AddAction(toolbar, "Fit", T("Fit", "Einpassen"), () => Active?.Fit());
        AddAction(toolbar, "Actual", "100 %", () => Active?.Actual());
        AddAction(toolbar, "ZoomOut", T("Zoom out", "Verkleinern"), () => Active?.ZoomBy(1 / 1.1));
        AddAction(toolbar, "ZoomIn", T("Zoom in", "Vergrößern"), () => Active?.ZoomBy(1.1));
        AddAction(toolbar, "Close", T("Close", "Schließen"), ClosePreview);
        root.Children.Add(toolbar); Grid.SetRow(toolbar, 1);
        Border frame = new() { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = Viewport, ClipToBounds = true };
        root.Children.Add(frame); Grid.SetRow(frame, 2);
        root.Children.Add(Status); Grid.SetRow(Status, 3);
        Content = root;
        SetActions(false);
        OpenButton.Click += async (_, _) => await OpenSelectedAsync();
        _variant.SelectionChanged += (_, _) => ClosePreview();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape) return;
            ClosePreview(); e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Closed += (_, _) => ClosePreview();
        if (!options.Auto)
        {
            // Read-only native-test telemetry, confined to this explicit evidence
            // directory. Disabled during timed runs to avoid measurement I/O.
            DispatcherTimer state = new() { Interval = TimeSpan.FromMilliseconds(100) };
            state.Tick += (_, _) => File.WriteAllText(Path.Combine(options.Output, "native-state.json"),
                JsonSerializer.Serialize(new { variant = Variant, fixture = _fixture.SelectedItem,
                    active = Active != null, physicalZoom = Active?.PhysicalZoom, offset = Active?.Offset,
                    viewport = Viewport.Bounds.ToString(), scale = RenderScaling,
                    openerFocused = OpenButton.IsFocused, imageFocusWithin = Active?.Control.IsKeyboardFocusWithin,
                    sourcesClear = SourcesClear, heldBitmaps = BitmapOwner.Count,
                    heldBytes = BitmapOwner.Bytes, status = Status.Text }));
            Opened += (_, _) => state.Start(); Closed += (_, _) => state.Stop();
        }
    }
    private void AddAction(Panel toolbar, string name, string label, Action action)
    {
        Button button = new() { Name = "Comparison" + name, Content = label };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => action(); toolbar.Children.Add(button); _actions.Add(button);
    }
    private void SetActions(bool enabled) { foreach (Button button in _actions) button.IsEnabled = enabled; }
    internal async Task LoadFixturesAsync()
    {
        Cases = JsonSerializer.Deserialize<FixtureCase[]>(await File.ReadAllTextAsync(Path.Combine(_options.Fixtures,
            "comparison-fixtures.json")), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        _manifest = await MediaAssetReader.OpenAsync(_options.Fixtures);
        if (_manifest.Status != MediaManifestStatus.Available || _manifest.Count != Cases.Count)
            throw new InvalidDataException("Fixture manifest must be available and complete.");
        _fixture.ItemsSource = Cases.Select(c => c.Name).ToArray(); _fixture.SelectedIndex = 0;
        Status.Text = T("Offline comparison; select one image and variant.", "Offlinevergleich; Bild und Variante wählen.");
    }
    internal GalleryEntry Entry(FixtureCase fixture)
    {
        MediaAssetReference asset = _manifest.GetPage().Single(a => a.OriginalFileName == fixture.Name);
        if (asset.Sha256 != fixture.Sha256 || asset.Length != fixture.Length) throw new InvalidDataException("Fixture identity changed.");
        return new(new(new ResourceId("fixture", "offline", "21"), _manifest.Directory, asset.AssetId), _manifest,
            asset, null, null, false, true);
    }
    internal async Task<BitmapOwner> DecodeAsync(FixtureCase fixture, CancellationToken token)
    {
        // Both A and B use exactly this entry point and request. 100% always has
        // original pixels, with the unchanged 64MiB/80M limits and serialized gate.
        var decoded = await GalleryImageDecoder.DecodeAsync(Entry(fixture), new GalleryDecodeRequest.Original(), token);
        return new BitmapOwner(decoded.Bitmap);
    }
    internal async Task OpenSelectedAsync()
    {
        ClosePreview();
        if (_fixture.SelectedIndex < 0) return;
        int generation = _generation;
        CancellationTokenSource pending = new(); _pending = pending;
        Status.Text = T("Loading…", "Wird geladen…");
        // Close remains available during a decode, including the noncancelable codec.
        _actions[^1].IsEnabled = true;
        try
        {
            BitmapOwner owner = await DecodeAsync(Cases[_fixture.SelectedIndex], pending.Token);
            if (generation != _generation || pending.IsCancellationRequested) { owner.Dispose(); return; }
            _manualOwner = owner; ShowBorrowed(Variant, owner.Bitmap);
        }
        catch (OperationCanceledException) { if (generation == _generation) Status.Text = T("Canceled.", "Abgebrochen."); }
        catch (Exception error) { if (generation == _generation) { ShowError(error); _actions[^1].IsEnabled = true; } }
        finally { if (ReferenceEquals(_pending, pending)) _pending = null; pending.Dispose(); }
    }
    internal void ShowError(Exception error) { Status.Text = error.Message; SetActions(false); }
    internal void ShowBorrowed(int variant, Bitmap bitmap)
    {
        _variant.SelectedIndex = variant;
        Detach(); Active = _views[variant]; Viewport.Children.Add(Active.Control);
        Active.Source = bitmap; Active.Fit(); Active.Control.Focus(); SetActions(true);
        Status.Text = T("Preview open. Escape closes; arrows pan the focused image.", "Vorschau offen. Escape schließt; Pfeile verschieben das fokussierte Bild.");
    }
    private void Detach()
    {
        // Clear BOTH sources before owner disposal; only one view is ever attached.
        foreach (IComparisonView view in _views) view.Source = null;
        Viewport.Children.Clear(); Active = null;
    }
    internal void ClosePreview()
    {
        _generation++; _pending?.Cancel(); Detach(); _manualOwner?.Dispose(); _manualOwner = null;
        SetActions(false); Status.Text = T("Preview closed.", "Vorschau geschlossen."); OpenButton.Focus();
    }
    internal bool SourcesClear => _views.All(v => v.Source == null) && Viewport.Children.Count == 0;
    internal void SelectFixture(FixtureCase fixture) => _fixture.SelectedIndex = Cases.ToList().IndexOf(fixture);
    internal async Task FrameAsync()
    {
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestAnimationFrame(_ => RequestAnimationFrame(_ => ready.TrySetResult()));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    internal void Screenshot(string path)
    {
        using RenderTargetBitmap image = new(new PixelSize((int)Math.Ceiling(Viewport.Bounds.Width * RenderScaling),
            (int)Math.Ceiling(Viewport.Bounds.Height * RenderScaling)), new Vector(96 * RenderScaling, 96 * RenderScaling));
        image.Render(Viewport); image.Save(path);
    }
}
