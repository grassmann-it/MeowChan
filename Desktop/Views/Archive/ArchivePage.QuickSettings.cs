using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan;

// The reader's quick settings popover (R7): "Reader settings…" in the View menu opens the display options readers reach for
// most often under the View button. The fields are those of ReaderQuickSettings (the same settings as the settings page); the
// shell supplies how a change is saved (QuickSettingsHost). The popover and the menu entry exist from the first use on.
public partial class ArchivePage
{
    private QuickSettingsPopover? _readerQuickSettings;

    /// <summary>How the popover reads and saves the settings; set by the shell. Without it the entry does nothing.</summary>
    internal QuickSettingsHost? QuickSettingsHost { get; set; }

    internal bool IsQuickSettingsOpen => _readerQuickSettings?.IsOpen == true;
    internal QuickSettingsPopover? ReaderQuickSettings => _readerQuickSettings;

    /// <summary>Opens (or, when open, closes) the reader's quick settings under the View button; under "More" when the button is folded away.</summary>
    internal void OpenReaderQuickSettings()
    {
        if (_ended || IsReaderDialogOpen || QuickSettingsHost is not { } host) return;
        if (_readerQuickSettings is { IsOpen: true } open) { open.Hide(); return; }
        _readerQuickSettings ??= new QuickSettingsPopover(QuickSettingsSurfaces.Get(QuickSettingsSurfaces.Reader), host, "ReaderQuickSettings");
        _readerQuickSettings.Show(QuickSettingsAnchor());
    }

    // The View button, or what stands in for it when the toolbar folded it into "More" (its parent is then the closed overflow).
    private Avalonia.Controls.Control QuickSettingsAnchor() =>
        ReaderViewOpen.IsEffectivelyVisible ? ReaderViewOpen
        : ReaderToolbar.More is { IsEffectivelyVisible: true } more ? more
        : ReaderToolbar;

    private void CloseQuickSettings()
    {
        _readerQuickSettings?.Dispose();
        _readerQuickSettings = null;
    }
}
