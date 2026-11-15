using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace MeowChan.Desktop;

/// <summary>
/// Binds controls built in code to the design tokens of WorkspaceStyles.axaml (D0) instead of literals.
/// The binding is dynamic, so a control resolves its token when it enters the tree and follows theme changes.
/// </summary>
internal static class WorkspaceTokens
{
    internal const string TextCaption = "Workspace.Text.Caption";
    internal const string TextMeta = "Workspace.Text.Meta";
    internal const string TextSubtitle = "Workspace.Text.Subtitle";
    internal const string TextTitle = "Workspace.Text.Title";
    internal const string RadiusBadge = "Workspace.Radius.Badge";
    internal const string RadiusControl = "Workspace.Radius.Control";
    internal const string SizeControl = "Workspace.Size.Control";

    /// <summary>Caption size (Workspace.Text.Caption, 11) for a text block or a button.</summary>
    internal static T CaptionText<T>(this T control) where T : Control
    {
        control[!TextElement.FontSizeProperty] = new DynamicResourceExtension(TextCaption);
        return control;
    }

    /// <summary>Meta size (Workspace.Text.Meta, 12) for a text block or a button.</summary>
    internal static T MetaText<T>(this T control) where T : Control => control.TextSize(TextMeta);

    /// <summary>Subtitle size (Workspace.Text.Subtitle, 16) for a text block or a button.</summary>
    internal static T SubtitleText<T>(this T control) where T : Control => control.TextSize(TextSubtitle);

    /// <summary>Title size (Workspace.Text.Title, 20) for a text block or a button.</summary>
    internal static T TitleText<T>(this T control) where T : Control => control.TextSize(TextTitle);

    private static T TextSize<T>(this T control, string token) where T : Control
    {
        control[!TextElement.FontSizeProperty] = new DynamicResourceExtension(token);
        return control;
    }

    /// <summary>Badge corners (Workspace.Radius.Badge) for a mark under 20 dip high.</summary>
    internal static Border BadgeCorners(this Border border)
    {
        border[!Border.CornerRadiusProperty] = new DynamicResourceExtension(RadiusBadge);
        return border;
    }

    /// <summary>
    /// The size of a button for a passive mark that stands beside buttons (Workspace.Size.Control, 32): at least a square of that size, exactly that high,
    /// with the control corners (Workspace.Radius.Control). The mark then lines up with the buttons next to it instead of being a smaller badge.
    /// </summary>
    internal static Border ControlSized(this Border border)
    {
        border[!Layoutable.HeightProperty] = new DynamicResourceExtension(SizeControl);
        border[!Layoutable.MinWidthProperty] = new DynamicResourceExtension(SizeControl);
        border[!Border.CornerRadiusProperty] = new DynamicResourceExtension(RadiusControl);
        return border;
    }
}
