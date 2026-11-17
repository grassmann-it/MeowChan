using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class LayoutSubscriptionLifetimeTests(ITestOutputHelper output)
{
    [Fact]
    public void MediaTilePanelReturnsLayoutSubscriptionsToBaselineAfter100Reattachments()
    {
        MediaTilePanel panel = new();
        // Deliver Avalonia's actual virtual lifecycle callbacks, without a native
        // window, renderer, layout pump or sleeps. Inspect the actual event rather
        // than a production counter that could drift away from the subscription.
        int baseline = Handlers(new Border());
        int maximumDetached = 0;
        for (int cycle = 0; cycle < 100; cycle++)
        {
            Lifecycle(panel, "OnAttachedToVisualTree");
            Assert.Equal(baseline + 1, Handlers(panel));
            Lifecycle(panel, "OnDetachedFromVisualTree");
            Lifecycle(panel, "OnDetachedFromVisualTree"); // cleanup is idempotent
            maximumDetached = Math.Max(maximumDetached, Handlers(panel));
        }
        output.WriteLine($"cycles=100; baseline={baseline}; maximum detached subscriptions={maximumDetached}; final={Handlers(panel)}");
        Assert.Equal(baseline, maximumDetached);
        Assert.Equal(baseline, Handlers(panel));
    }

    private static void Lifecycle(Control control, string method) => control.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(control, [null]);

    private static int Handlers(Layoutable control)
    {
        FieldInfo field = typeof(Layoutable).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(field => field.Name.Contains("layoutUpdated", StringComparison.OrdinalIgnoreCase)
                && typeof(Delegate).IsAssignableFrom(field.FieldType));
        return (field.GetValue(control) as Delegate)?.GetInvocationList().Length ?? 0;
    }
}
