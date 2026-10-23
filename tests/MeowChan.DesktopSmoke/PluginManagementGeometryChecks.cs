using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static partial class PluginManagementChecks
{
    private static async Task WaitForPluginMatrixGeometryAsync(MainWindow window, PluginsPage page,
        string mode, string language, ThemeVariant theme, double width, double height, bool requireDetails)
    {
        string phase = requireDetails ? "selected-details" : "native-resize-before-selection";
        Button back = page.FindControl<Button>("PluginBack")!;
        Grid list = page.FindControl<Grid>("PluginListPane")!;
        ScrollViewer details = page.FindControl<ScrollViewer>("PluginDetailsPane")!;
        string? lastState = null;
        string State()
        {
            Control? focused = TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement() as Control;
            return JsonSerializer.Serialize(new
            {
                phase, expectedMode = mode, expectedLanguage = language, expectedTheme = theme.Key?.ToString(),
                actualMode = (window.FindControl<ComboBox>("WorkspaceModeComboBox")?.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                actualLanguage = Texts.Language, actualTheme = window.ActualThemeVariant.Key?.ToString(),
                target = new { width, height }, requested = new { window.Width, window.Height },
                minimum = new { window.MinWidth, window.MinHeight },
                windowState = window.WindowState.ToString(), windowBounds = window.Bounds.ToString(),
                client = window.ClientSize.ToString(), frame = window.FrameSize?.ToString(), window.RenderScaling,
                pageBounds = page.Bounds.ToString(), pageVisible = page.IsEffectivelyVisible,
                listBounds = list.Bounds.ToString(), listVisible = list.IsVisible, listEffective = list.IsEffectivelyVisible,
                detailsBounds = details.Bounds.ToString(), detailsVisible = details.IsVisible, detailsEffective = details.IsEffectivelyVisible,
                backBounds = back.Bounds.ToString(), backVisible = back.IsVisible, backEffective = back.IsEffectivelyVisible,
                selectedKey = page.Selected?.Key,
                focus = focused?.Name ?? focused?.GetType().Name, focusVisible = focused?.IsEffectivelyVisible,
                focusInList = focused?.GetVisualAncestors().Contains(list) == true,
                focusInDetails = focused?.GetVisualAncestors().Contains(details) == true
            });
        }
        try
        {
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                string state = State();
                if (state != lastState) { Console.WriteLine("PLUGIN MATRIX GEOMETRY " + state); lastState = state; }
                bool actualMinimum = window.WindowState == WindowState.Normal
                    && Math.Abs(window.MinWidth - width) <= 1 && Math.Abs(window.MinHeight - height) <= 1
                    && PresentationChecks.MatchesRequestedSize(window, width, height);
                return actualMinimum && (!requireDetails || page.Bounds.Width < 850 && back.IsEffectivelyVisible);
            });
            Console.WriteLine("PLUGIN MATRIX GEOMETRY READY " + State());
        }
        catch (TimeoutException)
        {
            Console.WriteLine("PLUGIN MATRIX GEOMETRY TIMEOUT " + State());
            throw;
        }
    }
}
