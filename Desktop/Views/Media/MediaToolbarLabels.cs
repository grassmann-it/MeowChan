using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace MeowChan;

internal static class MediaToolbarLabels
{
    internal const double FilterLabelWidth = 1060;
    internal const double SidebarLabelsWidth = 1280;

    internal static bool ShowsFilterLabel(double rowWidth) => !double.IsFinite(rowWidth) || rowWidth <= 0 || rowWidth >= FilterLabelWidth;
    internal static bool ShowsSidebarLabels(double rowWidth) => double.IsFinite(rowWidth) && rowWidth >= SidebarLabelsWidth;

    internal sealed class SidebarContent : Grid
    {
        private readonly ContentControl _icon = new();
        private readonly TextBlock _label = new()
        {
            IsVisible = false, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };

        internal SidebarContent()
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto");
            VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetAccessibilityView(_label, AccessibilityView.Raw);
            Children.Add(_icon);
            Children.Add(_label);
        }

        internal void SetIcon(Control icon) => _icon.Content = icon;

        internal void Apply(ToggleButton button, bool show, string text, bool textFirst)
        {
            SetColumn(_icon, textFirst ? 1 : 0);
            SetColumn(_label, textFirst ? 0 : 1);
            _label.Margin = textFirst ? new Thickness(0, 0, 8, 0) : new Thickness(8, 0, 0, 0);
            _label.Text = text;
            _label.IsVisible = show;
            button.Classes.Set("icon-square", !show);
        }
    }
}
