using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// WV3: what the reader bars put under "More" follows WorkViewPolicy.Reader. Items the view reduces (Overflow) are under
/// More; Required items never are; what the width moves is always the tail of ReaderToolbarKeepVisibleOrder (the bar has
/// PrioritizedOverflow), so HTML export outlasts full screen, View, history and post capture.
/// </summary>
internal static class ReaderWorkViewChecks
{
    internal static void RequireOverflowFollowsPolicy(ArchivePage page, ActionToolBar toolbar, Func<string> describe)
    {
        WorkView view = page.ReaderWorkView;
        Control[] items = toolbar.Items.OfType<Control>().ToArray();
        Control[] overflowed = items.Where(item => toolbar.OverflowHost?.Children.Contains(item) == true && item.IsVisible).ToArray();
        string Id(Control item) => WorkViewElement.GetId(item)
            ?? throw new InvalidOperationException(toolbar.Name + "/" + item.Name + " has no work-view id: " + describe());
        Presence PresenceOf(Control item) => WorkViewPolicy.Resolve(Id(item), view);
        foreach (Control item in items.Where(item => item.IsVisible))
        {
            bool under = overflowed.Contains(item);
            if (PresenceOf(item) == Presence.Required && under)
                throw new InvalidOperationException($"{view}: Required {item.Name} moved under More: " + describe());
            if (PresenceOf(item) == Presence.Overflow && !under && toolbar.Name != "ReaderMediaTools")
                throw new InvalidOperationException($"{view}: {item.Name} belongs under More in this view: " + describe());
        }
        // The width race: among the visible items the view keeps on the bar, the moved ones are the lowest-ranked tail.
        Control[] racing = items.Where(item => item.IsVisible && PresenceOf(item) is Presence.Direct)
            .OrderBy(item => WorkViewPolicy.Get(Id(item)).OverflowOrder).ToArray();
        int firstMoved = Array.FindIndex(racing, overflowed.Contains);
        if (firstMoved >= 0 && racing.Skip(firstMoved).Any(item => !overflowed.Contains(item)))
            throw new InvalidOperationException($"{view}: {toolbar.Name} moved a more useful action into More before a less useful one: " + describe());
        if (overflowed.Length > 0 && toolbar.More?.IsEffectivelyVisible != true)
            throw new InvalidOperationException($"{view}: {toolbar.Name} has items under More but no visible More: " + describe());
    }

    /// <summary>Directly visible on the bar, or reachable under the bar's visible "More" (the view or the width moved it).</summary>
    internal static bool OnBarOrUnderMore(ArchivePage page, string name)
    {
        Control control = page.FindControl<Control>(name)!;
        if (control.IsEffectivelyVisible && control.Bounds.Width > 0) return true;
        return control.Parent is ActionToolBar toolbar && toolbar.OverflowHost?.Children.Contains(control) == true
            && toolbar.More?.IsEffectivelyVisible == true;
    }
}
