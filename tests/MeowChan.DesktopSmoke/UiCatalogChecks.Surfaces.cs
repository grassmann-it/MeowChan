using Avalonia;
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
        s.Add(new("text-buttons", TextButtonsAsync, Once: true));
    }

    private static async Task ShellMenusAsync(MainWindow w)
    {
        await Enter(w, "downloads");
        await StepAsync("shell-app-menu", async () =>
        {
            MenuItem root = w.FindControl<MenuItem>("WorkspaceMenuItem") ?? throw new InvalidOperationException("No app menu");
            root.IsSubMenuOpen = true;
            try
            {
                MenuItem first = root.Items.OfType<MenuItem>().FirstOrDefault() ?? throw new InvalidOperationException("No first menu item");
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
                await UntilAsync(() => combo.IsDropDownOpen && combo.ContainerFromIndex(0) is Control { Bounds.Width: > 0 }, "the view picker did not open");
                await Settle(w, 250);
                UiCatalog.RecordSurface(SurfaceOf((Control)combo.ContainerFromIndex(0)!, w), "shell-view-picker", "dropdown", "Footer view picker: Barebones, Simple, Advanced");
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
        // The Downloads results note (paragraph plus two long bullets: the hanging indent and the list rhythm). Its panel only shows
        // after an admission, so the step shows it for the capture and hides it again.
        await StepAsync("help-popover-admission-results", async () =>
        {
            await Enter(w, "downloads");
            Border panel = Need<Border>(w, "AdmissionResultsPanel");
            bool shown = panel.IsVisible;
            panel.IsVisible = true;
            try
            {
                await Settle(w, 200);
                HelpButton help = w.FindControl<HelpButton>("AdmissionResultsHelpButton") ?? throw new InvalidOperationException("No help button AdmissionResultsHelpButton");
                await FlyoutAsync(w, help.Flyout!, help, () => ((Flyout)help.Flyout!).Content as Control, "help-popover-admission-results", "popover", "Help popover AdmissionResultsHelpButton");
            }
            finally { panel.IsVisible = shown; }
        });
        // The plain "?" itself, four times enlarged beside its neighbours: rest, hover and pressed (pseudo classes set for the capture),
        // then keyboard focus as a window image (the focus ring lives in the adorner layer, not in the button).
        await StepAsync("help-button-states", async () =>
        {
            await Enter(w, "statistics");
            HelpButton help = Need<HelpButton>(w, "StatisticsHelpButton");
            Control row = help.Parent as Control ?? help;
            IPseudoClasses pseudo = help.Classes;
            UiCatalog.RecordSurface(row, "help-button-states", "rest x4", "Statistics header row with the help button, at rest", 4);
            foreach (string state in new[] { ":pointerover", ":pressed" })
            {
                pseudo.Set(state, true);
                try { await Settle(w, 120); UiCatalog.RecordSurface(row, "help-button-states", state.TrimStart(':') + " x4", "Help button " + state, 4); }
                finally { pseudo.Set(state, false); }
            }
            help.Focus(NavigationMethod.Tab);
            await Settle(w, 200);
            UiCatalog.Record(w, "help-button-states", "focus window", "Help button with keyboard focus (crop around the header)");
        });
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
            a.ArchiveLayoutPreference = "Table";
            try
            {
                DataGrid table = Named<DataGrid>(a, "ArchiveThreadTable");
                await UntilAsync(() => table.IsVisible && table.Bounds.Height > 0, "the table did not show");
                table.SelectedIndex = 0;
                await Settle(w, 300);
                table.Focus();
                await MenuAsync(w, table, "menu-archive-table-row", "Context menu of a thread row in the archive table (legacy ContextMenu)");
            }
            finally { a.ArchiveLayoutPreference = "List"; await Settle(w, 150); }
        });
    }

    // ---- Evidence for the catalog section "Textknöpfe ohne Icon": every visible button of every page (Advanced view, all controls
    // direct) that shows text and has no icon, read from the live visual tree. Written to text-buttons.json next to the images. ----

    private static bool HasIcon(Button button) =>
        button.Classes.Contains("command-icon") || button.Classes.Contains("icon-only") || button.Classes.Contains("icon-square")
        || button.GetVisualDescendants().Any(v => v is Avalonia.Controls.Shapes.Path or Image || (v is Border { OpacityMask: not null }) || (v is Border b && b.Classes.Contains("icon")));

    private static string? ShownText(Button button)
    {
        if (button.Content is string text && text.Trim().Length > 0) return text.Trim();
        return button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
    }

    private static void CollectTextButtons(MainWindow w, string view, List<object> found, HashSet<string> seen)
    {
        foreach (Button button in w.GetVisualDescendants().OfType<Button>())
        {
            if (!button.IsEffectivelyVisible || button.Bounds.Width <= 0 || HasIcon(button)) continue;
            string? text = ShownText(button);
            if (text == null) continue;
            string name = button.Name ?? "";
            string id = view + "|" + name + "|" + text;
            if (!seen.Add(id)) continue;
            string chain = string.Join(">", button.GetVisualAncestors().OfType<Control>().Select(c => c.Name).Where(n => !string.IsNullOrEmpty(n)).Take(3));
            found.Add(new { view, name, text, classes = string.Join(" ", button.Classes.Where(c => !c.StartsWith(':'))), parents = chain, tag = button.Tag as string ?? "" });
        }
    }

    private static async Task TextButtonsAsync(MainWindow w)
    {
        List<object> found = [];
        HashSet<string> seen = [];
        await PresentationChecks.SwitchModeAsync(w, "Advanced");
        foreach (string page in new[] { "downloads", "archive", "reader", "gallery", "media", "rules", "sources", "plugins", "statistics", "diagnostics", "settings" })
            await StepAsync("text-buttons:" + page, async () => { await Enter(w, page); CollectTextButtons(w, page, found, seen); });
        await StepAsync("text-buttons:booru", async () => { await EnterBooruAsync(w); await _booru!.MaterializeAsync(); CollectTextButtons(w, "booru", found, seen); });
        await StepAsync("text-buttons:archive-filters", async () =>
        {
            await Enter(w, "archive");
            w.ArchivePage.ToggleFilterSurfaceByShortcut();
            await UntilAsync(() => w.ArchivePage.IsFilterSurfaceOpen, "the filter surface did not open");
            await Settle(w, 300);
            CollectTextButtons(w, "archive-filters", found, seen);
            w.ArchivePage.CloseFilterSurface(restoreFocus: false);
        });
        await StepAsync("text-buttons:media-filters", async () =>
        {
            await Enter(w, "media");
            MediaPage page = w.MediaPageForTests!;
            Need<ToggleButton>(page, "MediaFilters").IsChecked = true;
            await UntilAsync(() => page.IsFilterSurfaceOpen, "the media filter surface did not open");
            await Settle(w, 300);
            CollectTextButtons(w, "media-filters", found, seen);
            page.CloseFilterSurface(restoreFocus: false);
        });
        File.WriteAllText(Path.Combine(UiCatalog.OutputDirectory, "text-buttons.json"),
            System.Text.Json.JsonSerializer.Serialize(found, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Console.WriteLine("SMOKE ui catalog text buttons: " + found.Count);
    }
}

/// <summary>Text-only button probe for the dialog gallery: every window it renders is scanned once (English, first theme) and the
/// result is written next to its manifest (text-buttons-dialogs.json) for the catalog section "Textknöpfe ohne Icon".</summary>
internal static class TextButtonProbe
{
    private static readonly List<object> Found = [];
    private static readonly HashSet<string> Seen = [];

    internal static void Reset() { Found.Clear(); Seen.Clear(); }

    internal static void Capture(Visual root, string dialog)
    {
        if (root is MainWindow) return;
        if (!string.Equals(Avalonia.Application.Current is null ? "en" : MeowChan.Desktop.Localization.Texts.Language, "en", StringComparison.Ordinal)) return;
        foreach (Button button in root.GetVisualDescendants().OfType<Button>())
        {
            if (!button.IsEffectivelyVisible || button.Bounds.Width <= 0) continue;
            bool icon = button.Classes.Contains("command-icon") || button.Classes.Contains("icon-only") || button.Classes.Contains("icon-square")
                || button.GetVisualDescendants().Any(v => v is Avalonia.Controls.Shapes.Path or Image || (v is Border { OpacityMask: not null }) || (v is Border b && b.Classes.Contains("icon")));
            if (icon) continue;
            string? text = button.Content is string c && c.Trim().Length > 0 ? c.Trim()
                : button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
            if (text == null) continue;
            bool footer = button.GetVisualAncestors().OfType<Control>().Any(a => a.Name == DialogFrame.FooterName);
            string name = button.Name ?? "";
            if (!Seen.Add(dialog + "|" + name + "|" + text)) continue;
            Found.Add(new { dialog, name, text, footer, classes = string.Join(" ", button.Classes.Where(k => !k.StartsWith(':'))) });
        }
    }

    internal static void Write(string directory) =>
        File.WriteAllText(Path.Combine(directory, "text-buttons-dialogs.json"),
            System.Text.Json.JsonSerializer.Serialize(Found, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
}
