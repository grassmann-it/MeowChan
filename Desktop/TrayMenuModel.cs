using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>Texts and icon of the tray entries that follow the session state (pause) as a plain model.</summary>
internal static class TrayMenuModel
{
    internal const string BaseTooltip = "MeowChan - Image Downloader";

    /// <summary>The entry names the action it will perform, not the current state.</summary>
    internal static string PauseHeader(bool paused, string? shortcut = null)
    {
        string label = Texts.T(paused ? "action.resume" : "action.pause");
        return string.IsNullOrEmpty(shortcut) ? label : label + "  " + shortcut;
    }

    internal static string PauseIcon(bool paused) => CommandCatalog.IconForState("session.pause", paused ? "paused" : "running");

    /// <summary>The tooltip says whether MeowChan is paused or how many downloads are running; otherwise it keeps its name.</summary>
    internal static string Tooltip(bool paused, int activeDownloads) =>
        paused ? "MeowChan · " + Texts.T("tray.tooltipPaused")
        : activeDownloads > 0 ? "MeowChan · " + Texts.T("tray.tooltipActive", activeDownloads)
        : BaseTooltip;

    internal static bool PauseEnabled(bool restoreCompleted, bool editorBusy, bool pauseTransitioning) =>
        restoreCompleted && !editorBusy && !pauseTransitioning;
}
