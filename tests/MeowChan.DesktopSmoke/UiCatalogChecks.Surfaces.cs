using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopSmoke;

// Popups of the shell and of the pages that are not dialogs: app menu, footer flyouts, the header display menu, help popovers and
// the legacy right-click menus. Every capture is its own step: a menu that cannot be opened is a listed gap, not a failed run.
internal static partial class UiCatalogChecks
{
    private static partial void AddSurfaceScenes(List<Scene> s)
    {
        s.Add(new("shell-menus", ShellMenusAsync));
        s.Add(new("help-popovers", HelpPopoversAsync));
        s.Add(new("row-menus", RowMenusAsync));
    }

    private static async Task ShellMenusAsync(MainWindow w)
    {
        await Enter(w, "downloads");
        await StepAsync("shell-app-menu", async () =>
        {
            MenuItem root = Need<MenuItem>(w, "WorkspaceMenuItem");
            root.IsSubMenuOpen = true;
            try
            {
                MenuItem first = Need<MenuItem>(w, "DownloadsMenuItem");
                await UntilAsync(() => root.IsSubMenuOpen && first.Bounds.Width > 0 && TopLevel.GetTopLevel(first) is { } top && !ReferenceEquals(top, w), "the app menu did not open");
                await Settle(w, 250);
                UiCatalog.RecordSurface(SurfaceOf(first, w), "shell-app-menu", "menu", "App menu (the dots at the right edge of the title bar): pages, tools, application");
            }
            finally { root.IsSubMenuOpen = false; await Settle(w, 150); }
        });
        await StepAsync("shell-more-flyout", async () =>
        {
            Button more = Need<Button>(w, "ToolsNavigationButton");
            MenuFlyout flyout = (MenuFlyout)more.Flyout!;
            flyout.ShowAt(more);
            try
            {
                await UntilAsync(() => flyout.IsOpen && flyout.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsVisible) is { Bounds.Width: > 0 }, "the More flyout did not open");
                await Settle(w, 250);
                UiCatalog.RecordSurface(SurfaceOf(flyout.Items.OfType<MenuItem>().First(i => i.IsVisible), w), "shell-more-flyout", "flyout", "Footer More flyout: the pages that do not fit the footer");
            }
            finally { flyout.Hide(); await Settle(w, 150); }
        });
        await StepAsync("shell-view-picker", async () =>
        {
            ComboBox combo = Need<ComboBox>(w, "WorkspaceModeComboBox");
            combo.IsDropDownOpen = true;
            try
            {
                await UntilAsync(() => combo.IsDropDownOpen && combo.GetVisualDescendants().OfType<ComboBoxItem>().FirstOrDefault() is { Bounds.Width: > 0 }, "the view picker did not open");
                await Settle(w, 250);
                UiCatalog.RecordSurface(SurfaceOf(combo.GetVisualDescendants().OfType<ComboBoxItem>().First(), w), "shell-view-picker", "dropdown", "Footer view picker: Barebones, Simple, Advanced");
            }
            finally { combo.IsDropDownOpen = false; await Settle(w, 150); }
        });
        await StepAsync("shell-header-io-menu", async () =>
        {
            ContextMenuHost host = w.HeaderIoMenuHost ?? throw new InvalidOperationException("No header display menu host");
            host.Show();
            try
            {
                await UntilAsync(() => host.OpenMenu is { Bounds.Width: > 0 }, "the header display menu did not open", 6);
                await Settle(w, 200);
                UiCatalog.RecordSurface(host.OpenMenu!, "shell-header-io-menu", "context menu", "Header display menu (right-click or Menu key on the speeds): graph arrangement, open statistics");
            }
            finally { host.Close(); await Settle(w, 120); }
        });
    }

    private static async Task HelpPopoversAsync(MainWindow w)
    {
        async Task Help(string page, string name, string key, Func<MainWindow, Control> scope)
        {
            await StepAsync(key, async () =>
            {
                await Enter(w, page);
                HelpButton help = Find<HelpButton>(scope(w), name) ?? throw new InvalidOperationException("No help button " + name);
                await FlyoutAsync(w, help.Flyout!, help, () => ((Flyout)help.Flyout!).Content as Control, key, "popover", "Help popover " + name);
            });
        }
        await Help("statistics", "StatisticsHelpButton", "help-popover-stats", x => x);
        await Help("rules", "RulesHelpButton", "help-popover-rules", x => x);
        await Help("plugins", "PluginsHelpButton", "help-popover-plugins", x => x);
    }

    // The legacy right-click menus of the data grids, opened the keyboard way (Shift+F10 / Menu key, no pointer position).
    private static async Task RowMenusAsync(MainWindow w)
    {
        await Enter(w, "downloads");
        await StepAsync("menu-downloads-thread-row", async () =>
        {
            DataGrid grid = Need<DataGrid>(w, "ThreadsDataGrid");
            grid.SelectedIndex = 0;
            await Settle(w, 200);
            grid.Focus();
            await MenuAsync(w, grid, "menu-downloads-thread-row", "Context menu of a thread row in Downloads (legacy ContextMenu)");
        });
        await StepAsync("menu-archive-table-row", async () =>
        {
            await Enter(w, "archive");
            ArchivePage a = w.ArchivePage;
            Click(Named<ToggleButton>(a, "ArchiveLayoutTable"));
            try
            {
                DataGrid table = Named<DataGrid>(a, "ArchiveThreadTable");
                await UntilAsync(() => table.IsVisible && table.Bounds.Height > 0, "the table did not show");
                table.SelectedIndex = 0;
                await Settle(w, 300);
                table.Focus();
                await MenuAsync(w, table, "menu-archive-table-row", "Context menu of a thread row in the archive table (legacy ContextMenu)");
            }
            finally { Click(Named<ToggleButton>(a, "ArchiveLayoutList")); await Settle(w, 150); }
        });
    }
}
