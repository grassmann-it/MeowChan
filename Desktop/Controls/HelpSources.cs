using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// HB2: help popovers whose lines come from texts the application already has, so the popover cannot drift from them. The work-view
/// popover lists the three views with the one-line description each of them has in the F1 help and the view menu.
/// </summary>
internal static class HelpSources
{
    /// <summary>The work views: a note, then one line per view (<c>mode.*</c> name and <c>workview.description.*</c> text).</summary>
    internal static HelpContent WorkViews() => new(
        Texts.T("help.workviews.title"),
        [Texts.T("help.workviews.p1")],
        [
            Texts.T("help.workviews.item", Texts.T("mode.barebones"), Texts.T("workview.description.barebones")),
            Texts.T("help.workviews.item", Texts.T("mode.simple"), Texts.T("workview.description.simple")),
            Texts.T("help.workviews.item", Texts.T("mode.advanced"), Texts.T("workview.description.advanced")),
        ]);
}
