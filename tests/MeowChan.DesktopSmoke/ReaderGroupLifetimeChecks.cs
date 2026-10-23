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
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
