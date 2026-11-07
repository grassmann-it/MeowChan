using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    partial void QueryUserPriorityAvailable(ref bool available)
        => available = _restoreCompleted && !_isClosing && !_session.Projection.IsStale;

    partial void ApplyFrozenUserPriority(ThreadRow[] rows, bool high) => _ = SetFrozenUserPriorityAsync(rows, high);

    private async Task SetFrozenUserPriorityAsync(ThreadRow[] rows, bool high)
    {
        int accepted = 0;
        var captured = rows.DistinctBy(row => row.WatchId).ToDictionary(row => row.WatchId);
        long[] frozen = captured.Keys.ToArray();
        await RunSelectionCommandAsync(frozen, async id =>
        {
            ThreadRow row = captured[id];
            // RunSelectionCommandAsync may wait for a projection between rows.
            // Recheck the captured object and opposite state after that wait.
            if (RowCommandBusy || !UserPriorityAvailable()
                || !IsUserPriorityTarget(row, high, IsCurrentUserPriorityRow)) return;
            if (!await _session.SetUserThreadPriorityAsync(row.WatchId, high))
                throw new InvalidOperationException(Texts.T("priority.stale"));
            accepted++;
        });
        if (!_isClosing && accepted > 0)
            ShowNotification(Texts.T("notification.session"), Texts.T(high ? "priority.highAccepted" : "priority.normalAccepted", accepted),
                NotificationType.Information, context: string.Join(",", frozen), operation: "selection.priority");
    }
}
