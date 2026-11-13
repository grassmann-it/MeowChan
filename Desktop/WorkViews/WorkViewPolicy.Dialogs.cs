using System.Collections.Generic;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.Desktop.WorkViews;

// Plan AP5 §1.14: dialogs, tray and the menu bars. Owned by WV9.
// Principle: a dialog follows an explicit action, so it is not reduced. The only two exceptions fold an extras section:
// the board admission's text rules and the export dialog's update-and-backup options. Everything that protects the
// user (confirmations, consent, approvals, previews with their reasons, the HTML safety notes) is Required everywhere,
// and the tray and the macOS menu bar carry no view dependence at all.
public static partial class WorkViewPolicy
{
    private static IEnumerable<WorkViewEntry> DialogsEntries() =>
    [
        // Board admission: board list, preview counts and Apply/Cancel stay; the text rules fold in Barebones (R6: rules in
        // use, or a preview that decided otherwise than the defaults, open the section).
        new("dialogs.board-admission.text-rules", WorkViewPage.Dialogs, WorkViewKind.Section, Collapsed, Direct, Direct),
        Same("dialogs.board-admission.preview", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        // Export (AP7): format, scope and target stay direct; updating a previous export and its backup options fold
        // in Barebones and Simple and open in Advanced (R6: an update in progress opens them).
        new("dialogs.export.options", WorkViewPage.Dialogs, WorkViewKind.Section, Collapsed, Collapsed, Direct),
        Same("dialogs.export.html-safety", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        // Dialogs that follow an explicit action or guard data, consent and credentials are never reduced (R3/R5).
        Same("dialogs.first-run-setup", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.delete-confirmation", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.archive-cleanup", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.archive-deletion", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.experimental-activation", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.plugin-approval", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.sdk-transition", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.media-conflict", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.shortcut-help", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, "viewer.shortcut-help"),
        // Managers opened on purpose (ratings, tags, reader groups, saved searches, bookmarks), import and transfer.
        Same("dialogs.managers", WorkViewPage.Dialogs, WorkViewKind.Derived, Required),
        // The tray menu and the macOS menu bar are never reduced (R3); a test keeps them free of the view.
        Same("dialogs.tray-menu", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
        Same("dialogs.mac-menu-bar", WorkViewPage.Dialogs, WorkViewKind.Derived, Required, safety: true),
    ];
}
