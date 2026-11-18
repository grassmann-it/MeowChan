using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// Registry housekeeping is process-wide. Do not let other collections register controls during the slot/GC assertions.
[CollectionDefinition("Work views", DisableParallelization = true)]
public sealed class WorkViewRegistryCollection { }

[Collection("Work views")]
public sealed class WorkViewElementRegistryTests
{
    [Fact]
    public void IdChangesMoveTheSameControlBetweenPagesWithoutDuplicateRegistrations()
    {
        Control control = new();
        for (int i = 0; i < 1024; i++)
        {
            WorkViewElement.SetId(control, "archive.results.gallery");
            WorkViewElement.SetId(control, "downloads.toolbar.columns");
        }

        Assert.Single(WorkViewElement.All(), candidate => ReferenceEquals(candidate, control));
        Assert.Single(WorkViewElement.For(WorkViewPage.Downloads), candidate => ReferenceEquals(candidate, control));
        Assert.DoesNotContain(control, WorkViewElement.For(WorkViewPage.Archive));

        WorkViewElement.SetId(control, "archive.results.gallery");
        Assert.Single(WorkViewElement.For(WorkViewPage.Archive), candidate => ReferenceEquals(candidate, control));
        Assert.DoesNotContain(control, WorkViewElement.For(WorkViewPage.Downloads));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ClearingAnIdRemovesItFromSnapshotsAndReassignmentRegistersItOnce(string? clearedId)
    {
        Control control = new(), neverRegistered = new();
        WorkViewElement.SetId(neverRegistered, clearedId);
        for (int i = 0; i < 3; i++)
        {
            WorkViewElement.SetId(control, "archive.results.gallery");
            IReadOnlyList<Control> snapshot = WorkViewElement.All();
            WorkViewElement.SetId(control, clearedId);
            Assert.DoesNotContain(control, WorkViewElement.All());
            Assert.DoesNotContain(control, WorkViewElement.For(WorkViewPage.Archive));
            Assert.Contains(control, snapshot); // An already returned snapshot is not a live registry view.
        }

        WorkViewElement.SetId(control, "downloads.toolbar.columns");
        Assert.Single(WorkViewElement.All(), candidate => ReferenceEquals(candidate, control));
        Assert.Single(WorkViewElement.For(WorkViewPage.Downloads), candidate => ReferenceEquals(candidate, control));
        Assert.DoesNotContain(control, WorkViewElement.For(WorkViewPage.Archive));
        Assert.DoesNotContain(neverRegistered, WorkViewElement.All());

        control.ClearValue(WorkViewElement.IdProperty);
        Assert.Null(WorkViewElement.GetId(control));
        Assert.DoesNotContain(control, WorkViewElement.All());
        WorkViewElement.SetId(control, "archive.results.gallery");
        Assert.Single(WorkViewElement.For(WorkViewPage.Archive), candidate => ReferenceEquals(candidate, control));
    }

    [Fact]
    public void DistinctControlsCanShareAnIdAndUnknownIdsBelongToNoPage()
    {
        Control first = new(), second = new(), unknown = new();
        WorkViewElement.SetId(first, "archive.results.gallery");
        WorkViewElement.SetId(second, "archive.results.gallery");
        WorkViewElement.SetId(unknown, "not-a-policy-id");

        IReadOnlyList<Control> all = WorkViewElement.All();
        Assert.Contains(first, all);
        Assert.Contains(second, all);
        Assert.Contains(unknown, all);
        Assert.Contains(first, WorkViewElement.For(WorkViewPage.Archive));
        Assert.Contains(second, WorkViewElement.For(WorkViewPage.Archive));
        foreach (WorkViewPage page in Enum.GetValues<WorkViewPage>())
            Assert.DoesNotContain(unknown, WorkViewElement.For(page));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("archive.results.gallery")]
    public void RegistryAndReleasedSnapshotsDoNotRetainControlsAndAllPrunesTheirSlots(string? finalId)
    {
        WorkViewElement.All(); // Start a fresh registration interval, even for a small registry.
        WeakReference<Control> deadSlot = RegisterCollectibleControl(finalId, takeSnapshot: true);
        CollectUnrootedControls();

        Assert.False(deadSlot.TryGetTarget(out _));
        Assert.Contains(deadSlot, RegisteredSlots());
        WorkViewElement.All();
        Assert.DoesNotContain(deadSlot, RegisteredSlots());
    }

    [Fact]
    public void RegistrationPruningIsPopulationAmortizedAndEventuallyRemovesDeadSlots()
    {
        // Keep more than the old 256-entry threshold alive. No timing assertion or user-facing population cap.
        Control[] live = RegisterControls(1024);
        WorkViewElement.All(); // Compacts and starts an interval proportional to the live population.
        int population = RegisteredSlots().Length;
        WeakReference<Control> deadSlot = RegisterCollectibleControl("archive.results.gallery", takeSnapshot: false);
        CollectUnrootedControls();
        Assert.False(deadSlot.TryGetTarget(out _));

        Control[] firstHalf = RegisterControls(population / 2);
        // Neither per-registration scans nor fixed-256 scans are allowed to prune this slot yet.
        Assert.Contains(deadSlot, RegisteredSlots());
        Control[] nextInterval = RegisterControls(population);
        Assert.DoesNotContain(deadSlot, RegisteredSlots()); // Registration alone eventually reclaims it; no All/For call.

        HashSet<Control> expected = new(live.Concat(firstHalf).Concat(nextInterval));
        Control[] actual = WorkViewElement.For(WorkViewPage.Archive).Where(expected.Contains).ToArray();
        Assert.Equal(expected.Count, actual.Length);
        Assert.True(expected.SetEquals(actual));
        GC.KeepAlive(live);
        GC.KeepAlive(firstHalf);
        GC.KeepAlive(nextInterval);
    }

    private static Control[] RegisterControls(int count)
    {
        Control[] controls = new Control[count];
        for (int i = 0; i < controls.Length; i++)
        {
            controls[i] = new Control();
            WorkViewElement.SetId(controls[i], "archive.results.gallery");
        }
        return controls;
    }

    // Creation, snapshot and all strong target locals end at this non-inlined boundary, before the caller forces GC.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Control> RegisterCollectibleControl(string? finalId, bool takeSnapshot)
    {
        Control control = new();
        WorkViewElement.SetId(control, "archive.results.gallery");
        WeakReference<Control> slot = RegisteredSlots().Single(reference =>
            reference.TryGetTarget(out Control? target) && ReferenceEquals(target, control));
        WorkViewElement.SetId(control, finalId);
        if (takeSnapshot)
        {
            IReadOnlyList<Control> snapshot = WorkViewElement.All();
            Assert.Equal(!string.IsNullOrEmpty(finalId), snapshot.Contains(control));
            GC.KeepAlive(snapshot);
        }
        return slot;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectUnrootedControls()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    // Observe weak-slot reclamation, not source text or elapsed time. Never alter/reset the production registry.
    private static WeakReference<Control>[] RegisteredSlots()
    {
        var slots = (List<WeakReference<Control>>)typeof(WorkViewElement)
            .GetField("Registered", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        lock (slots) return slots.ToArray();
    }
}
