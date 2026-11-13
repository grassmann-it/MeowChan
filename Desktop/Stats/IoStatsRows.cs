using System;
using System.Collections.Generic;
using System.Globalization;

namespace MeowChan.Desktop.Stats;

/// <summary>One line of the statistics table, already formatted for display. Raw numbers stay in the snapshot.</summary>
internal sealed record IoStatsRowText(string Key, bool IsTotal, string Name, string Now, string Session, string Today,
    string Retained, string DiskWritten, string DiskRead, string Requests, string Errors);

/// <summary>Builds the table of the statistics page from a snapshot: one row per source in the hub's order (largest session download first), the total last.</summary>
internal static class IoStatsRows
{
    internal static IReadOnlyList<IoStatsRowText> Build(IoStatsSnapshot snapshot, Func<string, string?>? displayName = null, CultureInfo? culture = null)
    {
        List<IoStatsRowText> rows = new(snapshot.Sources.Count + 1);
        foreach (IoSourceStats source in snapshot.Sources) rows.Add(Row(source, isTotal: false, IoStatsDisplay.SourceName(source.Key, displayName), culture));
        rows.Add(Row(snapshot.Total, isTotal: true, Localization.Texts.T("stats.total"), culture));
        return rows;
    }

    private static IoStatsRowText Row(IoSourceStats source, bool isTotal, string name, CultureInfo? culture) => new(
        isTotal ? string.Empty : source.Key, isTotal, name,
        ByteUnits.Rate(source.Rate.NetDown, culture), ByteUnits.Size(source.Session.NetDown, culture),
        ByteUnits.Size(source.Today.NetDown, culture), ByteUnits.Size(source.Retained.NetDown, culture),
        ByteUnits.Size(source.Session.DiskWritten, culture), ByteUnits.Size(source.Session.DiskRead, culture),
        Count(source.Session.Requests, culture), Count(source.Session.Errors, culture));

    /// <summary>A whole number with the language's digit grouping.</summary>
    internal static string Count(long value, CultureInfo? culture = null) =>
        Math.Max(value, 0).ToString("N0", culture ?? Localization.Texts.Culture);
}
