using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// The wheel of the gallery's picture (Config.GalleryWheelMode, audit V10): "Zoom" zooms at the pointer (the default, as before), "Browse"
// moves to the next or previous medium; Ctrl held does the other one (GalleryWheel.Resolve). OnViewWheel (GalleryViewer.ViewTransform.cs)
// asks for the action; this part holds the choice, the whole-step bundling of a touchpad and the "⋯" row that changes the setting.
// It is handler-only: the filmstrip's wheel (strip scroll), the video transport's sliders and the panels never reach it.
internal sealed partial class GalleryViewer
{
    private string _wheelMode = Config.GalleryWheelModeZoom;
    private readonly WheelSteps _browseWheel = new();
    private readonly WheelBrowseQueue _browseQueue = new();
    private readonly ToggleButton _wheelBrowseToggle = new() { Name = "GalleryWheelBrowseToggle" };

    /// <summary>The mouse wheel setting the viewer works with ("Zoom" or "Browse").</summary>
    internal string WheelMode => _wheelMode;

    /// <summary>The "⋯" row "Mouse wheel browses media" (smokes and tests).</summary>
    internal ToggleButton WheelBrowseToggle => _wheelBrowseToggle;

    /// <summary>Raised when the person changes the wheel setting in the "⋯" menu (the shell saves it); not when the shell applies it.</summary>
    internal event Action<string>? WheelModeChanged;

    /// <summary>Applies the setting (called with the other gallery preferences); anything but "Browse" reads as "Zoom".</summary>
    internal void ConfigureWheelMode(string? mode)
    {
        _wheelMode = Config.NormalizeGalleryWheelMode(mode);
        _wheelBrowseToggle.IsChecked = GalleryWheel.IsBrowse(_wheelMode);
    }

    // The menu row sits with the other view choices (after the filmstrip row, before "Full screen").
    private void InitializeWheelMode()
    {
        AddOverflowItem(_wheelBrowseToggle, "gallery.wheel-browse", check: true, keepOpen: true, label: () => Texts.T("gallery.wheelBrowse"));
        _overflowPanel.Children.Remove(_wheelBrowseToggle);
        int anchor = _overflowPanel.Children.IndexOf(_filmstripToggle);
        _overflowPanel.Children.Insert(anchor >= 0 ? anchor + 1 : _overflowPanel.Children.Count, _wheelBrowseToggle);
        string help = Texts.T("gallery.wheelBrowse.help");
        ToolTip.SetTip(_wheelBrowseToggle, help);
        AutomationProperties.SetName(_wheelBrowseToggle, Texts.T("gallery.wheelBrowse"));
        AutomationProperties.SetHelpText(_wheelBrowseToggle, help);
        _wheelBrowseToggle.IsChecked = GalleryWheel.IsBrowse(_wheelMode);
        _wheelBrowseToggle.Click += (_, _) =>
        {
            _wheelMode = _wheelBrowseToggle.IsChecked == true ? Config.GalleryWheelModeBrowse : Config.GalleryWheelModeZoom;
            WheelModeChanged?.Invoke(_wheelMode);
        };
    }

    /// <summary>The action of a wheel turn over the picture with these modifiers (the setting, inverted by Ctrl).</summary>
    private GalleryWheelAction WheelActionFor(KeyModifiers modifiers) => GalleryWheel.Resolve(_wheelMode, modifiers);

    /// <summary>
    /// Browse: a wheel turn goes to the previous (up) or next (down) medium, like the arrow keys; a touchpad's small deltas add up to one step. While the
    /// shell is still moving to the previous one, further turns only add up (WheelBrowseQueue) and go as one move when it has landed, so a spinning wheel
    /// does not start a decode per event.
    /// </summary>
    private void BrowseByWheel(double deltaY)
    {
        DateTime now = DateTime.UtcNow;
        int direction = GalleryWheel.BrowseDelta(_browseWheel.Add(deltaY, now));
        if (direction == 0) return;
        if (_navigationPending) _browseQueue.Add(direction, now);
        else Move(direction);
    }

    // The navigation has landed (BeginSlideshowMedia): the steps collected meanwhile go as one move, after the current show has run through.
    private void ScheduleQueuedBrowse()
    {
        if (_browseQueue.HasSteps) Dispatcher.UIThread.Post(MoveQueuedBrowse, DispatcherPriority.Input);
    }

    /// <summary>Sends the collected wheel steps as one move (the shell clamps it at the ends); nothing while another navigation is pending.</summary>
    internal void MoveQueuedBrowse()
    {
        if (_navigationPending || _terminalRetirement || _viewerInteractionStopped || !_interactionAvailable) return;
        int steps = _browseQueue.Take();
        if (steps != 0) Move(steps);
    }
}
