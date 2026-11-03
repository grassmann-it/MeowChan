using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Interfaces;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.DesktopSmoke;

// Root hook: await NativeTransferPreviewLifecycleChecks.RunAsync(owner) after Ready.
// Real native Avalonia windows/controls are used below. The package and picker are
// synthetic and local only: this does not claim OS picker/compositor or cross-platform
// acceptance, and it does not open a profile/archive catalog, provider, or network connection.
internal static class NativeTransferPreviewLifecycleChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly DateTimeOffset When = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        await CancelOrCloseRejectsLatePickerAsync(owner, closeWhilePending: false);
        await CancelOrCloseRejectsLatePickerAsync(owner, closeWhilePending: true);
        await PageShowFailureUsesTheTerminalFallbackAsync(owner, showBeforeFailure: false);
        await PageShowFailureUsesTheTerminalFallbackAsync(owner, showBeforeFailure: true);
        await PageEndLifetimeTerminatesPendingPickerAsync(owner);
        Console.WriteLine("SMOKE native transfer preview lifecycle: actual dialog Cancel/late-picker, show-failure fallback, EndLifetime, one disposal and language unsubscribe passed; synthetic local preview only, no OS picker/profile/network claim.");
    }

    private static async Task CancelOrCloseRejectsLatePickerAsync(MainWindow owner, bool closeWhilePending)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        TaskCompletionSource pickerEntered = Signal();
        TaskCompletionSource releasePicker = Signal();
        TaskCompletionSource pickerReturned = Signal();
        Task<string?> Picker() => PickAsync();
        async Task<string?> PickAsync()
        {
            pickerEntered.TrySetResult();
            await releasePicker.Task;
            try { return fixture.Source; }
            finally { pickerReturned.TrySetResult(); }
        }

        int labels = 0;
        NativeTransferPreviewDialog dialog = new(fixture.Session, Picker, key => { ++labels; return key; });
        Require(dialog.IsLanguageSubscribedForTests, "The live dialog never subscribed to Texts.Changed.");
        Task shown = dialog.ShowDialog(owner);
        Task? choosing = null;
        try
        {
            await WaitUntilAsync(() => dialog.IsVisible);
            choosing = dialog.ChooseForTestsAsync();
            await pickerEntered.Task.WaitAsync(Deadline);
            await WaitUntilAsync(() => dialog.CancelButton.IsEnabled);
            if (closeWhilePending)
            {
                SmokeActionClick.Execute(dialog.CloseButton);
                await shown.WaitAsync(Deadline);
                await dialog.TerminateAsync().WaitAsync(Deadline);
                Require(fixture.Session.Current.State == NativeTransferPreviewState.Closed,
                    "The actual Close control did not terminate the pending picker session.");
            }
            else
            {
                SmokeActionClick.Execute(dialog.CancelButton);
                Require(fixture.Session.Current.State == NativeTransferPreviewState.Canceled,
                    "The actual Cancel control did not publish Canceled while the picker was pending.");
                Require(!dialog.ChooseButton.IsEnabled && !dialog.CancelButton.IsEnabled,
                    "Cancel allowed a second picker before the first picker returned.");
            }

            string? state = dialog.StateText.Text;
            int labelsBeforeLateResult = labels;
            // Return a valid package, not null: without the generation guard this
            // would perform inspection and render Inventory, so this is not vacuous.
            releasePicker.TrySetResult();
            await pickerReturned.Task.WaitAsync(Deadline);
            await choosing.WaitAsync(Deadline);
            Require(fixture.Session.Current.State == (closeWhilePending
                    ? NativeTransferPreviewState.Closed : NativeTransferPreviewState.Canceled),
                "A late picker result replaced the canceled/closed session state.");
            Require(dialog.StateText.Text == state && dialog.SummaryText.Text == string.Empty,
                "A late picker result rendered inventory after Cancel/Close.");
            if (closeWhilePending)
                Require(labels == labelsBeforeLateResult, "A closed dialog performed a late label/render refresh.");
        }
        finally
        {
            releasePicker.TrySetResult();
            if (choosing != null) await choosing.WaitAsync(Deadline);
            dialog.Close();
            await shown.WaitAsync(Deadline);
            await dialog.TerminateAsync().WaitAsync(Deadline);
            Require(ReferenceEquals(dialog.TerminateAsync(), dialog.CleanupCompletion),
                "Repeated terminal calls did not return the shared cleanup task.");
            await dialog.CleanupCompletion.WaitAsync(Deadline);
            Require(fixture.Session.Current.State == NativeTransferPreviewState.Closed,
                "The actual dialog terminal path did not close its session.");
            Require(dialog.SessionDisposalCountForTests == 1,
                "The actual dialog terminal path disposed its session more than once.");
            Require(!dialog.IsLanguageSubscribedForTests,
                "A terminal dialog remained subscribed to Texts.Changed.");
        }
        Require(!Directory.Exists(fixture.Work) || Directory.GetFileSystemEntries(fixture.Work).Length == 0,
            "The direct dialog fixture left an owned operation child before fixture teardown.");
    }

    private static async Task PageShowFailureUsesTheTerminalFallbackAsync(MainWindow owner, bool showBeforeFailure)
    {
        using PageFixture fixture = await PageFixture.CreateAsync(owner);
        NativeTransferPreviewDialog? dialog = null;
        NativeTransferPreviewSession? session = null;
        string? work = null;
        fixture.Page.TransferPreviewWorkDirectoryCreatedForTests = path => work = path;
        fixture.Page.TransferPreviewDialogFactoryForTests = (created, picker) =>
        {
            session = created;
            return dialog = new NativeTransferPreviewDialog(created, picker, key => key);
        };
        Task? nativeShow = null;
        bool subscribedAtShow = false;
        bool nativeWasVisible = false;
        fixture.Page.TransferPreviewShowForTests = async (preview, window) =>
        {
            subscribedAtShow = preview.IsLanguageSubscribedForTests;
            if (showBeforeFailure)
            {
                nativeShow = preview.ShowDialog(window);
                await WaitUntilAsync(() => preview.IsVisible);
                nativeWasVisible = true;
            }
            throw new InvalidOperationException("synthetic ShowDialog failure");
        };

        if (showBeforeFailure)
            await fixture.Page.OpenTransferPreviewAsync().WaitAsync(Deadline);
        else
        {
            // The parsed/loaded toolbar and its real Click handler, not a detached
            // source assertion. The method-based case above observes the same finalizer.
            SmokeActionClick.Execute(fixture.Page.FindControl<Button>("ArchiveTransferPreview")!);
            await WaitUntilAsync(() => dialog != null && work != null && !Directory.Exists(work)
                && fixture.Page.FindControl<Button>("ArchiveTransferPreview")!.IsEnabled);
        }
        Require(subscribedAtShow, "The failed-show dialog never subscribed to language events.");
        Require(!showBeforeFailure || nativeWasVisible,
            "The post-native-show failure fixture never reached an actual visible dialog.");

        if (nativeShow != null) await nativeShow.WaitAsync(Deadline);
        NativeTransferPreviewDialog shownDialog = dialog ?? throw new InvalidOperationException("The real page did not construct its dialog.");
        NativeTransferPreviewSession shownSession = session ?? throw new InvalidOperationException("The real page did not create its session.");
        Require(!shownDialog.IsVisible, "The page left an orphan window after ShowDialog failure.");
        Require(ReferenceEquals(shownDialog.TerminateAsync(), shownDialog.CleanupCompletion),
            "Failed-show cleanup did not retain the shared terminal task.");
        Require(shownDialog.SessionDisposalCountForTests == 1,
            "ShowDialog failure did not use exactly one dialog-owned terminal disposal.");
        Require(!shownDialog.IsLanguageSubscribedForTests,
            "ShowDialog failure left the dialog subscribed to Texts.Changed.");
        Require(shownSession.Current.State == NativeTransferPreviewState.Closed,
            "ShowDialog failure did not close the dialog-owned session.");
        Require(work != null && !Directory.Exists(work),
            "The page retained its operation root after failed-show terminal drain.");
    }

    private static async Task PageEndLifetimeTerminatesPendingPickerAsync(MainWindow owner)
    {
        using PageFixture fixture = await PageFixture.CreateAsync(owner);
        NativeTransferPreviewDialog? dialog = null;
        NativeTransferPreviewSession? session = null;
        string? work = null;
        TaskCompletionSource pickerEntered = Signal();
        TaskCompletionSource releasePicker = Signal();
        TaskCompletionSource pickerReturned = Signal();
        string source = Path.Combine(TemporaryWorkspacePaths.TempRoot,
            "meowchan-transfer-preview-lifetime-" + Guid.NewGuid().ToString("N") + ".zip");
        fixture.Page.TransferPreviewWorkDirectoryCreatedForTests = path => work = path;
        fixture.Page.TransferPreviewChooseFileForTests = PickAsync;
        fixture.Page.TransferPreviewDialogFactoryForTests = (created, picker) =>
        {
            session = created;
            return dialog = new NativeTransferPreviewDialog(created, picker, key => key);
        };
        fixture.Page.TransferPreviewShowForTests = (preview, window) => preview.ShowDialog(window);

        Task? opening = null;
        Task? choosing = null;
        try
        {
            await WriteSyntheticPackageAsync(source);
            opening = fixture.Page.OpenTransferPreviewAsync();
            await WaitUntilAsync(() => dialog?.IsVisible == true);
            NativeTransferPreviewDialog active = dialog!;
            Require(active.IsLanguageSubscribedForTests, "The live page dialog never subscribed to language events.");
            choosing = active.ChooseForTestsAsync();
            await pickerEntered.Task.WaitAsync(Deadline);
            Require(active.CancelButton.IsEnabled, "The actual dialog did not expose Cancel during the pending picker.");
            string? stateBeforeEnd = active.StateText.Text;

            // This is the real ArchivePage lifetime path, not private-field injection.
            fixture.Page.EndLifetime();
            await opening.WaitAsync(Deadline);
            await active.TerminateAsync().WaitAsync(Deadline);
            Require(ReferenceEquals(active.TerminateAsync(), active.CleanupCompletion),
                "EndLifetime, Closed and page finally did not retain the shared terminal task.");
            Require(active.SessionDisposalCountForTests == 1,
                "ArchivePage.EndLifetime and the opening finally path did not share exactly one terminal disposal.");
            Require(!active.IsLanguageSubscribedForTests,
                "ArchivePage.EndLifetime left the dialog language subscription attached.");
            Require(session!.Current.State == NativeTransferPreviewState.Closed && !active.IsVisible,
                "ArchivePage.EndLifetime did not close its dialog-owned session and window.");
            Require(!CommandRouter.HasExplicitRegistration("archive.transfer-preview", fixture.Page),
                "EndLifetime retained the page transfer-preview command registration.");

            releasePicker.TrySetResult();
            await pickerReturned.Task.WaitAsync(Deadline);
            await choosing.WaitAsync(Deadline);
            Require(active.StateText.Text == stateBeforeEnd && active.SummaryText.Text == string.Empty,
                "A valid package returned after ArchivePage.EndLifetime rendered late dialog content.");
            Require(work != null && !Directory.Exists(work),
                "ArchivePage.EndLifetime did not release the operation root after terminal drain.");
        }
        finally
        {
            releasePicker.TrySetResult();
            fixture.Page.EndLifetime();
            if (choosing != null) await choosing.WaitAsync(Deadline);
            if (opening != null) await opening.WaitAsync(Deadline);
            File.Delete(source);
        }

        async Task<string?> PickAsync()
        {
            pickerEntered.TrySetResult();
            await releasePicker.Task;
            try { return source; }
            finally { pickerReturned.TrySetResult(); }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Native transfer preview smoke condition timed out.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PageFixture : IDisposable
    {
        internal ArchivePage Page { get; }
        private readonly Window _window;

        private PageFixture(ArchivePage page, Window window)
        { Page = page; _window = window; }

        internal static async Task<PageFixture> CreateAsync(MainWindow owner)
        {
            ArchivePage page = new(_ => throw new InvalidOperationException("Lifecycle smoke must not open a profile."),
                (_, _) => Task.CompletedTask, _ => { });
            Window window = new() { Width = 960, Height = 640, Content = page, Title = "Native transfer preview lifecycle" };
            window.Show(owner);
            page.IsAvailable = true;
            PageFixture fixture = new(page, window);
            await WaitUntilAsync(() => page.IsEffectivelyVisible
                && page.FindControl<Button>("ArchiveTransferPreview")?.IsEnabled == true);
            Require(CommandRouter.HasExplicitRegistration("archive.transfer-preview", page),
                "The attached ArchivePage did not register the real transfer-preview command.");
            return fixture;
        }

        public void Dispose()
        {
            Page.EndLifetime();
            _window.Close();
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Work { get; }
        internal string Source { get; }
        internal NativeTransferPreviewSession Session { get; }

        private Fixture(string work, string source, NativeTransferPreviewSession session)
        { Work = work; Source = source; Session = session; }

        internal static async Task<Fixture> CreateAsync()
        {
            string work = ArchivePage.CreateTransferPreviewWorkDirectory();
            try
            {
                string source = Path.Combine(TemporaryWorkspacePaths.TempRoot,
                    "meowchan-transfer-preview-smoke-" + Guid.NewGuid().ToString("N") + ".zip");
                await WriteSyntheticPackageAsync(source);
                return new Fixture(work, source, new NativeTransferPreviewSession(work));
            }
            catch
            {
                try { Directory.Delete(work, recursive: false); } catch { }
                throw;
            }
        }

        public void Dispose()
        {
            // The dialog is the sole session owner after construction. Never call
            // Session.DisposeAsync here; the terminal assertions above cover ownership.
            try { File.Delete(Source); } catch { }
            try { Directory.Delete(Work, recursive: false); } catch { }
        }
    }

    private static async Task WriteSyntheticPackageAsync(string path)
    {
        ResourceId id = new("4chan", "b", "7001");
        TransferPost post = new(new("15", 1, PostFieldPresence.All, Subject: "synthetic",
            Html: "<p>inert synthetic preview</p>", Attachments: ImmutableArray<CapturedAttachment>.Empty), []);
        NativeTransferCatalog catalog = new(When,
            [new TransferThread(id, null, "synthetic", ThreadSourceState.NotFound, When, null, null, null, [post])]);
        using MemoryStream output = new();
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(output, catalog,
            Array.Empty<TransferPayloadSource>(), Guid.Parse("5d7750f7-1c3a-4093-b770-7c854fcfb117"));
        Require(written.IsComplete, written.Error.ToString());
        File.WriteAllBytes(path, output.ToArray());
    }
}
