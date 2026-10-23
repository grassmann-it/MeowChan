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
            bool german = Texts.Language == "de";
            string title = CommandCatalog.Label("archive.delete-permanently");
            Window dialog = new()
            {
                Name = "ArchivePermanentDeleteDialog", Title = title, Width = 640, Height = 460,
                MinWidth = 360, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            TextBlock summary = new()
            {
                TextWrapping = TextWrapping.Wrap,
                Text = german
                    ? $"{plan.Catalog.Threads.Count} Threads und {plan.Files.OwnedFileCount} lokale Dateien ({plan.Files.OwnedBytes:N0} Bytes) endgültig löschen?"
                    : $"Permanently delete {plan.Catalog.Threads.Count} threads and {plan.Files.OwnedFileCount} local files ({plan.Files.OwnedBytes:N0} bytes)?"
            };
            List<string> details = plan.Catalog.Threads.Select(thread =>
                thread.Summary.Id.Source + " /" + thread.Summary.Id.Board + "/ " + thread.Summary.Id.Thread + " · " + thread.Summary.Title).ToList();
            details.Add("");
            details.Add(german ? $"{plan.RatingCount} Medienbewertungen werden entfernt." : $"{plan.RatingCount} media ratings will be removed.");
            details.Add(german
                ? "Archivbeiträge, lokale Originale und Favoriten werden entfernt. Die Threads werden nicht erneut vom beobachteten Board hinzugefügt. Exporte und fremde Dateien bleiben erhalten."
                : "Archived posts, local originals and favorites will be removed. Watched boards will not add these threads again. Exports and unrelated files remain available.");
            details.Add(Texts.T("bookmarks.retained"));
            foreach (ArchiveDirectoryPlan directory in plan.Files.Directories)
            {
                details.Add(""); details.Add(directory.Directory);
                if (directory.Blocker != null) details.Add(directory.Blocker);
                if (directory.Leftovers.Count > 0) details.Add((german ? "Bleiben erhalten: " : "Retained: ") + string.Join(", ", directory.Leftovers));
            }
            foreach (string shared in plan.Catalog.SharedMediaDirectories)
                details.Add((german ? "Gemeinsam mit anderen Threads verwendet: " : "Shared with other threads: ") + shared);
            TextBox detail = new()
            {
                Name = "ArchivePermanentDeleteDetails", Text = string.Join(Environment.NewLine, details),
                IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap
            };
            TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
            Button cancel = new() { Name = "ArchivePermanentDeleteCancel", Content = Texts.T("action.cancel") };
            Button delete = CommandBindings.CreateButton("archive.delete-permanently", () => { });
            delete.Name = "ArchivePermanentDeleteConfirm";
            delete.Command = null;
            delete.Background = new SolidColorBrush(Color.Parse("#B83535"));
            delete.Foreground = Brushes.White;
            delete.IsEnabled = plan.Catalog.Threads.Count > 0 && !plan.Files.HasBlockers && plan.Catalog.SharedMediaDirectories.Count == 0;
            if (!delete.IsEnabled)
                status.Text = german ? "Diese Auswahl kann nicht vollständig gelöscht werden. Bitte Details prüfen oder die Auswahl einschränken."
                    : "This selection cannot be fully deleted. Review the details or select fewer threads.";
            StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
            Button ratingRetry = CommandBindings.CreateButton("media.rating.cleanup-retry", () => { });
            ratingRetry.Command = null; ratingRetry.IsVisible = false;
            IReadOnlyList<ResourceId>? ratingRetryIds = null; // Deliberately session/dialog-only, never persisted.
            buttons.Children.Add(ratingRetry); buttons.Children.Add(cancel); buttons.Children.Add(delete);
            Grid layout = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 12, Margin = new Thickness(20) };
            layout.Children.Add(summary); layout.Children.Add(detail); Grid.SetRow(detail, 1);
            layout.Children.Add(status); Grid.SetRow(status, 2);
            layout.Children.Add(buttons); Grid.SetRow(buttons, 3); dialog.Content = layout;
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
                    status.Text = german ? "Bewertungen bereinigt. Es wurden keine weiteren Archivdateien gelöscht." : "Ratings cleaned up. No further archive files were deleted.";
                }
                catch (Exception error) { status.Text = error.Message; }
                finally { executing = false; ratingRetry.IsEnabled = cancel.IsEnabled = true; }
            }
            ratingRetry.Click += async (_, _) => await RetryRatingsAsync();
            CommandRouter ratingRouter = new(dialog);
            ratingRouter.Register(new("media.rating.cleanup-retry", dialog, () => dialog.IsVisible,
                () => !executing && ratingRetryIds?.Count > 0, () => _ = RetryRatingsAsync(), Priority: 300));
            dialog.KeyDown += (_, e) => ratingRouter.Route(e);
            dialog.Closed += (_, _) => ratingRouter.Dispose();
            delete.Click += async (_, _) =>
            {
                if (executing) return;
                executing = true; delete.IsEnabled = cancel.IsEnabled = false;
                status.Text = german ? "Lokale Daten werden gelöscht …" : "Deleting local data…";
                try
                {
                    if (_gallery != null) { CloseGallery(restoreOrigin: false); await GalleryCleanupTask; }
                    if (_largeReaderOpen) CloseLargeReader(restoreOrigin: false);
                    await ArchivePage.ReleaseMediaForPermanentDeletionAsync(ids);
                    ArchivePermanentDeletionResult result = await _session.DeleteArchivePermanentlyAsync(plan);
                    ratingRetryIds = result.RatingCleanupThreads;
                    ratingRetry.IsVisible = ratingRetryIds?.Count > 0;
                    List<string> outcome = result.Errors.ToList();
                    if (result.Files != null)
                        foreach (ArchiveDirectoryResult directory in result.Files.Directories)
                            if (directory.Leftovers.Count > 0)
                                outcome.Add(directory.Directory + Environment.NewLine +
                                    (german ? "Bleiben erhalten: " : "Retained: ") + string.Join(", ", directory.Leftovers));
                    outcome.Add(Texts.T("bookmarks.retained"));
                    detail.Text = string.Join(Environment.NewLine, outcome);
                    _postBookmarksDialog?.InvalidateAvailability();
                    status.Text = german ? $"{result.DeletedThreads.Count} Threads gelöscht."
                        : $"Deleted {result.DeletedThreads.Count} threads.";
                    if (result.Files != null)
                        status.Text += german ? $" {result.Files.DeletedFileCount} lokale Dateien entfernt."
                            : $" Removed {result.Files.DeletedFileCount} local files.";
                    await ArchivePage.ActivateAsync();
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    status.Text = error.Message;
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
