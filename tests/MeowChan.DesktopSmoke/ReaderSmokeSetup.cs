using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

// The reader defaults changed (media sidebar on, one choice "Clicking a medium" that Enter on a post follows). Checks that assume the old
// behavior (Enter opens the gallery, the old post widths) pin the choice or the sidebar in their own setup and restore it afterwards.
internal static class ReaderSmokeSetup
{
    /// <summary>
    /// Applies the click choice (built with <see cref="Config.ChooseReaderMediaClick"/>, so the legacy flag and the effective mode agree) and/or
    /// the sidebar switch to the page, and returns the previous media preferences for <see cref="Restore"/>.
    /// </summary>
    internal static ReaderMediaPreferences Choose(ArchivePage page, ReaderMediaClickMode? click = null, bool? sidebar = null)
    {
        ReaderMediaPreferences before = page.ReaderMedia;
        ReaderMediaPreferences next = before;
        if (click is { } mode)
        {
            Config scratch = new();
            scratch.ChooseReaderMediaClick(mode);
            ReaderInlineVideoOptions chosen = ReaderInlineVideoOptions.From(scratch);
            next = next with { Video = next.Video with { Enabled = chosen.Enabled, InPost = chosen.InPost } };
        }
        if (sidebar is { } shown) next = next with { Sidebar = shown };
        page.ApplyReaderMediaPreferences(next);
        return before;
    }

    internal static void Restore(ArchivePage page, ReaderMediaPreferences before) => page.ApplyReaderMediaPreferences(before);
}
