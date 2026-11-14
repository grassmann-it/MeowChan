using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The "Media folders" group of Storage and limits (read-only folder sources, PR3). It was a section of the Sources page until
// 14.11.2026 (Tom: Sources & Plugins is plain "Plugins" again, the folders live in Settings). The shell supplies the host's
// folder services once; the list is built when the page is first shown (or asked for), so the editor costs nothing at startup.
// The scan and hashing limits are the rows of the group "Media folder limits" below it (SettingsEditor.FolderSources.cs).
public partial class SettingsEditor
{
    private FolderSourcePanelServices? _folderSourceServices;
    private FolderSourcesPanel? _mediaFolders;

    public string MediaFoldersLabel => Texts.T("folderSource.section.title");
    public string MediaFoldersHelp => Texts.T("folderSource.section.help");
    public string MediaFoldersLimitsHint => Texts.T("folderSource.section.limitsHint");

    /// <summary>Set once by the shell: the host's folder services. Without them (the compatibility window) the group offers no list.</summary>
    internal FolderSourcePanelServices? FolderSources
    {
        set
        {
            if (value == null || _folderSourceServices != null) return;
            _folderSourceServices = value;
            if (_selectedCategoryId == "settings.worker-budgets") EnsureMediaFolders();
        }
    }

    /// <summary>The folder list; null until the shell supplies the host's folder services.</summary>
    internal FolderSourcesPanel? FolderSourcesSection
    {
        get { EnsureMediaFolders(); return _mediaFolders; }
    }

    /// <summary>Shows the folder sources again (after a change the host announced).</summary>
    internal void RefreshFolderSources() => _mediaFolders?.Refresh();

    private void EnsureMediaFolders()
    {
        if (_mediaFolders != null || _folderSourceServices == null || _isClosed) return;
        if (this.FindControl<ContentControl>("MediaFoldersHost") is not { } host) return;
        _mediaFolders = new FolderSourcesPanel(_folderSourceServices);
        host.Content = _mediaFolders;
    }
}
