namespace MeowChan;

public partial class MainWindow
{
    // Declared by Shell/MainWindow.TrayStats.cs (P4, tray text). A click on a statistics line of the tray menu restores the
    // window, also from the tray or a minimized state, and opens the statistics page.
    partial void ShowStatisticsFromTray()
    {
        if (_isClosing) return;
        RestoreWindow();
        ShowStatistics();
    }
}
