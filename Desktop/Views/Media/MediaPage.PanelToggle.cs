namespace MeowChan;

// S-4: the media panel's switch as a command (media.panel.toggle, Primary+Shift+M by default, like the reader's media sidebar). The shell's shortcut router
// and the command palette call it; it does what the Layout menu row does (the choice is saved) and, when it turns the panel off from inside, gives the
// keyboard back to the grid like the panel's ×.
public sealed partial class MediaPage
{
    /// <summary>Whether the switch can act now: the page is alive (the panel may still be hidden by a narrow page; the choice is kept then).</summary>
    internal bool CanTogglePanel => !_lifetime.IsCancellationRequested;

    /// <summary>The shortcut and the palette entry: the panel on or off in the layout (saved).</summary>
    internal void TogglePanelByShortcut()
    {
        if (!CanTogglePanel) return;
        if (_layout.Panel) ClosePanel();
        else ApplyLayout(_layout with { Panel = true });
    }
}
