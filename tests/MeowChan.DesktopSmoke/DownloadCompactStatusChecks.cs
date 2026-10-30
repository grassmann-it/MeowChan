using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// Passive snapshots only: never activate a source or schedule media from this scene.
internal static class DownloadCompactStatusChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Config config = window.SessionHost.Config;
        string mode = config.WorkspaceMode, language = Texts.Language;
        Dictionary<string, bool> densities = new[] { "Barebones", "Simple", "Advanced" }
            .ToDictionary(name => name, name => config.GetLayoutProfile(name).CompactDensity);
        double width = window.Width, height = window.Height;
        DesktopThreadEntry entry = SnapshotFixtures.Thread(98901, "98901", "Compact status fixture");
        MediaWork Media(string id, MediaWorkStatus state) => new()
        {
            ResourceUrl = entry.Snapshot.Url, Generation = 1, MediaIdentifier = id,
            DestinationPath = Path.Combine(entry.Snapshot.DownloadDirectory, id), DownloadUrl = "https://fixture.invalid/" + id,
            OriginalFileName = id + ".jpg", Status = state, HttpStatus = 429, Attempts = 2
        };
        ThreadWorkSnapshot work = new(entry.Snapshot.Url, WorkIntent.Watching, ThreadSourceStateValue.Active,
            null, false, new[] { Media("saved", MediaWorkStatus.Succeeded), Media("missing", MediaWorkStatus.NeedsIntervention) });
        entry = entry with { Snapshot = entry.Snapshot with { Work = work } };
        await using SyntheticShellRows rows = await SyntheticShellRows.CreateAsync(window, new[] { entry });
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        void Apply() => typeof(MainWindow).GetMethod("ApplyWorkspacePreferences", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        try
        {
            window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach (string currentLanguage in new[] { "en", "de" })
            foreach (string currentMode in new[] { "Barebones", "Simple", "Advanced" })
            foreach (bool compact in new[] { false, true })
            {
                Texts.SetLanguage(currentLanguage);
                await PresentationChecks.SwitchModeAsync(window, currentMode);
                config.CompactDensity = compact; Apply();
                window.Width = currentMode == "Barebones" ? 640 : 1000;
                window.Height = 600;
                DataGridColumn progress = grid.Columns.Single(column => column.SortMemberPath == nameof(ThreadRow.Completion));
                // Exercise the real shared template; a user-hidden column is not a failed binding.
                progress.IsVisible = true;
                grid.ScrollIntoView(rows.Threads[0], progress);
                await Program.WaitUntilAsync(() =>
                {
                    window.UpdateLayout();
                    return grid.GetVisualDescendants().OfType<TextBlock>().Any(text =>
                        ReferenceEquals(text.DataContext, rows.Threads[0]) && text.Classes.Contains("compact-work-status")
                        && text.IsEffectivelyVisible && text.Text == rows.Threads[0].CompactStatus);
                });
                ThreadRow row = rows.Threads[0];
                row.SetPaused(true);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
                Require(row.CompactStatus == row.ExceptionHint + " · " + Texts.T("work.paused"), "Compact row duplicated intervention or lost pause.");
                TextBlock status = grid.GetVisualDescendants().OfType<TextBlock>().Single(text =>
                    ReferenceEquals(text.DataContext, row) && text.Classes.Contains("compact-work-status"));
                Require(AutomationProperties.GetName(status) == row.CompactStatus, "Compact accessible name is stale.");
                StackPanel container = status.GetVisualAncestors().OfType<StackPanel>().First(panel => ReferenceEquals(panel.DataContext, row));
                Require(AutomationProperties.GetName(container) == row.CompactDetails
                    && Equals(ToolTip.GetTip(container), row.CompactDetails)
                    && row.CompactDetails.Contains(Texts.T("work.saved", 1, 2)), "Tooltip/accessibility lost counts or complete details.");
                await rows.UpdateAsync(entry with { Snapshot = entry.Snapshot with { Work = work with { Intent = WorkIntent.Completed } } });
                Require(rows.Threads[0].CompactStatus.Contains(Texts.T("work.completed")), "Live update lost completed lifecycle.");
                await rows.UpdateAsync(entry);
                rows.Threads[0].SetPaused(false);
                Program.CaptureWindow(window, $"smoke-compact-{currentLanguage}-{currentMode.ToLowerInvariant()}-{compact}.png");
            }
        }
        finally
        {
            Texts.SetLanguage(language);
            foreach (var saved in densities) config.GetLayoutProfile(saved.Key).CompactDensity = saved.Value;
            await PresentationChecks.SwitchModeAsync(window, mode); Apply();
            window.Width = width; window.Height = height;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
