using Avalonia.Threading;

namespace MeowChan.DesktopSmoke;

// The one wait-until-the-UI-settles helper of the smoke checks (it replaces twelve private copies that went
// by FlushAsync, SettleAsync, PumpAsync, TurnAsync and FlushTemplateAsync).
internal static class SmokeDispatcher
{
    // One empty dispatcher turn at Loaded priority (pending bindings, templates and layout work), then
    // backgroundTurns empty turns at Background priority (everything queued behind them).
    internal static async Task SettleAsync(int backgroundTurns = 1)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        for (int turn = 0; turn < backgroundTurns; turn++)
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
