using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace MeowChan.Desktop;

/// <summary>
/// Binds controls built in code to the design tokens of WorkspaceStyles.axaml (D0) instead of literals.
/// The binding is dynamic, so a control resolves its token when it enters the tree and follows theme changes.
/// </summary>
internal static class WorkspaceTokens
{
    internal const string TextCaption = "Workspace.Text.Caption";
    internal const string RadiusBadge = "Workspace.Radius.Badge";
    internal const string RadiusChip = "Workspace.Radius.Chip";

    /// <summary>Caption size (Workspace.Text.Caption, 11) for a text block or a button.</summary>
    internal static T CaptionText<T>(this T control) where T : Control
    {
        control[!TextElement.FontSizeProperty] = new DynamicResourceExtension(TextCaption);
        return control;
    }

    /// <summary>Badge corners (Workspace.Radius.Badge) for a mark under 20 dip high.</summary>
    internal static Border BadgeCorners(this Border border)
    {
        border[!Border.CornerRadiusProperty] = new DynamicResourceExtension(RadiusBadge);
        return border;
    }

    /// <summary>Pill corners (Workspace.Radius.Chip, full round up to 24 dip high).</summary>
    internal static Border PillCorners(this Border border)
    {
        border[!Border.CornerRadiusProperty] = new DynamicResourceExtension(RadiusChip);
        return border;
    }
}
