using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Ursa.Controls;

namespace MeowChan.SettingsComparison;

// Pointer/key activation is centralized: never calls Ursa's private animated scroll path.
public sealed class ComparisonAnchor : Anchor
{
    protected override Type StyleKeyOverride => typeof(Anchor);
    public Action<string>? Navigate { get; set; }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        AnchorItem? item = e.Source as AnchorItem ?? (e.Source as Avalonia.Visual)?.FindAncestorOfType<AnchorItem>();
        if (item?.AnchorId is { } id) { e.Handled = true; Navigate?.Invoke(id); }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            AnchorItem? item = e.Source as AnchorItem ?? (e.Source as Avalonia.Visual)?.FindAncestorOfType<AnchorItem>();
            if (item?.AnchorId is { } id) { e.Handled = true; Navigate?.Invoke(id); }
        }
        else base.OnKeyDown(e);
    }
}
