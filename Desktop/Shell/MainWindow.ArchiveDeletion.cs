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
            Window dialog = new()
            {
                Name = "ArchivePermanentDeleteDialog", Title = title, Width = 640, Height = 460,
                MinWidth = 360, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            TextBlock summary = new()
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Texts.Count("archive.delete.summary", plan.Catalog.Threads.Count, plan.Catalog.Threads.Count, plan.Files.OwnedFileCount, plan.Files.OwnedBytes)
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
            // Risk flow: Cancel stays on its own at the start, the destructive action sits apart at the end.
            StackPanel risky = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            risky.Children.Add(ratingRetry); risky.Children.Add(tagRetry); risky.Children.Add(delete);
            Grid actions = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 24 };
            actions.Children.Add(cancel); actions.Children.Add(risky); Grid.SetColumn(risky, 2);
            status.IsVisible = !string.IsNullOrEmpty(status.Text);
            status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) status.IsVisible = !string.IsNullOrEmpty(status.Text); };
            StackPanel footer = new() { Spacing = 8 };
            footer.Children.Add(status); footer.Children.Add(actions);
            TextBlock heading = new() { Text = title, TextWrapping = TextWrapping.Wrap };
            heading.Classes.Add("section-title");
            StackPanel header = new() { Spacing = 8 };
            header.Children.Add(heading); header.Children.Add(summary);
            detail.Margin = new Thickness(16, 4, 16, 16);
            detail.VerticalAlignment = VerticalAlignment.Top;
            dialog.Content = DialogFrame.Build(header, detail, footer, DialogFrame.ErrorStrip(failure));
            bool executing = false;
            cancel.Click += (_, _) => dialog.Close();
            dialog.Closing += (_, e) => { if (executing) e.Cancel = true; };
            dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape && !executing) { e.Handled = true; dialog.Close(); } };
            async Task RetryRatingsAsync()
            {
                if (executing || ratingRetryIds == null) return;
                ResourceId[] frozen = ratingRetryIds.ToArray();
                executing = true; ratingRetry.IsEnabled = cancel.IsEnabled = false;
                try
                {
                    await _session.RetryMediaRatingCleanupAsync(frozen);
                    ratingRetryIds = null; ratingRetry.IsVisible = false;
                    failure.Text = string.Empty;
                    status.Text = Texts.T("archive.delete.ratingsCleaned");
                }
                catch (Exception error) { status.Text = string.Empty; failure.Text = error.Message; }
                finally { executing = false; ratingRetry.IsEnabled = cancel.IsEnabled = true; }
            }
            ratingRetry.Click += async (_, _) => await RetryRatingsAsync();
            async Task RetryTagsAsync()
            {
                if (executing || tagRetryIds == null) return;
                ResourceId[] frozen = tagRetryIds.ToArray();
                executing = true; tagRetry.IsEnabled = cancel.IsEnabled = false;
                try
                {
                    await _session.RetryPrivateTagCleanupAsync(frozen);
                    tagRetryIds = null; tagRetry.IsVisible = false;
                    failure.Text = string.Empty;
                    status.Text = Texts.T("archive.delete.tagsCleaned");
                }
                catch (Exception error) { status.Text = string.Empty; failure.Text = error.Message; }
                finally { executing = false; tagRetry.IsEnabled = cancel.IsEnabled = true; }
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
                executing = true; delete.IsEnabled = cancel.IsEnabled = false;
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
                        status.Text += " " + Texts.T("archive.delete.filesRemoved", result.Files.DeletedFileCount);
                    await ArchivePage.ActivateAsync();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    status.Text = string.Empty;
                    failure.Text = error.Message;
                    localError(error.Message);
                    ShowNotification(Texts.T("notification.error"), error.Message, NotificationType.Error, context: string.Join(",", ids.Select(id => id.ToString())), operation: "archive.delete");
                }
                finally { executing = false; cancel.IsEnabled = true; cancel.Content = Texts.T("action.close"); }
            };
            dialog.Opened += (_, _) => cancel.Focus();
            await dialog.ShowDialog(this);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!_isClosing)
            {
                localError(error.Message);
                ShowNotification(Texts.T("notification.error"), error.Message, NotificationType.Error, context: string.Join(",", ids.Select(id => id.ToString())), operation: "archive.delete");
            }
        }
        finally
        {
            _archiveDeletionOpen = false;
            if (origin?.IsEffectivelyVisible == true) origin.Focus();
        }
    }
}
