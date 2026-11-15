using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// RL1: the reader's settings popover (the gear) as the smokes meet it. The former View menu is now the popover's session rows, so
// the checks that read the menu open the popover instead. A page without a shell gets a stand-in host (a private Config, nothing saved).
internal static class ReaderSettingsProbe
{
    internal static QuickSettingsHost StandIn(Config? config = null)
    {
        Config current = config ?? new Config();
        return new QuickSettingsHost
        {
            CurrentConfig = () => current,
            SaveAsync = change => { change(current); return Task.CompletedTask; },
            ShowAllSettings = _ => { }
        };
    }

    /// <summary>Opens the popover under the gear (or keeps it open) and waits until it has its size.</summary>
    internal static async Task<QuickSettingsSurface> OpenAsync(ArchivePage page)
    {
        page.QuickSettingsHost ??= StandIn();
        if (!page.IsQuickSettingsOpen) page.OpenReaderQuickSettings();
        await Program.WaitUntilAsync(() => page.IsQuickSettingsOpen && page.ReaderQuickSettings!.Surface.Bounds.Width > 0,
            () => "the reader settings popover did not open");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        QuickSettingsSurface surface = page.ReaderQuickSettings!.Surface;
        surface.RefreshSession();
        return surface;
    }

    internal static async Task CloseAsync(ArchivePage page)
    {
        if (!page.IsQuickSettingsOpen) return;
        page.ReaderQuickSettings!.Hide();
        await Program.WaitUntilAsync(() => !page.IsQuickSettingsOpen, () => "the reader settings popover did not close");
    }

    /// <summary>The switch of a session row (view option), or the button of an action row.</summary>
    internal static Control Row(QuickSettingsSurface surface, string commandId) =>
        surface.SessionControlOf(commandId) ?? throw new InvalidOperationException("The popover has no row for " + commandId + "; it has " + string.Join(", ", surface.SessionCommandIds));

    internal static CheckBox Switch(QuickSettingsSurface surface, string commandId) =>
        Row(surface, commandId) as CheckBox ?? throw new InvalidOperationException(commandId + " is not a switch row.");

    /// <summary>Opens the popover, reads one session switch, closes it again.</summary>
    internal static async Task<CheckBox> SwitchAsync(ArchivePage page, string commandId)
    {
        QuickSettingsSurface surface = await OpenAsync(page);
        return Switch(surface, commandId);
    }
}
