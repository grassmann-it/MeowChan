using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>Keyboard hints of the video bar: the seek step keys ("," and ".") and the repeat key (L) in the tooltips.</summary>
internal sealed partial class GalleryVideoPlayer
{
    private int _seekStepSeconds = Config.GalleryVideoSeekStepSecondsDefault;

    /// <summary>Whether a running slideshow decides about repeating (the repeat key then changes nothing).</summary>
    internal bool RepeatOwnedBySlideshow => _repeatOwnedBySlideshow;

    /// <summary>Why the slideshow owns the repeat (the tooltip text of the repeat button), or null.</summary>
    internal string? RepeatOwnedReason => _repeatOwnedBySlideshow ? _repeatOwnedReason : null;

    /// <summary>The seek step in seconds shown in the position tooltip (the viewer applies the keys themselves).</summary>
    internal void ConfigureSeekStep(int seconds)
    {
        seconds = VideoKeyboardSteps.ClampSeconds(seconds);
        if (_seekStepSeconds == seconds) return;
        _seekStepSeconds = seconds;
        RefreshTransportPresentation();
    }

    /// <summary>Position tooltip: the name, then the step keys with their seconds and the Ctrl-arrow seeks (only bound keys are named).</summary>
    private string SeekTooltip()
    {
        string name = Texts.T("viewer.video.position");
        string? back = CommandCatalog.ShortcutTooltipDisplay("video.step-back");
        string? forward = CommandCatalog.ShortcutTooltipDisplay("video.step-forward");
        string steps = back != null && forward != null ? name + " · " + Texts.T("viewer.video.seekKeys", back, forward, _seekStepSeconds)
            : back != null ? name + " · " + Texts.T("viewer.video.seekKeyBack", back, _seekStepSeconds)
            : forward != null ? name + " · " + Texts.T("viewer.video.seekKeyForward", forward, _seekStepSeconds) : name;
        return ShortcutHelp.Append(steps, ShortcutHelp.Commands("gallery",
            "video.seek-back", "video.seek-forward", "video.seek-back-far", "video.seek-forward-far"));
    }

    private void RefreshSeekTooltip()
    {
        string tip = SeekTooltip();
        AutomationProperties.SetName(_seek, Texts.T("viewer.video.position"));
        AutomationProperties.SetHelpText(_seek, tip);
        ToolTip.SetTip(_seek, tip);
    }
}
