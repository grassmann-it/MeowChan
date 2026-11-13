namespace MeowChan.Desktop;

/// <summary>
/// Where the main window's error banner puts its "Diagnostics" action: beside the message from this width up, below it in the
/// narrowest windows. It does not depend on the window height, so the banner has the same form at 760 and at 1440 dip.
/// </summary>
internal static class ShellFeedbackPlacement
{
    /// <summary>The window width (dip) from which the action sits beside the message.</summary>
    internal const double BesideFromWidth = 640;

    internal static bool ActionBeside(double windowWidth) => windowWidth <= 0 || windowWidth >= BesideFromWidth;
}
