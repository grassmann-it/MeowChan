using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

internal static class ReaderGroupChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        if (Environment.GetEnvironmentVariable("MEOWCHAN_READER_GROUPS_RESTORE_ONLY") == "1")
        {
            await ReaderGroupLifetimeChecks.RunGalleryOriginRestoreAsync(owner);
            return;
        }
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ResourceId a = new("local", "b", "1"), b = a with { Thread = "2" }, c = a with { Thread = "3" };
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
                foreach (ResourceId id in new[] { a, b, c })
                    await catalog.CaptureAsync(new(id, "https://example.test/b/thread/" + id.Thread, "Group fixture " + id.Thread,
                        ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile,
                        [new CapturedPost(id.Thread!, 0, PostFieldPresence.Html, Html: "Saved local post " + id.Thread)]));
            new Config { SaveUrlsOnExit = false }.SaveConfig(profile);
            await using (DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>()))
            {
                await host.StartAsync();
                ReaderGroupsDialog dialog = new(host);
                Task<ReaderGroupReadRequest?> completion = dialog.ShowDialog<ReaderGroupReadRequest?>(owner);
                await Program.WaitUntilAsync(() => dialog.CanExecute("reader-groups.create"));
                await dialog.ExecuteCommandAsync("reader-groups.create");
                dialog.FindControl<TextBox>("GroupName")!.Text = "Ordered fixture";
                dialog.FindControl<TextBox>("GroupSearch")!.Text = "Group fixture";
                ListBox candidates = dialog.FindControl<ListBox>("GroupCandidates")!;
                await Program.WaitUntilAsync(() => candidates.ItemCount == 3);
                candidates.SelectedItems!.Add(candidates.Items[2]);
                candidates.SelectedItems!.Add(candidates.Items[0]);
                await dialog.ExecuteCommandAsync("reader-groups.add-members");
                await dialog.ExecuteCommandAsync("reader-groups.save");
                ReaderGroupsSnapshot saved = (await host.LoadReaderGroupsAsync()).Snapshot!;
                Require(saved.Groups.Count == 1 && saved.Groups[0].Members.SequenceEqual(new[] { c, a }), "Group dialog lost explicit candidate selection order.");
                await dialog.ExecuteCommandAsync("reader-groups.read");
                ReaderGroupReadRequest request = await completion ?? throw new InvalidOperationException("Group dialog did not return a reader request.");
                Require(request.Threads.SequenceEqual(new[] { c, a }), "Read did not freeze saved member order.");
                ReaderGroupsDialog restored = new(host, request.Origin);
                Task<ReaderGroupReadRequest?> closing = restored.ShowDialog<ReaderGroupReadRequest?>(owner);
                await Program.WaitUntilAsync(() => restored.CanExecute("reader-groups.read"));
                Require(restored.FindControl<ListBox>("GroupMembers")!.SelectedIndex == 1, "Group dialog return lost selected member.");
                await NativeFeatureCheckpoint.HoldAsync("groups", restored, null, restored.FindControl<TextBox>("GroupName")!, restored.FindControl<ListBox>("Groups")!, restored.FindControl<ListBox>("GroupMembers")!);
                restored.Close(); await closing;
                await ReaderGroupVisualChecks.CaptureManagerMatrixAsync(owner, host, request.Origin);
                await host.SaveReaderGroupsAsync(saved with { Groups = [saved.Groups[0] with { Members = new[] { a, b, c } }] }, saved.Revision);
                Require(host.GetView().Threads.Count == 0, "Reader groups admitted a watch.");
            }
            using ProfileLease secondLease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog mutableCatalog = await ArchiveCatalog.OpenAsync(profile, secondLease);
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            Window readerWindow = new() { Width = 900, Height = 650, Content = page.DetachReader(), Title = "Group missing-target regression" };
            try
            {
                readerWindow.Show(owner); page.IsAvailable = true; page.BeginReaderGroupScope([a, b, c]);
                Require(await page.LoadReaderGroupThreadAsync(a), "First group member failed to load.");
                await mutableCatalog.DeleteThreadsAsync([b]);
                await page.NavigateReaderAsync(0, 1);
                Require(page.CurrentThread?.Summary.Id == a && page.CanSkipUnavailableGroupMember,
                    "Missing group target cleared the visible origin or offered no explicit skip.");
                Require(page.FindControl<TextBlock>("ReaderGroupAvailabilityMessage")!.IsVisible, "Missing-target explanation is outside the detached reader.");
                await ReaderGroupVisualChecks.CaptureMissingMatrixAsync(owner, readerWindow, page);
                await page.SkipUnavailableGroupMemberAsync();
                Require(page.CurrentThread?.Summary.Id == c, "Explicit skip did not advance within the frozen scope.");
                await page.NavigateReaderAsync(0, 1);
                Require(page.CurrentThread?.Summary.Id == c, "Group navigation wrapped around.");
                await page.NavigateReaderAsync(0, -1);
                Require(page.CurrentThread?.Summary.Id == a, "Backward navigation got stuck on a previously skipped target.");
                ReaderGroupStore store = new(profile, secondLease);
                Require((await store.LoadAsync()).Snapshot!.Groups[0].Members.SequenceEqual(new[] { a, b, c }), "Reader navigation rewrote memberships or removed a missing member.");
            }
            finally { page.EndLifetime(); readerWindow.Close(); }
            Console.WriteLine("SMOKE reader groups: explicit selection order, persistence, frozen read, dialog return, missing target preservation, explicit skip and no wrap passed.");
        }
        finally { try { Directory.Delete(profile, true); } catch (IOException) { } }
        await ReaderGroupLifetimeChecks.RunAsync(owner);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
