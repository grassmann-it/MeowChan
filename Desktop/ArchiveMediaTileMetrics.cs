namespace MeowChan.Desktop;

/// <summary>The measures of one archive media tile at one size (dip): the cell of the tile panel and the picture slot inside it.</summary>
internal readonly record struct ArchiveTileAppearance(double CellWidth, double CellHeight, double PreviewHeight)
{
    /// <summary>The longest side of the picture slot: the cell minus the tile's 8 dip margins and the corner room the badges and the check keep (170 at Medium).</summary>
    internal double PictureWidth => CellWidth - 20;
}

/// <summary>
/// The measures of the archive's saved-media tiles for the three tile sizes (pure, no control): Medium is the tile as it always was (190 by 210
/// dip, a 125 dip picture slot), Small and Large take the media page's cell widths (<see cref="MediaTileMetrics"/>, so the same size word means
/// the same column width on both pages) and a picture slot that grows with the cell. What sits under the picture (name, rating, the two-line
/// summary) does not change with the size, so the cell is the slot plus <see cref="BelowPreview"/>. These are layout measures of the tile, not limits of the user's data.
/// </summary>
internal static class ArchiveTileMetrics
{
    internal const double SmallPreviewHeight = 95, MediumPreviewHeight = 125, LargePreviewHeight = 170;
    /// <summary>What the rows under the picture and the tile's margins take: 210 less the 125 dip slot of the Medium tile.</summary>
    internal const double BelowPreview = 85;

    /// <summary>The Medium tile: today's 190 by 210 dip.</summary>
    internal static ArchiveTileAppearance Standard { get; } = For(MediaTileSize.Medium);

    internal static ArchiveTileAppearance For(MediaTileSize size)
    {
        (double width, double preview) = size switch
        {
            MediaTileSize.Small => (MediaTileMetrics.SmallCellWidth, SmallPreviewHeight),
            MediaTileSize.Large => (MediaTileMetrics.LargeCellWidth, LargePreviewHeight),
            _ => (MediaTileMetrics.MediumCellWidth, MediumPreviewHeight)
        };
        return new ArchiveTileAppearance(width, preview + BelowPreview, preview);
    }
}
