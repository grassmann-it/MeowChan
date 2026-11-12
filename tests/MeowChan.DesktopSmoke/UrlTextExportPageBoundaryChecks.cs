using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopSmoke;

// Root invokes after Ready in MEOWCHAN_URLTEXT_PAGE_BOUNDARY_ONLY mode.
// Real Page, native owner/dialog and managed events; no OS input/picker, files,
// provider, cleanup inventory/execution or archive/profile fixture is created.
internal static class UrlTextExportPageBoundaryChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly ResourceId First = new("4chan", "g", "123");
    private static readonly ResourceId Second = new("8kun", "tech", "456");
    private const string FirstUrl = "https://boards.4chan.org/g/thread/123";
    private const string SecondUrl = "https://8kun.top/tech/res/456.html";

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible && owner.SessionHost.State == DesktopSessionState.Ready,
            "Page-boundary checks require a visible, Ready synthetic smoke owner.");
        await CapturedMenuAndSummaryAsync(owner);
        await RejectedSummariesAsync(owner);
        foreach (bool closeOwner in new[] { false, true })
        {
            await EndDuringSummaryAsync(owner, closeOwner);
            await EndDuringPickerAsync(owner, closeOwner);
            await EndDuringWriteAsync(owner, closeOwner);
        }
        foreach (bool showFirst in new[] { false, true })
            await ShowFailureAsync(owner, showFirst);
        await ModalExclusionAsync(owner);
        await HtmlBlocksUrlTextAsync(owner);
        Console.WriteLine("SMOKE URLtext page boundary: captured menu/summary identity, rejected summaries, "
            + "Page-end and owner-close during summary/picker/write, started-write drain, show failures, "
            + "URLtext→HTML single/all-results/cleanup/preview and HTML→URLtext passed; "
            + "real native UI thread, synthetic callbacks, no OS picker/files/cleanup execution/client roundtrip.");
    }

    private static async Task CapturedMenuAndSummaryAsync(MainWindow owner)
    {
        await using Fixture f = await Fixture.CreateAsync(owner);
        ArchiveThreadRow a = new(Summary(First)), b = new(Summary(Second));
        f.SetRows(a, b);
        f.Select(a, b);
        // A URLtext export covers exactly one frozen thread: the product leaves the entry out of a
        // multi-selection menu (ArchivePage.Commands, d9eb18d5) instead of showing a disabled one.
        await f.OpenMenuAsync();
        Require(f.Menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "export.open-html-dialog")),
            "The multi-selection menu is not the real archive command menu.");
        Require(!f.Menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "export.open-urltext-dialog")),
            "Two selected rows offered URLtext.");
        f.Menu.Close();
        f.Select(a);
        MenuItem captured = await f.OpenUrlMenuAsync();
        Require(captured.Command?.CanExecute(null) == true, "Single visible row did not admit URLtext.");
        // Keep the original menu command, but change actual native table selection.
        f.Select(b);
        var summary = f.Hold<ArchiveThreadSummary?>(Summary(First));
        var entered = Signal();
        ResourceId? readId = null;
        f.Read = (id, _) => { readId = id; entered.TrySetResult(); return summary.Task; };
        captured.Command!.Execute(null);
        await entered.Task.WaitAsync(Deadline);
        Task opening = f.Page.UrlTextExportTask;
        f.Menu.Close();
        Require(readId == First && f.Reads == 1 && !opening.IsCompleted,
            "Menu invocation retargeted the captured ID or failed to retain its delayed read.");
        // Replace the entire visible result page while the summary is still delayed.
        ArchiveThreadRow replacement = new(Summary(Second));
        f.SetRows(replacement); f.Select(replacement);
        await f.Page.OpenUrlTextExportForThreadAsync(Second, null).WaitAsync(Deadline);
        Require(f.Reads == 1 && ReferenceEquals(opening, f.Page.UrlTextExportTask),
            "A repeated entry replaced the in-flight page operation.");
        summary.TrySetResult(Summary(First));
        UrlTextExportDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        Require(Label(dialog, "UrlTextExportSource").Text == Texts.T("urltext.export.source", First.Source)
            && Label(dialog, "UrlTextExportBoard").Text == Texts.T("urltext.export.board", First.Board)
            && Label(dialog, "UrlTextExportThread").Text == Texts.T("urltext.export.thread", First.Thread)
            && Label(dialog, "UrlTextExportUrl").Text == Texts.T("urltext.export.url", FirstUrl),
            "Actual consent controls do not show the captured summary identity.");
        Require(f.Picks == 0 && f.Writes == 0, "Showing consent selected or wrote a target.");
        var picker = f.Hold<string?>("synthetic-late-target");
        var picking = Signal();
        f.Choose = () => { picking.TrySetResult(); return picker.Task; };
        Click(dialog, "UrlTextExportSave");
        await picking.Task.WaitAsync(Deadline);
        f.SetRows(a); f.Select(a);
        picker.TrySetResult("synthetic-selected-target");
        await dialog.Operation.Completion.WaitAsync(Deadline);
        Require(f.Writes == 1 && f.WrittenPlan?.Id == First && f.WrittenPlan.Url == FirstUrl
            && f.WrittenTarget == "synthetic-selected-target",
            "Page callback wrote a different identity/target after selection changes.");
        dialog.Close();
        await opening.WaitAsync(Deadline);
        Require(dialog.Operation.Ended && !f.Page.IsReaderDialogOpen, "Closed consent retained the page lock.");
        Require(f.Readers == 0, "Synthetic summary path unexpectedly opened an archive reader.");
    }

    private static async Task RejectedSummariesAsync(MainWindow owner)
    {
        // The wrong-ID summary contains an otherwise valid page URL: parser success
        // alone must not authorize a dialog for a different selected thread.
        foreach (ArchiveThreadSummary? summary in new ArchiveThreadSummary?[]
        {
            null, Summary(Second), Summary(First) with { Url = SecondUrl }
        })
        {
            await using Fixture f = await Fixture.CreateAsync(owner);
            f.Read = (_, _) => Task.FromResult(summary);
            await f.Page.OpenUrlTextExportForThreadAsync(First, null).WaitAsync(Deadline);
            Require(f.Reads == 1 && f.Shows == 0 && f.Picks == 0 && f.Writes == 0,
                "Missing/mismatched summary passed the actual page boundary.");
            Require(f.Control<TextBlock>("ArchiveErrorText").Text == Texts.T(summary == null
                ? "urltext.export.error.summary" : "urltext.export.error.invalidUrl"),
                "Rejected summary did not render the fixed page diagnostic.");
            Require(!f.Page.IsReaderDialogOpen, "Rejected summary leaked the busy lock.");
        }
    }

    private static async Task EndDuringSummaryAsync(MainWindow owner, bool closeOwner)
    {
        await using Fixture f = await Fixture.CreateAsync(owner);
        var summary = f.Hold<ArchiveThreadSummary?>(Summary(First));
        CancellationToken readToken = default;
        f.Read = (_, token) => { readToken = token; return summary.Task; };
        Task opening = f.Page.OpenUrlTextExportForThreadAsync(First, null);
        Require(f.Reads == 1 && !opening.IsCompleted && readToken.CanBeCanceled,
            "Summary gate was not reached before lifetime end.");
        f.End(closeOwner);
        Require(readToken.IsCancellationRequested && !opening.IsCompleted,
            "Page/owner end did not cancel and retain the uncooperative summary await.");
        string? error = f.Control<TextBlock>("ArchiveErrorText").Text;
        summary.TrySetResult(Summary(First));
        await opening.WaitAsync(Deadline);
        Require(f.Shows == 0 && f.Picks == 0 && f.Writes == 0
            && f.Control<TextBlock>("ArchiveErrorText").Text == error,
            "Late summary opened consent, wrote, or rendered a stale diagnostic after lifetime end.");
    }

    private static async Task EndDuringPickerAsync(MainWindow owner, bool closeOwner)
    {
        await using Fixture f = await Fixture.CreateAsync(owner);
        var picker = f.Hold<string?>("synthetic-late-target");
        var entered = Signal();
        f.Choose = () => { entered.TrySetResult(); return picker.Task; };
        Task opening = f.Page.OpenUrlTextExportForThreadAsync(First, null);
        UrlTextExportDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        Click(dialog, "UrlTextExportSave");
        await entered.Task.WaitAsync(Deadline);
        Require(f.Picks == 1 && f.Writes == 0 && !dialog.Operation.Completion.IsCompleted,
            "Picker gate was not reached without a write.");
        f.End(closeOwner);
        await TurnAsync();
        Require(!opening.IsCompleted && !dialog.IsVisible,
            "Page opening abandoned the pending picker or retained the native dialog.");
        string? status = Label(dialog, "UrlTextExportStatus").Text;
        picker.TrySetResult("synthetic-late-target");
        await opening.WaitAsync(Deadline);
        await dialog.Operation.Completion.WaitAsync(Deadline);
        Require(f.Writes == 0 && dialog.Operation.Ended && !f.Page.IsReaderDialogOpen
            && Label(dialog, "UrlTextExportStatus").Text == status,
            "Late picker wrote or updated stale UI; page finally did not release the lock.");
    }

    private static async Task EndDuringWriteAsync(MainWindow owner, bool closeOwner)
    {
        await using Fixture f = await Fixture.CreateAsync(owner);
        var partial = new UrlTextExportFileWriteResult(UrlTextExportFileWriteStatus.PartialRetained,
            UrlTextExportFileWriteError.Canceled);
        var writer = f.Hold(partial);
        var entered = Signal();
        CancellationToken writeToken = default;
        f.Write = (_, _, token) => { writeToken = token; entered.TrySetResult(); return writer.Task; };
        Task opening = f.Page.OpenUrlTextExportForThreadAsync(First, null);
        UrlTextExportDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        Click(dialog, "UrlTextExportSave");
        await entered.Task.WaitAsync(Deadline);
        Require(f.Writes == 1 && !dialog.Operation.Completion.IsCompleted, "Writer gate was not entered.");
        f.End(closeOwner);
        await TurnAsync();
        Require(writeToken.IsCancellationRequested && !opening.IsCompleted && !dialog.IsVisible,
            "Page/owner end did not cancel and drain the already-started write.");
        string? status = Label(dialog, "UrlTextExportStatus").Text;
        string? error = f.Control<TextBlock>("ArchiveErrorText").Text;
        writer.TrySetResult(partial);
        await opening.WaitAsync(Deadline);
        Require(ReferenceEquals(partial, dialog.Operation.Result) && dialog.Operation.Ended
            && f.Writes == 1 && !f.Page.IsReaderDialogOpen
            && Label(dialog, "UrlTextExportStatus").Text == status
            && f.Control<TextBlock>("ArchiveErrorText").Text == error,
            "Page finally lost the partial receipt, wrote twice, or rendered after lifetime end.");
    }

    private static async Task ShowFailureAsync(MainWindow owner, bool showFirst)
    {
        await using Fixture f = await Fixture.CreateAsync(owner);
        UrlTextExportDialog? failed = null;
        f.Page.UrlTextShowForTests = (dialog, window) =>
        {
            failed = dialog;
            if (showFirst) _ = dialog.ShowDialog(window);
            throw new InvalidOperationException("PRIVATE SHOW DIAGNOSTIC");
        };
        await f.Page.OpenUrlTextExportForThreadAsync(First, null).WaitAsync(Deadline);
        Require(failed is { IsVisible: false } && failed.Operation.Ended
            && f.Picks == 0 && f.Writes == 0 && !f.Page.IsReaderDialogOpen,
            "Failed native show did not terminate the actual dialog and release page ownership.");
        Require(f.Control<TextBlock>("ArchiveErrorText").Text == Texts.T("urltext.export.error.write"),
            "Show failure leaked raw details or used a non-fixed diagnostic.");
        // A fresh operation must be admitted, not permanently locked out by failed show.
        f.InstallShow();
        Task retry = f.Page.OpenUrlTextExportForThreadAsync(Second, null);
        UrlTextExportDialog fresh = await f.Shown.Task.WaitAsync(Deadline);
        Require(!ReferenceEquals(failed, fresh), "Failed show reused its terminated dialog.");
        fresh.Close();
        await retry.WaitAsync(Deadline);
        Require(f.Reads == 2 && f.Picks == 0 && f.Writes == 0, "Failed-show retry changed consent semantics.");
    }

    private static async Task ModalExclusionAsync(MainWindow owner)
    {
        await using Fixture f = await Fixture.CreateAsync(owner);
        // Only attach the existing synthetic Ready host. Never open cleanup when
        // eligible: that would perform inventory outside this helper's scope.
        f.Page.AttachArchiveCleanup(owner.SessionHost);
        Require(f.Control<Button>("ArchiveCleanupOpen").IsEnabled
            && f.Control<Button>("ArchiveTransferPreview").IsEnabled
            && f.Control<Button>("ArchiveResultsExport").IsEnabled
            && f.Control<Button>("ArchiveResetFilters").IsEnabled,
            "Modal guard baseline was already disabled (vacuous exclusion).");
        int previewRoots = 0, previewShows = 0;
        f.Page.TransferPreviewWorkDirectoryCreatedForTests = _ => previewRoots++;
        f.Page.TransferPreviewShowForTests = (_, _) => { previewShows++; return Task.CompletedTask; };
        var summary = f.Hold<ArchiveThreadSummary?>(Summary(First));
        f.Read = (_, _) => summary.Task;
        Task opening = f.Page.OpenUrlTextExportForThreadAsync(First, null);
        Require(f.Reads == 1 && f.Page.IsReaderDialogOpen, "URLtext did not synchronously lock before summary await.");
        await CheckBlockedAsync();
        summary.TrySetResult(Summary(First));
        UrlTextExportDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        await CheckBlockedAsync();
        Require(f.Page.TryDismissOverlay() && dialog.IsVisible && !dialog.Operation.Ended,
            "Owner overlay dismiss incorrectly closed/routed the URLtext native dialog as HTML.");
        dialog.Close();
        await opening.WaitAsync(Deadline);
        Require(!f.Page.IsReaderDialogOpen && f.Control<Button>("ArchiveCleanupOpen").IsEnabled
            && f.Control<Button>("ArchiveTransferPreview").IsEnabled
            && f.Control<Button>("ArchiveResultsExport").IsEnabled
            && f.Control<Button>("ArchiveResetFilters").IsEnabled,
            "Terminal page finally did not restore modal-action eligibility or changed the unrelated reset baseline.");

        async Task CheckBlockedAsync()
        {
            // Reset is not a competing modal entry. Selection/filter changes may
            // coexist with the immutable captured plan; do not require a reset lock.
            Require(!f.Control<Button>("ArchiveResultsExport").IsEnabled,
                "URLtext lock did not disable the actual all-results button.");
            Require(f.Control<Button>("ArchiveResultsExport").Command?.CanExecute(null) == false,
                "URLtext lock did not fence the actual all-results action CanExecute.");
            await f.Page.OpenExportForThreadAsync(Second, null).WaitAsync(Deadline);
            await f.Page.OpenExportForAllResultsAsync(null).WaitAsync(Deadline);
            await f.Page.OpenArchiveCleanupAsync().WaitAsync(Deadline);
            await f.Page.OpenTransferPreviewAsync().WaitAsync(Deadline);
            Require(!f.Control<Border>("ArchiveExportDialog").IsVisible && !f.Page.IsArchiveCleanupOpen
                && f.Readers == 0 && previewRoots == 0 && previewShows == 0 && f.Picks == 0 && f.Writes == 0,
                "Competing HTML/all-results/cleanup/preview crossed a held URLtext page lock.");
        }
    }

    private static async Task HtmlBlocksUrlTextAsync(MainWindow owner)
    {
        await using Fixture f = await Fixture.CreateAsync(owner);
        await f.Page.OpenExportForThreadAsync(First, null).WaitAsync(Deadline);
        Require(f.Control<Border>("ArchiveExportDialog").IsVisible, "Real HTML dialog baseline did not open.");
        await f.Page.OpenUrlTextExportForThreadAsync(Second, null).WaitAsync(Deadline);
        Require(f.Reads == 0 && f.Shows == 0 && f.Picks == 0 && f.Writes == 0,
            "URLtext read/opened while the real HTML dialog was visible.");
        Click(f.Control<Button>("ArchiveExportClose"));
        Require(!f.Control<Border>("ArchiveExportDialog").IsVisible, "HTML close did not clear its own overlay.");
        Task opening = f.Page.OpenUrlTextExportForThreadAsync(Second, null);
        UrlTextExportDialog dialog = await f.Shown.Task.WaitAsync(Deadline);
        Require(f.Reads == 1, "HTML close left URLtext permanently blocked.");
        dialog.Close();
        await opening.WaitAsync(Deadline);
    }

    private static ArchiveThreadSummary Summary(ResourceId id) => new(id, id == First ? FirstUrl : SecondUrl,
        "PRIVATE SYNTHETIC TITLE", "/synthetic/not-opened", ThreadSourceState.NotFound,
        false, DateTimeOffset.UnixEpoch, null, 0, 1);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TextBlock Label(UrlTextExportDialog dialog, string name) =>
        dialog.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Name == name);
    private static void Click(UrlTextExportDialog dialog, string name) =>
        Click(dialog.GetVisualDescendants().OfType<Button>().Single(control => control.Name == name));
    private static void Click(Button button)
    {
        Require(button.IsEffectivelyEnabled, "Fixture cannot click a disabled action: " + button.Name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task TurnAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Dispatcher.UIThread.VerifyAccess();
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("URLtext page-boundary UI condition timed out.");
            await TurnAsync();
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ArchivePage Page { get; }
        internal Window Window { get; }
        internal ContextMenu Menu => Control<DataGrid>("ArchiveThreadTable").ContextMenu!;
        internal TaskCompletionSource<UrlTextExportDialog> Shown { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Readers, Reads, Shows, Picks, Writes;
        internal ArchiveUrlTextExportPlan? WrittenPlan;
        internal string? WrittenTarget;
        internal Func<ResourceId, CancellationToken, Task<ArchiveThreadSummary?>> Read =
            (id, _) => Task.FromResult<ArchiveThreadSummary?>(Summary(id));
        internal Func<Task<string?>> Choose = () => Task.FromResult<string?>("synthetic-no-file-target");
        internal Func<ArchiveUrlTextExportPlan, string, CancellationToken, Task<UrlTextExportFileWriteResult>> Write =
            (_, _, _) => Task.FromResult(new UrlTextExportFileWriteResult(
                UrlTextExportFileWriteStatus.Saved, UrlTextExportFileWriteError.None));
        private readonly List<Action> _releases = new();

        private Fixture()
        {
            Page = new ArchivePage(_ =>
            {
                Readers++;
                throw new InvalidOperationException("Page-boundary fixture must not open an archive reader.");
            }, (_, _) => throw new InvalidOperationException("No capture mutation permitted."),
                _ => throw new InvalidOperationException("No external folder launch permitted."));
            // Table mode below its 1450px side-preview threshold: real selection
            // changes must not start unrelated content reads.
            Window = new Window { Width = 960, Height = 700, Content = Page,
                Title = "Synthetic URLtext page boundary", ShowInTaskbar = false };
            Page.ArchiveLayoutPreference = "Table";
            // Only seed passive, applied-search data, never modal/busy/lifetime flags.
            // This makes the direct all-results regression non-vacuous without a DB.
            Seed("_appliedSearch", ArchiveSearchRequest.Create(new ArchiveSearchSpec()));
            Seed("_listTotal", 2);
            Page.UrlTextSummaryForTests = (id, token) => { Reads++; return Read(id, token); };
            Page.UrlTextChooseForTests = () => { Picks++; return Choose(); };
            Page.UrlTextWriteForTests = (plan, target, token) =>
            { Writes++; WrittenPlan = plan; WrittenTarget = target; return Write(plan, target, token); };
            InstallShow();
        }
        internal static async Task<Fixture> CreateAsync(MainWindow owner)
        {
            Dispatcher.UIThread.VerifyAccess();
            Fixture f = new();
            try
            {
                f.Window.Show(owner);
                f.Page.IsAvailable = true;
                await UntilAsync(() => f.Window.IsVisible && f.Page.IsEffectivelyVisible);
                Require(f.Window.TryGetPlatformHandle()?.Handle is nint handle && handle != 0,
                    "Fixture has no real native owner handle.");
                Require(f.Page.Bounds.Width < 1450, "Fixture unexpectedly enables side-preview reads.");
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        internal void InstallShow()
        {
            Shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Page.UrlTextShowForTests = (dialog, owner) =>
            {
                Dispatcher.UIThread.VerifyAccess();
                Shows++;
                Task shown = dialog.ShowDialog(owner);
                Require(dialog.IsVisible, "Actual URLtext dialog did not become visible.");
                Shown.TrySetResult(dialog);
                return shown;
            };
        }
        internal T Control<T>(string name) where T : Control => Page.FindControl<T>(name)
            ?? throw new InvalidOperationException("Missing page control: " + name);
        internal TaskCompletionSource<T> Hold<T>(T terminal)
        {
            var gate = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _releases.Add(() => gate.TrySetResult(terminal));
            return gate;
        }
        internal void SetRows(params ArchiveThreadRow[] rows)
        {
            Control<DataGrid>("ArchiveThreadTable").ItemsSource = rows;
            Control<ListBox>("ArchiveThreads").ItemsSource = rows;
            Window.UpdateLayout();
        }
        internal void Select(params ArchiveThreadRow[] rows)
        {
            DataGrid table = Control<DataGrid>("ArchiveThreadTable");
            table.SelectedItems.Clear();
            foreach (ArchiveThreadRow row in rows) table.SelectedItems.Add(row);
            Require(table.SelectedItems.Cast<ArchiveThreadRow>().SequenceEqual(rows), "Actual table selection differs from fixture.");
        }
        internal async Task OpenMenuAsync()
        {
            DataGrid table = Control<DataGrid>("ArchiveThreadTable");
            table.Focus();
            table.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Avalonia.Controls.Control.ContextRequestedEvent });
            await UntilAsync(() => Menu.IsOpen);
        }
        internal async Task<MenuItem> OpenUrlMenuAsync()
        {
            await OpenMenuAsync();
            return Menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "export.open-urltext-dialog"));
        }
        internal void End(bool closeOwner)
        {
            if (closeOwner) Window.Close();
            else { Page.EndLifetime(); Page.EndLifetime(); } // actual idempotent lifetime entry
        }
        private void Seed(string name, object value) =>
            (typeof(ArchivePage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(ArchivePage).FullName, name)).SetValue(Page, value);
        public async ValueTask DisposeAsync()
        {
            Dispatcher.UIThread.VerifyAccess();
            Menu.Close();
            Page.EndLifetime();
            foreach (Action release in _releases) release();
            try { await Page.UrlTextExportTask.WaitAsync(Deadline); }
            finally { Window.Close(); }
            Require(Readers == 0, "Fixture unexpectedly attempted archive/profile IO.");
        }
    }
}
