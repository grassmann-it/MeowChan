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
        foreach (string name in new[] { "Downloads", "Archive", "Rules", "Plugins", "Sources", "Diagnostics", "ContinueReading", "Settings", "Tools" })
        {
            Button button = window.FindControl<Button>(name + "NavigationButton")!;
            bool labeledIcon = button.Content is StackPanel { Children.Count: 2 } panel
                && panel.Children[0] is Border { OpacityMask: not null }
                && panel.Children[1] is TextBlock { Text.Length: > 0 };
            bool compactIcon = name is "Downloads" or "Settings" or "Tools"
                && button.Content is Border { OpacityMask: not null };
            if ((!labeledIcon && !compactIcon) || string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
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
        Console.WriteLine("SMOKE brand navigation: nine accessible icons (compact footer labels where needed) and header-logo About entry passed.");
    }
}
