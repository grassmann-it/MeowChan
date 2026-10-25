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

            viewer.SetFavoriteError("Favorite could not be saved");
            Require(info.IsVisible && Find<SelectableTextBlock>(viewer, "GalleryFavoriteError").IsEffectivelyVisible,
                "Focus mode hid a failed favorite write.");
            Require(Find<SelectableTextBlock>(viewer, "GalleryFavoriteError").Focus(NavigationMethod.Tab), "Could not focus the favorite error.");
            viewer.SetFavoriteError(null);
            Require(ReferenceEquals(window.FocusManager?.GetFocusedElement(), toggle), "Clearing the last visible error stranded focus.");
            viewer.SetRating(null, "Rating could not be saved");
            Require(info.IsVisible && Find<TextBlock>(viewer, "GalleryRatingStatus").IsEffectivelyVisible,
                "Focus mode hid a failed rating write.");
            viewer.SetRating(null, null);
            viewer.SetWarnings("Manifest warning retained");
            Require(info.IsVisible && Find<TextBlock>(viewer, "GalleryWarnings").IsEffectivelyVisible,
                "Focus mode hid a manifest warning.");
            viewer.SetWarnings(string.Empty);
            viewer.SetNotice("Media cannot be decoded");
            Require(Find<TextBlock>(viewer, "GalleryStatus").IsEffectivelyVisible, "Focus mode hid a media error.");
            viewer.SetNotice(string.Empty);

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
            await viewer.StopAsync();
            Require(toggle.IsChecked != true && fit.IsVisible, "Closing did not reset session-only focus mode.");
            GalleryViewer fresh = new();
            object? previousContent = window.Content;
            try
            {
                window.Content = fresh;
                await Program.WaitUntilAsync(() => fresh.Bounds.Width > 100);
                Require(Find<ToggleButton>(fresh, "GalleryToolsToggle").IsChecked != true, "Focus mode leaked into a new gallery.");
            }
            finally { await fresh.StopAsync(); window.Content = previousContent; }
            Console.WriteLine("SMOKE gallery focus: EN/DE Light/Dark at 640/850, visible navigation, reclaimed height, custom shortcut, focus transfer, retained errors and session reset passed.");
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
