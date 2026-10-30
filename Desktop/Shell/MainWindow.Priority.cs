using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    partial void QueryUserPriorityAvailable(ref bool available)
        => available = _restoreCompleted && !_isClosing && !_session.Projection.IsStale;

    partial void ApplyFrozenUserPriority(long[] watchIds, bool high) => _ = SetFrozenUserPriorityAsync(watchIds, high);

    private async Task SetFrozenUserPriorityAsync(long[] watchIds, bool high)
    {
        int accepted = 0;
        long[] frozen = watchIds.Distinct().ToArray();
        await RunSelectionCommandAsync(frozen, async id =>
        {
            if (!await _session.SetUserThreadPriorityAsync(id, high))
                throw new InvalidOperationException(Texts.T("priority.stale"));
            accepted++;
        });
        if (!_isClosing && accepted > 0)
            ShowNotification(Texts.T("notification.session"), Texts.T(high ? "priority.highAccepted" : "priority.normalAccepted", accepted),
                NotificationType.Information, context: string.Join(",", frozen), operation: "selection.priority");
    }
}
