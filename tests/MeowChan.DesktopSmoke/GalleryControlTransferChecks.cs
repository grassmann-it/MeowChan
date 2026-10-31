using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static partial class GalleryPlaybackFeatureChecks
{
    // Decoder-free positive regression: ordinary native feature suite plus the
    // MEOWCHAN_OWNED_CONTROL_TRANSFERS_ONLY=1 isolated entry point.
    // The causal cross-root negative baseline remains a separate opt-in probe.
    internal static async Task CheckOwnedControlTransfersAsync(MainWindow owner)
    {
        if (OperatingSystem.IsLinux())
            Require(Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1",
                "Owned transfers require the harness's private Xvfb display.");
        else
            Require(Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") == "1",
                "Owned transfers require an authorized disposable native desktop.");
        Dispatcher.UIThread.VerifyAccess();

        // Two-root forward/reverse coverage, followed by a distinct three-root burst.
        await OwnedTransferFixtureAsync(owner, threeRoots: false);
        await OwnedTransferFixtureAsync(owner, threeRoots: true);
        await CheckOwnedIndicatorReparentingAsync(owner);
    }

    private static async Task OwnedTransferFixtureAsync(MainWindow owner, bool threeRoots)
    {
        var hosts = Enumerable.Range(0, threeRoots ? 3 : 2).Select(_ => new Grid()).ToArray();
        var roots = hosts.Select((host, index) => new Window
        {
            Title = $"Owned transfer probe {index}", Width = 420, Height = 280,
            Content = host, ShowInTaskbar = false
        }).ToArray();
        var controls = new List<OwnedTransferProbe>();
        OwnedTransferProbe NewProbe()
        {
            var probe = new OwnedTransferProbe();
            controls.Add(probe);
            hosts[0].Children.Add(probe);
            roots[0].UpdateLayout();
            Require(probe.Leaf.IsArrangeValid && ReferenceEquals(TopLevel.GetTopLevel(probe), roots[0]),
                "Transfer probe did not establish its original real root.");
            return probe;
        }
        void LayoutAll()
        {
            // Intentionally source-first: destination-first could hide stale work.
            foreach (var root in roots) root.UpdateLayout();
        }
        async Task Arrived(OwnedTransferProbe probe, int target)
        {
            await UntilAsync(() => ReferenceEquals(probe.Parent, hosts[target]) &&
                ReferenceEquals(TopLevel.GetTopLevel(probe.Leaf), roots[target]),
                $"Owned transfer never reached actual target {target}.");
            LayoutAll();
            Require(hosts.Sum(host => host.Children.Count(child => ReferenceEquals(child, probe))) == 1 &&
                ReferenceEquals(probe.Child, probe.Leaf) && ReferenceEquals(probe.DataContext, probe.Token) &&
                probe.Leaf.Text == "retained value 37" && probe.Leaf.IsArrangeValid,
                "Transfer replaced/duplicated the owned control or lost its value state.");
        }
        async Task FlushCallbacks()
        {
            // Lower-priority barrier, not a sleep or a fabricated layout queue.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            LayoutAll();
        }
        try
        {
            foreach (var root in roots) root.Show(owner);
            LayoutAll();
            Require(!ReferenceEquals(CrossRootLayoutManager(roots[0]), CrossRootLayoutManager(roots[1])),
                "Transfer fixture requires distinct real layout managers.");
            var probe = NewProbe();
            int moved = 0;
            probe.Leaf.InvalidateArrange();
            Require(!probe.Leaf.IsArrangeValid, "No real pending descendant arrange.");
            GalleryControlTransfer.Move(probe, hosts[1], moved: () => moved++);
            Require(!ReferenceEquals(probe.Parent, hosts[1]) && moved == 0, "Cross-root move attached synchronously.");
            await Arrived(probe, 1);
            Require(moved == 1, "Forward completion was not delivered exactly once.");
            probe.Leaf.InvalidateArrange();
            GalleryControlTransfer.Move(probe, hosts[0], moved: () => moved++);
            Require(!ReferenceEquals(probe.Parent, hosts[0]) && moved == 1, "Reverse cross-root move attached synchronously.");
            await Arrived(probe, 0);
            Require(moved == 2, "Reverse completion was not delivered exactly once.");

            var sameRoot = new Grid();
            hosts[0].Children.Add(sameRoot);
            GalleryControlTransfer.Move(probe, sameRoot, moved: () => moved++);
            Require(ReferenceEquals(probe.Parent, sameRoot) && moved == 3, "Same-root move was not immediate.");
            GalleryControlTransfer.Move(probe, hosts[0]);
            LayoutAll();

            // Track was called by the constructor, before initial attachment.
            // Removing an ancestor before Move must not erase the remembered root.
            var ancestor = new Grid();
            var orphan = new OwnedTransferProbe();
            controls.Add(orphan);
            ancestor.Children.Add(orphan);
            hosts[0].Children.Add(ancestor);
            roots[0].UpdateLayout();
            orphan.Leaf.InvalidateArrange();
            hosts[0].Children.Remove(ancestor);
            GalleryControlTransfer.Move(orphan, hosts[1]);
            Require(!ReferenceEquals(orphan.Parent, hosts[1]), "Tracked orphan was attached before its old root drained.");
            await Arrived(orphan, 1);

            if (threeRoots)
            {
                var burst = NewProbe();
                int obsolete = 0, latest = 0;
                burst.Leaf.InvalidateArrange();
                GalleryControlTransfer.Move(burst, hosts[1], moved: () => obsolete++);
                GalleryControlTransfer.Move(burst, hosts[2], moved: () => latest++);
                Require(!ReferenceEquals(burst.Parent, hosts[1]) && !ReferenceEquals(burst.Parent, hosts[2]), "Burst attached before deferred processing.");
                await Arrived(burst, 2);
                await FlushCallbacks();
                Require(obsolete == 0 && latest == 1 && !hosts[1].Children.Contains(burst),
                    "A->B->C did not coalesce to exactly the latest target.");
            }

            var roundTrip = NewProbe();
            int abandoned = 0, returned = 0;
            roundTrip.Leaf.InvalidateArrange();
            GalleryControlTransfer.Move(roundTrip, hosts[1], moved: () => abandoned++);
            GalleryControlTransfer.Move(roundTrip, hosts[0], moved: () => returned++);
            await Arrived(roundTrip, 0);
            await FlushCallbacks();
            Require(abandoned == 0 && returned == 1 && ReferenceEquals(roundTrip.Parent, hosts[0]),
                "A->B->A retained an obsolete destination or lost the latest completion.");

            // Footer and its independently owned child request migration in the same
            // turn. The child ultimately becomes a sibling, not a copied descendant.
            var footer = new Grid();
            GalleryControlTransfer.Track(footer);
            var nested = new OwnedTransferProbe();
            controls.Add(nested);
            footer.Children.Add(nested);
            hosts[0].Children.Add(footer);
            roots[0].UpdateLayout();
            nested.Leaf.InvalidateArrange();
            int footerDone = 0, childDone = 0;
            try
            {
                GalleryControlTransfer.Move(footer, hosts[1], moved: () => footerDone++);
                GalleryControlTransfer.Move(nested, hosts[1], moved: () => childDone++);
                Require(!ReferenceEquals(footer.Parent, hosts[1]) && childDone == 0 && footerDone == 0,
                    "Cooperating cross-root transfers were not deferred.");
                await Arrived(nested, 1);
                await UntilAsync(() => ReferenceEquals(footer.Parent, hosts[1]) && footerDone == 1 && childDone == 1,
                    "Nested footer/child transfers failed to settle independently.");
                LayoutAll();
                Require(!footer.Children.Contains(nested), "Nested child retained two ownership paths.");
            }
            finally { GalleryControlTransfer.Cancel(footer); }

            // Request another control while the transfer callback actively drains
            // the detached first control's source manager, not in a synthetic call.
            var draining = NewProbe();
            var duringDrain = NewProbe();
            var drainTrigger = NewProbe();
            bool drainEntered = false, drainExited = false, otherDone = false;
            // Establish the pending arrange and enqueue the transfer in one Normal
            // dispatcher turn. A Render queued by earlier fixture layout may run
            // before this turn, but cannot consume the armed callback between these
            // statements. The helper's Normal continuation then precedes Render.
            // Do not ignore an attached-source callback: that would let this test
            // silently pass without observing the actual detached-source drain.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                roots[0].UpdateLayout();
                drainTrigger.OnArrange = () =>
                {
                    drainEntered = true;
                    Require(draining.Parent is null,
                        "Source layout was not drained while the first control was detached.");
                    GalleryControlTransfer.Move(duringDrain, hosts[1], moved: () =>
                    {
                        Require(drainExited && !drainTrigger.InArrange,
                            "Different-control completion ran inside source drain arrange.");
                        otherDone = true;
                    });
                    Require(!ReferenceEquals(duringDrain.Parent, hosts[1]) && !otherDone,
                        "Different-control request reparented during source drain.");
                };
                drainTrigger.AfterArrange = () => drainExited = true;
                draining.Leaf.InvalidateArrange();
                drainTrigger.InvalidateArrange();
                Require(!draining.Leaf.IsArrangeValid && !drainTrigger.IsArrangeValid,
                    "Source-drain probe did not enqueue real pending arrange work.");
                GalleryControlTransfer.Move(draining, hosts[1]);
                Require(!drainEntered && !ReferenceEquals(draining.Parent, hosts[1]),
                    "Source-drain transfer did not wait for the requesting stack to unwind.");
            }, DispatcherPriority.Normal);
            await Arrived(draining, 1);
            await Arrived(duringDrain, 1);
            await UntilAsync(() => otherDone, "Different-control drain request never completed.");
            Require(drainEntered && drainExited, "Transfer never exercised real source-drain reentrancy.");

            // Real root content removal and hiding precede Move. Constructor Track
            // must preserve the old manager even though no ancestor exposes it now.
            foreach (bool hide in new[] { false, true })
            {
                var removedRoot = NewProbe();
                removedRoot.Leaf.InvalidateArrange();
                roots[0].Content = null;
                if (hide) roots[0].Hide();
                try
                {
                    int detachedDone = 0;
                    GalleryControlTransfer.Move(removedRoot, hosts[1], moved: () => detachedDone++);
                    Require(!ReferenceEquals(removedRoot.Parent, hosts[1]) && detachedDone == 0,
                        "Content-null/hidden-root move attached synchronously.");
                    await Arrived(removedRoot, 1);
                    Require(detachedDone == 1, "Content-null/hidden-root completion was lost.");
                }
                finally
                {
                    roots[0].Content = hosts[0];
                    if (hide) roots[0].Show(owner);
                    LayoutAll();
                }
            }

            foreach (bool cancel in new[] { false, true })
            {
                var blocked = NewProbe();
                int callbacks = 0;
                blocked.Leaf.InvalidateArrange();
                GalleryControlTransfer.Move(blocked, hosts[1], canAttach: () => cancel,
                    moved: () => callbacks++);
                if (cancel)
                {
                    GalleryControlTransfer.Cancel(blocked);
                    Require(blocked.Parent is null, "Cancel did not synchronously detach.");
                }
                await FlushCallbacks();
                Require(blocked.Parent is null && callbacks == 0 &&
                    hosts.All(host => !host.Children.Contains(blocked)),
                    "Rejected/cancelled transfer attached or delivered completion.");
                if (cancel)
                {
                    GalleryControlTransfer.Move(blocked, hosts[1], moved: () => callbacks++);
                    await FlushCallbacks();
                    Require(blocked.Parent is null && callbacks == 0, "Cancel was not terminal.");
                }
            }

            var reentrant = NewProbe();
            bool entered = false, exited = false, completed = false;
            reentrant.OnArrange = () =>
            {
                entered = true;
                GalleryControlTransfer.Move(reentrant, hosts[1], moved: () =>
                {
                    Require(exited && !reentrant.InArrange, "Completion ran inside source ArrangeOverride.");
                    completed = true;
                });
                Require(!completed && !ReferenceEquals(TopLevel.GetTopLevel(reentrant), roots[1]),
                    "Reentrant move reparented while the old manager's callback was running.");
            };
            reentrant.AfterArrange = () => exited = true;
            reentrant.Leaf.InvalidateArrange();
            reentrant.InvalidateArrange();
            roots[0].UpdateLayout(); // The request originates inside a genuine manager-driven arrange.
            Require(entered && exited && !completed, "Real reentrant arrange boundary was not exercised/deferred.");
            await Arrived(reentrant, 1);
            await UntilAsync(() => completed, "Reentrant completion never followed arrange exit.");
            Console.WriteLine($"OWNED_CONTROL_TRANSFERS roots={roots.Length} PASS (no decoder/audio)");
        }
        finally
        {
            foreach (var control in controls) GalleryControlTransfer.Cancel(control);
            foreach (var root in roots.Reverse()) root.Close();
        }
    }

    // Relocated from GalleryOverlayActivityTests.ReparentingUsesTheSameIndicatorsAndNeverDuplicatesOrReservesAFooterRow:
    // real MoveOverlayControl now requires the UI dispatcher. No viewer constructor,
    // VideoView, decoder or audio device is needed to exercise its placement guard.
    private static async Task CheckOwnedIndicatorReparentingAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        var viewer = (GalleryViewer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(GalleryViewer));
        var managed = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto") };
        var nativeOverlay = new Grid();
        var indicators = new[] { GalleryViewer.CreateEdgeIndicator(false), GalleryViewer.CreateEdgeIndicator(true) };
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        // Set the actual indicator identities so the full indicator-specific
        // _viewerInteractionStopped guard is exercised, not its non-indicator arm.
        // Uninitialized bool fields intentionally remain false (live viewer).
        foreach (var pair in new[] { ("_controlsEdgeIndicator", indicators[0]), ("_postEdgeIndicator", indicators[1]) })
        {
            var field = typeof(GalleryViewer).GetField(pair.Item1, flags)
                ?? throw new MissingFieldException(typeof(GalleryViewer).FullName, pair.Item1);
            field.SetValue(viewer, pair.Item2);
        }
        var move = typeof(GalleryViewer).GetMethod("MoveOverlayControl", flags)
            ?? throw new MissingMethodException(typeof(GalleryViewer).FullName, "MoveOverlayControl");
        void MoveIndicator(Button indicator, Panel target)
        {
            try { move.Invoke(viewer, new object[] { indicator, target }); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        var managedRoot = new Window
        {
            Title = "Owned indicator managed root", Width = 420, Height = 280,
            Content = managed, ShowInTaskbar = false
        };
        var overlayRoot = new Window
        {
            Title = "Owned indicator overlay root", Width = 420, Height = 280,
            Content = nativeOverlay, ShowInTaskbar = false
        };
        try
        {
            foreach (Button indicator in indicators)
            {
                GalleryControlTransfer.Track(indicator);
                managed.Children.Add(indicator);
            }
            managedRoot.Show(owner);
            overlayRoot.Show(owner);
            managedRoot.UpdateLayout();
            overlayRoot.UpdateLayout();
            for (int cycle = 0; cycle < 12; cycle++)
            {
                Grid target = cycle % 2 == 0 ? nativeOverlay : managed;
                Grid previous = cycle % 2 == 0 ? managed : nativeOverlay;
                Window sourceRoot = cycle % 2 == 0 ? managedRoot : overlayRoot;
                Window targetRoot = cycle % 2 == 0 ? overlayRoot : managedRoot;
                foreach (Button indicator in indicators)
                {
                    indicator.InvalidateArrange();
                    MoveIndicator(indicator, target);
                    MoveIndicator(indicator, target); // placement remains idempotent
                }
                await UntilAsync(() => indicators.All(indicator => ReferenceEquals(indicator.Parent, target) &&
                    ReferenceEquals(TopLevel.GetTopLevel(indicator), targetRoot)),
                    $"Indicator reparenting cycle {cycle} never reached the real target root.");
                sourceRoot.UpdateLayout();
                targetRoot.UpdateLayout();
                // Retain every assertion from the original 12-cycle unit test,
                // after awaiting the now-deferred cross-root placement.
                foreach (Button indicator in indicators)
                {
                    Require(ReferenceEquals(target, indicator.Parent), "Indicator lost its requested parent.");
                    Require(Grid.GetRow(indicator) == 0, "Indicator reserved a footer row.");
                }
                Require(previous.Children.Count == 0, "Previous indicator host retained children.");
                Require(target.Children.Count == indicators.Length &&
                    target.Children.Select((child, index) => ReferenceEquals(child, indicators[index])).All(same => same),
                    "Indicator placement replaced, reordered or duplicated the original controls.");
                Require(managed.RowDefinitions.Count == 3, "Indicator placement changed managed footer row count.");
            }
            Console.WriteLine("OWNED_CONTROL_TRANSFERS indicators=2 cycles=12 identity/order/row-count PASS");
        }
        finally
        {
            foreach (Button indicator in indicators) GalleryControlTransfer.Cancel(indicator);
            overlayRoot.Close();
            managedRoot.Close();
        }
    }

    private sealed class OwnedTransferProbe : Decorator
    {
        internal readonly object Token = new();
        internal TextBlock Leaf { get; } = new() { Text = "retained value 37", Width = 110, Height = 40 };
        internal Action? OnArrange;
        internal Action? AfterArrange;
        internal bool InArrange;

        internal OwnedTransferProbe()
        {
            DataContext = Token;
            Child = Leaf;
            GalleryControlTransfer.Track(this);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            InArrange = true;
            var action = OnArrange;
            OnArrange = null; // One request per explicitly armed real layout pass.
            try
            {
                action?.Invoke();
                return base.ArrangeOverride(finalSize);
            }
            finally
            {
                InArrange = false;
                AfterArrange?.Invoke();
            }
        }
    }
}
