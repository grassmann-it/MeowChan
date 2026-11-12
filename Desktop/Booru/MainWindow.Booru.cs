using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Commands;

namespace MeowChan;

// Relocated from Desktop/Shell for the B6 adapt slice. Inert until the shared integration calls
// InitializeBooru() from MainWindow.Navigation.cs and registers the "booru.open" command in CommandCatalog.
public partial class MainWindow
{
    private BooruWorkbenchWindow? _booruWindow;
    private MenuItem? _booruMenu;

    private bool CanOpenBooru => !_isClosing && _session.State == DesktopSessionState.Ready && _session.BooruServices != null;

    private void InitializeBooru()
    {
        _booruMenu = CommandBindings.CreateMenuItem("booru.open", ShowBooruWorkbench, () => CanOpenBooru);
        _booruMenu.IsVisible = CanOpenBooru;
        int at = _postBookmarksMenu is null ? -1 : WorkspaceMenuItem.Items.IndexOf(_postBookmarksMenu);
        WorkspaceMenuItem.Items.Insert(at >= 0 ? at + 1 : WorkspaceMenuItem.Items.Count, _booruMenu);
        // The entry exists only while ports are connected; checked when the menu opens.
        WorkspaceMenuItem.SubmenuOpened += (_, _) =>
        {
            _booruMenu.IsVisible = CanOpenBooru;
            CommandBindings.Refresh(_booruMenu);
        };
        Closed += (_, _) => _booruWindow?.Close();
    }

    private void ShowBooruWorkbench()
    {
        if (!CanOpenBooru) return;
        if (_booruWindow is { } open) { open.Activate(); return; }
        BooruWorkbenchServices services = _session.BooruServices!;
        BooruWorkbenchWindow window = new(services);
        _booruWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_booruWindow, window)) _booruWindow = null; };
        window.Show(this);
    }
}
