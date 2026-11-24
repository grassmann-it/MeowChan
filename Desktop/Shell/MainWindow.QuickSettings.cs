using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan;

// Quick settings popovers (R7): the shell's side of QuickSettingsHost. A change from a popover takes the path every direct
// preference takes (the view-preferences write: merged into the confirmed configuration, coalesced, persisted, retried), is
// applied to the reader and gallery at once, and the settings page, when it exists, shows the confirmed value in that editor.
public partial class MainWindow
{
    private QuickSettingsHost CreateQuickSettingsHost() => new()
    {
        CurrentConfig = () => _config,
        SaveAsync = SaveQuickSettingsAsync,
        // A change saved while closing (S4 F3) refreshes nothing: the settings page has ended its lifetime.
        Applied = ids => { if (!_isClosing) _settingsEditor?.RefreshConfirmedSettings(ids); },
        ShowAllSettings = ShowSettingsAt
    };

    // S4 F3: a change still waiting when the app quits (the popover flushes when it closes, CloseApplicationAsync flushes both reader
    // appliers) is saved while closing too: the session takes view-preference writes until it seals its final writes (session-stop),
    // which comes after the final preferences were awaited. Only applying it to the closing gallery is left out. After the seal the
    // session itself refuses the write, and the applier reports and drops it as any failed save.
    private async Task SaveQuickSettingsAsync(Action<Config> change)
    {
        await _session.SaveViewPreferencesAsync(change);
        if (_isClosing) return;
        ApplyGalleryPreferences();
        _mediaPage?.ConfigurePanelInfo(ReaderMediaPreferences.From(_config));
    }

    /// <summary>
    /// "Link folder…" of the empty Media page: the settings page at the media folders group (Storage and limits), with the dialog of "Add folder…"
    /// opened. The folder flow itself is the group's own; this only takes the person there and starts it.
    /// </summary>
    private void ShowLinkFolderFlow()
    {
        if (!NavigateWorkspace(WorkspacePage.Settings)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosing || _settingsEditor is not { } editor) return;
            editor.RevealSetting(SettingsFieldId.MediaFolders);
            editor.BeginAddFolderSource();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>"All settings" of a popover: the settings page, with the control of the setting <paramref name="fieldId"/> revealed.</summary>
    private void ShowSettingsAt(string? fieldId)
    {
        if (!NavigateWorkspace(WorkspacePage.Settings) || fieldId == null) return;
        // The page takes its own first focus while it is shown; the reveal comes after it.
        Dispatcher.UIThread.Post(() => { if (!_isClosing) _settingsEditor?.RevealSetting(fieldId); }, DispatcherPriority.Loaded);
    }
}
