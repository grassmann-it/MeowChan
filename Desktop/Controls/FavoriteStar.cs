using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The one favourite mark of the app: an outline heart when not marked, a filled heart in the favourite colour when marked (Tom, 25.11.2026:
/// the favourite is a heart everywhere, the star belongs to the rating, <see cref="RatingStar"/>). The type keeps its old name for its callers.
/// Reader card, thread list and header, gallery toolbar, Media tiles and Archive tiles all draw it from here (the same
/// "heart" / "heart-filled" icon pair of the command catalog); there is no tick or other variant. The mark never stands alone: every
/// control that shows it also names the state (tooltip, automation name) so colour is not the only carrier.
/// </summary>
internal static class FavoriteStar
{
    private const string MarkedTag = "favorite-star:marked", UnmarkedTag = "favorite-star:unmarked";

    /// <summary>
    /// A heart icon of <paramref name="size"/> dip. Unmarked follows the foreground of the control that hosts it; a marked heart keeps the
    /// filled shape but follows the foreground too when <paramref name="tinted"/> is false (a failed write shows the error edge, not gold).
    /// </summary>
    internal static Border Create(bool marked, int size = 16, bool tinted = true, bool mixed = false) =>
        Draw(marked ? WorkspaceIcons.HeartFilled : WorkspaceIcons.Heart, marked, size, tinted, mixed, MarkedTag, UnmarkedTag);

    /// <summary>The shared drawing of a favourite heart and a rating star: one path of WorkspaceIcons scaled to <paramref name="size"/>, tinted when set.</summary>
    internal static Border Draw(string key, bool marked, int size, bool tinted, bool mixed, string markedTag, string unmarkedTag)
    {
        // One drawing: the icon of WorkspaceIcons (outline / filled path on the 16 dip grid), scaled to <size>; this wrapper only carries
        // the state tag and the favourite tint. The path builds its geometry when it joins a visual tree (no platform at construction).
        Avalonia.Controls.Shapes.Path star = WorkspaceIcons.Create(key, inheritForeground: !(mixed || marked && tinted));
        // Gold only reads on the dark surfaces; the theme key carries the darker amber for Light (FavoriteTintContrastTests).
        if (mixed || marked && tinted)
        {
            star[!Avalonia.Controls.Shapes.Shape.StrokeProperty] = new DynamicResourceExtension("Workspace.Favorite");
            if (marked) star[!Avalonia.Controls.Shapes.Shape.FillProperty] = new DynamicResourceExtension("Workspace.Favorite");
        }
        star.Stretch = Stretch.Uniform; star.Width = size; star.Height = size;
        return new Border
        {
            Width = size, Height = size, Tag = marked ? markedTag : unmarkedTag, Child = star, IsHitTestVisible = false,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
    }

    /// <summary>True or false for the content a favourite control shows, null when the content is no favourite heart.</summary>
    internal static bool? MarkOf(object? content) => content is Border { Tag: string tag } && (tag == MarkedTag || tag == UnmarkedTag)
        ? tag == MarkedTag : null;
}
