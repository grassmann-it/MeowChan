using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

/// <summary>
/// Where keyboard focus goes when the large reader closes (O2-01). The control that opened it is often no good target: the list itself (Enter), a
/// tapped row or a cell of it (double-click) is not focusable, or was recycled while the reader was open, and focus was left on nothing. The
/// list's selected row is what the user left; a DataGrid takes focus itself.
/// </summary>
internal static class ReaderOriginFocus
{
    /// <summary>The control to focus for <paramref name="origin"/>: its list's selected row, its grid, the origin itself when it takes focus, else <paramref name="fallback"/> (resolved the same way).</summary>
    internal static Control? Resolve(Control? origin, Control? fallback = null) => ResolveOne(origin) ?? ResolveOne(fallback);

    private static Control? ResolveOne(Control? control)
    {
        if (control == null) return null;
        Control? host = control is ListBox or DataGrid ? control : null;
        if (host == null && control.IsAttachedToVisualTree())
            host = (Control?)control.FindAncestorOfType<ListBox>() ?? control.FindAncestorOfType<DataGrid>();
        if (host is DataGrid grid) return Usable(grid) ? grid : null;
        if (host is ListBox list)
        {
            if (list.SelectedIndex >= 0 && list.ContainerFromIndex(list.SelectedIndex) is Control row && Usable(row)) return row;
            return Usable(list) ? list : null;
        }
        return Usable(control) ? control : null;
    }

    private static bool Usable(Control control) => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.IsAttachedToVisualTree();
}
