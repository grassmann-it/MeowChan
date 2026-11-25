using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopSmoke;

// GL3 (smoke id "gallery-toolbar", step "layout menu"): the "Layout" button beside "More", its flyout and the same rows on a right click of free
// dock area. Each row changes exactly one block, the flyout stays open for a switch, Escape closes it and the focus returns to the button, "Reset layout"
// closes it and raises the undo (LayoutResetPerformed), a button never counts as free dock area. Captures of the open flyout and of the context
// menu are taken in the theme and language of the scenario (Light/Dark at 760x500 and 1440x920).
internal static partial class GalleryToolbarLayoutChecks
{
    private static async Task RequireLayoutMenuAsync(MainWindow window, GalleryViewer viewer, string tag, string output)
    {
        Button layout = viewer.LayoutButton;
        // Placement (UX spec S4 GL3+a): the button on the bar in Advanced from CompactToolbarWidth up; otherwise the same rows are a section of "More".
        bool onBar = viewer.CurrentWorkView == MeowChan.Desktop.WorkViews.WorkView.Advanced && viewer.Bounds.Width >= GalleryChromePlan.CompactWidth;
        Require(layout.IsEffectivelyVisible == onBar, tag + $": the Layout button is {(layout.IsEffectivelyVisible ? "on" : "off")} the bar, the plan says {(onBar ? "on" : "off")}.");
        if (!onBar)
        {
            await RequireLayoutSectionAsync(window, viewer, tag);
            return;
        }
        Require(ReferenceEquals(layout.Parent, viewer.OverflowButton.Parent)
            && ((Panel)layout.Parent!).Children.IndexOf(layout) == ((Panel)layout.Parent!).Children.IndexOf(viewer.OverflowButton) + 1,
            tag + ": the Layout button does not sit right beside the More button.");
        Require(!string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(layout)) && ToolTip.GetTip(layout) is string { Length: > 0 },
            tag + ": the Layout button has no name or tooltip.");

        // The keyboard opens it (Enter on the focused button); the first usable row takes the focus.
        layout.Focus(NavigationMethod.Tab);
        PressKey(layout, Key.Enter);
        await UntilAsync(() => viewer.IsLayoutMenuOpen && viewer.LayoutMenuRows.IsEffectivelyVisible && viewer.LayoutMenuRows.Bounds.Height > 0);
        await LayoutAsync(window);
        LayoutMenuPanel rows = viewer.LayoutMenuRows;
        Require(rows.Model!.Title.Contains(LayoutText("gallery.mode.view")), tag + ": the menu does not name the mode it edits.");
        Require(rows.Buttons.Count == GalleryLayoutMenu.RowIds.Count && rows.Buttons.All(row => row.IsEffectivelyVisible && row.Bounds.Height >= 28),
            tag + ": a layout row is missing or too small.");
        CaptureLayoutPopup(viewer, Path.Combine(output, $"smoke-gallery-layout-{tag}-flyout.png"));

        // A switch changes exactly one block, shows its state at once and keeps the menu open.
        Border info = Find<Border>(viewer, "GalleryOverlayInfo");
        Border tools = Find<Border>(viewer, "GalleryOverlayControls");
        GalleryLayoutChoice before = viewer.LayoutChoice(GalleryChromeMode.View);
        Require(info.IsVisible && tools.IsVisible, tag + ": the info line or the tools were not shown before the test.");
        Click(rows.ButtonOf(GalleryLayoutMenu.InfoId)!);
        await LayoutAsync(window);
        Require(viewer.IsLayoutMenuOpen && !info.IsVisible && tools.IsVisible && viewer.LayoutChoice(GalleryChromeMode.View) == before with { Info = false },
            tag + ": the info row did not hide exactly the info line and keep the menu open.");
        Require((rows.ButtonOf(GalleryLayoutMenu.InfoId) as Avalonia.Controls.Primitives.ToggleButton)?.IsChecked == false,
            tag + ": the info row does not show its new state.");
        Require(rows.ButtonOf(GalleryLayoutMenu.ResetId)!.IsEnabled, tag + ": the reset stays disabled after a change.");
        Click(rows.ButtonOf(GalleryLayoutMenu.InfoId)!);
        await LayoutAsync(window);
        Require(info.IsVisible && viewer.LayoutChoice(GalleryChromeMode.View) == before, tag + ": the info row did not bring the info line back (as no choice).");

        // Escape closes the menu and the focus returns to the Layout button.
        PressKey((Control)TopLevel.GetTopLevel(viewer.LayoutMenuRows)!.FocusManager!.GetFocusedElement()!, Key.Escape);
        await UntilAsync(() => !viewer.IsLayoutMenuOpen);
        await UntilAsync(() => window.FocusManager?.GetFocusedElement() == layout);

        // The same rows on a right click of free area; a button is never free area.
        StackPanel dock = Find<StackPanel>(viewer, "GalleryDock");
        Require(!viewer.IsFreeDockArea(viewer.OverflowButton, dock) && !viewer.IsFreeDockArea(Find<Button>(viewer, "GalleryNext"), dock),
            tag + ": a button counts as free dock area.");
        Require(viewer.IsFreeDockArea(info, dock) || !info.IsVisible, tag + ": the info line is not free dock area.");
        Require(viewer.ShowLayoutContextMenu(false), tag + ": the layout context menu did not open.");
        ContextMenu menu = viewer.LayoutContextMenu!;
        Require(menu.IsOpen, tag + ": the layout context menu is not open.");
        string[] ids = menu.Items.OfType<MenuItem>().Select(item => item.Tag as string ?? string.Empty).ToArray();
        Require(ids.SequenceEqual(GalleryLayoutMenu.RowIds), tag + ": the context menu does not hold the flyout's rows: " + string.Join(", ", ids));
        CaptureContextMenu(menu, Path.Combine(output, $"smoke-gallery-layout-{tag}-context.png"));
        MenuItem toolsItem = menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == GalleryLayoutMenu.ToolsId);
        toolsItem.Command!.Execute(null);
        await LayoutAsync(window);
        Require(!Find<Control>(viewer, "GalleryZoomTools").IsVisible && viewer.LayoutChoice(GalleryChromeMode.View).Tools == false,
            tag + ": the context menu's tools row did not hide the tools.");

        // "Reset layout" closes the flyout, takes the choice back and tells the window what it replaced (the undo).
        GalleryLayoutChoice? replaced = null;
        void Reset(GalleryChromeMode mode, GalleryLayoutChoice previous) => replaced = previous;
        viewer.LayoutResetPerformed += Reset;
        Require(viewer.SetLayoutMenuOpen(true), tag + ": the flyout could not reopen.");
        await UntilAsync(() => viewer.IsLayoutMenuOpen && viewer.LayoutMenuRows.ButtonOf(GalleryLayoutMenu.ResetId) != null);
        Click(viewer.LayoutMenuRows.ButtonOf(GalleryLayoutMenu.ResetId)!);
        await UntilAsync(() => !viewer.IsLayoutMenuOpen);
        viewer.LayoutResetPerformed -= Reset;
        await LayoutAsync(window);
        Require(replaced == before with { Tools = false } && viewer.LayoutChoice(GalleryChromeMode.View) == GalleryLayoutChoice.Default
            && Find<Control>(viewer, "GalleryZoomTools").IsVisible && info.IsVisible,
            tag + ": the reset did not restore the view's default or did not report the replaced choice.");
        // The undo: the old choice comes back through ChangeLayoutChoice, then the default again for the next scenario.
        viewer.ChangeLayoutChoice(GalleryChromeMode.View, replaced!);
        await LayoutAsync(window);
        Require(!Find<Control>(viewer, "GalleryZoomTools").IsVisible, tag + ": the undo did not bring the choice back.");
        viewer.ResetLayout(GalleryChromeMode.View);
        await LayoutAsync(window);
        Console.WriteLine("SMOKE gallery layout menu " + tag + ": button beside More, flyout rows, one block per row, Escape returns the focus, right-click rows equal flyout rows, reset with undo.");
    }

    // Simple/Barebones and below CompactWidth: the rows are a section of "More" (switches keep the menu open, "Reset layout" closes it).
    private static async Task RequireLayoutSectionAsync(MainWindow window, GalleryViewer viewer, string tag)
    {
        Require(viewer.LayoutSection.IsVisible, tag + ": neither the Layout button nor its section under More is shown.");
        viewer.SetOverflowOpen(true);
        await UntilAsync(() => viewer.LayoutSectionRows.IsEffectivelyVisible && viewer.LayoutSectionRows.Bounds.Height > 0);
        await LayoutAsync(window);
        LayoutMenuPanel rows = viewer.LayoutSectionRows;
        Require(rows.Buttons.Count == GalleryLayoutMenu.RowIds.Count, tag + ": the section under More does not hold the layout rows.");
        GalleryLayoutChoice before = viewer.LayoutChoice(GalleryChromeMode.View);
        Click(rows.ButtonOf(GalleryLayoutMenu.InfoId)!);
        await LayoutAsync(window);
        Require(viewer.LayoutChoice(GalleryChromeMode.View) == before with { Info = false } && viewer.IsOverflowOpen,
            tag + ": a switch in the section did not change exactly one block or closed More.");
        Click(rows.ButtonOf(GalleryLayoutMenu.ResetId)!);
        await UntilAsync(() => !viewer.IsOverflowOpen);
        Require(viewer.LayoutChoice(GalleryChromeMode.View) == GalleryLayoutChoice.Default, tag + ": Reset layout in the section did not restore the default.");
        Console.WriteLine("SMOKE gallery layout section under More " + tag + ": rows, one block per switch, reset closes.");
    }

    private static string LayoutText(string key) => MeowChan.Desktop.Localization.Texts.T(key);

    private static void CaptureLayoutPopup(GalleryViewer viewer, string path)
    {
        if (TopLevel.GetTopLevel(viewer.LayoutMenuRows) is not { } popup) return;
        double scale = popup.RenderScaling;
        Size size = popup.ClientSize;
        if (size.Width < 1 || size.Height < 1) return;
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(popup);
        bitmap.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE popup render: " + path);
    }

    private static void CaptureContextMenu(ContextMenu menu, string path)
    {
        if (menu.Parent is not Control surface || TopLevel.GetTopLevel(surface) is not { } popup) return;
        double scale = popup.RenderScaling;
        Size size = popup.ClientSize;
        if (size.Width < 1 || size.Height < 1) return;
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(popup);
        bitmap.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine("SMOKE popup render: " + path);
    }
}
