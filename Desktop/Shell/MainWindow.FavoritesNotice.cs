using Avalonia.Controls.Notifications;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    // The favorites file could not be read at startup: one error notice for the whole session (the host hands the sentence out once),
    // never one per star or tile. The stars show their own "unavailable" state; the file itself is left exactly as it is.
    private void NotifyFavoritesUnreadable()
    {
        if (_isClosing || _session.TakeFavoritesUnreadableNotice() is not { } notice) return;
        ShowNotification(Texts.T("notification.error"), notice, NotificationType.Error, operation: "favorites.unreadable");
    }
}
