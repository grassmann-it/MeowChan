using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

internal static class ArchiveMediaCountsChecks
{
    internal static async Task RunAsync(Window owner)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("archive-media-counts-");
        string profile = Path.Combine(root, "profile"), otherProfile = Path.Combine(root, "other-profile");
        ArchivePage? page = null, switched = null;
        Window? host = null, switchedHost = null;
        TaskCompletionSource? release = null, profileRelease = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            using (ProfileLease other = ProfileLease.Acquire(otherProfile))
            { await using ArchiveCatalog ignored = await ArchiveCatalog.OpenAsync(otherProfile, other); }
            var a = new ResourceId("fixture", "board", "alpha");
            var b = new ResourceId("fixture", "board", "bravo");
            foreach (var (id, title) in new[] { (a, "Alpha"), (b, "Bravo") }.Concat(Enumerable.Range(0, 52).Select(i =>
                (new ResourceId("fixture", "board", "text-" + i), "zz text " + i.ToString("D2")))))
            {
                string directory = Path.Combine(root, "media", id.Thread!); Directory.CreateDirectory(directory);
                await catalog.RegisterThreadAsync(new(id, Url(id), title, directory, true));
                await catalog.CaptureAsync(new(id, Url(id), title, ThreadSourceState.Active, DateTimeOffset.UtcNow, directory,
                    [new("1", 1, PostFieldPresence.All, Html: "Captured text without attachments", Attachments: [])]));
            }
            string aMedia = Path.Combine(root, "media", a.Thread!), bMedia = Path.Combine(root, "media", b.Thread!);
            await ManifestAsync(aMedia, 2); await ManifestAsync(bMedia, 1);
            ThreadWorkSnapshot[] work = [new(Url(a), WorkIntent.Suppressed, ThreadSourceStateValue.Active, null, false, [])];
            page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { })
                { IsAvailable = true, GetWorkSnapshots = () => work, ArchiveLayoutPreference = "Table" };
            host = new() { Title = "Archive media counts — isolated fixture", Width = 1220, Height = 760, Content = page };
            host.Show(owner); await Settle();
            int inventories = 0;
            var realInventory = page.ReadArchiveMediaInventoryAsync;
            page.ReadArchiveMediaInventoryAsync = async (directories, token) => {
                Interlocked.Increment(ref inventories); return await realInventory(directories, token);
            };
            page.IsVisible = false;
            Require(await page.ApplySearchSpecAsync(new(sort: ArchiveThreadSort.Title)), "Initial applied search failed.");
            Require(inventories == 0, "A hidden result page started media inventory.");
            page.IsVisible = true; // Empty-work stamps must not suppress the first visible count.
            await page.ArchiveMediaCountTask;
            ArchiveThreadRow[] Rows() => page.FindControl<DataGrid>("ArchiveThreadTable")!.ItemsSource.Cast<ArchiveThreadRow>().ToArray();
            ArchiveThreadRow Row(ResourceId id) => Rows().Single(row => row.Summary.Id == id);
            Require(Rows().Length == 50 && inventories == 50, "Inventory escaped the current 50-row result page.");
            Require(Row(a).CompactMedia == "2" && Row(b).CompactMedia == "1", "Empty/no work hid saved versions.");
            Require(Rows().Where(row => row.Summary.Id != a && row.Summary.Id != b).All(row => row.CompactMedia == "0"), "Text-only empty folders were not zero.");
            DataGrid table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
            table.SelectedItem = Row(a);
            await page.LoadThreadAsync(a); await page.MediaLoadTask;
            page.FindControl<Button>("ArchiveFavoritesFilter")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!page.FindControl<ListBox>("ArchiveMediaTiles")!.ItemsSource!.Cast<ArchiveAssetRow>().Any(), "Right-side media filter did not alter its visible subset.");
            page.FindControl<ComboBox>("ArchiveVersionScope")!.SelectedIndex = 0;
            Require(Row(a).CompactMedia == "2", "Right-side favorites/version filters changed the thread's total saved count.");
            var rowObjects = Rows(); var query = page.AppliedSearchSpec;
            var selection = table.SelectedItems.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id).ToArray();
            await ManifestAsync(aMedia, 3);
            work = [new(Url(a), WorkIntent.Watching, ThreadSourceStateValue.Active, null, false, [new() {
                ResourceUrl = Url(a), Generation = 2, MediaIdentifier = "new", DestinationPath = Path.Combine(aMedia, "file-2.png"),
                DownloadUrl = "https://counts.invalid/file-2.png", Status = MediaWorkStatus.Succeeded, Attempts = 1, LastAttemptAt = DateTimeOffset.UtcNow }])];
            page.NotifyArchiveMediaCountsChanged(); await page.ArchiveMediaCountTask;
            Require(Row(a).CompactMedia == "3", "A completed-work notification did not refresh durable media inventory.");
            Require(Rows().SequenceEqual(rowObjects, ReferenceEqualityComparer.Instance) && ReferenceEquals(query, page.AppliedSearchSpec), "Count refresh rebuilt rows or reapplied the query.");
            Require(selection.SequenceEqual(table.SelectedItems.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id)), "Count refresh changed selection.");
            await ManifestAsync(aMedia, 4);
            await page.ActivateAsync(); await page.ArchiveMediaCountTask;
            Require(Row(a).CompactMedia == "4", "Explicit refresh kept stale media inventory.");
            Require(selection.SequenceEqual(table.SelectedItems.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id)), "Explicit refresh lost retained selection.");

            // A cancelled NAS-style inventory deliberately ignores cancellation. Its late answer
            // cannot write into the old row or the next query's new row objects.
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            page.ReadArchiveMediaInventoryAsync = async (_, _) => { entered.TrySetResult(); await release.Task; return new(999); };
            Require(await page.ApplySearchSpecAsync(new(text: "Alpha")), "Alpha search failed.");
            await entered.Task;
            ArchiveThreadRow old = Row(a); Task oldCount = page.ArchiveMediaCountTask;
            page.ReadArchiveMediaInventoryAsync = realInventory;
            Require(await page.ApplySearchSpecAsync(new(text: "Bravo")), "Bravo search failed.");
            Task currentCount = page.ArchiveMediaCountTask;
            release.SetResult(); await oldCount; await currentCount;
            Require(old.MediaCount == null && Row(b).CompactMedia == "1" && Rows().Length == 1, "A late cancelled query published stale media counts.");

            // Reusing an opener for a different profile cannot apply that profile's inventory to
            // a result page captured from the old database.
            int opens = 0;
            TaskCompletionSource profileEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            profileRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            switched = new(async token => {
                if (Interlocked.Increment(ref opens) == 2) {
                    profileEntered.TrySetResult(); await profileRelease.Task;
                    return await ArchiveReader.OpenAsync(otherProfile, token);
                }
                return await ArchiveReader.OpenAsync(profile, token);
            }, (_, _) => Task.CompletedTask, _ => { }) { IsAvailable = true };
            switchedHost = new() { Width = 900, Height = 700, Content = switched };
            switchedHost.Show(owner); await Settle();
            Require(await switched.ApplySearchSpecAsync(new(text: "Alpha")), "Profile guard fixture search failed.");
            await profileEntered.Task; profileRelease.SetResult(); await switched.ArchiveMediaCountTask;
            var retained = switched.FindControl<ListBox>("ArchiveThreads")!.ItemsSource!.Cast<ArchiveThreadRow>().Single();
            Require(retained.MediaCount == null && retained.CompactMedia == "—", "A different profile's read was accepted for old result rows.");
            Console.WriteLine("SMOKE archive media counts: PASS; 50-row bound; empty/no-work and text-only; right-filter independence; completion notification without query/row/selection replacement; explicit refresh; late query and database-profile guards. In-process controls and synthetic work notification; no native input or real download claimed.");
        }
        finally
        {
            release?.TrySetResult(); profileRelease?.TrySetResult();
            page?.EndLifetime(); switched?.EndLifetime();
            if (page != null) await page.ArchiveMediaCountTask;
            if (switched != null) await switched.ArchiveMediaCountTask;
            host?.Close(); switchedHost?.Close();
            Directory.Delete(root, recursive: true);
        }
    }
    private static string Url(ResourceId id) => "https://counts.invalid/" + id.Thread;
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task ManifestAsync(string directory, int count)
    {
        Dictionary<string, object> entries = new();
        for (int i = 0; i < count; i++)
        {
            string file = $"file-{i}.png";
            byte[] bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1cAAAAASUVORK5CYII=");
            await File.WriteAllBytesAsync(Path.Combine(directory, file), bytes);
            entries[file] = new { MediaIdentifier = "media-" + i, Variant = "original", SourceUrl = "https://counts.invalid/" + file,
                FileName = file, Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
    }
}
