using Avalonia.Controls;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Settings cells of the work-views smoke (WV8, work-views-settings): in every view each group of the policy is registered,
/// collapsed with its counter or flat as the policy says (a changed value or an error would open it), and the search finds a
/// setting inside a collapsed group and opens that group. Nothing is saved; the probe only changes what is expanded.
/// </summary>
internal static class WorkViewSettingsChecks
{
    private static SettingsEditor Editor(MainWindow window) =>
        window.FindControl<ContentControl>("SettingsPageHost")?.Content as SettingsEditor
        ?? throw new InvalidOperationException("The settings page is not built.");

    /// <summary>The groups sit as the policy says for this view (called per cell, before the capture).</summary>
    internal static void CheckGroups(MainWindow window, string view)
    {
        WorkView workView = WorkViewSetting.Parse(view);
        SettingsEditor editor = Editor(window);
        if (editor.CurrentWorkView != workView)
            throw new InvalidOperationException($"settings/{view}: the editor is in {editor.CurrentWorkView}.");
        IReadOnlyList<SettingsEditor.GroupState> states = editor.GroupStates();
        string[] rows = WorkViewPolicy.Entries.Where(entry => entry.Page == WorkViewPage.Settings
            && entry.Id.StartsWith(WorkViewPolicy.SettingsGroupPrefix, StringComparison.Ordinal)).Select(entry => entry.Id).ToArray();
        string[] missing = rows.Except(states.Select(state => state.Id)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"settings/{view}: policy groups without an expander: {string.Join(", ", missing)}.");
        foreach (SettingsEditor.GroupState state in states)
        {
            Presence presence = WorkViewPolicy.Resolve(state.Id, workView);
            bool collapsed = presence == Presence.Collapsed;
            bool needsAttention = state.Changed > 0 || state.HasError;
            if (state.Flat == collapsed)
                throw new InvalidOperationException($"settings/{view}: {state.Id} is {(state.Flat ? "flat" : "wrapped")}, the policy says {presence}.");
            if (state.Expanded != (!collapsed || needsAttention))
                throw new InvalidOperationException($"settings/{view}: {state.Id} is {(state.Expanded ? "open" : "closed")}, the policy says {presence}"
                    + $" (changed {state.Changed}, error {state.HasError}).");
            if (!state.Flat && (state.Header is not { Length: > 0 } header || !header.Contains("(" + state.Count + ")", StringComparison.Ordinal)))
                throw new InvalidOperationException($"settings/{view}: {state.Id} has no counter in its header ({state.Header}).");
        }
        Console.WriteLine($"SMOKE work-views settings {view}: {states.Count} groups, " +
            string.Join(", ", states.Select(state => state.Id["settings.group.".Length..] + (state.Flat ? "=flat" : state.Expanded ? "=open" : "=closed(" + state.Count + ")"))) + ".");
    }

    /// <summary>
    /// The search always finds everything: a setting inside a group of this view is found and its group opens (R6, AP12).
    /// Afterwards the view is applied again, so the capture matrix keeps its collapsed look.
    /// </summary>
    internal static void ProbeSearch(MainWindow window, string view)
    {
        WorkView workView = WorkViewSetting.Parse(view);
        SettingsEditor editor = Editor(window);
        // Found through its English name in every language (the index also holds the English texts).
        (string Label, string CategoryId, int Groups, bool AllOpen)? hit = editor.ProbeSearchJump("clipboard");
        try
        {
            if (hit is not { } found)
                throw new InvalidOperationException($"settings/{view}: the search found no clipboard setting.");
            bool inGroup = found.Groups > 0;
            if (WorkViewPolicy.Resolve("settings.group.behavior", workView) == Presence.Collapsed && !inGroup)
                throw new InvalidOperationException($"settings/{view}: the hit {found.Label} is in no group although the view collapses it.");
            if (!found.AllOpen)
                throw new InvalidOperationException($"settings/{view}: the search jumped to {found.Label} but left its group closed.");
            Console.WriteLine($"SMOKE work-views settings {view}: search found '{found.Label}' in {found.CategoryId}; groups on the way opened={found.AllOpen}.");
        }
        finally
        {
            editor.ProbeSearchJump(string.Empty);
            editor.ApplyWorkView(workView, viewChanged: true);
        }
    }
}
