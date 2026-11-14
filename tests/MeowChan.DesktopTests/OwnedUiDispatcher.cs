using System.Reflection;
using Avalonia.Threading;

namespace MeowChan.DesktopTests;

/// <summary>
/// Avalonia 12.1.4: ToolTip, ItemCollection and similar objects verify the process-primary <see cref="Dispatcher.UIThread"/>, which belongs to
/// whichever xunit worker touched it first. Tests in a DisableParallelization collection align that slot with their own thread's dispatcher
/// for a synchronous body and restore it (same seam as ThreadPriorityContextMenuTests and WorkViewActionStateGuardTests). Only valid in a
/// non-parallel collection, because the slot is process-wide. Fails closed if the seam moves.
/// </summary>
internal static class OwnedUiDispatcher
{
    internal static void Run(Action test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia primary dispatcher slot is missing.");
        object? previous = primary.GetValue(null);
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            owner.Invoke(() =>
            {
                owner.VerifyAccess();
                Assert.Same(owner, Dispatcher.UIThread);
                test();
            }, DispatcherPriority.Send);
        }
        finally { primary.SetValue(null, previous); }
    }
}
