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
/// <para>
/// Dialog language (modals plan MD1): the window comes from <see cref="DialogWindow"/> (kind, size step, fit), the
/// title stands in the title bar only and is never repeated here (owner decision E1). The header is for a real
/// subtitle or introduction and is left out otherwise (<c>header: null</c>); the actions come from
/// <see cref="DialogFooter"/> with the primary action right-most, and Escape from <see cref="DialogEscape"/>.
/// A dialog whose only action is Close may leave the footer out as well: × in the title bar closes it.
/// </para>
/// </remarks>
internal static class DialogFrame
{
    internal const string HeaderName = "DialogFrameHeader";
    internal const string ErrorStripName = "DialogFrameErrorStrip";
    internal const string BodyName = "DialogFrameBody";
    internal const string FooterName = "DialogFrameFooter";

    /// <summary>
    /// Builds the four-row frame. A footer that is not already a dialog footer is wrapped in one. Without a header the
    /// body (or the error strip) starts one header inset below the title bar; without a footer the body reaches the bottom.
    /// </summary>
    /// <param name="fillBody">
    /// The body gets exactly the height between header and footer instead of scrolling as a whole. For a body whose own
    /// list scrolls (and virtualizes): inside a vertically scrolling body such a list would be measured at infinite height.
    /// </param>
    internal static Grid Build(Control? header, Control body, Control? footer, Control? errorStrip = null, bool fillBody = false)
    {
        ScrollViewer scroll = new()
        {
            Name = BodyName, Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = fillBody ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto
        };
        Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        if (header != null) layout.Children.Add(new Border { Name = HeaderName, Child = header, Padding = new Thickness(16, 16, 16, 8) });
        else layout.Margin = new Thickness(0, 16, 0, 0);
        if (errorStrip != null)
        {
            Border errorHost = new() { Name = ErrorStripName, Child = errorStrip, Padding = new Thickness(16, 0, 16, 8) };
            // The host follows the strip so a hidden strip leaves no blank band above the body.
            errorHost.IsVisible = errorStrip.IsVisible;
            errorStrip.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) errorHost.IsVisible = errorStrip.IsVisible; };
            Grid.SetRow(errorHost, 1); layout.Children.Add(errorHost);
        }
        Grid.SetRow(scroll, 2); layout.Children.Add(scroll);
        if (footer != null)
        {
            Border footerHost = footer is Border border && border.Classes.Contains("workspace-dialog-footer")
                ? border : new Border { Child = footer };
            footerHost.Name = FooterName;
            if (!footerHost.Classes.Contains("workspace-dialog-footer")) footerHost.Classes.Add("workspace-dialog-footer");
            Grid.SetRow(footerHost, 3); layout.Children.Add(footerHost);
        }
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
