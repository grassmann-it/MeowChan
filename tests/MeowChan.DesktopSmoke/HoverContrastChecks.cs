using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using SkiaSharp;
using Shape = Avalonia.Controls.Shapes.Path;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Hover, open and selected states of the shell's state surfaces under the real Semi templates (UX review 2026-11-13):
/// footer navigation, the work-view drop-down, the Tools menu, a context menu and a settings drop-down, in Light and Dark.
/// Real pointer input sets the states; the rendered pixels are sampled. Text needs 4.5:1 on its fill, a state boundary
/// (hover outline, selection bar, overlay edge) 3:1 against the fill inside it and the surface around it.
/// </summary>
internal static class HoverContrastChecks
{
    private const double TextMinimum = 4.5, BoundaryMinimum = 3.0;

    internal static async Task RunAsync(MainWindow window)
    {
        foreach (NotificationCard notification in window.GetVisualDescendants().OfType<NotificationCard>().ToArray())
            notification.Close();
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        string originalMode = window.SessionHost.Config.WorkspaceMode;
        double originalWidth = window.Width;
        List<string> failures = new();
        MouseDevice mouse = new();
        try
        {
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                string tag = theme.ToString().ToLowerInvariant();
                Application.Current.RequestedThemeVariant = theme;
                Click(window.FindControl<Button>("DownloadsNavigationButton")!);
                await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads && window.ActualThemeVariant == theme);
                await SettleAsync(window);
                await FooterAsync(window, mouse, tag, failures);
                await WorkViewDropDownAsync(window, mouse, tag, failures);
                await ContextMenuAsync(window, mouse, tag, failures);
                await ToolsMenuAsync(window, mouse, tag, failures);
                await SettingsDropDownAsync(window, mouse, tag, failures);
                await SettingsSwitchAsync(window, mouse, tag, failures);
            }
        }
        finally
        {
            Leave(window, mouse);
            Application.Current.RequestedThemeVariant = originalTheme;
            window.Width = originalWidth;
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
            await PresentationChecks.SwitchModeAsync(window, originalMode);
        }
        if (failures.Count > 0) throw new InvalidOperationException("Hover contrast: " + string.Join("; ", failures));
        Console.WriteLine("SMOKE hover contrast: footer navigation (hover, selected, selected+hover), work-view selector and drop-down, Tools menu, "
            + "columns context menu, a settings drop-down and the settings switches (off and on) passed rendered text 4.5:1 and state boundaries 3:1 in Light and Dark.");
    }

    private static async Task FooterAsync(MainWindow window, MouseDevice mouse, string tag, List<string> failures)
    {
        Button selected = window.FindControl<Button>("DownloadsNavigationButton")!;
        Border bar = window.FindControl<Border>("WorkspaceNavigation")!;
        Button[] targets = new[] { "SettingsNavigationButton", "ArchiveNavigationButton" }
            .Select(name => window.FindControl<Button>(name)!).Where(button => button.IsEffectivelyVisible).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("No footer destination is visible to hover.");

        Leave(window, mouse);
        await SettleAsync(window);
        using (SKBitmap rest = CaptureWindow(window, $"smoke-hover-{tag}-footer-rest.png"))
        {
            Color around = Sample(rest, bar, window, 6, 2);
            Color fill = Sample(rest, selected, window, 6, selected.Bounds.Height / 2);
            Require(failures, tag, "footer selected label", Contrast(Strongest(rest, Label(selected), window, fill), fill), TextMinimum);
            // Selected = AccentSurface + a 3-px accent bar at the bottom edge (never the tint alone).
            Color edge = Sample(rest, selected, window, 6, selected.Bounds.Height - 1.5);
            Require(failures, tag, "footer selected bar/fill", Contrast(edge, fill), BoundaryMinimum);
            Require(failures, tag, "footer selected bar/bar surface", Contrast(edge, around), BoundaryMinimum);
        }
        foreach (Button target in targets.Append(selected))
        {
            await HoverAsync(window, window, target, mouse, () => target.IsPointerOver);
            string name = target.Name!.Replace("NavigationButton", "", StringComparison.Ordinal).ToLowerInvariant();
            using SKBitmap image = CaptureWindow(window, $"smoke-hover-{tag}-footer-{name}.png");
            Color around = Sample(image, bar, window, 6, 2);
            Color fill = Sample(image, target, window, 6, target.Bounds.Height / 2);
            Require(failures, tag, $"footer {name} hover label", Contrast(Strongest(image, Label(target), window, fill), fill), TextMinimum);
            if (target == selected)
            {
                Color edge = Sample(image, target, window, 6, target.Bounds.Height - 1.5);
                Require(failures, tag, "footer selected+hover bar/fill", Contrast(edge, fill), BoundaryMinimum);
                Require(failures, tag, "footer selected+hover bar/bar surface", Contrast(edge, around), BoundaryMinimum);
                continue;
            }
            Color outline = Sample(image, target, window, 0.5, target.Bounds.Height / 2);
            Require(failures, tag, $"footer {name} hover outline/fill", Contrast(outline, fill), BoundaryMinimum);
            Require(failures, tag, $"footer {name} hover outline/bar surface", Contrast(outline, around), BoundaryMinimum);
            Shape? icon = target.GetVisualDescendants().OfType<Shape>().FirstOrDefault(path => path.IsEffectivelyVisible);
            if (icon != null) Require(failures, tag, $"footer {name} hover icon", Contrast(Strongest(image, icon, window, fill), fill), BoundaryMinimum);
        }

        ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        foreach (bool hover in new[] { false, true })
        {
            if (hover) await HoverAsync(window, window, modes, mouse, () => modes.IsPointerOver);
            else { Leave(window, mouse); await SettleAsync(window); }
            using SKBitmap image = CaptureWindow(window, $"smoke-hover-{tag}-footer-mode-{(hover ? "hover" : "rest")}.png");
            Color around = Sample(image, bar, window, 6, 2);
            Color fill = Sample(image, modes, window, 6, modes.Bounds.Height / 2);
            Color outline = Sample(image, modes, window, 0.5, modes.Bounds.Height / 2);
            TextBlock text = modes.GetVisualDescendants().OfType<TextBlock>().First(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text));
            Require(failures, tag, $"footer mode {(hover ? "hover" : "rest")} text", Contrast(Strongest(image, text, window, fill), fill), TextMinimum);
            Require(failures, tag, $"footer mode {(hover ? "hover" : "rest")} outline/bar surface", Contrast(outline, around), BoundaryMinimum);
        }
        Leave(window, mouse);
    }

    private static async Task WorkViewDropDownAsync(MainWindow window, MouseDevice mouse, string tag, List<string> failures)
    {
        ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        object? before = modes.SelectedItem;
        await DropDownAsync(window, modes, mouse, $"smoke-hover-{tag}-workview-dropdown.png", tag, "work view", failures);
        if (!ReferenceEquals(before, modes.SelectedItem)) throw new InvalidOperationException("Hovering the work-view drop-down changed the work view.");
    }

    private static async Task SettingsDropDownAsync(MainWindow window, MouseDevice mouse, string tag, List<string> failures)
    {
        Click(window.FindControl<Button>("SettingsNavigationButton")!);
        await Program.WaitUntilAsync(() => window.SettingsEditor.IsEffectivelyVisible);
        await SettleAsync(window);
        // Another drop-down than the work view (the footer one is measured already), e.g. the language or theme choice.
        ComboBox combo = window.SettingsEditor.GetVisualDescendants().OfType<ComboBox>()
            .Where(candidate => candidate.IsEffectivelyVisible && candidate.IsEffectivelyEnabled && candidate.ItemCount >= 2 && candidate.Bounds.Width > 0)
            .OrderBy(candidate => candidate.Name == "WorkspaceModeInput").First();
        object? before = combo.SelectedItem;
        await DropDownAsync(window, combo, mouse, $"smoke-hover-{tag}-settings-dropdown.png", tag, "settings " + (combo.Name ?? "combo"), failures);
        if (!ReferenceEquals(before, combo.SelectedItem)) throw new InvalidOperationException("Hovering a settings drop-down changed its value.");
        Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads);
    }

    // AX-02: a settings switch under the real Semi template. The track is the control's visible boundary (3:1 against the row around it, WCAG 1.4.11)
    // in the off and the on state, and the knob stays distinct from the track (3:1). Rendered pixels: the track is the commonest colour inside the switch
    // that is not the row colour; the knob is the strongest pixel on the centre line between the two ends of the switch.
    private static async Task SettingsSwitchAsync(MainWindow main, MouseDevice mouse, string tag, List<string> failures)
    {
        Click(main.FindControl<Button>("SettingsNavigationButton")!);
        await Program.WaitUntilAsync(() => main.SettingsEditor.IsEffectivelyVisible);
        Leave(main, mouse);
        await SettleAsync(main);
        ToggleSwitch[] switches = main.SettingsEditor.GetVisualDescendants().OfType<ToggleSwitch>()
            .Where(candidate => candidate.IsEffectivelyVisible && candidate.IsEffectivelyEnabled && candidate.OnContent == null && candidate.OffContent == null
                && candidate.Bounds.Width >= 24 && candidate.Bounds.Height >= 12 && InsideWindow(candidate, main)).ToArray();
        ToggleSwitch? off = switches.FirstOrDefault(candidate => candidate.IsChecked != true), on = switches.FirstOrDefault(candidate => candidate.IsChecked == true);
        if (off == null && on == null) throw new InvalidOperationException("No settings switch is visible to measure.");
        using (SKBitmap image = CaptureWindow(main, $"smoke-hover-{tag}-settings-switches.png"))
        {
            foreach ((ToggleSwitch? toggle, string state) in new[] { (off, "off"), (on, "on") })
            {
                if (toggle == null) { Console.WriteLine($"SMOKE hover probe: {tag} settings switch {state}: none visible, not measured"); continue; }
                Color around = Sample(image, toggle, main, -3, toggle.Bounds.Height / 2);
                double scale = main.RenderScaling;
                Point origin = toggle.TranslatePoint(default, main) ?? throw new InvalidOperationException("No switch position.");
                int left = Math.Max(0, (int)Math.Floor(origin.X * scale)), right = Math.Min(image.Width, (int)Math.Ceiling((origin.X + toggle.Bounds.Width) * scale));
                int top = Math.Max(0, (int)Math.Floor(origin.Y * scale)), bottom = Math.Min(image.Height, (int)Math.Ceiling((origin.Y + toggle.Bounds.Height) * scale));
                Dictionary<Color, int> counts = new();
                for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    Color color = Pixel(image, x, y);
                    if (color != around) counts[color] = counts.GetValueOrDefault(color) + 1;
                }
                if (counts.Count == 0) { failures.Add($"{tag} settings switch {state}: nothing but the row colour inside the switch"); continue; }
                Color track = counts.OrderByDescending(pair => pair.Value).First().Key;
                Require(failures, tag, $"settings switch {state} track/row", Contrast(track, around), BoundaryMinimum);
                int mid = (top + bottom) / 2, first = left, last = right - 1;
                while (first < last && Pixel(image, first, mid) == around) first++;
                while (last > first && Pixel(image, last, mid) == around) last--;
                double knob = 1;
                for (int x = first + 2; x <= last - 2; x++) knob = Math.Max(knob, Contrast(Pixel(image, x, mid), track));
                Require(failures, tag, $"settings switch {state} knob/track", knob, BoundaryMinimum);
            }
        }
        Click(main.FindControl<Button>("DownloadsNavigationButton")!);
        await Program.WaitUntilAsync(() => main.SelectedPage == MainWindow.WorkspacePage.Downloads);
    }

    private static Color Pixel(SKBitmap image, int x, int y)
    {
        SKColor pixel = image.GetPixel(x, y);
        return Color.FromArgb(255, pixel.Red, pixel.Green, pixel.Blue);
    }

    private static bool InsideWindow(Control control, Window window) =>
        control.TranslatePoint(default, window) is { } origin && origin.X >= 0 && origin.Y >= 0
        && origin.X + control.Bounds.Width <= window.Bounds.Width && origin.Y + control.Bounds.Height <= window.Bounds.Height;

    private static async Task DropDownAsync(MainWindow window, ComboBox combo, MouseDevice mouse, string file, string tag, string scene,
        List<string> failures)
    {
        Leave(window, mouse);
        combo.IsDropDownOpen = true;
        Exception? failureBeforeCleanup = null;
        try
        {
            ComboBoxItem[] items = Array.Empty<ComboBoxItem>();
            await Program.WaitUntilAsync(() =>
            {
                items = Popup(combo).Child?.GetVisualDescendants().OfType<ComboBoxItem>()
                    .Where(item => item.IsEffectivelyVisible && item.Bounds.Height > 0).ToArray() ?? Array.Empty<ComboBoxItem>();
                return combo.IsDropDownOpen && items.Length >= 2;
            });
            Control surface = (Control)Popup(combo).Child!;
            ComboBoxItem target = items.First(item => !item.IsSelected && item.IsEffectivelyEnabled);
            TopLevel root = TopLevel.GetTopLevel(target) ?? throw new InvalidOperationException("The drop-down has no top level.");
            await HoverAsync(window, root, target, mouse, () => target.IsPointerOver);
            using SKBitmap image = Capture(surface, file);
            ProbeRow(image, surface, target, tag, scene + " hover", failures, outline: true);
            ComboBoxItem? selected = items.FirstOrDefault(item => item.IsSelected);
            if (selected != null) ProbeRow(image, surface, selected, tag, scene + " selected", failures, outline: false, bar: true);
            ProbeOverlayEdge(window, image, surface, tag, scene, failures);
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                Leave(window, mouse);
                combo.IsDropDownOpen = false;
                await Program.WaitUntilAsync(() => !combo.IsDropDownOpen);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in HoverContrastChecks: " + exception));
        }
    }

    private static async Task ToolsMenuAsync(MainWindow window, MouseDevice mouse, string tag, List<string> failures)
    {
        Button tools = window.FindControl<Button>("ToolsNavigationButton")!;
        MenuFlyout menu = tools.Flyout as MenuFlyout ?? throw new InvalidOperationException("Tools has no navigation menu.");
        double width = window.Width;
        window.Width = window.MinWidth; // hides destinations so the Tools menu carries them
        Exception? failureBeforeCleanup = null;
        try
        {
            await Program.WaitUntilAsync(() => tools.IsEffectivelyVisible && tools.Bounds.Width > 0);
            await SettleAsync(window);
            menu.ShowAt(tools);
            MenuItem[] items = Array.Empty<MenuItem>();
            await Program.WaitUntilAsync(() =>
            {
                items = menu.Items.OfType<MenuItem>().Where(item => item.IsEffectivelyVisible && item.Bounds.Height > 0).ToArray();
                return menu.IsOpen && items.Length >= 2;
            });
            await ProbeMenuAsync(window, mouse, items, $"smoke-hover-{tag}-tools-menu.png", tag, "tools menu", failures);
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                Leave(window, mouse);
                menu.Hide();
                await Program.WaitUntilAsync(() => !menu.IsOpen);
                window.Width = width;
                await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - width) <= 1);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in HoverContrastChecks: " + exception));
        }
    }

    private static async Task ContextMenuAsync(MainWindow window, MouseDevice mouse, string tag, List<string> failures)
    {
        // An empty list shows the empty-state guide instead of Search, Columns and Copy URLs (4b79032e), so the columns menu
        // needs one passive, network-free row to exist at all.
        await using SyntheticShellRows synthetic = await SyntheticShellRows.CreateAsync(window,
            [SnapshotFixtures.Thread(990001, "990001", "Hover contrast fixture", completed: 3, known: 5)]);
        Button columns = window.FindControl<Button>("DownloadsColumnsButton")!;
        ActionToolBar actions = window.FindControl<ActionToolBar>("WorkspaceListActions")!;
        window.UpdateLayout();
        bool overflow = actions.OverflowHost?.Children.Contains(columns) == true;
        if (overflow) actions.More!.IsChecked = true;
        try
        {
            await Program.WaitUntilAsync(() => columns.IsEffectivelyVisible && columns.IsEffectivelyEnabled);
            SmokeActionClick.Execute(columns);
            ContextMenu menu = columns.ContextMenu ?? throw new InvalidOperationException("The columns context menu is missing.");
            MenuItem[] items = Array.Empty<MenuItem>();
            await Program.WaitUntilAsync(() =>
            {
                items = menu.Items.OfType<MenuItem>().Where(item => item.IsEffectivelyVisible && item.Bounds.Height > 0).ToArray();
                return menu.IsOpen && items.Length >= 2;
            });
            Exception? failureBeforeCleanup = null;
            try { await ProbeMenuAsync(window, mouse, items, $"smoke-hover-{tag}-context-menu.png", tag, "context menu", failures); }
            catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
            finally
            {
                await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
                {
                    Leave(window, mouse);
                    menu.Close();
                    await Program.WaitUntilAsync(() => !menu.IsOpen);
                }, exception => Console.Error.WriteLine("SMOKE cleanup failed in HoverContrastChecks: " + exception));
            }
        }
        finally { if (overflow) actions.More!.IsChecked = false; }
    }

    private static async Task ProbeMenuAsync(MainWindow window, MouseDevice mouse, MenuItem[] items, string file, string tag, string scene,
        List<string> failures)
    {
        // Hover a row with a row above it, so the outline is measured against a neighbouring resting row too.
        MenuItem target = items.Skip(1).FirstOrDefault(item => item.IsEffectivelyEnabled) ?? items.First(item => item.IsEffectivelyEnabled);
        TopLevel root = TopLevel.GetTopLevel(target) ?? throw new InvalidOperationException("The menu has no top level.");
        Control surface = target.GetVisualAncestors().OfType<Control>().First(control => control is ContextMenu or MenuFlyoutPresenter);
        await HoverAsync(window, root, target, mouse, () => target.IsSelected || target.IsPointerOver);
        using SKBitmap image = Capture(surface, file);
        ProbeRow(image, surface, target, tag, scene + " hover", failures, outline: true);
        ProbeOverlayEdge(window, image, surface, tag, scene, failures);
    }

    // A row of a menu or drop-down: label on its fill; hover outline (or selection bar) against the fill and the row above.
    private static void ProbeRow(SKBitmap image, Control surface, Control row, string tag, string scene, List<string> failures,
        bool outline, bool bar = false)
    {
        double mid = row.Bounds.Height / 2;
        Color fill = Sample(image, row, surface, 8, mid);
        Control label = row.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text) && block.Name != "PART_InputGestureText");
        Require(failures, tag, scene + " label", Contrast(Strongest(image, label, surface, fill), fill), TextMinimum);
        Color above = Sample(image, row, surface, 8, -1.5);
        if (outline)
        {
            Color edge = Sample(image, row, surface, 0.5, mid);
            Color top = Sample(image, row, surface, 8, 0.5);
            Require(failures, tag, scene + " outline/fill", Math.Min(Contrast(edge, fill), Contrast(top, fill)), BoundaryMinimum);
            Require(failures, tag, scene + " outline/neighbour", Contrast(top, above), BoundaryMinimum);
        }
        if (bar)
        {
            Color edge = Sample(image, row, surface, 1.5, mid);
            Require(failures, tag, scene + " bar/fill", Contrast(edge, fill), BoundaryMinimum);
        }
    }

    // The overlay's own edge: against its interior and against the shell surfaces it opens over.
    private static void ProbeOverlayEdge(MainWindow window, SKBitmap image, Control surface, string tag, string scene, List<string> failures)
    {
        Border frame = surface as Border ?? surface.GetVisualDescendants().OfType<Border>()
            .First(border => border.BorderThickness.Left >= 1 && border.Background is ISolidColorBrush { Color.A: 255 });
        Color edge = Sample(image, frame, surface, 0.5, frame.Bounds.Height / 2);
        Color inside = Solid(frame.Background); // the opaque overlay surface itself (rows on it carry their own states)
        Color panel = SmokeThemeResources.Color(window, "Workspace.Panel");
        Color page = SmokeThemeResources.Color(window, "Workspace.Background");
        Require(failures, tag, scene + " overlay edge/interior", Contrast(edge, inside), BoundaryMinimum);
        Require(failures, tag, scene + " overlay edge/shell", Math.Min(Contrast(edge, panel), Contrast(edge, page)), BoundaryMinimum);
        Console.WriteLine("SMOKE hover overlay: " + JsonSerializer.Serialize(new
        {
            theme = tag, scene, surface = Solid(frame.Background).ToString(), border = (frame.BorderBrush as ISolidColorBrush)?.Color.ToString(),
            edge = edge.ToString(), inside = inside.ToString()
        }));
    }

    private static Control Label(Button button) => button.GetVisualDescendants().OfType<TextBlock>()
        .First(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text));

    private static void Require(List<string> failures, string tag, string what, double ratio, double minimum)
    {
        Console.WriteLine($"SMOKE hover probe: {tag} {what}: {ratio:0.00}:1 (needs {minimum:0.0})");
        if (ratio < minimum) failures.Add($"{tag} {what} {ratio:0.00}:1 < {minimum:0.0}");
    }

    private static async Task HoverAsync(Window window, TopLevel root, Control target, MouseDevice mouse, Func<bool> active)
    {
        Leave(window, mouse);
        Point center = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root)
            ?? throw new InvalidOperationException("No pointer position for " + target.GetType().Name);
        Input(root, mouse, RawPointerEventType.Move, center);
        await Program.WaitUntilAsync(active, () => $"{target.GetType().Name} {target.Name}: over={target.IsPointerOver}");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(220); // settled brush transitions, not interpolated frames
        root.UpdateLayout();
    }

    private static void Leave(Window window, MouseDevice mouse) =>
        Input(window, mouse, RawPointerEventType.Move, new Point(1, window.Bounds.Height - 2));

    private static async Task SettleAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Task.Delay(220);
        window.UpdateLayout();
    }

    private static Popup Popup(ComboBox combo) => combo.GetVisualDescendants().OfType<Popup>().Single(popup => popup.Name == "PART_Popup");

    private static SKBitmap CaptureWindow(Window window, string file)
    {
        Program.CaptureWindow(window, file);
        return SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, file));
    }

    // Native popups are their own top level and absent from owner captures: render the overlay itself.
    private static SKBitmap Capture(Control surface, string file)
    {
        surface.UpdateLayout();
        double scale = TopLevel.GetTopLevel(surface)?.RenderScaling ?? 1;
        using RenderTargetBitmap bitmap = new(PixelSize.FromSize(surface.Bounds.Size, scale), new Vector(96 * scale, 96 * scale));
        bitmap.Render(surface);
        string path = Path.Combine(AppContext.BaseDirectory, file);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE hover overlay render: " + path);
        return SKBitmap.Decode(path);
    }

    private static Color Sample(SKBitmap image, Visual element, Visual space, double x, double y)
    {
        Point point = element.TranslatePoint(new Point(x, y), space) ?? throw new InvalidOperationException("No sample position.");
        double scale = TopLevel.GetTopLevel(space)?.RenderScaling ?? 1;
        int px = Math.Clamp((int)Math.Floor(point.X * scale), 0, image.Width - 1);
        int py = Math.Clamp((int)Math.Floor(point.Y * scale), 0, image.Height - 1);
        SKColor pixel = image.GetPixel(px, py);
        return Color.FromArgb(255, pixel.Red, pixel.Green, pixel.Blue);
    }

    private static Color Strongest(SKBitmap image, Control area, Visual space, Color background)
    {
        Point start = area.TranslatePoint(default, space) ?? throw new InvalidOperationException("No label position.");
        double scale = TopLevel.GetTopLevel(space)?.RenderScaling ?? 1;
        Color strongest = background;
        for (int y = Math.Max(0, (int)Math.Ceiling(start.Y * scale)); y < Math.Min(image.Height, Math.Floor((start.Y + area.Bounds.Height) * scale)); y++)
        for (int x = Math.Max(0, (int)Math.Ceiling(start.X * scale)); x < Math.Min(image.Width, Math.Floor((start.X + area.Bounds.Width) * scale)); x++)
        {
            SKColor pixel = image.GetPixel(x, y);
            Color color = Color.FromArgb(255, pixel.Red, pixel.Green, pixel.Blue);
            if (Contrast(color, background) > Contrast(strongest, background)) strongest = color;
        }
        return strongest;
    }

    private static Color Solid(IBrush? brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Transparent;

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte channel) { double value = channel / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        static double Luminance(Color color) => .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static void Input(TopLevel root, MouseDevice mouse, RawPointerEventType type, Point point)
        => AvaloniaLocator.Current.GetRequiredService<IInputManager>().ProcessInput(
            new RawPointerEventArgs(mouse, (ulong)Environment.TickCount64,
                // Avalonia 12 keeps the root internal; routing raw input through it gives real hover states (see CommandButtonContrastChecks).
                typeof(TopLevel).GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(root) as IInputRoot
                    ?? throw new InvalidOperationException("No input root for " + root.GetType().Name),
                type, point, RawInputModifiers.None));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
