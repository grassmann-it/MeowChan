using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopSmoke;

internal static class CommandRefreshChecks
{
    internal static void Run()
    {
        Button button = new();
        CommandBindings.ApplyLabeled(button, "watch.copy-all-links", "Copy 1 URL");
        object? content = button.Content;
        for (int i = 0; i < 1000; i++)
            CommandBindings.ApplyLabeled(button, "watch.copy-all-links", "Copy 1 URL");
        Require(ReferenceEquals(content, button.Content), "Unchanged refresh replaced button visuals.");

        CommandBindings.ApplyLabeled(button, "watch.copy-all-links", "Copy 2 URLs");
        Require(ReferenceEquals(content, button.Content), "A changed label rebuilt the icon subtree.");
        Require(((StackPanel)button.Content!).Children.OfType<TextBlock>().Single().Text == "Copy 2 URLs"
            && AutomationProperties.GetName(button) == "Copy 2 URLs"
            && ToolTip.GetTip(button)?.ToString()?.StartsWith("Copy 2 URLs", StringComparison.Ordinal) == true,
            "Reused content retained a stale label, accessible name or tooltip.");

        Border first = (Border)CommandBindings.Icon("watch.copy-all-links");
        Border second = (Border)CommandBindings.Icon("watch.copy-all-links", 24);
        Require(!ReferenceEquals(first, second) && !ReferenceEquals(first.OpacityMask, second.OpacityMask)
            && ReferenceEquals(((ImageBrush)first.OpacityMask!).Source, ((ImageBrush)second.OpacityMask!).Source),
            "Icons must share their decoded mask while keeping controls and brushes independent.");

        CommandBindings.Apply(button, "reader.close", iconOnly: true);
        Require(button.Content is Border, "Switching to icon-only retained the old label.");
        content = button.Content;
        CommandBindings.Apply(button, "reader.close", () => "Unavailable", iconOnly: true);
        Require(ReferenceEquals(content, button.Content) && ToolTip.GetTip(button)!.ToString()!.Contains("Unavailable"),
            "Reused icon did not refresh its availability tooltip.");
        CommandBindings.Apply(button, "reader.close", iconOnly: true, iconSize: 24);
        Require(button.Content is Border { Width: 24 }, "Icon size changes were ignored.");
        button.Content = "External content";
        CommandBindings.Apply(button, "reader.close", iconOnly: true, iconSize: 24);
        Require(button.Content is Border, "Externally replaced content was not restored.");
        CommandBindings.Apply(button, "reader.close");
        Require(button.Content is string, "Plain-label mode retained an icon subtree.");
        Console.WriteLine("SMOKE command refresh: 1,000 unchanged updates reused visuals; labels, accessibility, tooltips and mode/size changes stayed current; decoded icon masks shared.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
