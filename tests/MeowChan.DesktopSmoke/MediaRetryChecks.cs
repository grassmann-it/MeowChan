using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

/// <summary>Exercises the actual desktop command against a stale passive watch, without network work.</summary>
internal static class MediaRetryChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        DesktopThreadEntry entry = SnapshotFixtures.Thread(long.MaxValue - 50, "123", "Media pause fixture");
        DateTimeOffset due = DateTimeOffset.UtcNow.AddMinutes(15);
        ThreadWorkSnapshot work = new(entry.Snapshot.Url, WorkIntent.Watching, ThreadSourceStateValue.Active, null, false,
            new[] { new MediaWork { ResourceUrl = entry.Snapshot.Url, Generation = 1, MediaIdentifier = "fixture",
                DestinationPath = Path.Combine(entry.Snapshot.DownloadDirectory, "fixture.jpg"),
                DownloadUrl = "https://fixture.invalid/media.jpg", OriginalFileName = "fixture.jpg",
                Status = MediaWorkStatus.Waiting, HttpStatus = 404, Attempts = 1, DueAt = due } });
        await using SyntheticShellRows synthetic = await SyntheticShellRows.CreateAsync(window,
            [entry with { Snapshot = entry.Snapshot with { Work = work } }]);
        ThreadRow row = synthetic.Threads[0];
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        string language = Texts.Language;
        try
        {
            // The fixture published through the real immutable source/search transaction.
            await Program.WaitUntilAsync(() => window.VisibleThreads.Contains(row)
                && grid.CollectionView.Cast<ThreadRow>().Contains(row));
            grid.SelectedItem = row;
            await Program.WaitUntilAsync(() => ReferenceEquals(grid.SelectedItem, row) && grid.SelectedItems.Contains(row));
            foreach (string selectedLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(selectedLanguage);
                // The fixture is intentionally not a provider watch: the host must return stale safely.
                await synthetic.WaitForAppliedAsync();
                Button retry = window.FindControl<Button>("InspectorMediaRetryButton")!;
                await Program.WaitUntilAsync(() => retry.IsEnabled);
                if (window.FindControl<Button>("InspectorCheckButton")!.IsEnabled)
                    throw new InvalidOperationException("A passive fixture unexpectedly enabled source checking.");
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => retry.IsEnabled && window.GetVisualDescendants().OfType<NotificationCard>()
                    .Any(card => card.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("work.retryNone"))));
                if (!row.Progress.Contains("404") || row.Completion != 0)
                    throw new InvalidOperationException("Missing media was shown as successfully saved.");
            }
        }
        finally
        {
            Texts.SetLanguage(language);
            await synthetic.DisposeAsync();
        }
        Console.WriteLine("SMOKE media retry: EN/DE durable pause projection, separate command and stale-watch protection passed.");
    }
}
