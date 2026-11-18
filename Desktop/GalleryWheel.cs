using System;
using Avalonia.Input;
using MeowChan.Desktop.Controls;

namespace MeowChan.Desktop;

/// <summary>What a turn of the mouse wheel over the gallery's picture or video does.</summary>
internal enum GalleryWheelAction
{
    /// <summary>Zoom at the pointer.</summary>
    Zoom,
    /// <summary>Move to the next or previous medium.</summary>
    Browse
}

/// <summary>
/// The wheel of the gallery's picture: the setting <see cref="Config.GalleryWheelMode"/> says what the plain wheel does (zoom, the
/// long-standing behaviour, or browse), Ctrl (⌘ on macOS) held does the other one. Only the picture and the video surface use this;
/// the filmstrip (it scrolls its strip), the video controls (volume, position) and the panels keep their own wheel.
/// </summary>
internal static class GalleryWheel
{
    /// <summary>Whether the stored mode is "Browse"; anything else (including a missing value) is the default, zoom.</summary>
    internal static bool IsBrowse(string? mode) => Config.NormalizeGalleryWheelMode(mode) == Config.GalleryWheelModeBrowse;

    /// <summary>The action of a wheel turn made with <paramref name="modifiers"/> while the setting is <paramref name="mode"/>: Ctrl inverts the mode.</summary>
    internal static GalleryWheelAction Resolve(string? mode, KeyModifiers modifiers)
    {
        bool browse = IsBrowse(mode);
        if (ItemActivation.IsZoomWheel(modifiers)) browse = !browse;
        return browse ? GalleryWheelAction.Browse : GalleryWheelAction.Zoom;
    }

    /// <summary>
    /// The step to the neighbouring medium for whole wheel steps (positive: wheel up, away from the user): up looks back (-1), down
    /// goes on (+1), the same direction as the filmstrip's wheel. Several steps of one event still move by one medium, as a key does.
    /// </summary>
    internal static int BrowseDelta(int wheelSteps) => wheelSteps > 0 ? -1 : wheelSteps < 0 ? 1 : 0;
}

/// <summary>
/// The wheel steps that arrive while the shell is still moving to the previous one (Browse mode): a spinning wheel raised one move per event and
/// each started its own decode. They add up here, and the viewer sends the net sum as one move when the navigation has landed, so the latest
/// target wins and one navigation is in flight at a time. A turn the other way drops what was collected; so does a pause (<see cref="WheelSteps.Pause"/>).
/// </summary>
internal sealed class WheelBrowseQueue
{
    private int _steps;
    private DateTime _last = DateTime.MinValue;

    /// <summary>Whether steps wait for the navigation to land.</summary>
    internal bool HasSteps => _steps != 0;

    /// <summary>Adds one step in <paramref name="direction"/> (-1 back, +1 on), at <paramref name="now"/>.</summary>
    internal void Add(int direction, DateTime now)
    {
        if (direction == 0) return;
        if (now - _last > WheelSteps.Pause || Math.Sign(_steps) != Math.Sign(direction)) _steps = 0;
        _last = now;
        _steps = (int)Math.Clamp((long)_steps + Math.Sign(direction), int.MinValue / 2, int.MaxValue / 2);
    }

    /// <summary>The net steps (negative: back); the queue is empty afterwards.</summary>
    internal int Take()
    {
        int steps = _steps;
        _steps = 0;
        return steps;
    }

    internal void Clear() => _steps = 0;
}
