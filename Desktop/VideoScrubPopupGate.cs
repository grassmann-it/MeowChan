namespace MeowChan.Desktop;

/// <summary>
/// The window facts that decide whether a seek-bar hover popup may exist. The popup is a separate top-level
/// window on desktop platforms, so it must not outlive its owner's visibility or activation.
/// </summary>
/// <param name="Attached">The timeline control is part of a visual tree.</param>
/// <param name="Active">At least one owning window is the active window of the app.</param>
/// <param name="Minimized">An owning window is minimised.</param>
/// <param name="Visible">Every owning window is shown (not hidden to the tray).</param>
internal readonly record struct VideoScrubEnvironment(bool Attached, bool Active, bool Minimized, bool Visible)
{
    /// <summary>Nothing is known yet: the popup may not show.</summary>
    internal static VideoScrubEnvironment Unknown => default;

    /// <summary>A normal, shown, active window with the timeline attached.</summary>
    internal static VideoScrubEnvironment Normal => new(true, true, false, true);

    internal bool IsUsable => Attached && Active && !Minimized && Visible;
}

/// <summary>
/// Pure state machine for the timeline hover preview popup (no Avalonia types). The popup may be open only while
/// <see cref="MayShow"/> is true. Every environment loss (deactivation, minimise, hidden window, detach), Escape and
/// retirement hide it, and it stays hidden until the pointer is over the timeline again: a fresh pointer-enter, or
/// the first pointer move after the window is usable again. Escape additionally waits for the pointer to leave and
/// re-enter, so a key press is not undone by the next mouse jiggle.
/// </summary>
internal sealed class VideoScrubPopupGate
{
    private VideoScrubEnvironment _environment = VideoScrubEnvironment.Unknown;
    private bool _pointerOver;
    private bool _escapeLatched;
    private bool _resumeLatched;
    private bool _retired;

    internal VideoScrubEnvironment Environment => _environment;

    /// <summary>Whether the preview popup may currently be open.</summary>
    internal bool MayShow => !_retired && _environment.IsUsable && _pointerOver && !_escapeLatched && !_resumeLatched;

    /// <summary>
    /// Applies fresh window facts. Losing usability forgets the pointer and demands fresh pointer evidence.
    /// Returns <see cref="MayShow"/>.
    /// </summary>
    internal bool ApplyEnvironment(VideoScrubEnvironment environment)
    {
        bool wasUsable = _environment.IsUsable;
        _environment = environment;
        if (wasUsable && !environment.IsUsable)
        {
            _pointerOver = false;
            _resumeLatched = true;
        }
        return MayShow;
    }

    /// <summary>The pointer entered the timeline. Clears every latch.</summary>
    internal bool OnPointerEntered()
    {
        _pointerOver = true;
        _escapeLatched = false;
        _resumeLatched = false;
        return MayShow;
    }

    /// <summary>
    /// The pointer moved over the timeline. Moves that arrive while the window is unusable never count; the first
    /// move after it became usable again lifts the resume latch (but never the Escape latch).
    /// </summary>
    internal bool OnPointerMoved()
    {
        if (!_environment.IsUsable) return false;
        _resumeLatched = false;
        _pointerOver = true;
        return MayShow;
    }

    /// <summary>The pointer left the timeline. Clears the Escape latch.</summary>
    internal void OnPointerExited()
    {
        _pointerOver = false;
        _escapeLatched = false;
    }

    /// <summary>Escape was pressed: hide until the pointer leaves and enters again.</summary>
    internal void OnEscape() => _escapeLatched = true;

    /// <summary>The medium changed or its session was replaced: the pointer must be seen over the timeline again.</summary>
    internal void OnMediaChanged() => _pointerOver = false;

    /// <summary>The gallery/player is closed for good: the popup never shows again.</summary>
    internal void Retire()
    {
        _retired = true;
        _pointerOver = false;
    }
}
