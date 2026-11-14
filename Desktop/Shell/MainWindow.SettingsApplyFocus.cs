using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MeowChan;

// A settings save gates the workspace while it applies (UpdateInteractionState: not ready), and Avalonia takes the keyboard focus
// away from a control that becomes disabled. The open reader (large reader window included) lost its focused toolbar button on
// every save, although the live apply keeps the reader's place. The focus of each window comes back once the controls are
// enabled again, unless something else took it meanwhile.
public partial class MainWindow
{
    private readonly record struct HeldFocus(TopLevel TopLevel, Control Element);

    private List<HeldFocus> HoldFocusForSettingsApply()
    {
        List<HeldFocus> held = new();
        IEnumerable<TopLevel> tops = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            ?? (IEnumerable<TopLevel>)new[] { this };
        foreach (TopLevel top in tops.Append(this).Distinct())
            if (top.FocusManager?.GetFocusedElement() is Control element) held.Add(new(top, element));
        return held;
    }

    private static void RestoreFocusAfterSettingsApply(List<HeldFocus> held)
    {
        foreach (HeldFocus entry in held)
        {
            Control element = entry.Element;
            if (ShouldRestoreFocus(element.IsAttachedToVisualTree() && ReferenceEquals(TopLevel.GetTopLevel(element), entry.TopLevel),
                    element.IsEffectivelyEnabled, element.IsEffectivelyVisible, entry.TopLevel.FocusManager?.GetFocusedElement() != null))
                element.Focus();
        }
    }

    /// <summary>The held focus returns only to a control still in its window, enabled and shown, while that window has no focus.</summary>
    internal static bool ShouldRestoreFocus(bool inSameWindow, bool enabled, bool visible, bool windowHasFocus) =>
        inSameWindow && enabled && visible && !windowHasFocus;
}
