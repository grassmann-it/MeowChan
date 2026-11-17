using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The labels of the Archive thread menu (final review 17.11.2026, AR-02). The menu is opened on a row that is already highlighted, so an entry never
/// repeats its target ("· for 4chan /wg/ 9000…" said nothing the row did not). The one case that names a target is a batch: with several threads selected an
/// entry that acts on all of them (or on those of them it can act on) says how many, always in the one grammar "Export (3 threads)".
/// </summary>
internal static class ArchiveMenuLabels
{
    /// <summary>
    /// <paramref name="action"/> as the menu shows it. <paramref name="selected"/> is the number of selected threads, <paramref name="targets"/> the number
    /// the entry acts on (null for an entry that acts on the lead thread only). One selected thread never gets a suffix, whatever the count.
    /// </summary>
    internal static string Action(string action, int selected, int? targets) =>
        targets is { } count && selected > 1 ? Texts.Count("archive.command.batchCount", count, action, count) : action;
}
