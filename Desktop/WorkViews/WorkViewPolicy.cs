using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop.WorkViews;

/// <summary>How strongly an element is present in one view (plan AP5 §1 legend). Rank: Required = Direct &gt; Collapsed &gt; Overflow &gt; Hidden.</summary>
public enum Presence
{
    /// <summary>K: no own control on this surface; reached through <see cref="WorkViewEntry.Alternative"/> (context menu, app menu, shortcut).
    /// For a column: not offered in this view's column chooser.</summary>
    Hidden,
    /// <summary>⋯: in the "More" overflow of the same surface, with label, shortcut and disabled reason; the shortcut keeps working.
    /// For a column: off by default, offered in "Columns…".</summary>
    Overflow,
    /// <summary>E: a section/expander whose header (with its counter) is visible, collapsed when the view is entered.</summary>
    Collapsed,
    /// <summary>D: directly visible on the surface. For a column: on by default.</summary>
    Direct,
    /// <summary>P: mandatory in every view (data, state, safety); a toolbar item never moves into the overflow.</summary>
    Required
}

/// <summary>What an entry describes; decides which applier reads it and which policy rules apply.</summary>
public enum WorkViewKind
{
    /// <summary>A command button, usually an <see cref="ActionToolBar"/> item (applier: overflow mode / omission).</summary>
    Action,
    /// <summary>A free control outside a toolbar (applier: <see cref="WorkViewGate"/>).</summary>
    Element,
    /// <summary>An expander or collapsible section (applier: <see cref="WorkViewPresenter.ApplyExpander"/>).</summary>
    Section,
    /// <summary>A data-grid column default (read by the page's column code; a saved user choice wins).</summary>
    Column,
    /// <summary>A footer destination (direct in the footer, otherwise in the footer's Tools/"More" menu).</summary>
    Destination,
    /// <summary>A value code reads without a registered control (e.g. the header graph rows); no tree registration expected.</summary>
    Derived
}

/// <summary>The pages the policy is split by; each has its own partial file WorkViewPolicy.&lt;Page&gt;.cs.</summary>
/// Status is the container of the Diagnostics and Statistics segments: it owns only its own rows (the status line and the
/// segment bar); each segment keeps its page and rows.
public enum WorkViewPage { Shell, Downloads, Archive, Reader, Gallery, Media, Rules, Sources, Plugins, Diagnostics, Statistics, Settings, Dialogs, Status }

/// <summary>
/// One row of the work-view matrix. <paramref name="Alternative"/> is required where any view is <see cref="Presence.Hidden"/>
/// (R1): the id of another entry, or a command id, that reaches the same function in that view.
/// <paramref name="Safety"/> entries must be <see cref="Presence.Required"/> everywhere (R5).
/// <paramref name="MonotonyException"/> explains a deliberate break of R12 (Barebones ⊆ Simple ⊆ Advanced); none is known.
/// </summary>
public sealed record WorkViewEntry(string Id, WorkViewPage Page, WorkViewKind Kind,
    Presence Barebones, Presence Simple, Presence Advanced,
    string? CommandId = null, string? Alternative = null, bool Safety = false, int OverflowOrder = 0,
    string? MonotonyException = null)
{
    public Presence For(WorkView view) => view switch
    {
        WorkView.Advanced => Advanced,
        WorkView.Simple => Simple,
        _ => Barebones
    };
}

/// <summary>
/// The single, pure table of what every work view shows (plan AP5 §1–§3). No Avalonia: DesktopTests read it directly.
/// Each page owns one partial file (<c>WorkViewPolicy.&lt;Page&gt;.cs</c>) with a static <c>&lt;Page&gt;Entries()</c> method;
/// this file only joins them. Pages add rows there and nowhere else, so parallel page packages do not collide.
/// </summary>
public static partial class WorkViewPolicy
{
    // Methods, not static fields: partial files have no defined field initialization order.
    private static readonly Lazy<WorkViewEntry[]> AllEntries = new(() =>
        ShellEntries().Concat(DownloadsEntries()).Concat(ArchiveEntries()).Concat(ReaderEntries()).Concat(GalleryEntries())
            .Concat(MediaEntries()).Concat(RulesEntries()).Concat(SourcesEntries()).Concat(PluginsEntries())
            .Concat(DiagnosticsEntries()).Concat(StatisticsEntries()).Concat(SettingsEntries()).Concat(DialogsEntries())
            .Concat(StatusEntries()).ToArray());

    private static readonly Lazy<Dictionary<string, WorkViewEntry>> ById = new(() =>
        AllEntries.Value.ToDictionary(entry => entry.Id, StringComparer.Ordinal));

    public static IReadOnlyList<WorkViewEntry> Entries => AllEntries.Value;

    public static WorkViewEntry? Find(string id) => ById.Value.GetValueOrDefault(id);

    public static WorkViewEntry Get(string id) => Find(id)
        ?? throw new KeyNotFoundException("The work-view policy has no entry " + id + ".");

    /// <summary>
    /// The presence of <paramref name="id"/> in <paramref name="view"/>. R6 "state beats view": an element whose state differs
    /// from its default (an active filter, a search scope other than text, a field with an error, a changed setting in a
    /// collapsed group) is raised to at least <see cref="Presence.Direct"/>.
    /// </summary>
    public static Presence Resolve(string id, WorkView view, bool nonDefaultState = false)
    {
        Presence presence = Get(id).For(view);
        return nonDefaultState && Rank(presence) < Rank(Presence.Direct) ? Presence.Direct : presence;
    }

    /// <summary>R12 order. Required and Direct are equally strong: both are directly on the surface.</summary>
    public static int Rank(Presence presence) => presence switch
    {
        Presence.Required or Presence.Direct => 3,
        Presence.Collapsed => 2,
        Presence.Overflow => 1,
        _ => 0
    };

    /// <summary>Whether a free element is on the surface (Direct, Required, or Collapsed with its header).</summary>
    public static bool OnSurface(Presence presence) => Rank(presence) >= Rank(Presence.Collapsed);

    // ---- Columns (Kind = Column): Direct/Required = on by default, Overflow = off but offered, Hidden = not offered ----

    public static string DownloadColumnId(string gridName, string sortMember) => "downloads.columns." + gridName + "." + sortMember;

    public static string ArchiveColumnId(string gridName, string key) => "archive.columns." + gridName + "." + key;

    /// <summary>The column's own row, else its grid's "*" row (columns added later follow the grid's rule).</summary>
    public static string ColumnEntryId(string columnId) => Find(columnId) != null ? columnId
        : columnId[..(columnId.LastIndexOf('.') + 1)] + "*";

    public static Presence ColumnPresence(string columnId, WorkView view) => Resolve(ColumnEntryId(columnId), view);

    public static bool DefaultColumnVisible(string columnId, WorkView view) => Rank(ColumnPresence(columnId, view)) >= Rank(Presence.Direct);

    /// <summary>Whether the view's column chooser offers the column at all.</summary>
    public static bool ColumnOffered(string columnId, WorkView view) => ColumnPresence(columnId, view) != Presence.Hidden;

    /// <summary>Visibility of a column: never where it is not offered, else a saved user choice (R10), else the view default.</summary>
    public static bool ColumnVisible(string columnId, WorkView view, bool? saved) =>
        ColumnOffered(columnId, view) && (saved ?? DefaultColumnVisible(columnId, view));

    // ---- Footer destinations (Kind = Destination) ----

    /// <summary>The entry of a main-page command of <see cref="Commands.MainPageOrder"/>, e.g. navigation.archive → shell.footer.archive.</summary>
    public static string FooterId(string navigationCommandId) =>
        "shell.footer." + (navigationCommandId.StartsWith("navigation.", StringComparison.Ordinal)
            ? navigationCommandId["navigation.".Length..] : navigationCommandId);

    public static bool FooterDirect(string navigationCommandId, WorkView view) =>
        Rank(Resolve(FooterId(navigationCommandId), view)) >= Rank(Presence.Direct);

    // Shorthands for the page partials.
    private static WorkViewEntry Same(string id, WorkViewPage page, WorkViewKind kind, Presence all, string? commandId = null,
        bool safety = false) => new(id, page, kind, all, all, all, commandId, Safety: safety);
}
