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
        using ThreadRow row = new(entry with { Snapshot = entry.Snapshot with { Work = work } });
        DataGrid grid = window.FindControl<DataGrid>("ThreadsDataGrid")!;
        object? previous = grid.SelectedItem;
        string language = Texts.Language;
        window.UrlList.Add(row);
        grid.SelectedItem = row;
        try
        {
            foreach (string selectedLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(selectedLanguage);
                // The fixture is intentionally not a provider watch: the host must return stale safely.
                row.UpdateSnapshot(entry with { Snapshot = entry.Snapshot with { Work = work } });
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
            window.UrlList.Remove(row);
            grid.SelectedItem = previous;
            Texts.SetLanguage(language);
        }
        Console.WriteLine("SMOKE media retry: EN/DE durable pause projection, separate command and stale-watch protection passed.");
    }
}
