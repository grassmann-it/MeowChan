using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

[CollectionDefinition("Shortcut observers", DisableParallelization = true)]
public sealed class ShortcutObserverCollection;

/// <summary>Performance review 2026-11-14 (M1, M3): re-applying an unchanged state is a no-op and registers nothing new.</summary>
[Collection("Shortcut observers")]
public sealed class ToolbarUpdateTests
{
    [Fact]
    public void ChangeGateReportsOnlyTheFirstValueAndRealChanges()
    {
        ChangeGate<(WorkView View, bool Flag, string Language)> gate = default;
        Assert.True(gate.Changed((WorkView.Simple, false, "en")));
        for (int i = 0; i < 20; i++) Assert.False(gate.Changed((WorkView.Simple, false, "en")));
        Assert.True(gate.Changed((WorkView.Advanced, false, "en")));
        Assert.True(gate.Changed((WorkView.Advanced, true, "en")));
        Assert.True(gate.Changed((WorkView.Advanced, true, "de")));
        Assert.False(gate.Changed((WorkView.Advanced, true, "de")));
        gate.Reset();
        Assert.True(gate.Changed((WorkView.Advanced, true, "de")));
    }

    [Fact]
    public void ApplyToolBarItemReportsWhichValuesChanged()
    {
        Button item = new();
        WorkViewElement.SetId(item, "shell.header.disk-graph"); // Hidden, Hidden, Direct
        Assert.True(WorkViewPresenter.ApplyToolBarItem(item, WorkView.Simple, null, false, out bool first));
        Assert.True(first);
        Assert.True(WorkViewPresenter.ApplyToolBarItem(item, WorkView.Simple, null, false, out bool again));
        Assert.False(again);
        Assert.True(WorkViewPresenter.ApplyToolBarItem(item, WorkView.Advanced, null, false, out bool back));
        Assert.True(back);
        Assert.False(WorkViewPresenter.ApplyToolBarItem(new Button(), WorkView.Simple, null, false, out bool noId));
        Assert.False(noId);
    }

    [Fact]
    public void ReapplyingAPresentationRegistersOneObserverPerOwner()
    {
        Button a = new(), b = new();
        int before = CommandBindings.ObserverCount;
        for (int i = 0; i < 200; i++)
        {
            CommandBindings.Apply(a, "archive.refresh");
            CommandBindings.Apply(b, "archive.refresh", () => i.ToString());
        }
        Assert.Equal(before + 2, CommandBindings.ObserverCount);
        GC.KeepAlive(a); GC.KeepAlive(b);
    }

    [Fact]
    public void ObserversRegisterAgainOnceAfterAShortcutSnapshotIsPublished()
    {
        Button button = new();
        CommandBindings.Apply(button, "archive.refresh");
        long revision = ShortcutService.Revision;
        ShortcutService.Publish(new ShortcutBindingsSection());
        // Off the dispatcher thread Publish only posts; the re-registration is then not observable here.
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) Assert.True(ShortcutService.Revision > revision);
        // Publish ran the refresher, which applied again: exactly one entry for this owner, however often it is re-applied.
        int afterPublish = CommandBindings.ObserverCount;
        for (int i = 0; i < 100; i++) CommandBindings.Apply(button, "archive.refresh");
        Assert.Equal(afterPublish, CommandBindings.ObserverCount);
        Assert.True(afterPublish >= 1);
        GC.KeepAlive(button);
    }

    [Fact]
    public void AllocationFreeResolveAgreesWithTheListFormIncludingPriorityTies()
    {
        Random random = new(14112026);
        for (int round = 0; round < 500; round++)
        {
            int count = random.Next(0, 9);
            double[] widths = new double[count + 3];
            OverflowMode[] modes = new OverflowMode[count + 3];
            bool[] visible = new bool[count + 3];
            int[] priorities = new int[count + 3];
            for (int i = 0; i < count; i++)
            {
                widths[i] = random.Next(10, 120);
                modes[i] = (OverflowMode)random.Next(0, 3);
                visible[i] = random.Next(0, 5) != 0;
                priorities[i] = random.Next(0, 4);
            }
            double available = random.Next(0, 500), more = random.Next(0, 40);
            foreach (bool prioritized in new[] { false, true })
            {
                bool[] expected = ActionOverflowLayout.Resolve(widths.Take(count).ToArray(), modes.Take(count).ToArray(),
                    visible.Take(count).ToArray(), available, more, prioritized ? priorities.Take(count).ToArray() : null);
                bool[] actual = new bool[count + 3];
                Array.Fill(actual, true); // stale results of an earlier pass must be overwritten
                ActionOverflowLayout.Resolve(count, widths, modes, visible, available, more,
                    prioritized ? priorities : null, actual, new int[count + 3]);
                Assert.Equal(expected, actual.Take(count).ToArray());
            }
        }
    }

    [Fact]
    public void PriorityOrderKeepsItemOrderForTiesAndMovesTheHighestRankIntoMoreFirst()
    {
        // Same expectations as the list form's own tests, through the allocation-free path.
        double[] widths = { 50, 50, 50, 50 };
        OverflowMode[] modes = { OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded, OverflowMode.AsNeeded };
        bool[] visible = { true, true, true, true };
        int[] priorities = { 3, 1, 1, 2 };
        bool[] result = new bool[4];
        ActionOverflowLayout.Resolve(4, widths, modes, visible, 130, 20, priorities, result, new int[4]);
        // Budget 110: ranks 1 (item 1), 1 (item 2) fit; rank 2 (item 3) does not and spills, then rank 3 (item 0).
        Assert.Equal(new[] { true, false, false, true }, result);
    }
}
