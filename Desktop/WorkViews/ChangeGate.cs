using System;

namespace MeowChan.Desktop.WorkViews;

/// <summary>
/// Remembers the last value a presentation was applied for. <see cref="Changed"/> is true the first time and whenever the
/// value differs, so a page that is asked to re-apply on every state change does the work only on a real change.
/// </summary>
internal struct ChangeGate<T> where T : IEquatable<T>
{
    private bool _has;
    private T _last;

    /// <summary>Records <paramref name="value"/>; true when it is the first one or differs from the previous one.</summary>
    public bool Changed(T value)
    {
        if (_has && _last.Equals(value)) return false;
        _has = true;
        _last = value;
        return true;
    }

    /// <summary>Forgets the value: the next <see cref="Changed"/> reports a change.</summary>
    public void Reset() { _has = false; _last = default!; }
}
