using System;
using Avalonia.Controls.Notifications;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    // A recovered UI-thread failure: the last action may be incomplete, so it is always shown and kept
    // in the session feedback. The full stack trace is in the log file only.
    internal void ShowUnexpectedError(Exception error) =>
        ShowNotification(Texts.T("notification.error"), Texts.T("error.unexpected", error.Message),
            NotificationType.Error, operation: "app.unhandled");
}
