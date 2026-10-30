using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

/// <summary>Uses the real gallery, layout and focus manager on the isolated smoke display.</summary>
internal static class GalleryFocusChecks
{
    internal static async Task RunAsync(MainWindow window, GalleryViewer viewer, GalleryEntry entry)
    {
        double width = window.Width, height = window.Height, minimumWidth = window.MinWidth;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        string language = Texts.Language;
        var originalBindings = window.SessionHost.Config.ShortcutBindings;
        ToggleButton toggle = Find<ToggleButton>(viewer, "GalleryToolsToggle");
        Border info = Find<Border>(viewer, "GalleryOverlayInfo");
        Button fit = Find<Button>(viewer, "GalleryFit");
        viewer.SetWarnings(string.Empty);
        viewer.SetPosition("1 / 61", false, true);
        viewer.SetRating(null, null);
        try
        {
            Require(toggle.IsChecked != true && fit.IsEffectivelyVisible, "A new gallery must start with expanded tools.");
            // A user-defined gesture follows the normal Gallery route and retains
            // focus on the reveal control when the old target is removed.
            ShortcutService.Publish(originalBindings.Set("gallery.tools-toggle", new[] { ShortcutGesture.Parse("Ctrl+Shift+F9") }));
            Require(fit.Focus(NavigationMethod.Tab), "Could not focus a tool before collapse.");
            fit.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F9,
                KeyModifiers = KeyModifiers.Control | KeyModifiers.Shift, Source = fit });
            Require(toggle.IsChecked == true && !fit.IsEffectivelyVisible && !info.IsVisible,
                "Custom gallery shortcut did not collapse tools and metadata.");
            Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), toggle), "Collapsed tool retained keyboard focus.");
            Require(AutomationProperties.GetName(toggle) == Texts.T("viewer.tools.show"), "Reveal action has no state-specific accessible name.");
            int moves = 0;
            void Move(int delta) => moves += delta;
            viewer.MoveRequested += Move;
            try
            {
                Click(Find<Button>(viewer, "GalleryNext"));
                Require(moves == 1, "Compact next action lost its original gallery command.");
                await viewer.ShowAsync(entry, CancellationToken.None);
                Require(toggle.IsChecked == true, "Changing media reset the current session's focus mode.");
            }
            finally { viewer.MoveRequested -= Move; }

            Require(toggle.Focus(NavigationMethod.Tab), "Could not focus reveal control before diagnostics.");
            object? diagnosticFocus = window.FocusManager?.GetFocusedElement();
            var diagnosticSelection = viewer.CaptureState();
            long diagnosticRevision = viewer.AssociationRevision;
            var reports = new List<GalleryDiagnosticNotice>();
            void CheckDiagnosticState()
            {
                Require(ReferenceEquals(diagnosticFocus, window.FocusManager?.GetFocusedElement())
                    && viewer.CaptureState() == diagnosticSelection && toggle.IsChecked == true && !info.IsVisible,
                    "Technical diagnostics changed focus, selection/context/zoom or expanded collapsed metadata.");
                Require(!Find<Control>(viewer, "GalleryFavoriteError").IsEffectivelyVisible
                    && string.IsNullOrEmpty(Find<SelectableTextBlock>(viewer, "GalleryFavoriteError").Text)
                    && string.IsNullOrEmpty(Find<TextBlock>(viewer, "GalleryWarnings").Text)
                    && !(Find<TextBlock>(viewer, "GalleryRatingStatus").Text ?? "").Contains("Rating could not be saved")
                    && !(Find<TextBlock>(viewer, "GalleryStatus").Text ?? "").Contains("Media cannot be decoded"),
                    "Technical diagnostic text leaked into the gallery.");
            }
            void CheckReport(string operation, string message, Action publish)
            {
                int before = reports.Count;
                publish();
                Require(reports.Count == before + 1, "Diagnostic must publish exactly once: " + operation);
                GalleryDiagnosticNotice report = reports[^1];
                Require(report.Operation == operation && report.Message == message
                    && report.Entry?.EntryKey == entry.EntryKey && report.Revision == diagnosticRevision && !report.MediaUnavailable,
                    "Technical diagnostic lost exact operation/message/association identity: " + operation);
                CheckDiagnosticState();
            }
            viewer.DiagnosticNotice += reports.Add;
            try
            {
                CheckReport("favorite", "Favorite could not be saved", () => viewer.SetFavoriteError("Favorite could not be saved"));
                viewer.SetFavoriteError(null);
                Require(reports.Count == 1, "Clearing favorite error emitted another report.");
                CheckDiagnosticState();
                CheckReport("rating", "Rating could not be saved", () => viewer.SetRating(null, "Rating could not be saved"));
                viewer.SetRating(null, null);
                Require(reports.Count == 2, "Clearing rating error emitted another report.");
                CheckDiagnosticState();
                CheckReport("source-warning", "Manifest warning retained", () => viewer.SetWarnings("Manifest warning retained"));
                viewer.SetWarnings(string.Empty);
                Require(reports.Count == 3, "Clearing source warning emitted another report.");
                CheckDiagnosticState();
                CheckReport("notice", "Media cannot be decoded", () => viewer.SetNotice("Media cannot be decoded"));
                viewer.SetNotice(string.Empty);
                Require(reports.Count == 4, "Clearing notice emitted another report.");
                CheckDiagnosticState();
            }
            finally { viewer.DiagnosticNotice -= reports.Add; }

            window.MinWidth = 640;
            foreach (ThemeVariant variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (string testedLanguage in new[] { "en", "de" })
            foreach (double testedWidth in new[] { 640d, 850d })
            {
                Application.Current.RequestedThemeVariant = variant;
                Texts.SetLanguage(testedLanguage);
                window.Width = testedWidth; window.Height = 530;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - testedWidth) < .5 / window.RenderScaling);
                if (toggle.IsChecked == true) Click(toggle);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                double expandedHeight = Find<Grid>(viewer, "GalleryViewport").Bounds.Height;
                Program.CaptureWindow(window, $"smoke-gallery-tools-{testedLanguage}-{variant}-{testedWidth}.png");
                Click(toggle);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Require(Find<Grid>(viewer, "GalleryViewport").Bounds.Height > expandedHeight + 20,
                    "Collapsing tools did not reclaim usable media height.");
                foreach (string name in new[] { "GalleryClose", "GalleryPrevious", "GalleryNext", "GalleryCompactPosition", "GalleryToolsToggle" })
                {
                    Control control = Find<Control>(viewer, name);
                    Point? point = control.TranslatePoint(default, viewer);
                    Require(control.IsEffectivelyVisible && point.HasValue && point.Value.X >= 0
                        && point.Value.X + control.Bounds.Width <= viewer.Bounds.Width + 1
                        && point.Value.Y + control.Bounds.Height <= viewer.Bounds.Height + 1,
                        "Compact gallery action is clipped: " + name);
                }
                Require(AutomationProperties.GetName(toggle) == Texts.T("viewer.tools.show"), "Live language change lost the reveal label.");
                Program.CaptureWindow(window, $"smoke-gallery-focus-{testedLanguage}-{variant}-{testedWidth}.png");
            }
            int teardownPreferenceWrites = 0;
            viewer.PlaybackPreferencesChanged += _ => teardownPreferenceWrites++;
            await viewer.StopAsync();
            Require(toggle.IsChecked == true && !fit.IsVisible && teardownPreferenceWrites == 0,
                "Closing overwrote the remembered focus choice or published a lifecycle preference change.");
            GalleryViewer fresh = new();
            object? previousContent = window.Content;
            try
            {
                window.Content = fresh;
                await Program.WaitUntilAsync(() => fresh.Bounds.Width > 100);
                Require(Find<ToggleButton>(fresh, "GalleryToolsToggle").IsChecked != true, "Focus mode leaked into a new gallery.");
            }
            finally { await fresh.StopAsync(); window.Content = previousContent; }
            Console.WriteLine("SMOKE gallery focus: EN/DE Light/Dark at 640/850, visible navigation, reclaimed height, custom shortcut, focus transfer, retained errors and teardown without preference writes passed.");
        }
        finally
        {
            if (toggle.IsChecked == true) Click(toggle);
            ShortcutService.Publish(originalBindings);
            Texts.SetLanguage(language);
            Application.Current.RequestedThemeVariant = theme;
            window.MinWidth = minimumWidth; window.Width = width; window.Height = height;
        }
    }

    internal static async Task CheckVideoAsync(MainWindow window, GalleryViewer viewer, GalleryVideoPlayer player, ThemeVariant theme)
    {
        ToggleButton toggle = Find<ToggleButton>(viewer, "GalleryToolsToggle");
        var before = await player.InspectPlaybackAsync();
        Require(before.HasPlayback && before.SurfaceNonUniform, "Video focus check requires a real decoded video.");
        Click(toggle);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        foreach (string name in new[] { "GalleryVideoTransport", "GalleryVideoPlayPause", "GalleryVideoSeek", "GalleryVideoMute", "GalleryVideoLoop",
            "GalleryClose", "GalleryPrevious", "GalleryNext", "GalleryToolsToggle" })
            Require(Find<Control>(viewer, name).IsEffectivelyVisible, "Video focus hid an essential control: " + name);
        Require(!Find<Button>(viewer, "GalleryFit").IsEffectivelyVisible, "Video focus did not hide secondary tools.");
        Program.CaptureWindow(window, $"smoke-gallery-video-focus-{theme}.png");
        Click(toggle);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        Require(Find<Button>(viewer, "GalleryFit").IsEffectivelyVisible, "Video tools did not expand again.");
        Require(ReferenceEquals(player, viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single())
            && (await player.InspectPlaybackAsync()).HasPlayback, "Collapsing tools replaced or stopped the real video.");
        Program.CaptureWindow(window, $"smoke-gallery-video-tools-{theme}.png");
        Console.WriteLine("SMOKE real video media focus: transport and navigation remain visible across collapse/expand in " + theme);
    }

    private static T Find<T>(GalleryViewer viewer, string name) where T : Control =>
        viewer.GetGalleryVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
