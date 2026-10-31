using Avalonia.Layout;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static partial class GalleryPlaybackFeatureChecks
{
    // Explicit diagnostic entry point only: deliberately not registered in RunAsync.
    // No GalleryViewer, VideoView, decoder, audio device, or personal window mutation.
    internal static async Task CheckCrossRootMigrationAsync(MainWindow owner)
    {
        if (OperatingSystem.IsLinux())
            Require(Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1",
                "Cross-root probe requires the harness's private Xvfb display.");
        else
            Require(Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") == "1",
                "Cross-root probe requires an explicitly authorized disposable native desktop.");

        // A single dispatcher callback includes setup, invalidation, migration, execution,
        // and disposal. Never yield with the deliberately poisoned old manager alive.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            bool forward = CrossRootMigrationCase(owner, reverse: false, drainDetached: false);
            bool backward = CrossRootMigrationCase(owner, reverse: true, drainDetached: false);
            bool forwardControl = CrossRootMigrationCase(owner, reverse: false, drainDetached: true);
            bool backwardControl = CrossRootMigrationCase(owner, reverse: true, drainDetached: true);
            Console.WriteLine($"CROSS_ROOT_MIGRATION summary directForwardReproduced={forward} " +
                $"directBackwardReproduced={backward} detachedDrainForwardReproduced={forwardControl} " +
                $"detachedDrainBackwardReproduced={backwardControl}; control is NOT a production fix");
            Require(forward && backward,
                "Cross-root baseline NOT reproduced in both directions; retain logs, do not claim a regression pass.");
            Require(!forwardControl && !backwardControl,
                "Detached source.UpdateLayout control also reproduced the fault; candidate invariants not established.");
        }, DispatcherPriority.Normal);
    }

    private static bool CrossRootMigrationCase(MainWindow owner, bool reverse, bool drainDetached)
    {
        Grid mainHost = new();
        Grid overlayHost = new();
        Window mainRoot = new()
        {
            Title = "Cross-root diagnostic main", Width = 420, Height = 280,
            Content = mainHost, ShowInTaskbar = false
        };
        Window overlayRoot = new()
        {
            Title = "Cross-root diagnostic overlay", Width = 420, Height = 280,
            Content = overlayHost, ShowInTaskbar = false
        };
        Window source = reverse ? overlayRoot : mainRoot;
        Window destination = reverse ? mainRoot : overlayRoot;
        Grid sourceHost = reverse ? overlayHost : mainHost;
        Grid destinationHost = reverse ? mainHost : overlayHost;
        Border descendant = new() { Width = 80, Height = 50 };
        Grid subtree = new();
        subtree.Children.Add(descendant);
        sourceHost.Children.Add(subtree);
        string label = $"{(reverse ? "overlay-to-main" : "main-to-overlay")}/{(drainDetached ? "detached-drain-control" : "direct-baseline")}";
        try
        {
            mainRoot.Show(owner);
            overlayRoot.Show(mainRoot);
            mainRoot.UpdateLayout();
            overlayRoot.UpdateLayout();
            object oldManager = CrossRootLayoutManager(source);
            object newManager = CrossRootLayoutManager(destination);
            Require(!ReferenceEquals(oldManager, newManager), "Probe requires two distinct real layout managers.");
            Require(descendant.IsMeasureValid && descendant.IsArrangeValid &&
                ReferenceEquals(TopLevel.GetTopLevel(descendant), source), "Source descendant was not initially laid out.");

            // Real InvalidateArrange enqueues the descendant on its current root.
            // Detach/add invalidates measure on the destination. Do not run that
            // destination manager before executing the old queue: its invalid ancestor
            // is what leads Arrange -> AncestorMeasureInvalid -> old.InvalidateArrange.
            descendant.InvalidateArrange();
            Require(!descendant.IsArrangeValid, "Pending descendant arrange was not established before migration.");
            Console.WriteLine($"CROSS_ROOT_MIGRATION {label} before-detach measure={descendant.IsMeasureValid} arrange={descendant.IsArrangeValid}");
            sourceHost.Children.Remove(subtree);
            Require(TopLevel.GetTopLevel(descendant) is null, "Subtree did not detach.");
            if (drainDetached)
            {
                source.UpdateLayout();
                Console.WriteLine($"CROSS_ROOT_MIGRATION {label} source.UpdateLayout completed while detached");
            }
            destinationHost.Children.Add(subtree);
            Require(ReferenceEquals(TopLevel.GetTopLevel(descendant), destination), "Subtree did not attach to destination.");
            Console.WriteLine($"CROSS_ROOT_MIGRATION {label} after-attach descendantMeasure={descendant.IsMeasureValid} " +
                $"descendantArrange={descendant.IsArrangeValid} destinationMeasure={destination.IsMeasureValid} " +
                $"destinationHostMeasure={destinationHost.IsMeasureValid}; executing OLD manager first");
            bool reproduced = false;
            try
            {
                CrossRootExecuteLayoutPass(oldManager);
            }
            catch (ArgumentException error) when (
                error.Message == "Attempt to call InvalidateArrange on wrong LayoutManager." &&
                error.StackTrace?.Contains("ExecuteArrangePass", StringComparison.Ordinal) == true)
            {
                reproduced = true;
                Console.WriteLine($"CROSS_ROOT_MIGRATION {label} EXPECTED_REAL_FAULT {error}");
            }
            if (reproduced)
            {
                // ExecuteArrangePass has a retained after-measure list on this error
                // path. Do NOT attempt to heal/reuse that manager or pump dispatch.
                return true;
            }
            CrossRootExecuteLayoutPass(newManager);
            CrossRootExecuteLayoutPass(oldManager);
            Require(descendant.IsMeasureValid && descendant.IsArrangeValid &&
                ReferenceEquals(TopLevel.GetTopLevel(descendant), destination),
                "No expected fault, but destination failed to settle with correct root ownership.");
            Console.WriteLine($"CROSS_ROOT_MIGRATION {label} NO_FAULT destination settled; " +
                (drainDetached ? "candidate control observation only" : "baseline NOT reproduced"));
            return false;
        }
        finally
        {
            // Closing disposes each real manager, including its outstanding queues.
            // Keep the parent alive until the owned fixture has been closed.
            try { overlayRoot.Close(); }
            finally { mainRoot.Close(); }
            foreach (Window root in new[] { overlayRoot, mainRoot })
            {
                object manager = CrossRootLayoutManager(root);
                bool disposed = (bool)manager.GetType().GetField("_disposed",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
                Require(disposed && !root.IsVisible,
                    "Probe manager was not synchronously disposed before yielding poisoned queues.");
            }
        }
    }

    // Read-only access to the actual internal interface; no queue inspection or mutation.
    private static object CrossRootLayoutManager(Window window)
    {
        // Pinned Avalonia 12 keeps ILayoutRoot on PresentationSource, not Window.
        var property = typeof(TopLevel).GetProperty("LayoutManager",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(TopLevel).FullName, "LayoutManager");
        return property.GetValue(window)
            ?? throw new InvalidOperationException("Real window returned no layout manager.");
    }

    private static void CrossRootExecuteLayoutPass(object manager)
    {
        var execute = manager.GetType().GetMethod("ExecuteLayoutPass", Type.EmptyTypes)
            ?? throw new MissingMethodException(manager.GetType().FullName, "ExecuteLayoutPass");
        try
        {
            execute.Invoke(manager, null);
        }
        catch (System.Reflection.TargetInvocationException error) when (error.InnerException is not null)
        {
            // Preserve the genuine framework exception and original stack so the
            // narrow expected-fault filter above still checks ExecuteArrangePass.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

}
