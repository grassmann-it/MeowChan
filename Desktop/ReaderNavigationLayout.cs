using System;

namespace MeowChan.Desktop;

/// <summary>
/// The arrow cross is a pure overlay floating at the lower right of the reader surface. Content (post list, media tiles and
/// table, references) uses the whole width up to the right edge and the whole height: there is no reserved lane and no extra
/// scroll extent. Instead the cross hides itself while the pointer is idle (like a video player's controls), so it never sits
/// over the last post while nobody moves the mouse; it is shown again by any pointer movement over the reader, while the
/// pointer is over or near it, while it has keyboard focus and briefly after a navigation shortcut.
/// </summary>
internal static class ReaderNavigationLayout
{
    internal const double NavigationSize = 108;
    // Distance of the cross from the right edge of the reader surface (it floats inside the list's scrollbar edge).
    internal const double RightInset = 24;
    // The pointer counts as near the cross inside its rectangle grown by this much on every side.
    internal const double NearMargin = 64;
    // Idle time without pointer movement before the cross fades out.
    internal const int IdleHideDelayMsDefault = 2000, IdleHideDelayMsMinimum = 500, IdleHideDelayMsMaximum = 30_000;
    // Post width "fit to content": a card is as wide as its content needs, up to this reading width. The same number
    // is the MaxWidth setter of the "ListBox.reader-fit-width ListBoxItem" style in ArchivePage.axaml (a test pins both).
    internal const double CardMaxWidth = 1120;

    internal static int ClampIdleHideDelayMs(int milliseconds) => Math.Clamp(milliseconds, IdleHideDelayMsMinimum, IdleHideDelayMsMaximum);
}
