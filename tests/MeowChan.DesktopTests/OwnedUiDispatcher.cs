using System.Reflection;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

/// <summary>
/// Avalonia 12.1.4: ToolTip, ItemCollection and similar objects verify the process-primary <see cref="Dispatcher.UIThread"/>, which belongs to
/// whichever xunit worker touched it first. Tests in a DisableParallelization collection align that slot with their own thread's dispatcher
/// for a synchronous body and restore it (same seam as ThreadPriorityContextMenuTests and WorkViewActionStateGuardTests). Only valid in a
/// non-parallel collection, because the slot is process-wide. Fails closed if the seam moves.
/// Controls created under another owner cannot be changed from this one, and ShortcutService keeps every registered presentation (a failed one
/// even stays registered), so one control left behind by an earlier test would break the next shortcut refresh. The registry is emptied
/// before and after the body.
/// </summary>
internal static class OwnedUiDispatcher
{
    // Headless host construction publishes shortcuts synchronously. Keep that publication inside the fixture's
    // dispatcher and restore the enclosing test's observers and snapshot before returning the host.
    internal static DesktopSessionHost CreateHost(Func<DesktopSessionHost> create)
    {
        using ShortcutTestState shortcuts = new();
        DesktopSessionHost? host = null;
        Run(() => { host = create(); });
        return host!;
    }

    internal static void Run(Action test)
    {
        FieldInfo primary = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia primary dispatcher slot is missing.");
        FieldInfo refreshers = typeof(ShortcutService).GetField("Refreshers", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The shortcut refresher registry is missing.");
        void ClearRefreshers() => ((List<WeakReference<Action>>)refreshers.GetValue(null)!).Clear();
        object? previous = primary.GetValue(null);
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        try
        {
            primary.SetValue(null, owner);
            ClearRefreshers();
            owner.Invoke(() =>
            {
                owner.VerifyAccess();
                Assert.Same(owner, Dispatcher.UIThread);
                test();
            }, DispatcherPriority.Send);
        }
        finally { ClearRefreshers(); primary.SetValue(null, previous); }
    }
}

// Scoped shortcut globals for nonparallel tests. The retained controls and their registration revisions must agree again on exit.
internal sealed class ShortcutTestState : IDisposable
{
    private static readonly FieldInfo Handlers = Field("Changed");
    private static readonly FieldInfo Snapshot = Field("_current");
    private static readonly FieldInfo PruneCounter = Field("_observersUntilPrune");
    private static readonly List<WeakReference<Action>> Refreshers = (List<WeakReference<Action>>)Field("Refreshers").GetValue(null)!;
    private static readonly PropertyInfo Section = typeof(ShortcutService).GetProperty(nameof(ShortcutService.CurrentSection))!;
    private static readonly PropertyInfo Revision = typeof(ShortcutService).GetProperty(nameof(ShortcutService.Revision))!;

    private readonly object? _handlers = Handlers.GetValue(null);
    private readonly object? _pruneCounter = PruneCounter.GetValue(null);
    private readonly EffectiveShortcutBindings _snapshot = ShortcutService.Current;
    private readonly ShortcutBindingsSection _section = ShortcutService.CurrentSection;
    private readonly long _revision = ShortcutService.Revision;
    private readonly WeakReference<Action>[] _refreshers = Refreshers.ToArray();

    internal ShortcutTestState()
    {
        Refreshers.Clear();
        Handlers.SetValue(null, null);
    }

    public void Dispose()
    {
        Refreshers.Clear();
        Refreshers.AddRange(_refreshers);
        PruneCounter.SetValue(null, _pruneCounter);
        Snapshot.SetValue(null, _snapshot);
        Section.SetValue(null, _section);
        Revision.SetValue(null, _revision);
        Handlers.SetValue(null, _handlers);
    }

    private static FieldInfo Field(string name) => typeof(ShortcutService).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingFieldException(typeof(ShortcutService).FullName, name);
}
