using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// Review 14.11. night: R06 (one throwing presentation refresh must not stop the others or drop registrations) and R20
// (pruning dead observers stays linear while many observers live). Uses private lists, never the process-wide observers.
public sealed class ShortcutServiceRefreshTests
{
    [Fact]
    public void AThrowingRefreshDoesNotStopTheOthersAndStaysRegistered()
    {
        List<WeakReference<Action>> list = new();
        int untilPrune = ShortcutService.MinimumObserversBetweenPrunes;
        List<string> calls = new();
        Action failing = null!, second = null!, third = null!;
        failing = () => { calls.Add("failing"); throw new InvalidOperationException("broken presentation"); };
        // Well-behaved presentations register again from their refresh, as CommandBindings does.
        second = () => { calls.Add("second"); ShortcutService.Observe(list, second, ref untilPrune); };
        third = () => { calls.Add("third"); ShortcutService.Observe(list, third, ref untilPrune); };
        foreach (Action refresh in new[] { failing, second, third }) ShortcutService.Observe(list, refresh, ref untilPrune);

        IReadOnlyList<Exception> failures = ShortcutService.RefreshAll(list);
        Assert.Equal(new[] { "failing", "second", "third" }, calls);
        Assert.Equal("broken presentation", Assert.Single(failures).Message);
        Assert.Equal(3, list.Count); // the failing one is kept for the next snapshot

        calls.Clear();
        Assert.Single(ShortcutService.RefreshAll(list));
        Assert.Equal(new[] { "failing", "second", "third" }, calls.Order(StringComparer.Ordinal));
        Assert.Equal(3, list.Count);
        GC.KeepAlive(failing); GC.KeepAlive(second); GC.KeepAlive(third);
    }

    [Fact]
    public void ARefreshThatRegisteredItselfBeforeFailingIsNotAddedTwice()
    {
        List<WeakReference<Action>> list = new();
        int untilPrune = ShortcutService.MinimumObserversBetweenPrunes;
        Action half = null!;
        half = () => { ShortcutService.Observe(list, half, ref untilPrune); throw new InvalidOperationException(); };
        ShortcutService.Observe(list, half, ref untilPrune);

        Assert.Single(ShortcutService.RefreshAll(list));
        Assert.Single(list);
        Assert.Empty(ShortcutService.RefreshAll(new List<WeakReference<Action>>()));
        GC.KeepAlive(half);
    }

    [Fact]
    public void ManyLiveObserversArePrunedLinearlyNotOnEveryRegistration()
    {
        List<WeakReference<Action>> list = new();
        int untilPrune = ShortcutService.MinimumObserversBetweenPrunes;
        Action[] live = Enumerable.Range(0, 3000).Select(_ => (Action)(() => { })).ToArray();
        int scans = live.Count(refresh => ShortcutService.Observe(list, refresh, ref untilPrune));
        Assert.Equal(3000, list.Count);
        // Floor 1024, then each scan waits for as many registrations as it left alive: 1024 → 2048 → (next at 4096).
        Assert.InRange(scans, 1, 2);

        // Dead observers are dropped at the next scan.
        List<WeakReference<Action>> withDead = new();
        int countdown = 1;
        withDead.Add(new WeakReference<Action>(null!));
        Assert.True(ShortcutService.Observe(withDead, live[0], ref countdown));
        Assert.Single(withDead);
        Assert.Equal(ShortcutService.MinimumObserversBetweenPrunes, countdown);
        GC.KeepAlive(live);
    }
}
