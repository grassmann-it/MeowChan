using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

/// <summary>Captures actual live product windows; these are UI renders, not native screenshots/AT evidence.</summary>
internal static class PostBookmarkVisualEvidence
{
    internal static string? ComparisonFixtureDigest { get; set; }

    internal static async Task CaptureMatrixAsync(MainWindow owner, Window target, string scene, string? fixtureDigest = null)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_BOOKMARK_COMPARISON_CAPTURES") == "0") return;
        string language = Texts.Language, mode = owner.SessionHost.Config.WorkspaceMode;
        string configLanguage = owner.SessionHost.Config.Language, configTheme = owner.SessionHost.Config.Theme;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double expectedScale = double.TryParse(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double requestedScale) && requestedScale > 0
            ? requestedScale : owner.RenderScaling;
        double ownerWidth = owner.Width, ownerHeight = owner.Height, targetWidth = target.Width, targetHeight = target.Height;
        WindowState ownerState = owner.WindowState, targetState = target.WindowState;
        try
        {
            owner.WindowState = target.WindowState = WindowState.Normal;
            foreach (string nextLanguage in new[] { "en", "de" })
            foreach (ThemeVariant nextTheme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            foreach (string nextMode in new[] { "Barebones", "Simple", "Advanced" })
            foreach (bool wide in new[] { false, true })
            {
                await SwitchModeAsync(owner, nextMode);
                // Keep the owner Config coherent too: a queued ApplyAppearance must not
                // reset a DE/Light request back to an EN/Dark configuration.
                await owner.SessionHost.SaveViewPreferencesAsync(config =>
                { config.Language = nextLanguage; config.Theme = nextTheme.ToString(); });
                Texts.SetLanguage(nextLanguage); Application.Current.RequestedThemeVariant = nextTheme;
                double width = wide ? 1180 : nextMode == "Barebones" ? 640 : 760;
                double height = wide ? 820 : nextMode == "Barebones" ? 480 : 500;
                owner.Width = width; owner.Height = height; target.Width = width; target.Height = height;
                await SettleAsync(owner, target);
                (string actualLabel, string expectedLabel) = LocalizedLabel(target, nextLanguage);
                if (actualLabel != expectedLabel)
                    throw new InvalidOperationException($"Capture locale mismatch: expected {expectedLabel}, actual {actualLabel}.");
                await SettleAsync(owner, target);
                string stem = $"smoke-bookmark-{scene}-{nextLanguage}-{nextTheme.ToString().ToLowerInvariant()}-{nextMode.ToLowerInvariant()}-{(wide ? "wide" : "minimum")}.png";
                await WaitForCaptureAsync(owner, target, width, height, expectedScale, nextLanguage, nextTheme, stem);
                // Program.CaptureWindow logs the process-default profile mode, which may differ
                // from an injected child shell. Bind this capture to the actual supplied owner.
                // No await or further layout/input mutation between this check and the bitmap render.
                if (!CaptureReady(owner, target, width, height, expectedScale, nextLanguage, nextTheme))
                    throw new InvalidOperationException("Bookmark capture readiness changed before render: " + stem);
                double scale = target.RenderScaling;
                using (RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(target.Bounds.Width * scale),
                    (int)Math.Ceiling(target.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale)))
                {
                    bitmap.Render(target); bitmap.Save(Path.Combine(AppContext.BaseDirectory, stem), PngBitmapEncoderOptions.Default);
                }
                Console.WriteLine("SMOKE window render: " + Path.Combine(AppContext.BaseDirectory, stem));
                PostBookmarkComparisonChecks.Log(new { task = "visual-scene", scene, file = stem,
                    source_digest = Environment.GetEnvironmentVariable("MEOWCHAN_COMPARISON_SOURCE_DIGEST") ?? "not-provided",
                    fixture_digest = fixtureDigest ?? ComparisonFixtureDigest ?? "not-provided",
                    fixture_scope = fixtureDigest == null ? "canonical comparison dataset inputs" : "gallery media manifest containing verified image checksums; other seed semantics bound by source digest",
                    observer_kind = "harness", capture_kind = "RenderTargetBitmap of actual live product Window",
                    language = Texts.Language, requested_language = nextLanguage, actual_label = actualLabel, expected_label = expectedLabel,
                    config_language = owner.SessionHost.Config.Language, config_theme = owner.SessionHost.Config.Theme,
                    theme = target.ActualThemeVariant.ToString(), requested_theme = nextTheme.ToString(), owner_mode = owner.SessionHost.Config.WorkspaceMode,
                    mode_scope = ReferenceEquals(owner, target) ? "actual shell" : "owned dialog is mode-independent; actual owner mode switched",
                    requested_dip = new { width, height }, actual_client_dip = target.ClientSize.ToString(), actual_bounds_dip = target.Bounds.ToString(),
                    actual_render_scaling = target.RenderScaling, focus_type = target.FocusManager?.GetFocusedElement()?.GetType().Name,
                    human_visual_review = "pending", native_at_ime_open = true });
            }
        }
        finally
        {
            await owner.SessionHost.SaveViewPreferencesAsync(config =>
            { config.Language = configLanguage; config.Theme = configTheme; });
            await SwitchModeAsync(owner, mode);
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            owner.Width = ownerWidth; owner.Height = ownerHeight; target.Width = targetWidth; target.Height = targetHeight;
            owner.WindowState = ownerState; target.WindowState = targetState;
            await SettleAsync(owner, target);
        }
    }

    private static bool CaptureReady(MainWindow owner, Window target, double width, double height, double scale,
        string language, ThemeVariant theme)
    {
        bool Matches(Window window) => window.IsVisible && window.WindowState == WindowState.Normal
            && Math.Abs(window.Bounds.Width - width) <= 1 && Math.Abs(window.Bounds.Height - height) <= 1
            && Math.Abs(window.ClientSize.Width - width) <= 1 && Math.Abs(window.ClientSize.Height - height) <= 1
            && Math.Abs(window.RenderScaling - scale) < 0.025 && window.ActualThemeVariant == theme;
        return Matches(owner) && Matches(target) && Texts.Language == language
            && owner.SessionHost.Config.Language == language && owner.SessionHost.Config.Theme == theme.ToString();
    }

    // Layout dispatches alone do not acknowledge a native resize. Observe the same
    // unchanged request twice, without reissuing it, measuring or arranging the windows.
    private static async Task WaitForCaptureAsync(MainWindow owner, Window target, double width, double height,
        double scale, string language, ThemeVariant theme, string file)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        string? previous = null;
        int stable = 0, observations = 0;
        while (true)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            string actual = JsonSerializer.Serialize(new { owner = Describe(owner), target = Describe(target),
                language = Texts.Language, config_language = owner.SessionHost.Config.Language,
                config_theme = owner.SessionHost.Config.Theme });
            bool ready = CaptureReady(owner, target, width, height, scale, language, theme);
            stable = ready ? actual == previous ? stable + 1 : 1 : 0;
            observations++;
            if (stable >= 2) return;
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(5))
            {
                PostBookmarkComparisonChecks.Log(new { task = "capture-readiness-timeout", file,
                    source_digest = Environment.GetEnvironmentVariable("MEOWCHAN_COMPARISON_SOURCE_DIGEST"),
                    requested = new { width, height, scale, language, theme = theme.ToString() },
                    elapsed_ms = elapsed.Elapsed.TotalMilliseconds, observations, stable_observations = stable,
                    owner = Describe(owner), target = Describe(target), actual_language = Texts.Language,
                    config_language = owner.SessionHost.Config.Language, config_theme = owner.SessionHost.Config.Theme });
                throw new TimeoutException($"Bookmark windows did not stabilize at requested {width}x{height}, scale {scale}, {language}/{theme} within five seconds ({file}). {actual}");
            }
            previous = actual;
            await Task.Delay(50); // Existing bounded smoke-readiness cadence; no fixed capture delay.
        }

        static object Describe(Window window) => new { type = window.GetType().Name,
            requested_width = window.Width, requested_height = window.Height,
            bounds = window.Bounds.ToString(), client = window.ClientSize.ToString(),
            scale = window.RenderScaling, theme = window.ActualThemeVariant.ToString(),
            state = window.WindowState.ToString(), visible = window.IsVisible };
    }

    private static (string Actual, string Expected) LocalizedLabel(Window target, string language)
    {
        if (target is PostBookmarksDialog dialog)
        {
            Button save = dialog.FindControl<Button>("Save")!;
            if (!save.IsEffectivelyVisible) throw new InvalidOperationException("Capture Save label is not visible.");
            return (save.Content?.ToString() ?? "", Texts.T("bookmarks.save"));
        }
        MainWindow shell = (MainWindow)target;
        ContentControl galleryHost = shell.FindControl<ContentControl>("GalleryHost")!;
        Button close;
        if (galleryHost.IsVisible && galleryHost.Content is MeowChan.Desktop.GalleryViewer gallery)
        {
            gallery.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.LeftShift });
            close = gallery.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose");
        }
        else close = shell.ArchivePage.FindControl<Button>("ArchiveCloseReader")!;
        string actual = close.Content is Control content
            ? string.Join("", content.GetVisualDescendants().Prepend(content).OfType<TextBlock>().Select(text => text.Text))
            : close.Content?.ToString() ?? "";
        if (!close.IsEffectivelyVisible) throw new InvalidOperationException("Capture localized-label control is not visible.");
        return (actual, language == "de" ? "Schließen" : "Close");
    }

    private static async Task SwitchModeAsync(MainWindow owner, string mode)
    {
        ComboBox modes = owner.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        modes.SelectedItem = modes.Items.OfType<ComboBoxItem>().Single(item => item.Tag as string == mode);
        await Program.WaitUntilAsync(() => owner.SessionHost.Config.WorkspaceMode == mode
            && (modes.SelectedItem as ComboBoxItem)?.Tag as string == mode);
    }
    private static async Task SettleAsync(Window owner, Window target)
    {
        owner.UpdateLayout(); target.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        owner.UpdateLayout(); target.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        owner.UpdateLayout(); target.UpdateLayout();
    }
}
