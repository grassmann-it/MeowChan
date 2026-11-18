using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one favourite mark of the app: ☆ an outline star when not marked, ★ a filled star in the favourite colour when marked.
/// Reader card, thread list and header, gallery toolbar, Media tiles and Archive tiles all draw it from here (the same
/// "star" / "star-filled" icon pair of the command catalog); there is no tick or other variant. The mark never stands alone: every
/// control that shows it also names the state (tooltip, automation name) so colour is not the only carrier.
/// </summary>
internal static class FavoriteStar
{
    private const string MarkedTag = "favorite-star:marked", UnmarkedTag = "favorite-star:unmarked";

    /// <summary>
    /// A star icon of <paramref name="size"/> dip. Unmarked follows the foreground of the control that hosts it; a marked star keeps the
    /// filled shape but follows the foreground too when <paramref name="tinted"/> is false (a failed write shows the error edge, not gold).
    /// </summary>
    internal static Border Create(bool marked, int size = 16, bool tinted = true)
    {
        // One drawing: the star of WorkspaceIcons (outline / filled path on the 16 dip grid), scaled to <size>; this wrapper only carries
        // the state tag and the favourite tint. The path builds its geometry when it joins a visual tree (no platform at construction).
        Avalonia.Controls.Shapes.Path star = WorkspaceIcons.Create(marked ? WorkspaceIcons.StarFilled : WorkspaceIcons.Star, inheritForeground: !(marked && tinted));
        // Gold only reads on the dark surfaces; the theme key carries the darker amber for Light (FavoriteTintContrastTests).
        if (marked && tinted)
        {
            star[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("Workspace.Favorite");
            star[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("Workspace.Favorite");
        }
        star.Stretch = Stretch.Uniform; star.Width = size; star.Height = size;
        return new Border
        {
            Width = size, Height = size, Tag = marked ? MarkedTag : UnmarkedTag, Child = star, IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
    }

    /// <summary>True or false for the content a favourite control shows, null when the content is no favourite star.</summary>
    internal static bool? MarkOf(object? content) => content is Border { Tag: string tag } && (tag == MarkedTag || tag == UnmarkedTag)
        ? tag == MarkedTag : null;
}
