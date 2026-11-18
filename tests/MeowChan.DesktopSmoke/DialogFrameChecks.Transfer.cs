using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Transfer group: native import, inventory-only preview, startup recovery of interrupted transfers and
// the media alternatives list. Private fixtures only: an unreadable synthetic package drives the
// import/preview error strip, and the recovery/conflict dialogs run on in-memory delegates.
internal static partial class DialogFrameChecks
{
    private static readonly TimeSpan TransferDeadline = TimeSpan.FromSeconds(20);

    private static partial async Task RunTransferAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string garbage = Path.Combine(TemporaryWorkspacePaths.TempRoot,
            "meowchan-dialog-frame-transfer-" + Guid.NewGuid().ToString("N") + ".zip");
        File.WriteAllBytes(garbage, System.Text.Encoding.UTF8.GetBytes("not a native transfer package"));
        try
        {
            await TransferImportAsync(owner, garbage);
            await TransferPreviewAsync(owner, garbage);
        }
        finally { try { File.Delete(garbage); } catch { } }
        await InterruptedTransfersAsync(owner);
        await MediaConflictsAsync(owner);
    }

    private static async Task TransferImportAsync(MainWindow owner, string garbage)
    {
        string work = ArchivePage.CreateTransferImportWorkDirectory();
        NativeTransferImportDialog dialog = new(owner.SessionHost.CreateNativeTransferImportSession(work),
            () => Task.FromResult<string?>(garbage));
        Task shown = dialog.ShowDialog(owner);
        try
        {
            await TransferWaitAsync(() => dialog.IsVisible);
            await VerifyFrameAsync(dialog, "transfer import", "transfer-import");
            await dialog.ChooseForTestsAsync();
            await TransferWaitAsync(() => !string.IsNullOrEmpty(dialog.ErrorText.Text));
            Require(!dialog.ApplyButton.IsEnabled, "transfer import: Apply enabled for an unreadable package.");
            await VerifyErrorStripAsync(dialog, "transfer import error", dialog.ErrorText.Text);
            Capture(dialog, "transfer-import-error");
            await TransferAtMinimumAsync(dialog, "transfer import minimum", "transfer-import-min");
            dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await shown.WaitAsync(TransferDeadline);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await dialog.TerminateAsync().WaitAsync(TransferDeadline);
            try { Directory.Delete(work, recursive: false); } catch { }
        }
        Require(dialog.SessionDisposalCountForTests == 1, "transfer import: session not disposed exactly once.");
    }

    private static async Task TransferPreviewAsync(MainWindow owner, string garbage)
    {
        string work = ArchivePage.CreateTransferPreviewWorkDirectory();
        NativeTransferPreviewDialog dialog = new(new NativeTransferPreviewSession(work), () => Task.FromResult<string?>(garbage));
        Task shown = dialog.ShowDialog(owner);
        try
        {
            await TransferWaitAsync(() => dialog.IsVisible);
            await VerifyFrameAsync(dialog, "transfer preview", "transfer-preview");
            await dialog.ChooseForTestsAsync();
            await TransferWaitAsync(() => !string.IsNullOrEmpty(dialog.ErrorText.Text));
            await VerifyErrorStripAsync(dialog, "transfer preview error", dialog.ErrorText.Text);
            Capture(dialog, "transfer-preview-error");
            await TransferAtMinimumAsync(dialog, "transfer preview minimum", "transfer-preview-min");
            dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await shown.WaitAsync(TransferDeadline);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await dialog.TerminateAsync().WaitAsync(TransferDeadline);
            try { Directory.Delete(work, recursive: false); } catch { }
        }
        Require(dialog.SessionDisposalCountForTests == 1, "transfer preview: session not disposed exactly once.");
    }

    private static async Task InterruptedTransfersAsync(MainWindow owner)
    {
        // Enough rows that the body scrolls at minimum size while Discard/Keep stay in place.
        InterruptedNativeMediaOperation[] operations = Enumerable.Range(0, 12).Select(index =>
            new InterruptedNativeMediaOperation(index % 2 == 1, Guid.NewGuid(), new string('a', 64),
                new("4chan", "b", (7001 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                MeowChan.Transfer.NativeMediaImportOperationState.Prepared, DateTimeOffset.UtcNow, 2048)).ToArray();
        InterruptedTransfersDialog dialog = new(operations);
        dialog.Show(owner);
        try
        {
            await TransferWaitAsync(() => dialog.IsVisible);
            await VerifyFrameAsync(dialog, "interrupted transfers", "interrupted-transfers");
            Button discard = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "InterruptedTransfersDiscard");
            discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await TransferWaitAsync(() => Equals(discard.Content, Texts.T("transfer.interrupted.confirm", operations.Length, 2048L * operations.Length)));
            await TransferAtMinimumAsync(dialog, "interrupted transfers confirm minimum", "interrupted-transfers-min");
            Require(!dialog.Decision.IsCompleted, "interrupted transfers: the first press already decided.");
            // Escape first only withdraws the confirmation, the second Escape keeps everything.
            dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await Settle();
            Require(dialog.IsVisible && Equals(discard.Content, Texts.T("transfer.interrupted.discard")),
                "interrupted transfers: Escape did not only withdraw the confirmation.");
            dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Require(!await dialog.Decision.WaitAsync(TransferDeadline), "interrupted transfers: Escape did not keep.");
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    private static async Task MediaConflictsAsync(MainWindow owner)
    {
        string active = new('d', 64);
        MediaConflictRow[] rows = Enumerable.Range(0, 14).Select(index =>
        {
            string id = index.ToString("x2", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, 'c');
            return new MediaConflictRow(id, new("4chan", "g", (100 + index).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                active, id, 10, 20 + index, MediaConflictUserStatus.Open);
        }).ToArray();
        int reloads = 0;
        MediaConflictActionOutcome next = MediaConflictActionOutcome.Applied;
        MediaConflictsDialog dialog = new(
            (offset, limit, _) =>
            {
                reloads++;
                return Task.FromResult(new MediaConflictPage(rows.Length, rows.Skip(offset).Take(limit).ToArray()));
            },
            (_, _, _) => Task.FromResult(next),
            _ => Task.FromResult(new MediaReclaimSummary(0, 0)),
            _ => throw new InvalidOperationException("media conflicts: nothing to reclaim was reclaimed."));
        dialog.Show(owner);
        try
        {
            await TransferWaitAsync(() => dialog.IsVisible && dialog.RowsForTests.Children.Count == rows.Length);
            await VerifyFrameAsync(dialog, "media conflicts", "media-conflicts");
            // A refused decision must survive the reload that follows it. Count the reloads so the
            // assertion cannot pass on the status written before the list was read again.
            next = MediaConflictActionOutcome.NotAllowed;
            KeepOf(dialog, 0).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await TransferWaitAsync(() => dialog.StatusForTests.Text == Texts.T("transfer.conflicts.error.action"));
            await TransferWaitAsync(() => reloads >= 2);
            await Settle();
            Require(dialog.StatusForTests.Text == Texts.T("transfer.conflicts.error.action"),
                "media conflicts: the list reload erased a refused decision without an explanation.");
            // An unavailable promotion keeps its own, more specific explanation across the reload.
            next = MediaConflictActionOutcome.Unavailable;
            KeepOf(dialog, 1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await TransferWaitAsync(() => dialog.StatusForTests.Text == Texts.T("transfer.conflicts.error.promoteUnavailable"));
            await TransferWaitAsync(() => reloads >= 3);
            await Settle();
            Require(dialog.StatusForTests.Text == Texts.T("transfer.conflicts.error.promoteUnavailable"),
                "media conflicts: the reload erased the promotion-specific failure.");
            // A recorded decision clears its own outcome again.
            next = MediaConflictActionOutcome.Applied;
            KeepOf(dialog, 2).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await TransferWaitAsync(() => reloads >= 4 && dialog.StatusForTests.Text == string.Empty);
            Console.WriteLine("SMOKE media conflicts: a refused decision and an unavailable promotion stay visible across the list reload; a recorded decision clears only its own outcome.");
            // The polite status stays in the footer, above Reclaim/Close; the row actions stay in the body.
            Button reclaim = dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MediaConflictsReclaim");
            reclaim.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await TransferWaitAsync(() => dialog.StatusForTests.Text == Texts.T("transfer.conflicts.reclaim.none"));
            Control footer = Part<Control>(dialog, MeowChan.Desktop.Controls.DialogFrame.FooterName, "media conflicts");
            Require(dialog.StatusForTests.GetVisualAncestors().Contains(footer), "media conflicts: status is not in the footer.");
            await TransferAtMinimumAsync(dialog, "media conflicts minimum", "media-conflicts-min");
            dialog.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            await TransferWaitAsync(() => !dialog.IsVisible);
        }
        finally { if (dialog.IsVisible) dialog.Close(); }
    }

    private static Button KeepOf(MediaConflictsDialog dialog, int row)
    {
        Control border = dialog.RowsForTests.Children.OfType<Control>().ElementAt(row);
        return border.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "MediaConflictKeep");
    }

    private static async Task TransferAtMinimumAsync(Window dialog, string label, string capture)
    {
        double width = dialog.Width, height = dialog.Height;
        dialog.Width = dialog.MinWidth;
        dialog.Height = dialog.MinHeight;
        await TransferWaitAsync(() => Math.Abs(dialog.Bounds.Width - dialog.MinWidth) < 1 && Math.Abs(dialog.Bounds.Height - dialog.MinHeight) < 1);
        await VerifyFrameAsync(dialog, label, capture);
        dialog.Width = width;
        dialog.Height = height;
        await Settle();
    }

    private static async Task TransferWaitAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TransferDeadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("dialog frame transfer: condition not reached in time.");
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
