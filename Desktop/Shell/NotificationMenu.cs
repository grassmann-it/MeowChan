using System;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The context menu of a notification (a toast or the shell banner): copy its text, dismiss it (what its close button does), close all toasts.
/// Pure composition; the window supplies what each entry does.
/// </summary>
internal static class NotificationMenu
{
    internal const string Copy = "feedback.copy-text";
    internal const string Dismiss = "feedback.dismiss";
    internal const string DismissAll = "feedback.dismiss-all";

    internal static ContextMenuBuilder Compose(string title, Action copyText, Action dismiss, Action dismissAll)
    {
        ArgumentNullException.ThrowIfNull(copyText);
        ArgumentNullException.ThrowIfNull(dismiss);
        ArgumentNullException.ThrowIfNull(dismissAll);
        string name = Texts.T("ctxmenu.name.notification");
        return new ContextMenuBuilder().Named(string.IsNullOrWhiteSpace(title) ? name : name + ": " + title)
            .Command(Copy, copyText)
            .Section().Command(Dismiss, dismiss).Command(DismissAll, dismissAll);
    }
}
