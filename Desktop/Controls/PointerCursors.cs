using System;
using Avalonia.Input;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The hand cursor of a click target that is no button (a file name, a picture, a badge), made once and only when there is a platform to make it
/// (a view built in a test without one simply has no cursor and is still complete).
/// </summary>
internal static class PointerCursors
{
    private static Cursor? s_hand;

    /// <summary>The hand cursor, or null while no platform can make it.</summary>
    internal static Cursor? Hand
    {
        get
        {
            if (s_hand != null) return s_hand;
            try { return s_hand = new Cursor(StandardCursorType.Hand); }
            catch (Exception error) when (error is InvalidOperationException or NullReferenceException) { return null; }
        }
    }
}
