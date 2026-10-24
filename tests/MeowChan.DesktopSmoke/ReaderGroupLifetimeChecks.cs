using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

/// <summary>Real shell continuation regression: a retired initial read must not close a newer reader.</summary>
internal static class ReaderGroupLifetimeChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-group-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo pageField = typeof(MainWindow).GetField("_archivePage", flags)!;
        FieldInfo returnField = typeof(MainWindow).GetField("_readerGroupReturn", flags)!;
        FieldInfo dialogField = typeof(MainWindow).GetField("_readerGroupsDialog", flags)!;
        MethodInfo closeReader = typeof(MainWindow).GetMethod("CloseLargeReader", flags)!;
        ArchivePage? previousPage = (ArchivePage?)pageField.GetValue(owner);
        ArchivePage? page = null;
        Task? retiredRead = null;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int opens = 0;
        ResourceId oldId = new("local", "lifetime", "1"), newId = oldId with { Thread = "2" };
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            foreach (ResourceId id in new[] { oldId, newId })
                await catalog.CaptureAsync(new(id, "https://example.test/lifetime/thread/" + id.Thread,
                    "Lifetime " + id.Thread, ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile,
                    [new CapturedPost(id.Thread!, 0, PostFieldPresence.Html, Html: "Saved lifetime post")]));
            page = new ArchivePage(async token =>
            {
                if (Interlocked.Increment(ref opens) == 1)
                {
                    entered.TrySetResult();
                    // Model an OS/local read which completes after cancellation rather than observing it immediately.
                    await release.Task;
                    return await ArchiveReader.OpenAsync(profile, CancellationToken.None);
                }
                return await ArchiveReader.OpenAsync(profile, token);
            }, (_, _) => Task.CompletedTask, _ => { });
            page.IsAvailable = true;
            page.CloseReaderRequested = () => closeReader.Invoke(owner, [true]);
            pageField.SetValue(owner, page);
            ReaderGroupReadRequest oldRequest = new(Guid.NewGuid(), [oldId], new(null, null, default, null));
            retiredRead = owner.OpenReaderGroupAsync(oldRequest);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(owner.FindControl<ContentControl>("ReaderHost")!.IsVisible, "Delayed group reader never opened.");

            // Exercise the actual reader-close button and deferred manager return while the old load is pending.
            page.FindControl<Button>("ArchiveCloseReader")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => dialogField.GetValue(owner) is ReaderGroupsDialog manager
                && manager.CanExecute("reader-groups.create"));
            ReaderGroupsDialog reopened = (ReaderGroupsDialog)dialogField.GetValue(owner)!;
            reopened.Close();
            await Program.WaitUntilAsync(() => dialogField.GetValue(owner) == null);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            ReaderGroupReadRequest currentRequest = new(Guid.NewGuid(), [newId], new(null, null, default, null));
            await owner.OpenReaderGroupAsync(currentRequest);
            Require(owner.FindControl<ContentControl>("ReaderHost")!.IsVisible && page.CurrentThread?.Summary.Id == newId,
                "The replacement group reader did not open before the retired read completed.");
            Require(ReferenceEquals(returnField.GetValue(owner), currentRequest), "Replacement reader did not own its return request.");

            release.TrySetResult();
            await retiredRead.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(owner.FindControl<ContentControl>("ReaderHost")!.IsVisible && page.CurrentThread?.Summary.Id == newId,
                "A canceled initial group read closed or replaced the newer reader.");
            Require(ReferenceEquals(returnField.GetValue(owner), currentRequest) && dialogField.GetValue(owner) == null,
                "A canceled initial group read overwrote the new return request or reopened a stale manager.");
            Console.WriteLine("SMOKE reader groups I02: delayed initial read → real Close → manager → newer reader → retired completion leaves newer reader and return request intact.");
        }
        finally
        {
            release.TrySetResult();
            if (retiredRead != null) await retiredRead.WaitAsync(TimeSpan.FromSeconds(10));
            closeReader.Invoke(owner, [false]);
            if (dialogField.GetValue(owner) is ReaderGroupsDialog manager)
            {
                await Program.WaitUntilAsync(() => manager.CanExecute("reader-groups.create"));
                manager.Close();
                await Program.WaitUntilAsync(() => dialogField.GetValue(owner) == null);
            }
            page?.EndLifetime();
            pageField.SetValue(owner, previousPage);
            try { Directory.Delete(profile, true); } catch (IOException) { }
        }
        await RunGalleryOriginRestoreAsync(owner);
    }

    internal static async Task RunGalleryOriginRestoreAsync(MainWindow owner)
    {
        await RunGalleryOriginRestoreAsync(owner, lateFailure: false);
        await RunGalleryOriginRestoreAsync(owner, lateFailure: true);
    }

    private static async Task RunGalleryOriginRestoreAsync(MainWindow owner, bool lateFailure)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-group-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ResourceId a = new("local", "restore", "1"), b = a with { Thread = "2" }, c = a with { Thread = "3" };
        TaskCompletionSource oldEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource newEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource oldRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource newRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int armed = 0;
        ArchivePage? page = null;
        Window? reader = null;
        Task? oldRead = null, newRead = null;
        try
        {
            using ProfileLease lease = ProfileLease.Acquire(profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
            foreach (ResourceId id in new[] { a, b, c })
                await catalog.CaptureAsync(new(id, "https://example.test/restore/thread/" + id.Thread,
                    "Restore " + id.Thread, ThreadSourceState.Archived, DateTimeOffset.UtcNow, profile,
                    [new CapturedPost(id.Thread!, 0, PostFieldPresence.Html, Html: "Saved group restore post")]));
            ReaderGroupStore store = new(profile, lease);
            var saved = await store.SaveAsync(new(1, 0, [new(Guid.NewGuid(), "Frozen order", [a, b, c])]), 0);
            page = new ArchivePage(async token =>
            {
                // Arm immediately before each real navigation call: its preflight opens synchronously.
                int gate = Interlocked.Exchange(ref armed, 0);
                if (gate == 1)
                {
                    oldEntered.TrySetResult();
                    await oldRelease.Task; // Deliberately model a completion after cancellation.
                    if (lateFailure) throw new IOException("Retired group preflight failed late.");
                    return await ArchiveReader.OpenAsync(profile, CancellationToken.None);
                }
                if (gate == 2) { newEntered.TrySetResult(); await newRelease.Task; }
                return await ArchiveReader.OpenAsync(profile, token);
            }, (_, _) => Task.CompletedTask, _ => { }) { ReaderRefreshAllowed = () => false };
            reader = new Window { Width = 1000, Height = 720, Content = page.DetachReader(), Title = "Group origin restore regression" };
            reader.Show(owner); page.IsAvailable = true;
            page.BeginReaderGroupScope(saved.Groups[0].Members);
            Require(await page.LoadReaderGroupThreadAsync(a), "Group restore origin did not load.");
            await page.MediaLoadTask;
            await page.FocusPostAsync(a.Thread!);

            // A later explicit saved reorder must not mutate the already-open reader scope.
            saved = await store.SaveAsync(saved with { Groups = [saved.Groups[0] with { Members = new[] { c, b, a } }] }, saved.Revision);
            Action restore = page.CaptureGalleryOrigin();
            armed = 1; oldRead = page.NavigateReaderAsync(0, 1);
            await oldEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            restore(); // Exact callback invoked by the shell when closing a group-origin gallery.
            Require(page.CurrentThread?.Summary.Id == a, "Gallery callback did not restore the group origin.");

            armed = 2; newRead = page.NavigateReaderAsync(0, 1);
            await newEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            oldRelease.TrySetResult();
            await oldRead.WaitAsync(TimeSpan.FromSeconds(10));
            Require(page.CurrentThread?.Summary.Id == a && page.IsBusy
                && !page.FindControl<Button>("ReaderNextThread")!.IsEnabled,
                "Retired group completion overwrote the restored origin or unlocked the replacement read.");
            Require(!page.FindControl<TextBlock>("ReaderGroupAvailabilityMessage")!.IsVisible
                && !page.CanSkipUnavailableGroupMember,
                "Retired group failure published an unavailable-target state into the restored origin.");
            newRelease.TrySetResult();
            await newRead.WaitAsync(TimeSpan.FromSeconds(10));
            Require(page.CurrentThread?.Summary.Id == b, "Replacement read lost the frozen next member.");
            await page.NavigateReaderAsync(0, 1);
            Require(page.CurrentThread?.Summary.Id == c, "Saved reordering or origin restoration replaced the frozen group scope.");
            Require((await store.LoadAsync()).Snapshot!.Groups[0].Members.SequenceEqual(new[] { c, b, a }),
                "Reader origin restoration rewrote the newer saved group order.");
            Console.WriteLine("SMOKE reader groups restore: late " + (lateFailure ? "failure" : "success")
                + " cannot overwrite gallery origin or unlock replacement; frozen order survives saved reorder.");
        }
        finally
        {
            oldRelease.TrySetResult(); newRelease.TrySetResult();
            if (oldRead != null) await oldRead.WaitAsync(TimeSpan.FromSeconds(10));
            if (newRead != null) await newRead.WaitAsync(TimeSpan.FromSeconds(10));
            page?.EndLifetime(); reader?.Close();
            try { Directory.Delete(profile, true); } catch (IOException) { }
        }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
