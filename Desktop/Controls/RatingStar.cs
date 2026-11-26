using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// One star of a five-star rating: an outline star up to the rating not reached, a filled star in the rating tint up to it. Since 25.11.2026 the
/// star belongs to the rating alone; the favourite is the heart of <see cref="FavoriteStar"/>. Same drawing, size and tint rules as the heart.
/// </summary>
internal static class RatingStar
{
    private const string FilledTag = "rating-star:filled", EmptyTag = "rating-star:empty";
    /// <summary>The theme colour of a filled rating star (gold; a colour of its own beside the red heart since 26.11.2026).</summary>
    internal const string Token = "Workspace.Rating";

    /// <summary>A star of <paramref name="size"/> dip; a filled star follows the host's foreground when <paramref name="tinted"/> is false (a preview).</summary>
    internal static Border Create(bool filled, int size = 16, bool tinted = true) =>
        FavoriteStar.Draw(filled ? WorkspaceIcons.StarFilled : WorkspaceIcons.Star, filled, size, tinted, mixed: false, FilledTag, EmptyTag, Token);

    /// <summary>True or false for a rating star, null when the content is no rating star.</summary>
    internal static bool? FilledOf(object? content) => content is Border { Tag: string tag } && (tag == FilledTag || tag == EmptyTag)
        ? tag == FilledTag : null;
}
