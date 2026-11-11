using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Shared dialog chrome: the header and the local error strip stay on top, the action
/// footer stays at the bottom, and only the body scrolls between them.
/// </summary>
/// <remarks>
/// Layout only. Callers keep their own commands, drafts, focus and close semantics.
/// Part names are fixed so smoke checks can verify every dialog the same way; they are
/// not registered in a name scope, so several frames may live in one window.
/// </remarks>
internal static class DialogFrame
{
    internal const string HeaderName = "DialogFrameHeader";
    internal const string ErrorStripName = "DialogFrameErrorStrip";
    internal const string BodyName = "DialogFrameBody";
    internal const string FooterName = "DialogFrameFooter";

    /// <summary>Builds the four-row frame. A footer that is not already a dialog footer is wrapped in one.</summary>
    internal static Grid Build(Control header, Control body, Control footer, Control? errorStrip = null)
    {
        Border headerHost = new() { Name = HeaderName, Child = header, Padding = new Thickness(16, 16, 16, 8) };
        ScrollViewer scroll = new()
        {
            Name = BodyName, Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Border footerHost = footer is Border border && border.Classes.Contains("workspace-dialog-footer")
            ? border : new Border { Child = footer };
        footerHost.Name = FooterName;
        if (!footerHost.Classes.Contains("workspace-dialog-footer")) footerHost.Classes.Add("workspace-dialog-footer");
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        layout.Children.Add(headerHost);
        if (errorStrip != null)
        {
            Border errorHost = new() { Name = ErrorStripName, Child = errorStrip, Padding = new Thickness(16, 0, 16, 8) };
            // The host follows the strip so a hidden strip leaves no blank band above the body.
            errorHost.IsVisible = errorStrip.IsVisible;
            errorStrip.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) errorHost.IsVisible = errorStrip.IsVisible; };
            Grid.SetRow(errorHost, 1); layout.Children.Add(errorHost);
        }
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        Grid.SetRow(footerHost, 3); layout.Children.Add(footerHost);
        KeyboardNavigation.SetTabNavigation(layout, KeyboardNavigationMode.Cycle);
        return layout;
    }

    /// <summary>
    /// Wraps an existing error text in the local strip. The strip is visible only while the
    /// text is non-empty; long messages scroll inside the strip instead of pushing the footer away.
    /// </summary>
    internal static Border ErrorStrip(TextBlock error, double maxHeight = 120)
    {
        if (!error.Classes.Contains("workspace-error")) error.Classes.Add("workspace-error");
        if (error.TextWrapping == Avalonia.Media.TextWrapping.NoWrap) error.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        Border strip = new()
        {
            Child = new ScrollViewer { Content = error, MaxHeight = maxHeight, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
        };
        strip.Classes.Add("workspace-error");
        strip.IsVisible = !string.IsNullOrWhiteSpace(error.Text);
        error.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty) strip.IsVisible = !string.IsNullOrWhiteSpace(error.Text);
        };
        return strip;
    }
}
