using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop;

// Keep the native selectable text control and its configured reading font. Only
// media-free bodies get a reading measure; attachment/text layouts remain intact.
public sealed class ReaderBodyTextBlock : SelectableTextBlock
{
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public static readonly StyledProperty<bool> IsTextOnlyProperty =
        AvaloniaProperty.Register<ReaderBodyTextBlock, bool>(nameof(IsTextOnly));

    public bool IsTextOnly
    {
        get => GetValue(IsTextOnlyProperty);
        set => SetValue(IsTextOnlyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsTextOnlyProperty || change.Property == FontSizeProperty)
        {
            // Roughly 80 typical Latin characters at the configured reading size.
            MaxWidth = IsTextOnly ? FontSize * 44 : double.PositiveInfinity;
            HorizontalAlignment = IsTextOnly ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        }
    }
}
