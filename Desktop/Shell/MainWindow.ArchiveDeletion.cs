using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    private bool _archiveDeletionOpen;
    // S4 F4: while the dialog runs a step that cannot be cancelled half-way (the deletion itself, or a cleanup retry), the step's end;
    // null otherwise. The dialog refuses its own close meanwhile, and a close of the app is asked at the veto point.
    private TaskCompletionSource? _archiveDeletionRun;
    private Window? _archiveDeletionDialog;
    private bool _archiveDeletionCloseAsked;

    /// <summary>A permanent deletion (or one of its cleanup retries) is running right now (test and smoke seam).</summary>
    internal bool IsArchiveDeletionRunning => _archiveDeletionRun != null;

    private void BeginArchiveDeletionRun() => _archiveDeletionRun ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    private void EndArchiveDeletionRun()
    {
        TaskCompletionSource? run = _archiveDeletionRun;
        _archiveDeletionRun = null;
        run?.TrySetResult();
    }

    // S4 F4: a permanent deletion removes the media folders first and the catalog rows afterwards, thread by thread, and has no point
    // where it could stop cleanly. The shutdown time limit would end the process in its middle, which leaves deleted threads back in the
    // archive with their media gone. Exempting the step from the limit would bring back the unbounded wait behind a hidden window, so
    // the close is asked here, at the veto point, where nothing has changed yet: "Quit when finished" waits with the window as it is
    // (the deletion dialog shows the progress) and then quits; "Keep MeowChan open" keeps everything. A second close request while the
    // question is open or the wait runs is kept back; the first one ends the app.
    private async Task<bool> ConfirmCloseDuringArchiveDeletionAsync()
    {
        if (_archiveDeletionRun == null) return true;
        if (_archiveDeletionCloseAsked) return false;
        _archiveDeletionCloseAsked = true;
        try
        {
            Window owner = _archiveDeletionDialog is { IsVisible: true } dialog ? dialog : this;
            bool quitAfter = await DialogPrompt.ConfirmAsync(owner, CommandCatalog.Label("archive.delete-permanently"),
                Texts.T("shutdown.deletion.running"), Texts.T("shutdown.deletion.quitAfter"), Texts.T("shutdown.deletion.keepOpen"), danger: false);
            if (!quitAfter) return false;
            if (_archiveDeletionRun is { } run)
            {
                Serilog.Log.Information("Closing the application waits for the running permanent deletion to finish.");
                await run.Task;
            }
            return true;
        }
        finally { _archiveDeletionCloseAsked = false; }
    }

    private async Task DeleteArchivePermanentlyAsync(IReadOnlyList<ResourceId> selection, Control? origin)
    {
        if (_archiveDeletionOpen || _isClosing || selection.Count == 0) return;
        ResourceId[] ids = selection.Distinct().ToArray();
        Action<string> localError = ArchivePage.CaptureActionErrorReporter();
        _archiveDeletionOpen = true;
        try
        {
            ArchivePermanentDeletionPlan plan = await _session.PrepareArchiveDeletionAsync(ids);
            if (_isClosing) return;
            string title = CommandCatalog.Label("archive.delete-permanently");
            // Modals plan MD3: the form step; the title stands in the title bar only, the destructive action is the primary one.
            Window dialog = DialogWindow.Create(title, DialogSize.M, modal: true);
            dialog.Name = "ArchivePermanentDeleteDialog";
            _archiveDeletionDialog = dialog;
            TextBlock summary = new()
            {
                TextWrapping = TextWrapping.Wrap,
                Text = CountPhrases.ArchiveDeletionSummary(plan.Catalog.Threads.Count, plan.Files.OwnedFileCount, plan.Files.OwnedBytes)
            };
            List<string> details = plan.Catalog.Threads.Select(thread =>
                thread.Summary.Id.Source + " /" + thread.Summary.Id.Board + "/ " + thread.Summary.Id.Thread + " · " + thread.Summary.Title).ToList();
            details.Add("");
            details.Add(Texts.Count("archive.delete.ratings", plan.RatingCount));
            details.Add(Texts.T("archive.delete.effects"));
            details.Add(Texts.T("bookmarks.retained"));
            foreach (ArchiveDirectoryPlan directory in plan.Files.Directories)
            {
                details.Add(""); details.Add(directory.Directory);
                if (directory.Blocker != null) details.Add(directory.Blocker);
                if (directory.Leftovers.Count > 0) details.Add(Texts.T("archive.delete.retained") + string.Join(", ", directory.Leftovers));
            }
            foreach (string shared in plan.Catalog.SharedMediaDirectories)
                details.Add(Texts.T("archive.delete.shared") + shared);
            TextBox detail = new()
            {
                Name = "ArchivePermanentDeleteDetails", Text = string.Join(Environment.NewLine, details),
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap
            };
            TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
            Avalonia.Automation.AutomationProperties.SetLiveSetting(status, Avalonia.Automation.AutomationLiveSetting.Polite);
            // Blockers and failures stay local and above the body; progress and results remain in the footer.
            TextBlock failure = new() { TextWrapping = TextWrapping.Wrap };
            Button cancel = new() { Name = "ArchivePermanentDeleteCancel", Content = Texts.T("action.cancel") };
            Button delete = CommandBindings.CreateButton("archive.delete-permanently", () => { });
            delete.Name = "ArchivePermanentDeleteConfirm";
            delete.Command = null;
            delete.Classes.Add("danger");
            delete.IsEnabled = plan.Catalog.Threads.Count > 0 && !plan.Files.HasBlockers && plan.Catalog.SharedMediaDirectories.Count == 0;
            if (!delete.IsEnabled)
                failure.Text = Texts.T("archive.delete.blocked");
            Button ratingRetry = CommandBindings.CreateButton("media.rating.cleanup-retry", () => { });
            ratingRetry.Command = null; ratingRetry.IsVisible = false;
            IReadOnlyList<ResourceId>? ratingRetryIds = null; // Deliberately session/dialog-only, never persisted.
            // Own tags of the deleted threads: a failed cleanup is visible and can be retried here, idempotently.
            Button tagRetry = new() { Name = "ArchivePermanentDeleteTagRetry", Content = Texts.T("archive.delete.tagRetry"), IsVisible = false };
            Avalonia.Automation.AutomationProperties.SetName(tagRetry, Texts.T("archive.delete.tagRetry"));
            IReadOnlyList<ResourceId>? tagRetryIds = null;
            status.IsVisible = !string.IsNullOrEmpty(status.Text);
            status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) status.IsVisible = !string.IsNullOrEmpty(status.Text); };
            // Risk flow: Cancel and the retries stay left of the destructive action, which is the primary one right-most.
            // The status or result summary keeps the full width above the buttons.
            Border footer = DialogFooter.Build(delete, new Control[] { cancel, ratingRetry, tagRetry }, status, statusAbove: true);
            detail.Margin = new Thickness(16, 4, 16, 16);
            detail.VerticalAlignment = VerticalAlignment.Top;
            DialogWindow.Frame(dialog, DialogFrame.Build(summary, detail, footer, DialogFrame.ErrorStrip(failure)), modal: true);
            bool executing = false;
            dialog.Closing += (_, e) => { if (executing) e.Cancel = true; };
            DialogEscape.Attach(dialog, isBusy: () => executing);
            cancel.Click += (_, _) => dialog.Close();
            async Task RetryRatingsAsync()
            {
                if (executing || ratingRetryIds == null) return;
                ResourceId[] frozen = ratingRetryIds.ToArray();
                executing = true; BeginArchiveDeletionRun(); ratingRetry.IsEnabled = cancel.IsEnabled = false;
                try
                {
                    await _session.RetryMediaRatingCleanupAsync(frozen);
                    ratingRetryIds = null; ratingRetry.IsVisible = false;
                    failure.Text = string.Empty;
                    status.Text = Texts.T("archive.delete.ratingsCleaned");
                }
                catch (Exception error) { status.Text = string.Empty; failure.Text = Texts.T("archive.delete.cleanupRetryFailed", error.Message); }
                finally { executing = false; EndArchiveDeletionRun(); ratingRetry.IsEnabled = cancel.IsEnabled = true; }
            }
            ratingRetry.Click += async (_, _) => await RetryRatingsAsync();
            async Task RetryTagsAsync()
            {
                if (executing || tagRetryIds == null) return;
                ResourceId[] frozen = tagRetryIds.ToArray();
                executing = true; BeginArchiveDeletionRun(); tagRetry.IsEnabled = cancel.IsEnabled = false;
                try
                {
                    await _session.RetryPrivateTagCleanupAsync(frozen);
                    tagRetryIds = null; tagRetry.IsVisible = false;
                    failure.Text = string.Empty;
                    status.Text = Texts.T("archive.delete.tagsCleaned");
                }
                catch (Exception error) { status.Text = string.Empty; failure.Text = Texts.T("archive.delete.cleanupRetryFailed", error.Message); }
                finally { executing = false; EndArchiveDeletionRun(); tagRetry.IsEnabled = cancel.IsEnabled = true; }
            }
            tagRetry.Click += async (_, _) => await RetryTagsAsync();
            CommandRouter ratingRouter = new(dialog);
            ratingRouter.Register(new("media.rating.cleanup-retry", dialog, () => dialog.IsVisible,
                () => !executing && ratingRetryIds?.Count > 0, () => _ = RetryRatingsAsync(), Priority: 300));
            dialog.KeyDown += (_, e) => ratingRouter.Route(e);
            dialog.Closed += (_, _) => ratingRouter.Dispose();
            delete.Click += async (_, _) =>
            {
                if (executing) return;
                executing = true; BeginArchiveDeletionRun(); delete.IsEnabled = cancel.IsEnabled = false;
                failure.Text = string.Empty;
                status.Text = Texts.T("archive.delete.deleting");
                try
                {
                    if (_gallery != null) { CloseGallery(restoreOrigin: false); await GalleryCleanupTask; }
                    if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
                    await ArchivePage.ReleaseMediaForPermanentDeletionAsync(ids);
                    ArchivePermanentDeletionResult result = await _session.DeleteArchivePermanentlyAsync(plan);
                    ratingRetryIds = result.RatingCleanupThreads;
                    ratingRetry.IsVisible = ratingRetryIds?.Count > 0;
                    tagRetryIds = result.TagCleanupThreads;
                    tagRetry.IsVisible = tagRetryIds?.Count > 0;
                    List<string> outcome = result.Errors.ToList();
                    if (result.Files != null)
                        foreach (ArchiveDirectoryResult directory in result.Files.Directories)
                            if (directory.Leftovers.Count > 0)
                                outcome.Add(directory.Directory + Environment.NewLine +
                                    Texts.T("archive.delete.retained") + string.Join(", ", directory.Leftovers));
                    outcome.Add(Texts.T("bookmarks.retained"));
                    detail.Text = string.Join(Environment.NewLine, outcome);
                    _postBookmarksDialog?.InvalidateAvailability();
                    status.Text = Texts.Count("archive.delete.deleted", result.DeletedThreads.Count);
                    if (result.Files != null)
                        status.Text += " " + Texts.Count("archive.delete.filesRemoved", result.Files.DeletedFileCount);
                    await ArchivePage.ActivateAsync();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    status.Text = string.Empty;
                    failure.Text = Texts.T("archive.delete.failed", error.Message);
                    localError(Texts.T("archive.delete.failed", error.Message));
                    ShowNotification(Texts.T("notification.error"), Texts.T("archive.delete.failed", error.Message), NotificationType.Error, context: string.Join(",", ids.Select(id => id.ToString())), operation: "archive.delete");
                }
                finally { executing = false; EndArchiveDeletionRun(); cancel.IsEnabled = true; cancel.Content = Texts.T("action.close"); }
            };
            dialog.Opened += (_, _) => cancel.Focus();
            await dialog.ShowDialog(this);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_isClosing)
            {
                localError(Texts.T("archive.delete.failed", error.Message));
                ShowNotification(Texts.T("notification.error"), Texts.T("archive.delete.failed", error.Message), NotificationType.Error, context: string.Join(",", ids.Select(id => id.ToString())), operation: "archive.delete");
            }
        }
        finally
        {
            _archiveDeletionOpen = false;
            _archiveDeletionDialog = null;
            EndArchiveDeletionRun();
            if (origin?.IsEffectivelyVisible == true) origin.Focus();
        }
    }
}
