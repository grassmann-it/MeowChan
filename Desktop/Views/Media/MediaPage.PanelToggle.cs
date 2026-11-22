namespace MeowChan;

// S-4: the media panel's switch as a command (media.panel.toggle, Primary+Shift+M by default, like the reader's media sidebar). The shell's shortcut router
// and the command palette call it; it does what the Layout menu row does (the choice is saved) and, when it turns the panel off from inside, gives the
// keyboard back to the grid like the panel's ×.
public sealed partial class MediaPage
{
    // Keep the panel action in the right-hand view-tools cluster, immediately beside Layout. The panel is a persistent page region now;
    // hiding its only visible switch in Layout made the affordance too far away from the thing it controls.
    private readonly Avalonia.Controls.Primitives.ToggleButton _panelToggle = new() { Name = "MediaPanelToggle" };
    private bool _panelToggleSyncing;
    private MeowChan.Desktop.MediaPanelSide? _panelIconSide;

    /// <summary>The icon key of the switch now: the half of the frame the panel sits in (panel-left or panel-right).</summary>
    internal string PanelToggleIconKey => MeowChan.Desktop.Commands.CommandCatalog.IconForState(MeowChan.Desktop.MediaPanelKeys.ToggleCommandId, _layout.PanelSide == MeowChan.Desktop.MediaPanelSide.Left ? "left" : "right");

    /// <summary>The direct media-sidebar switch in the toolbar (smoke and accessibility seam).</summary>
    internal Avalonia.Controls.Primitives.ToggleButton PanelToggleButton => _panelToggle;

    private void InitializePanelToggle()
    {
        _panelToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_panelToggleSyncing) TogglePanelByShortcut();
        };
    }

    private void SyncPanelToggle()
    {
        _panelToggleSyncing = true;
        try
        {
            _panelToggle.IsChecked = _layout.Panel;
            if (_panelIconSide != _layout.PanelSide)
            {
                _panelIconSide = _layout.PanelSide;
                _mediaLabelContent.SetIcon(MeowChan.Desktop.Commands.CommandBindings.NamedIcon(PanelToggleIconKey));
                _panelToggle.Content ??= _mediaLabelContent;
                ReapplyToolbarLabels();
            }
            else ReapplyToolbarLabels(force: false);
            _panelToggle.IsEnabled = CanTogglePanel;
        }
        finally { _panelToggleSyncing = false; }
    }

    /// <summary>Whether the switch can act now: the page is alive (the panel may still be hidden by a narrow page; the choice is kept then).</summary>
    internal bool CanTogglePanel => !_lifetime.IsCancellationRequested;

    /// <summary>The shortcut and the palette entry: the panel on or off in the layout (saved).</summary>
    internal void TogglePanelByShortcut()
    {
        if (!CanTogglePanel) return;
        if (_layout.Panel) ClosePanel();
        else ApplyLayout(_layout with { Panel = true });
    }
}
