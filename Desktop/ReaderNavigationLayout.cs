using System;

namespace MeowChan.Desktop;

/// <summary>
/// The navigation bar (thread arrows with the thread counter, post arrows with the post counter) is a pure overlay floating at
/// the lower right of the reader surface. Content (post list, media tiles and table, references) uses the whole width up to the
/// right edge and the whole height: there is no reserved lane and no extra scroll extent. In the "Auto" mode the bar hides itself
/// while the pointer is idle (like a video player's controls), so it never sits over the last post while nobody moves the mouse;
/// it is shown again by any pointer movement over the reader, while the pointer is over or near it, while it has keyboard focus
/// and briefly after a navigation shortcut. "Always" keeps it shown, "Off" removes it (the keys still work).
/// </summary>
internal static class ReaderNavigationLayout
{
    // The capsule: 40 dip high (border 1 + inner padding 3 + 32 dip button + 3 + 1), never wider than 320 dip. A counter is at least
    // 56 dip wide (fixed width for up to 5 digits at the Meta text size). MaxWidth and MinWidth are literals in ArchivePage.axaml; a test pins them.
    internal const double BarHeight = 40, BarMaxWidth = 320, CounterMinWidth = 56;
    // Below this reader surface width the counters are hidden and the bar shrinks to the four arrows (a layout break, not a user limit).
    internal const double CompactBelowSurfaceWidth = 700;
    // Distance of the bar from the right edge of the reader surface (it floats inside the list's scrollbar edge).
    internal const double RightInset = 24;
    // The pointer counts as near the bar inside its rectangle grown by this much on every side.
    internal const double NearMargin = 64;
    // Idle time without pointer movement before the bar fades out: the "Hide controls after" setting
    // (Config.ControlsIdleHideMs), shared with the gallery's overlay controls.
    internal const int IdleHideDelayMsDefault = Config.ControlsIdleHideMsDefault, IdleHideDelayMsMinimum = Config.ControlsIdleHideMsMinimum,
        IdleHideDelayMsMaximum = Config.ControlsIdleHideMsMaximum;
    // Post width "fit to content": a card is as wide as its content needs, up to this reading width. The same number
    // is the MaxWidth setter of the "ListBox.reader-fit-width ListBoxItem" style in ArchivePage.axaml (a test pins both).
    internal const double CardMaxWidth = 1120;
    // Conversation indentation is local to a card. Keeping it below 40% of the readable width prevents the
    // deepest tree from consuming the text column or the navigation lane at 900/1440 DIP.
    internal const double ConversationIndentStep = 20;
    internal const double ConversationIndentMaximumShare = 0.4;

    internal static double ConversationIndent(int depth, double availableWidth, double cardMaxWidth = CardMaxWidth)
    {
        int level = Math.Clamp(depth, 0, 16);
        if (!double.IsFinite(cardMaxWidth) || cardMaxWidth <= 0) cardMaxWidth = CardMaxWidth;
        double readable = double.IsFinite(availableWidth) && availableWidth > 0 ? availableWidth : cardMaxWidth;
        readable = Math.Min(readable, cardMaxWidth);
        return Math.Min(level * ConversationIndentStep, readable * ConversationIndentMaximumShare);
    }

    /// <summary>The compact form (arrows only) applies in a narrow reader or when the position display is switched off.</summary>
    internal static bool IsCompact(double surfaceWidth, bool showPosition) => !showPosition || surfaceWidth < CompactBelowSurfaceWidth;

    /// <summary>"12 / 405" for the zero-based <paramref name="index"/> within <paramref name="count"/>; a position that is not in the list
    /// (index below 0, or no entries) reads "–". Digits only, so the text is the same in every language.</summary>
    internal static string CounterText(int index, int count) => index < 0 || count <= 0 || index >= count
        ? "\u2013"
        : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " / " + count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static int ClampIdleHideDelayMs(int milliseconds) => Math.Clamp(milliseconds, IdleHideDelayMsMinimum, IdleHideDelayMsMaximum);
}
