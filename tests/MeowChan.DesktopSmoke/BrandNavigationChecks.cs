using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace MeowChan.DesktopSmoke;

internal static class BrandNavigationChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        foreach (string name in new[] { "Downloads", "Archive", "Rules", "Sources", "Diagnostics", "Settings", "Tools" })
        {
            Button button = window.FindControl<Button>(name + "NavigationButton")!;
            if (button.Content is not StackPanel { Children.Count: 2 } panel
                || panel.Children[0] is not Border { OpacityMask: not null }
                || panel.Children[1] is not TextBlock { Text.Length: > 0 }
                || string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                throw new InvalidOperationException(name + " navigation lacks an icon, visible label or accessible name.");
        }

        Button brand = window.FindControl<Button>("BrandAboutButton")!;
        if (!brand.IsEffectivelyVisible || window.FindControl<Image>("WorkspaceLogo")!.Source is not Bitmap)
            throw new InvalidOperationException("The clickable header logo is not visible.");
        brand.Focus();
        brand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any(about => about.IsVisible));
        AboutWindow about = window.OwnedWindows.OfType<AboutWindow>().Single();
        about.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !about.IsVisible);
        Console.WriteLine("SMOKE brand navigation: seven labeled icons and header-logo About entry passed.");
    }
}
