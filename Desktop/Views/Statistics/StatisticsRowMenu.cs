using System;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Stats;

/// <summary>
/// The context menu of a row of the statistics source table: copy the row or only the source's name, and reset the statistics
/// (destructive, last; it asks first like the page's own Reset button). Pure composition; the page supplies what the entries do.
/// </summary>
internal static class StatisticsRowMenu
{
    internal const string CopyRow = "statistics.copy-row";
    internal const string CopySource = "statistics.copy-source";
    internal const string Reset = "statistics.reset";

    /// <summary>The row as text: the name, then one line per column "label: value", in the table's column order.</summary>
    internal static string RowText(StatisticsRow row, int retentionDays) => string.Join("\n",
        row.Name,
        Texts.T("stats.col.rate") + ": " + row.Now,
        Texts.T("stats.col.session") + ": " + row.Session,
        Texts.T("stats.col.today") + ": " + row.Today,
        Texts.T("stats.col.retained", retentionDays) + ": " + row.Retained,
        Texts.T("stats.col.diskWritten") + ": " + row.DiskWritten,
        Texts.T("stats.col.diskRead") + ": " + row.DiskRead,
        Texts.T("stats.col.requests") + ": " + row.Requests,
        Texts.T("stats.col.errors") + ": " + row.Errors);

    /// <param name="name">The row's displayed name (the menu's accessible name).</param>
    /// <param name="isTotal">The total row has no single source, so "Copy source name" is not offered.</param>
    /// <param name="canReset">False while a reset runs or no hub is attached.</param>
    internal static ContextMenuBuilder Compose(string name, bool isTotal, bool canReset, Action copyRow, Action copySource, Action reset)
    {
        ArgumentNullException.ThrowIfNull(copyRow);
        ArgumentNullException.ThrowIfNull(copySource);
        ArgumentNullException.ThrowIfNull(reset);
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("nav.statistics") + ": " + name).Command(CopyRow, copyRow);
        if (!isTotal) menu.Command(CopySource, copySource);
        return menu.Section().Command(Reset, reset, canReset, canReset ? null : Texts.T("menu.unavailable.target"));
    }
}
