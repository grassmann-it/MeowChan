using Avalonia.Controls;
using Avalonia.VisualTree;

namespace MeowChan.DesktopSmoke;

internal static class SmokeVisualTree
{
    internal static bool IsAttachedAndEffectivelyVisible(Control? control) => control is not null
        && control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;
}
