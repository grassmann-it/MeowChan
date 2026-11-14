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
        Applied = ids => _settingsEditor?.RefreshConfirmedSettings(ids),
        ShowAllSettings = ShowSettingsAt
    };

    private async Task SaveQuickSettingsAsync(Action<Config> change)
    {
        if (_isClosing) throw new InvalidOperationException("The window is closing.");
        await _session.SaveViewPreferencesAsync(change);
        if (!_isClosing) ApplyGalleryPreferences();
    }

    /// <summary>"All settings" of a popover: the settings page, with the control of the setting <paramref name="fieldId"/> revealed.</summary>
    private void ShowSettingsAt(string? fieldId)
    {
        if (!NavigateWorkspace(WorkspacePage.Settings) || fieldId == null) return;
        // The page takes its own first focus while it is shown; the reveal comes after it.
        Dispatcher.UIThread.Post(() => { if (!_isClosing) _settingsEditor?.RevealSetting(fieldId); }, DispatcherPriority.Loaded);
    }
}
