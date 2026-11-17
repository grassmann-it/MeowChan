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
        int attachedInvocations = 0;
        int detachedInvocations = 0;
        for (int cycle = 0; cycle < 100; cycle++)
        {
            Lifecycle(panel, "OnAttachedToVisualTree");
            Assert.Equal(baseline + 1, Handlers(panel));
            attachedInvocations += PulseLayout(panel);
            Lifecycle(panel, "OnDetachedFromVisualTree");
            Lifecycle(panel, "OnDetachedFromVisualTree"); // cleanup is idempotent
            maximumDetached = Math.Max(maximumDetached, Handlers(panel));
            detachedInvocations += PulseLayout(panel);
        }
        // Further layout passes after the final detach must not run the handler.
        int stoppedAt = detachedInvocations;
        for (int pass = 0; pass < 100; pass++) detachedInvocations += PulseLayout(panel);
        output.WriteLine($"cycles=100; baseline={baseline}; maximum detached subscriptions={maximumDetached}; final={Handlers(panel)}");
        output.WriteLine($"attached invocations={attachedInvocations}; detached invocations after cycles={stoppedAt}; after 100 more layout pulses={detachedInvocations}");
        Assert.Equal(100, attachedInvocations);
        Assert.Equal(stoppedAt, detachedInvocations);
        Assert.Equal(0, detachedInvocations);
        Assert.Equal(baseline, maximumDetached);
        Assert.Equal(baseline, Handlers(panel));
    }

    private static void Lifecycle(Control control, string method) => control.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(control, [null]);

    private static readonly FieldInfo LayoutUpdatedField = typeof(Layoutable)
        .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
        .Single(field => field.Name.Contains("layoutUpdated", StringComparison.OrdinalIgnoreCase)
            && typeof(Delegate).IsAssignableFrom(field.FieldType));

    private static Delegate[] Subscriptions(Layoutable control) =>
        (LayoutUpdatedField.GetValue(control) as Delegate)?.GetInvocationList() ?? [];

    private static int Handlers(Layoutable control) => Subscriptions(control).Length;

    // Deliver a deterministic layout pulse to Avalonia's real event delegates.
    // Count completed invocations, not a simulated subscription or production seam.
    private static int PulseLayout(Layoutable control)
    {
        int invoked = 0;
        foreach (Delegate handler in Subscriptions(control))
        {
            handler.DynamicInvoke(control, EventArgs.Empty);
            invoked++;
        }
        return invoked;
    }
}
