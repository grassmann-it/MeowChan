using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Work views (AP5/WV2): the archive page's own checks for the work-views smoke. The page checks its controls against
/// <see cref="WorkViewPolicy"/> (header tools, scope chip, filter field groups), and the filter surface, which the page capture
/// shows closed, is opened once per cell for an extra capture smoke-wv-archive-filters-… (not part of the index matrix).
/// </summary>
internal static class WorkViewArchiveChecks
{
    internal static void RequirePlacement(MainWindow window, string view)
    {
        string[] problems = [.. window.ArchivePage.WorkViewPlacementProblems()];
        if (problems.Length > 0)
            throw new InvalidOperationException($"archive/{view}: " + string.Join(" ", problems));
    }

    /// <summary>Opens the filter surface (and "More filters" when the view has one), checks and captures it, closes both again.</summary>
    internal static async Task CaptureFilterSurfaceAsync(MainWindow window, string view, string file, string output)
    {
        ArchivePage archive = window.ArchivePage;
        Expander filters = archive.FindControl<Expander>("ArchiveFilters")!;
        Expander more = archive.FindControl<Expander>("ArchiveMoreFilters")!;
        Border surface = archive.FindControl<Border>("ArchiveFilterSurface")!;
        try
        {
            filters.IsExpanded = true;
            await SettleAsync(window);
            RequirePlacement(window, view);
            // The groups the policy puts on the surface are really shown when the surface is (the ones under More sit in the closed section).
            if (surface.IsEffectivelyVisible)
            {
                foreach (Control group in archive.GetVisualDescendants().OfType<StackPanel>()
                    .Where(panel => WorkViewElement.GetId(panel) is { } id && WorkViewPolicy.ArchiveFilterGroupIds().Contains(id)))
                {
                    bool onSurface = archive.FilterGroupOnSurface(group);
                    if (onSurface && !group.IsEffectivelyVisible)
                        throw new InvalidOperationException($"archive/{view}: {WorkViewElement.GetId(group)} belongs on the filter surface but is not shown.");
                }
            }
            if (more.IsVisible)
            {
                more.IsExpanded = true;
                await SettleAsync(window);
            }
            Program.CaptureWindow(window, file, output);
        }
        finally
        {
            more.IsExpanded = false;
            filters.IsExpanded = false;
            await SettleAsync(window);
        }
    }

    private static async Task SettleAsync(MainWindow window)
    {
        for (int pass = 0; pass < 3; pass++)
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Task.Delay(150);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }
}
