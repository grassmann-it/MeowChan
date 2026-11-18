using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Rules;
using SkiaSharp;
using PopConfirm = Ursa.Controls.PopConfirm;

namespace MeowChan.DesktopSmoke;

/// <summary>Rendered production command content under the real Semi state templates.</summary>
internal static class CommandButtonContrastChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        // Dismiss earlier deliberate session/clipboard failures before sampling command pixels.
        foreach (NotificationCard notification in window.GetVisualDescendants().OfType<NotificationCard>().ToArray())
            notification.Close();
        await Program.WaitUntilAsync(() => !window.GetVisualDescendants().OfType<NotificationCard>()
            .Any(notification => notification.IsEffectivelyVisible));
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        List<string> failures = new();
        // Use the existing guarded OS-input harness, not a second MouseDevice routed
        // straight into InputManager. The native top level must own hit testing and
        // pointer-over refreshes as pages/layout change. No state/pseudoclass writes.
        using MediaNativeInput mouse = new(window);
        Console.WriteLine("SMOKE command input: " + mouse.Backend);
        await Program.WaitUntilAsync(() => window.IsActive);
        try
        {
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                Click(window.FindControl<Button>("DownloadsNavigationButton")!);
                await InspectStatesAsync(window, window.FindControl<Button>("AddUrlButton")!, theme, mouse, failures);
                Click(window.FindControl<Button>("SettingsNavigationButton")!);
                await Program.WaitUntilAsync(() => window.SettingsEditor.IsEffectivelyVisible);
                await InspectStatesAsync(window, window.SettingsEditor.FindControl<Button>("SaveSettingsButton")!, theme, mouse, failures);
                await InspectStatesAsync(window, window.SettingsEditor.FindControl<Button>("CancelSettingsButton")!, theme, mouse, failures);
                SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Rules);
                await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Rules && window.RulesPage.IsEditable);
                window.RulesPage.EditScope(MediaRuleScope.Global);
                CheckBox enabled = window.RulesPage.FindControl<CheckBox>("RuleEnabledInput")!;
                enabled.IsChecked = enabled.IsChecked != true;
                await Program.WaitUntilAsync(() => window.RulesPage.CanSave);
                await InspectStatesAsync(window, window.RulesPage.FindControl<Button>("SaveRuleButton")!, theme, mouse, failures);
                Button discard = window.RulesPage.FindControl<Button>("DiscardRuleButton")!;
                Click(discard);
                PopConfirm confirmation = discard.GetVisualAncestors().OfType<PopConfirm>().Single();
                await Program.WaitUntilAsync(() => window.RulesPage.IsDiscardConfirmationOpen
                    && confirmation.IsDropdownOpen);
                confirmation.ConfirmCommand!.Execute(null);
                await Program.WaitUntilAsync(() => !window.RulesPage.IsDiscardConfirmationOpen
                    && !confirmation.IsDropdownOpen && !window.RulesPage.HasDiscardableChanges);
                await InspectToggleAsync(window, theme, mouse, failures);
            }
        }
        finally
        {
            await ReleaseAwayAsync(window, mouse);
            Application.Current.RequestedThemeVariant = originalTheme;
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        }
        if (failures.Count > 0) throw new InvalidOperationException("Command button contrast: " + string.Join("; ", failures));
        Console.WriteLine("SMOKE command buttons: actual Add/Save/Save rule and neutral/toggle content followed Semi normal, pointerover, pressed and disabled foregrounds in Light/Dark; rendered pixels passed.");
    }

    private static async Task InspectToggleAsync(MainWindow owner, ThemeVariant theme, MediaNativeInput mouse, List<string> failures)
    {
        ToggleButton toggle = new() { Name = "CommandToggleFixture", Margin = new Thickness(20) };
        CommandBindings.Apply(toggle, "favorites.toggle", iconWithLabel: true);
        ArchiveFavoriteButton favorite = new() { Margin = new Thickness(20), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        StackPanel contents = new() { Children = { toggle, favorite } };
        Grid root = owner.FindControl<Grid>("WorkspaceRoot")!;
        Border fixture = new() { Width = 360, Height = 200, Child = contents, Background = owner.Background,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            ZIndex = 1000 };
        Grid.SetRow(fixture, 1);
        root.Children.Add(fixture);
        try
        {
            await Program.WaitUntilAsync(() => fixture.IsEffectivelyVisible && fixture.Bounds.Width > 0);
            await Task.Delay(220);
            foreach (bool selected in new[] { false, true })
            {
                toggle.IsChecked = selected;
                favorite.Configure(() => true, () => selected, () => Task.CompletedTask);
                await InspectStatesAsync(owner, toggle, theme, mouse, failures, selected ? "checked" : "unchecked");
                // The star's pointer acceptance (its favorite-star hit surface, hover fill and
                // real clicks) is the reader-post-star smoke's; here only the vector content's
                // normal/disabled rendering is checked.
                await InspectStatesAsync(owner, favorite, theme, mouse, failures, selected ? "checked" : "unchecked",
                    pointerStates: false);
            }
        }
        finally { root.Children.Remove(fixture); }
        Console.WriteLine("SMOKE archive-favorite contrast: checked/unchecked vector content in normal/disabled states; native hover/press is the reader-post-star smoke's.");
    }

    private static async Task InspectStatesAsync(Window window, Button button, ThemeVariant theme, MediaNativeInput mouse,
        List<string> failures, string context = "command", bool pointerStates = true)
    {
        await Program.WaitUntilAsync(() => button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Bounds.Width > 0);
        int clicks = 0;
        void ObserveClick(object? sender, RoutedEventArgs args) => clicks++;
        button.Click += ObserveClick;
        try
        {
            foreach (string state in pointerStates ? new[] { "normal", "pointerover", "pressed", "disabled" } : new[] { "normal", "disabled" })
            {
                await ReleaseAwayAsync(window, mouse);
                button.SetCurrentValue(InputElement.IsEnabledProperty, state != "disabled");
                if (state is "pointerover" or "pressed")
                {
                    await SmokeDispatcher.SettleAsync();
                    window.UpdateLayout();
                    Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    if (window.InputHitTest(center) is not Visual hit || !hit.GetSelfAndVisualAncestors().Contains(button))
                        throw new InvalidOperationException($"Command pointer target is obscured: {button.Name}; center={center}; bounds={button.Bounds}; hit={window.InputHitTest(center)?.GetType().Name}");
                    await mouse.MoveAsync(button);
                    if (state == "pressed") await mouse.ButtonAsync(true);
                }
                try
                {
                    await Program.WaitUntilAsync(() => button.IsPointerOver == (state is "pointerover" or "pressed")
                        && button.IsPressed == (state == "pressed") && button.IsEffectivelyEnabled == (state != "disabled"));
                }
                catch
                {
                    Console.WriteLine($"SMOKE command input failed: {button.Name}/{context}/{state}: over={button.IsPointerOver}, pressed={button.IsPressed}, enabled={button.IsEffectivelyEnabled}, bounds={button.Bounds}, window={window.Bounds}");
                    Point? center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
                    Visual? hit = center.HasValue ? window.InputHitTest(center.Value) as Visual : null;
                    Console.WriteLine("SMOKE command input target: " + JsonSerializer.Serialize(new
                    {
                        center = center?.ToString(), root = TopLevel.GetTopLevel(button)?.GetType().Name,
                        button.IsHitTestVisible, window.IsEnabled, window.IsActive,
                        hit = hit?.GetType().Name,
                        hitAncestors = hit?.GetSelfAndVisualAncestors().Select(visual => visual is Control control
                            ? control.GetType().Name + ":" + control.Name : visual.GetType().Name).ToArray(),
                        openConfirmations = window.GetVisualDescendants().OfType<PopConfirm>()
                            .Where(surface => surface.IsDropdownOpen).Select(surface => surface.GetType().Name).ToArray(),
                        ownedWindows = window.OwnedWindows.Select(child => new { child.Name, child.IsVisible, child.IsEnabled }).ToArray()
                    }));
                    throw;
                }
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
                await Task.Delay(220); // Measure settled Semi brush transitions, not interpolated frames.
                window.UpdateLayout();
                InspectPixels(window, button, theme, state, context, failures);
            }
        }
        finally
        {
            await ReleaseAwayAsync(window, mouse);
            button.SetCurrentValue(InputElement.IsEnabledProperty, true);
            button.Click -= ObserveClick;
        }
        if (clicks != 0) throw new InvalidOperationException("Contrast pointer inspection unexpectedly executed " + button.Name);
    }

    private static void InspectPixels(Window window, Button button, ThemeVariant theme, string state, string context, List<string> failures)
    {
        TextBlock? label = button.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault();
        // CommandContent uses a mask, but ArchiveFavoriteButton now renders the
        // production vector star. Sample that real shape instead of assuming a mask.
        Control icon;
        Color iconColor;
        if (button is ArchiveFavoriteButton)
        {
            Avalonia.Controls.Shapes.Path star = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                .Single(path => path.Tag is WorkspaceIcons.Star or WorkspaceIcons.StarFilled);
            icon = star;
            iconColor = Solid(star.Stroke);
        }
        else
        {
            Border mask = button.GetVisualDescendants().OfType<Border>().Single(border => border.OpacityMask != null);
            icon = mask;
            iconColor = Solid(mask.Background);
        }
        Color textColor = Solid(label?.Foreground ?? icon.GetValue(TextElement.ForegroundProperty));
        Color expectedIcon = button is ArchiveFavoriteButton { IsChecked: true }
            ? SmokeThemeResources.Color(window, "Workspace.Favorite") : textColor;
        string image = $"smoke-command-{theme.ToString().ToLowerInvariant()}-{button.Name}-{context}-{state}.png";
        Program.CaptureWindow(window, image);
        using SKBitmap bitmap = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, image));
        Point position = button.TranslatePoint(new Point(4, button.Bounds.Height / 2), window)!.Value;
        Color background = Pixel(bitmap, position, window.RenderScaling);
        Color iconPixel = StrongestPixel(bitmap, icon, window, background);
        Color textPixel = label is null ? iconPixel : StrongestPixel(bitmap, label, window, background);
        double iconRatio = Contrast(iconPixel, background);
        double textRatio = Contrast(textPixel, background);
        Console.WriteLine("SMOKE command contrast: " + JsonSerializer.Serialize(new
        {
            theme = theme.ToString(), button.Name, context, state, button.IsPointerOver, button.IsPressed, button.IsEffectivelyEnabled,
            foreground = textColor.ToString(), expectedIcon = expectedIcon.ToString(), icon = iconColor.ToString(), background = background.ToString(),
            iconPixel = iconPixel.ToString(), textPixel = textPixel.ToString(), iconRatio, textRatio,
            window.RenderScaling, image
        }));
        bool enabledContrast = state == "disabled" || iconRatio >= 3 && (label is null || textRatio >= 4.5);
        if (window.ActualThemeVariant != theme || !icon.IsEffectivelyVisible || label?.IsEffectivelyVisible == false
            || iconColor != expectedIcon || !enabledContrast)
            failures.Add($"{theme}/{button.Name}/{context}/{state}: icon={iconColor}, label={textColor}, rendered={iconRatio:0.###}/{textRatio:0.###}");
    }

    private static Color StrongestPixel(SKBitmap bitmap, Control control, Window window, Color background)
    {
        Point start = control.TranslatePoint(default, window)!.Value;
        double scale = window.RenderScaling;
        Color strongest = background;
        for (int y = (int)Math.Ceiling(start.Y * scale); y < Math.Floor((start.Y + control.Bounds.Height) * scale); y++)
        for (int x = (int)Math.Ceiling(start.X * scale); x < Math.Floor((start.X + control.Bounds.Width) * scale); x++)
        {
            SKColor pixel = bitmap.GetPixel(x, y);
            Color color = Color.FromArgb(pixel.Alpha, pixel.Red, pixel.Green, pixel.Blue);
            if (Contrast(color, background) > Contrast(strongest, background)) strongest = color;
        }
        return strongest;
    }

    private static Color Pixel(SKBitmap bitmap, Point position, double scale)
    {
        SKColor pixel = bitmap.GetPixel((int)Math.Round(position.X * scale), (int)Math.Round(position.Y * scale));
        return Color.FromArgb(pixel.Alpha, pixel.Red, pixel.Green, pixel.Blue);
    }

    private static Color Solid(IBrush? brush)
    {
        ISolidColorBrush solid = brush as ISolidColorBrush ?? throw new InvalidOperationException("Command content did not resolve a solid brush.");
        return Color.FromArgb((byte)Math.Round(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B);
    }

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static async Task ReleaseAwayAsync(Window window, MediaNativeInput mouse)
    {
        // Release outside the inspected command: contrast sampling must never save,
        // cancel, toggle or otherwise execute it. Stay inside the client, off WM grips.
        await mouse.MoveAsync(window, new Point(window.Bounds.Width / 2, window.Bounds.Height - 12));
        await mouse.ButtonAsync(false);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
