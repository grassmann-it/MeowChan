using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopSmoke;

internal static class SettingsFooterOverlapChecks
{
    // Called at the existing field-error capture boundary, before CaptureWindow's
    // UpdateLayout. This observer never measures, arranges, focuses or navigates.
    internal static void AssertCurrentScene(MainWindow window, string stage = "before-layout",
        string expectedLanguage = "de", double expectedWidth = 640, double expectedHeight = 480,
        bool requireFullLabels = false)
    {
        double expectedScale = double.TryParse(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double requestedScale)
            ? requestedScale : 1;
        Border footer = window.FindControl<Border>("WorkspaceNavigation")!;
        StackPanel panel = window.FindControl<StackPanel>("WorkspaceNavigationButtons")!;
        ComboBox mode = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        Grid layout = (Grid)panel.Parent!;
        Control[] controls = panel.Children.Where(control => control.IsVisible).Append(mode).ToArray();
        var overlaps = (from first in controls.Select((control, index) => (control, index))
                        from second in controls.Skip(first.index + 1)
                        let a = Box(first.control)
                        let b = Box(second)
                        let intersection = a.HasValue && b.HasValue ? a.Value.Intersect(b.Value) : (Rect?)null
                        select new { first = first.control.Name, second = second.Name,
                            first_box = a?.ToString(), second_box = b?.ToString(),
                            intersection = intersection?.ToString(), width = intersection?.Width,
                            height = intersection?.Height,
                            disjoint = intersection.HasValue && (intersection.Value.Width <= 1 || intersection.Value.Height <= 1) }).ToArray();
        var state = new {
            diagnostic = "settings-footer-before-diagnostic-layout", stage, explicit_diagnostic_layout_calls = 0,
            expected_language = expectedLanguage, expected_width = expectedWidth, expected_height = expectedHeight,
            expected_scale = expectedScale, require_full_labels = requireFullLabels,
            language = Texts.Language, mode = window.SessionHost.Config.WorkspaceMode,
            settings_visible = window.SettingsPageVisible, field_error = window.SettingsEditor.HasError,
            window = new { window.Width, window.Height, bounds = window.Bounds.ToString(),
                client = window.ClientSize.ToString(), window.MinWidth, window.MinHeight,
                scale = window.RenderScaling, state = window.WindowState.ToString(),
                theme = window.ActualThemeVariant.ToString() },
            focus = Describe(window.FocusManager?.GetFocusedElement() as Control),
            banner = Describe(window.FindControl<Control>("ShellActionErrorBanner")),
            footer = Describe(footer), footer_padding = footer.Padding.ToString(),
            panel = Describe(panel), panel.Spacing, grid = Describe(layout), layout.ColumnSpacing,
            columns = layout.ColumnDefinitions.Select(column => new { requested = column.Width.ToString(), actual = column.ActualWidth }).ToArray(),
            calculated_available = window.ClientSize.Width - footer.Padding.Left - footer.Padding.Right - mode.Width - layout.ColumnSpacing,
            controls = controls.Select(Describe).ToArray(), overlaps,
            settings_center_hit = Hit(window.FindControl<Button>("SettingsNavigationButton")!),
            mode_center_hit = Hit(mode)
        };
        string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
        Console.WriteLine("SMOKE settings footer pre-layout " + json);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"smoke-settings-footer-{stage}.json"), json);
        CaptureWithoutLayout();

        Require(window.SettingsPageVisible && window.SettingsEditor.HasError && Texts.Language == expectedLanguage
            && window.SessionHost.Config.WorkspaceMode == "Barebones", "Footer probe did not preserve the requested localized Barebones field-error scene.");
        Require(window.WindowState == WindowState.Normal && Math.Abs(window.Bounds.Width - expectedWidth) <= 1
            && Math.Abs(window.Bounds.Height - expectedHeight) <= 1
            && Math.Abs(window.ClientSize.Width - expectedWidth) <= 1 && Math.Abs(window.ClientSize.Height - expectedHeight) <= 1
            && new[] { 1d, 1.5d, 2d }.Contains(expectedScale)
            && Math.Abs(window.RenderScaling - expectedScale) < 0.025 && window.ActualThemeVariant.ToString() == "Dark",
            "Footer probe did not reach the requested actual Dark/client/scale scene.");
        Rect? footerBox = Box(footer);
        foreach (Control control in controls)
        {
            Rect? box = Box(control);
            Require(control.IsEffectivelyVisible && box is { Width: > 0, Height: > 0 }
                && ReferenceEquals(TopLevel.GetTopLevel(control), window), "A visible footer control has no actual window presentation: " + control.Name);
            Require(box!.Value.X >= -1 && box.Value.Y >= -1 && box.Value.Right <= window.Bounds.Width + 1
                && box.Value.Bottom <= window.Bounds.Height + 1 && footerBox.HasValue
                && box.Value.X >= footerBox.Value.X - 1 && box.Value.Y >= footerBox.Value.Y - 1
                && box.Value.Right <= footerBox.Value.Right + 1 && box.Value.Bottom <= footerBox.Value.Bottom + 1,
                "Footer control is clipped outside the actual footer/client: " + control.Name);
        }
        Require(overlaps.All(overlap => overlap.disjoint), "Actual footer controls overlap: "
            + string.Join("; ", overlaps.Where(overlap => !overlap.disjoint).Select(overlap => $"{overlap.first}/{overlap.second} {overlap.intersection}")));
        Require(window.FindControl<Button>("ContinueReadingNavigationButton")!.IsEffectivelyVisible,
            "Continue reading ceased to be a direct footer action.");
        foreach (string name in new[] { "DownloadsNavigationButton", "SettingsNavigationButton", "ToolsNavigationButton", "WorkspaceModeComboBox" })
        {
            Control control = window.FindControl<Control>(name)!;
            Require(control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Focusable, "An eligible footer destination cannot receive user focus: " + name);
        }
        Require(CenterHits(window.FindControl<Button>("SettingsNavigationButton")!) && CenterHits(mode),
            "Settings or view selector center is not actually hit-test reachable.");
        if (requireFullLabels)
            foreach (var (name, command) in new[] {
                ("DownloadsNavigationButton", "navigation.downloads"),
                ("ContinueReadingNavigationButton", "navigation.continue-reading"),
                ("ToolsNavigationButton", "navigation.tools-menu"),
                ("SettingsNavigationButton", "navigation.settings") })
            {
                Button button = window.FindControl<Button>(name)!;
                Require(button.Content is Control content && content.GetVisualDescendants().OfType<TextBlock>()
                    .Any(label => label.IsEffectivelyVisible && label.Text == CommandCatalog.Label(command)
                        && label.Bounds.Width > 0 && label.Bounds.Height > 0
                        && label.Bounds.Width + 1 >= label.DesiredSize.Width
                        && label.TranslatePoint(default, button) is { } origin
                        && origin.X >= -1 && origin.Y >= -1
                        && origin.X + label.Bounds.Width <= button.Bounds.Width + 1
                        && origin.Y + label.Bounds.Height <= button.Bounds.Height + 1),
                    "A label remained compact or clipped after return/widening: " + name);
            }
        Console.WriteLine($"SMOKE settings footer {stage}: actual {expectedLanguage}/Barebones{expectedWidth}x{expectedHeight}/scale{expectedScale} controls attached, unclipped, disjoint; full-label gate={requireFullLabels}.");

        Rect? Box(Control control) => control.TranslatePoint(default, window) is { } origin ? new Rect(origin, control.Bounds.Size) : null;
        bool CenterHits(Control control) => Box(control) is { } box && window.InputHitTest(box.Center) is Visual hit
            && hit.GetSelfAndVisualAncestors().Contains(control);
        object Hit(Control control)
        {
            Rect? box = Box(control);
            Visual? hit = box.HasValue ? window.InputHitTest(box.Value.Center) as Visual : null;
            return new { control.Name, center = box?.Center.ToString(),
                hit = hit is Control found ? found.GetType().Name + ":" + found.Name : hit?.GetType().Name,
                matches = hit?.GetSelfAndVisualAncestors().Contains(control) == true };
        }
        object? Describe(Control? control)
        {
            if (control == null) return null;
            TemplatedControl? styled = control as TemplatedControl;
            Control? content = (control as ContentControl)?.Content as Control;
            return new { type = control.GetType().Name, control.Name, control.IsVisible, control.IsEffectivelyVisible,
                control.IsEnabled, control.IsEffectivelyEnabled, control.Focusable, control.IsFocused,
                bounds = control.Bounds.ToString(), box = Box(control)?.ToString(), desired = control.DesiredSize.ToString(),
                root = TopLevel.GetTopLevel(control)?.GetType().Name, margin = control.Margin.ToString(),
                control.HorizontalAlignment, control.VerticalAlignment,
                font = new { family = styled?.FontFamily.ToString(), size = styled?.FontSize,
                    weight = styled?.FontWeight.ToString(), style = styled?.FontStyle.ToString() },
                padding = styled?.Padding.ToString(), border_thickness = styled?.BorderThickness.ToString(), content_type = (control as ContentControl)?.Content?.GetType().Name,
                content_desired = content?.DesiredSize.ToString(), content_bounds = content?.Bounds.ToString(),
                rendered_labels = control.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible)
                    .Select(text => new { text.Text, text.FontSize, font_family = text.FontFamily.ToString(), desired = text.DesiredSize.ToString(), bounds = text.Bounds.ToString() }).ToArray(),
                content_labels = content?.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray() };
        }
        void CaptureWithoutLayout()
        {
            try
            {
                double scale = window.RenderScaling;
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
                    (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                bitmap.Render(window);
                string path = Path.Combine(AppContext.BaseDirectory, $"smoke-settings-footer-{stage}.png");
                bitmap.Save(path, PngBitmapEncoderOptions.Default);
                Console.WriteLine("SMOKE settings footer raw window render (no diagnostic UpdateLayout): " + path);
            }
            catch (Exception error) { Console.WriteLine("SMOKE settings footer diagnostic render failed: " + error); }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
