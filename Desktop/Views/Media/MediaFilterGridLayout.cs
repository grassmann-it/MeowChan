using System;
using System.Collections.Generic;

namespace MeowChan;

/// <summary>
/// How the fields of the media filter section are laid out (M5; pure rules, <see cref="MediaFilterFieldGrid"/> and <see cref="MediaPage"/> apply
/// them). The fields stand in a grid of 4, 3, 2 or 1 columns chosen by the width of the page, each with its label above it. The cells are
/// placed column by column, so the order of the cells in the tree is the order of the tab key: down the first column, then down the next.
/// The widths and heights are layout measures of this section in dip (like <see cref="MediaToolbarLayout"/>), not limits of the user's data,
/// so they are documented here and are no settings.
/// </summary>
internal static class MediaFilterGridLayout
{
    /// <summary>From this page width on the fields stand in four columns.</summary>
    internal const double FourColumnsWidth = 1100;
    /// <summary>From this page width on three columns; below it two (down to <see cref="TwoColumnsWidth"/>).</summary>
    internal const double ThreeColumnsWidth = 760;
    /// <summary>From this page width on two columns; below it every field has a row of its own.</summary>
    internal const double TwoColumnsWidth = 640;
    /// <summary>The widest column count there is.</summary>
    internal const int MaximumColumns = 4;

    /// <summary>Below this page height the section may take only <see cref="CompactHeightShare"/> of it, so the tiles keep their room.</summary>
    internal const double CompactHeight = 640;
    /// <summary>The share of a short page that the open section may take at most.</summary>
    internal const double CompactHeightShare = 0.4;

    /// <summary>The longest the tag row grows: the search popover under it is 280-360 wide, the row beside it stays as wide.</summary>
    internal const double TagRowMaxWidth = 360;

    /// <summary>The column count for the width of the page. An unknown width (not measured yet) is the widest layout.</summary>
    internal static int Columns(double pageWidth) =>
        !double.IsFinite(pageWidth) || pageWidth <= 0 ? MaximumColumns
        : pageWidth >= FourColumnsWidth ? 4
        : pageWidth >= ThreeColumnsWidth ? 3
        : pageWidth >= TwoColumnsWidth ? 2 : 1;

    /// <summary>The most the section may take: <paramref name="regular"/> (the fixed ceiling) on a tall page, 40 % of the page on a short one.</summary>
    internal static double MaxHeight(double pageHeight, double regular) =>
        !double.IsFinite(pageHeight) || pageHeight <= 0 || pageHeight >= CompactHeight ? regular : Math.Min(regular, pageHeight * CompactHeightShare);

    /// <summary>Where the cell with this index sits.</summary>
    internal readonly record struct Slot(int Column, int Row);

    /// <summary>
    /// The places of <paramref name="count"/> cells in <paramref name="columns"/> columns, column by column: the rows are as many as the
    /// cells need (so the columns are as even as they can be), and cell i stands in column i / rows, row i % rows. The list order is
    /// therefore the keyboard order. Fewer columns than asked for are used when the cells fill fewer of them.
    /// </summary>
    internal static IReadOnlyList<Slot> Place(int count, int columns)
    {
        if (count <= 0) return Array.Empty<Slot>();
        int rows = (count + Math.Max(1, columns) - 1) / Math.Max(1, columns);
        Slot[] slots = new Slot[count];
        for (int index = 0; index < count; index++) slots[index] = new Slot(index / rows, index % rows);
        return slots;
    }
}
