using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal sealed class TaggingQueueNextMarker : Border
{
    internal TaggingQueueNextMarker()
    {
        IsVisible = false;
        Margin = new Thickness(0, 0, 4, 0);
        VerticalAlignment = VerticalAlignment.Center;
        // The containing row or tile already announces the continuation.
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    internal void Show(bool next)
    {
        IsVisible = next;
        if (next) Child ??= CommandBindings.IconFromKey("chevron-right", 12);
        ToolTip.SetTip(this, next ? Texts.T("tagging.page.next") : null);
    }
}
