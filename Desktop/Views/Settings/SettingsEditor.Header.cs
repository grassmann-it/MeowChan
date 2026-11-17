namespace MeowChan;

public partial class SettingsEditor
{
    // Resize can move the content column without changing the field's size (notably at the category breakpoint).
    // Translate the field after arrangement, when its new position in the shared content column is final.
    private void PositionSearchPanelLater() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(PositionSearchPanel, Avalonia.Threading.DispatcherPriority.Loaded);
}
