using System.Text.Json;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop;
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
        MouseDevice mouse = new();
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
                window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
            Input(window, mouse, RawPointerEventType.LeftButtonUp, new Point(1, 1));
            Application.Current.RequestedThemeVariant = originalTheme;
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        }
        if (failures.Count > 0) throw new InvalidOperationException("Command button contrast: " + string.Join("; ", failures));
        Console.WriteLine("SMOKE command buttons: actual Add/Save/Save rule and neutral/toggle content followed Semi normal, pointerover, pressed and disabled foregrounds in Light/Dark; rendered pixels passed.");
    }

    private static async Task InspectToggleAsync(MainWindow owner, ThemeVariant theme, MouseDevice mouse, List<string> failures)
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
                await InspectStatesAsync(owner, favorite, theme, mouse, failures, selected ? "checked" : "unchecked");
            }
        }
        finally { root.Children.Remove(fixture); }
    }

    private static async Task InspectStatesAsync(Window window, Button button, ThemeVariant theme, MouseDevice mouse,
        List<string> failures, string context = "command")
    {
        await Program.WaitUntilAsync(() => button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Bounds.Width > 0);
        int clicks = 0;
        void ObserveClick(object? sender, RoutedEventArgs args) => clicks++;
        button.Click += ObserveClick;
        try
        {
            foreach (string state in new[] { "normal", "pointerover", "pressed", "disabled" })
            {
                Point outside = new Point(1, window.Bounds.Height - 2);
                Input(window, mouse, RawPointerEventType.Move, outside);
                Input(window, mouse, RawPointerEventType.LeftButtonUp, outside);
                button.SetCurrentValue(InputElement.IsEnabledProperty, state != "disabled");
                if (state is "pointerover" or "pressed")
                {
                    Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    Input(window, mouse, RawPointerEventType.Move, center);
                    if (state == "pressed") Input(window, mouse, RawPointerEventType.LeftButtonDown, center, RawInputModifiers.LeftMouseButton);
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
            Point outside = new Point(1, window.Bounds.Height - 2);
            Input(window, mouse, RawPointerEventType.Move, outside);
            Input(window, mouse, RawPointerEventType.LeftButtonUp, outside);
            button.SetCurrentValue(InputElement.IsEnabledProperty, true);
            button.Click -= ObserveClick;
        }
        if (clicks != 0) throw new InvalidOperationException("Contrast pointer inspection unexpectedly executed " + button.Name);
    }

    private static void InspectPixels(Window window, Button button, ThemeVariant theme, string state, string context, List<string> failures)
    {
        TextBlock? label = button.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault();
        Border icon = button.GetVisualDescendants().OfType<Border>().Single(border => border.OpacityMask != null);
        Color textColor = Solid(label?.Foreground ?? icon.GetValue(TextElement.ForegroundProperty));
        Color iconColor = Solid(icon.Background);
        // Specialized archive-favorite content deliberately uses Icon directly
        // and owns a Gold foreground. Its existing neutral icon must be retained.
        Color expectedIcon = button is ArchiveFavoriteButton ? Solid(window.Foreground) : textColor;
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

    private static void Input(Window window, MouseDevice mouse, RawPointerEventType type, Point point, RawInputModifiers modifiers = RawInputModifiers.None)
        => AvaloniaLocator.Current.GetRequiredService<IInputManager>().ProcessInput(
            new RawPointerEventArgs(mouse, (ulong)Environment.TickCount64,
                // Avalonia 12 keeps the root internal. The harness routes raw input
                // through it so hover/press are real control states, not styled mocks.
                typeof(TopLevel).GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) as IInputRoot
                    ?? throw new InvalidOperationException("No input root for the rendered window."),
                type, point, modifiers));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
