using System;

namespace MeowChan.Desktop.MediaActions;

/// <summary>
/// The lifetime of one opened media menu: the target it was opened for and whether that target is still the thing
/// on screen. The surface supplies the guard (navigation, rebuild, selection, thread or entry replacement); an
/// invalidated or no-longer-current session refuses every action instead of redirecting it.
/// </summary>
internal sealed class MediaMenuSession
{
    private readonly Func<bool> _guard;
    private bool _invalidated;

    internal MediaMenuSession(MediaMenuSurface surface, MediaMenuTarget? target, Func<bool> guard)
    {
        Surface = surface;
        Target = target;
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    internal MediaMenuSurface Surface { get; }
    /// <summary>The occurrence frozen when the menu opened; null for a menu without a single target (for example "export all").</summary>
    internal MediaMenuTarget? Target { get; }
    internal bool IsInvalidated => _invalidated;
    /// <summary>True only while the menu may still act: not invalidated and the surface's guard still holds.</summary>
    internal bool IsCurrent => !_invalidated && _guard();
    internal event Action? Invalidated;

    internal void Invalidate()
    {
        if (_invalidated) return;
        _invalidated = true;
        Invalidated?.Invoke();
    }
}
