using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Archive;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class LargeReaderHostingSafetyTests
{
    // Every production checkpoint is a separate test. A new checkpoint cannot
    // silently go untested; Assert.True(reached) also rejects dead fault seams.
    public static IEnumerable<object[]> FailureSteps()
    {
        string[] files = ["ArchivePage.Reader.cs", "ArchivePage.MediaSidebar.cs", "ArchivePage.ReaderHosting.cs"];
        return files.SelectMany(file => Regex.Matches(Source("Desktop/Views/Archive/" + file),
                "ReaderHostingStep\\(\"([^\"]+)\"\\)").Select(match => match.Groups[1].Value))
            .Distinct().Select(step => new object[] { step });
    }

    [Theory]
    [MemberData(nameof(FailureSteps))]
    public void EachHostingFailureRestoresOneOwnerAndTheShellCanOpenAgain(string failingStep) => WithPage((page, root, host) =>
    {
        // Start with the sidebar in the embedded host so both removals and both
        // additions of the sidebar transfer run, not just the empty-host path.
        bool coldSidebar = failingStep is "sidebar.created" or "sidebar.configured";
        if (!coldSidebar)
            typeof(ArchivePage).GetMethod("MoveMediaSidebarTo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, [page.FindControl<Grid>("ArchiveReaderPane")!]);
        Control[] sidebarSurfaces = page.FindControl<Grid>("ArchiveReaderPane")!.Children
            .Where(child => child is ReaderMediaSidebar or GridSplitter).ToArray();
        if (!coldSidebar) Assert.Equal(2, sidebarSurfaces.Length);
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        Border export = page.FindControl<Border>("ArchiveExportDialog")!;
        var margin = pane.Margin;
        int column = Grid.GetColumn(pane);
        pane.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Local);
        var injected = new InvalidOperationException("injected: " + failingStep);
        bool reached = false;
        page.ReaderHostingStepCompletedForTests = step =>
        {
            if (step != failingStep) return;
            reached = true;
            throw injected;
        };

        Exception failure = Assert.Throws<InvalidOperationException>(() =>
            page.HostLargeReader(host, () => { }, () => page.SetReaderScope([])));
        Assert.True(reached, "Unreached failure checkpoint: " + failingStep);
        Assert.Same(injected, failure); // no swallowed/replaced original failure
        AssertRestored(page, root, host, pane, export);
        foreach (Control sidebarSurface in sidebarSurfaces)
        {
            Assert.Same(pane, sidebarSurface.Parent);
            AssertOneOwner(root, sidebarSurface);
        }
        Assert.Equal(margin, pane.Margin);
        Assert.Equal(column, Grid.GetColumn(pane));
        Assert.Equal(KeyboardNavigationMode.Local, pane.GetValue(KeyboardNavigation.TabNavigationProperty));
        pane.ClearValue(KeyboardNavigation.TabNavigationProperty);
        Assert.Equal(KeyboardNavigationMode.Continue, pane.GetValue(KeyboardNavigation.TabNavigationProperty));

        // A surviving workspace action still routes, then a second open and
        // normal close exercise usability, not merely Parent != null.
        Button workspaceAction = new();
        root.Children.Add(workspaceAction);
        int actions = 0;
        workspaceAction.Click += (_, _) => actions++;
        workspaceAction.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(1, actions);
        page.ReaderHostingStepCompletedForTests = null;
        page.HostLargeReader(host, () => { }, () => page.SetReaderScope([]));
        Assert.True(host.IsVisible);
        Assert.True(page.IsLargeReader);
        AssertOneOwner(root, pane);
        AssertOneOwner(root, export);
        Control content = Assert.IsAssignableFrom<Control>(host.Content);
        host.Content = null;
        host.IsVisible = false;
        page.AttachReader(content);
        AssertRestored(page, root, host, pane, export);
    });

    [Fact]
    public void NormalSidebarAndReaderTransfersStayInTheShellPresentation() => WithPage((page, root, host) =>
    {
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        typeof(ArchivePage).GetMethod("MoveMediaSidebarTo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [pane]);
        ReaderMediaSidebar sidebar = Assert.IsType<ReaderMediaSidebar>(page.MediaSidebar);
        GridSplitter splitter = Assert.Single(pane.Children.OfType<GridSplitter>());
        for (int cycle = 0; cycle < 3; cycle++)
        {
            pane.InvalidateMeasure();
            sidebar.InvalidateMeasure();
            Assert.Contains(root, pane.GetLogicalAncestors());
            page.HostLargeReader(host, () => { }, () => { });
            Assert.Contains(root, pane.GetLogicalAncestors());
            Assert.Contains(root, sidebar.GetLogicalAncestors());
            Assert.Same(pane.Parent, sidebar.Parent);
            Assert.Same(sidebar.Parent, splitter.Parent);
            Control content = Assert.IsAssignableFrom<Control>(host.Content);
            host.Content = null;
            host.IsVisible = false;
            page.AttachReader(content);
            // The normal sidebar return is deferred until layout asks to show it.
            typeof(ArchivePage).GetMethod("MoveMediaSidebarTo", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, [pane]);
            Assert.Contains(root, pane.GetLogicalAncestors());
            Assert.Same(pane, sidebar.Parent);
            Assert.Same(pane, splitter.Parent);
            AssertOneOwner(root, sidebar);
            AssertOneOwner(root, pane);
        }
        // The production host is a ContentControl in MainWindow, not another Window.
        string shell = Source("Desktop/Shell/MainWindow.axaml");
        Assert.Contains("x:Name=\"ReaderHost\"", shell);
        Assert.DoesNotContain("new Window", Source("Desktop/Shell/MainWindow.Reader.cs"));
    });

    [Fact]
    public void GalleryPlacementAlreadyUsesTheOriginalDetachDrainTransaction()
    {
        string overlay = Source("Desktop/GalleryViewer.Overlay.cs");
        Assert.Contains("MoveOverlayControl(_contextPanel, overlay ? OverlayHost : _body)", overlay);
        Assert.Contains("MoveOverlayControl(_chrome,", overlay);
        Assert.Contains("GalleryControlTransfer.Move(control, target,", overlay);
        string transfer = Source("Desktop/GalleryControlTransfer.cs");
        int detach = transfer.IndexOf("parent.Children.Remove(_control);", transfer.IndexOf("private void Execute", StringComparison.Ordinal), StringComparison.Ordinal);
        int drain = transfer.IndexOf("root.UpdateLayout();", detach, StringComparison.Ordinal);
        int attach = transfer.IndexOf("target.Children.Add(_control)", drain, StringComparison.Ordinal);
        Assert.True(detach >= 0 && drain > detach && attach > drain, "Detach, real source-root drain, then attach must retain their order.");
        Assert.Contains("bool sameRoot =", transfer); // same-presentation moves intentionally need no cross-root drain
    }

    [Fact]
    public void RecoveryContinuesAfterARepairThrowsAndRetainsBothFailures()
    {
        var original = new InvalidOperationException("open");
        var repair = new InvalidOperationException("repair");
        bool repaired = false;
        var failure = Assert.IsType<AggregateException>(ArchivePage.RollBackReaderHosting(original,
            () => throw repair, () => repaired = true));
        Assert.True(repaired);
        Assert.Equal(new Exception[] { original, repair }, failure.InnerExceptions);
        Assert.False(UnhandledErrors.TryRecover(failure, "reader.open.hosting")); // tests never turn a failure green
    }

    [Fact]
    public void AggregatingRepairFailuresCannotTurnOutOfMemoryIntoARecoverableError()
    {
        Exception failure = ArchivePage.RollBackReaderHosting(new OutOfMemoryException("injected"),
            () => throw new InvalidOperationException("repair"));
        Assert.True(ArchivePage.IsFatalReaderHostingFailure(failure));
        Assert.False(ArchivePage.IsFatalReaderHostingFailure(new InvalidOperationException("recoverable")));
    }

    [Fact]
    public void FailureInsideTheHostSetterAlsoRollsBack() => WithPage((page, root, host) =>
    {
        var injected = new InvalidOperationException("content notification");
        bool fired = false;
        host.PropertyChanged += (_, args) =>
        {
            if (args.Property != ContentControl.ContentProperty || fired || args.NewValue == null) return;
            fired = true;
            throw injected;
        };
        Assert.Same(injected, Assert.Throws<InvalidOperationException>(() => page.HostLargeReader(host, () => { }, () => { })));
        Assert.True(fired);
        AssertRestored(page, root, host, page.FindControl<Grid>("ArchiveReaderPane")!, page.FindControl<Border>("ArchiveExportDialog")!);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExceptionsInsidePresentationAndScopeCallbacksRestoreOwnership(bool inScope) => WithPage((page, root, host) =>
    {
        var injected = new InvalidOperationException("setup callback");
        bool presented = false;
        Assert.Same(injected, Assert.Throws<InvalidOperationException>(() => page.HostLargeReader(host,
            () => { presented = true; if (!inScope) throw injected; },
            () => throw injected)));
        Assert.True(presented);
        AssertRestored(page, root, host, page.FindControl<Grid>("ArchiveReaderPane")!, page.FindControl<Border>("ArchiveExportDialog")!);
    });

    [Fact]
    public void ALayoutRepairFailureStillRestoresOwnershipAndIsNotLost() => WithPage((page, root, host) =>
    {
        Grid pane = page.FindControl<Grid>("ArchiveReaderPane")!;
        var original = new InvalidOperationException("scope");
        var secondary = new InvalidOperationException("margin repair");
        bool repairing = false, fired = false;
        var margin = pane.Margin;
        pane.PropertyChanged += (_, args) =>
        {
            if (!repairing || fired || args.Property != Control.MarginProperty || pane.Margin != margin) return;
            fired = true;
            throw secondary;
        };
        var failure = Assert.Throws<AggregateException>(() => page.HostLargeReader(host, () => { }, () =>
        {
            repairing = true;
            throw original;
        }));
        Assert.True(fired);
        Assert.Equal(new Exception[] { original, secondary }, failure.Flatten().InnerExceptions);
        AssertRestored(page, root, host, pane, page.FindControl<Border>("ArchiveExportDialog")!);
        page.HostLargeReader(host, () => { }, () => { });
        Assert.True(page.IsLargeReader);
        AssertOneOwner(root, pane);
    });

    private static void AssertRestored(ArchivePage page, Panel root, ContentControl host, Grid pane, Border export)
    {
        Assert.Null(host.Content);
        Assert.False(host.IsVisible);
        Assert.False(page.IsLargeReader);
        Assert.Same(page.FindControl<Grid>("ArchiveSplit"), pane.Parent);
        Assert.Same(page.FindControl<Grid>("ArchiveRoot"), export.Parent);
        AssertOneOwner(root, pane);
        AssertOneOwner(root, export);
        foreach (Control child in pane.Children.Where(child => child is ReaderMediaSidebar or GridSplitter))
            AssertOneOwner(root, child);
        Assert.False(page.FindControl<Button>("ArchiveCloseReader")!.IsVisible);
        Assert.True(page.FindControl<Button>("ArchiveReadPosts")!.IsVisible);
    }

    private static void AssertOneOwner(Control root, Control surface) =>
        Assert.Single(root.GetLogicalDescendants(), child => ReferenceEquals(child, surface));

    private static void WithPage(Action<ArchivePage, StackPanel, ContentControl> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        ArchivePage page = new();
        var id = new ResourceId("synthetic-A", "board-A", "42");
        var view = new ArchivedThreadView(new ArchiveThreadSummary(id, "https://example.invalid/42", "hosting fixture",
            "/synthetic-never-opened/media", ThreadSourceState.NotFound, true, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch, 1, 1), [new CapturedPost("42", 0, PostFieldPresence.All, Html: "stored post")]);
        typeof(ArchivePage).GetMethod("ShowCapturedView", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [view, 0, "42"]);
        Assert.Same(view, page.CurrentThread);
        ContentControl host = new() { IsVisible = false };
        StackPanel root = new();
        root.Children.Add(page);
        root.Children.Add(host);
        try { test(page, root, host); }
        finally { page.ReaderHostingStepCompletedForTests = null; (page as IDisposable)?.Dispose(); }
    });

    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));
}
