using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

// PR3: the "Folders" section (read-only folder sources) below the source list and its details.
public partial class SourcesPage
{
    private FolderSourcesPanel? _folderSources;

    /// <summary>The folder section; null until the shell supplies the host's folder services.</summary>
    internal FolderSourcesPanel? FolderSourcesSection => _folderSources;

    /// <summary>Set once by the shell: builds the folder section and places it after the sources.</summary>
    internal FolderSourcePanelServices? FolderSources
    {
        set
        {
            if (value == null || _folderSources != null) return;
            _folderSources = new FolderSourcesPanel(value);
            if (this.FindControl<Grid>("SourcesSplit") is { Parent: StackPanel outer } split)
                outer.Children.Insert(outer.Children.IndexOf(split) + 1, _folderSources);
        }
    }

    /// <summary>Shows the folder sources again (after a change the host announced).</summary>
    internal void RefreshFolderSources() => _folderSources?.Refresh();
}
