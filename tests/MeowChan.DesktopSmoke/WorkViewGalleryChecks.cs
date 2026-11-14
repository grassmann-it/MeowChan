using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Gallery cells of the work-views smoke (WV4, work-views-gallery). The gallery stays open while the matrix switches the view, so
/// every cell also proves the change of view at run time. Per cell: the movable tools sit on the bar or under More as the policy
/// says, the "where am I" line follows its gate, the way back stands alone at the top right with the star directly left of it,
/// the post panel starts below that row, and a menu opened and closed returns focus to More.
/// </summary>
internal static class WorkViewGalleryChecks
{
    private static GalleryViewer Viewer(MainWindow window) =>
        window.FindControl<ContentControl>("GalleryHost")?.Content as GalleryViewer
        ?? throw new InvalidOperationException("The gallery is not open.");

    private static Control Tool(GalleryViewer viewer, string id) =>
        viewer.WorkViewControl(id) ?? throw new InvalidOperationException($"gallery: no control is registered for {id}.");

    /// <summary>No orphaned row: every control-backed row of the gallery is registered by this viewer (the transport only exists for a video).</summary>
    internal static void CheckRegistrations(MainWindow window)
    {
        GalleryViewer viewer = Viewer(window);
        HashSet<string> registered = viewer.RegisteredWorkViewIds.ToHashSet(StringComparer.Ordinal);
        string[] orphaned = WorkViewPolicy.Entries
            .Where(entry => entry.Page == WorkViewPage.Gallery && entry.Kind is WorkViewKind.Action or WorkViewKind.Element
                && entry.Id != "gallery.video.transport" && !registered.Contains(entry.Id)).Select(entry => entry.Id).ToArray();
        if (orphaned.Length > 0)
            throw new InvalidOperationException("gallery: work-view rows without a registered control: " + string.Join(", ", orphaned));
        Console.WriteLine($"SMOKE work-views registrations: gallery: {registered.Count} ids registered, no orphaned policy row.");
    }

    internal static async Task CheckAsync(MainWindow window, WorkView view, string viewName)
    {
        GalleryViewer viewer = Viewer(window);
        string scene = $"gallery/{viewName}";
        if (viewer.CurrentWorkView != view)
            throw new InvalidOperationException($"{scene}: the viewer is laid out for {viewer.CurrentWorkView}.");
        CheckTools(viewer, view, scene);
        CheckScope(viewer, view, scene);
        CheckTopRow(viewer, scene);
        await CheckPostPanelBelowTopRowAsync(window, viewer, scene);
        await CheckMoreAsync(window, viewer, view, scene);
        Console.WriteLine($"SMOKE work-views gallery {viewName}: bar=[{string.Join(", ", viewer.ToolbarToolIds)}]; More=[{string.Join(", ", viewer.MenuToolIds)}].");
    }

    // The fixture shows no slideshow, so the policy's own answer for the view is the expectation.
    private static void CheckTools(GalleryViewer viewer, WorkView view, string scene)
    {
        string[] expected = WorkViewPolicy.GalleryBarToolIds(view).ToArray();
        if (!viewer.ToolbarToolIds.SequenceEqual(expected))
            throw new InvalidOperationException($"{scene}: the bar holds [{string.Join(", ", viewer.ToolbarToolIds)}], the policy says [{string.Join(", ", expected)}].");
        foreach (string id in WorkViewPolicy.GalleryMovableToolIds)
        {
            Control control = Tool(viewer, id);
            bool onBar = expected.Contains(id);
            // A control of a rule that only applies to a slideshow (the seconds number) is not shown outside one.
            bool shown = onBar ? viewer.WorkViewToolGroup.Children.Contains(control) || id == "gallery.toolbar.slideshow" : control is not Button || viewer.OverflowItems.Contains((Button)control);
            if (!shown)
                throw new InvalidOperationException($"{scene}: {id} is not where the policy puts it ({(onBar ? "bar" : "More")}).");
            if (control is Button button && onBar && !button.IsEffectivelyVisible)
                throw new InvalidOperationException($"{scene}: {id} is on the bar but not visible.");
            if (control is Button inMenu && !onBar && inMenu.IsEffectivelyVisible && !viewer.IsOverflowOpen)
                throw new InvalidOperationException($"{scene}: {id} belongs under More but shows on the surface.");
        }
        if (viewer.WorkViewToolGroup.IsVisible != (viewer.WorkViewToolGroup.Children.Count > 0 && !viewer.IsMediaFocus))
            throw new InvalidOperationException($"{scene}: the tool group is {(viewer.WorkViewToolGroup.IsVisible ? "shown" : "hidden")} for {viewer.WorkViewToolGroup.Children.Count} tools.");
    }

    private static void CheckScope(GalleryViewer viewer, WorkView view, string scene)
    {
        Control scope = Tool(viewer, "gallery.info.scope");
        bool expected = WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.info.scope", view));
        if (scope.IsVisible != expected)
            throw new InvalidOperationException($"{scene}: the scope line is {(scope.IsVisible ? "shown" : "hidden")}, the policy says {WorkViewPolicy.Resolve("gallery.info.scope", view)}.");
        // The position between the arrows is never reduced.
        if (!Tool(viewer, "gallery.position").IsVisible) throw new InvalidOperationException($"{scene}: the position is hidden.");
    }

    // Tom's rule: the way back alone at the top right edge, the star directly left of it, the other actions apart from both.
    private static void CheckTopRow(GalleryViewer viewer, string scene)
    {
        Control back = Tool(viewer, "gallery.back");
        Point? backOrigin = back.TranslatePoint(default, viewer);
        if (!back.IsEffectivelyVisible || backOrigin is not { } origin || origin.X + back.Bounds.Width < viewer.Bounds.Width - 24
            || origin.X + back.Bounds.Width > viewer.Bounds.Width + 1 || origin.Y > 24)
            throw new InvalidOperationException($"{scene}: the way back is not at the top right edge ({backOrigin}, {back.Bounds.Size} in {viewer.Bounds.Size}).");
        Control star = Tool(viewer, "gallery.favorite.indicator");
        if (star.IsVisible && star.TranslatePoint(default, viewer) is { } starOrigin)
        {
            double gap = origin.X - (starOrigin.X + star.Bounds.Width);
            if (gap < 0 || gap > 24 || Math.Abs(starOrigin.Y - origin.Y) > 16)
                throw new InvalidOperationException($"{scene}: the star is not directly left of the way back (gap {gap:0.#}, rows {starOrigin.Y:0.#}/{origin.Y:0.#}).");
        }
        // Nothing else of the viewer's toolbar stands in the top row beside the two.
        foreach (Control tool in new[] { Tool(viewer, "gallery.nav.previous"), Tool(viewer, "gallery.nav.next"), Tool(viewer, "gallery.favorite"), Tool(viewer, "gallery.toolbar.more") })
            if (tool.IsEffectivelyVisible && tool.TranslatePoint(default, viewer) is { } point && point.Y < origin.Y + back.Bounds.Height + 8
                && point.X + tool.Bounds.Width > origin.X - 40)
                throw new InvalidOperationException($"{scene}: {tool.Name} shares the top row with the way back.");
    }

    // The post panel (and the tag panel slot that will share it) starts below the way back and the star, never under them.
    private static async Task CheckPostPanelBelowTopRowAsync(MainWindow window, GalleryViewer viewer, string scene)
    {
        ToggleButton toggle = (ToggleButton)Tool(viewer, "gallery.toolbar.post-panel");
        Border panel = (Border)Tool(viewer, "gallery.post-panel");
        bool wasOpen = toggle.IsChecked == true;
        if (!wasOpen) toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            await Program.WaitUntilAsync(() => panel.IsVisible && panel.Bounds.Height > 0);
            for (int pass = 0; pass < 2; pass++)
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, Avalonia.Threading.DispatcherPriority.Background);
            Control back = Tool(viewer, "gallery.back");
            double backBottom = back.TranslatePoint(new Point(0, back.Bounds.Height), viewer)?.Y ?? 0;
            double panelTop = panel.TranslatePoint(default, viewer)?.Y ?? 0;
            if (panelTop < backBottom - 0.5)
                throw new InvalidOperationException($"{scene}: the post panel starts at {panelTop:0.#} under the way back (bottom {backBottom:0.#}).");
        }
        finally
        {
            if (!wasOpen && toggle.IsChecked == true) toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => wasOpen || !panel.IsVisible);
        }
    }

    // More opens with the rows of its tools (in policy order among the policy's menu tools) and, closed again, keeps the keyboard on More (A19).
    private static async Task CheckMoreAsync(MainWindow window, GalleryViewer viewer, WorkView view, string scene)
    {
        Button more = viewer.OverflowButton;
        more.Focus(NavigationMethod.Tab);
        viewer.SetOverflowOpen(true);
        await Program.WaitUntilAsync(() => viewer.IsOverflowOpen && viewer.OverflowMenuContent.IsEffectivelyVisible);
        foreach (string id in viewer.MenuToolIds)
            if (Tool(viewer, id) is Button row && !viewer.OverflowItems.Contains(row))
                throw new InvalidOperationException($"{scene}: {id} is not a row of More.");
        foreach (string id in viewer.ToolbarToolIds)
            if (Tool(viewer, id) is Button { Tag: not null } moved && viewer.OverflowItems.Contains(moved))
                throw new InvalidOperationException($"{scene}: {id} is on the bar and also a row of More.");
        viewer.SetOverflowOpen(false);
        await Program.WaitUntilAsync(() => !viewer.IsOverflowOpen);
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, Avalonia.Threading.DispatcherPriority.Background);
        if (!more.IsKeyboardFocusWithin)
            throw new InvalidOperationException($"{scene}: focus did not return to More after the menu closed.");
        // A19 for the dialogs: a reduced command's dialog returns to More, a command on the bar returns to its own origin.
        Control? target = viewer.FocusReturnTarget(null, "media.tags.edit");
        bool reduced = WorkViewPolicy.GalleryCommandReduced("media.tags.edit", view);
        if (reduced != ReferenceEquals(target, more))
            throw new InvalidOperationException($"{scene}: the tags dialog would return focus to {(target == null ? "nothing" : target.Name)}, reduced={reduced}.");
    }
}
