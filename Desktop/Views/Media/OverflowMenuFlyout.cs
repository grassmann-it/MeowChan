using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;

namespace MeowChan.Desktop;

internal sealed class OverflowMenuFlyout : MenuFlyout
{
    public OverflowMenuFlyout() => Items.Clear();

    protected override void OnOpening(CancelEventArgs args)
    {
        base.OnOpening(args);
        if (!Items.OfType<MenuItem>().Any(item => item.IsVisible))
            args.Cancel = true;
    }
}
