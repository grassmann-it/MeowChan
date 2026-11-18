using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// CM1 (smoke id context-menu-layout): popup menus on the real window in Light and Dark, English and German. A ContextMenu made by the
/// builder with very long labels, one with short labels, one with the catalog commands that have the longest key names, a MenuFlyout with
/// long key names and the footer "More" flyout are opened and measured: the menu is between 224 and 416 dip, no row's shortcut is clipped,
/// every shortcut ends on the same right edge, a label has at least 24 dip of room before its shortcut, and a label that does not fit
/// (only at the maximum width) is trimmed with an ellipsis and says its text as a tooltip. Captures per scene next to the assembly.
/// </summary>
internal static class ContextMenuLayoutChecks
{
    private const double Gap = 24;
    private const double Tolerance = 0.75;
    // A trimmed label ends where its ellipsis fits: the menu may stop up to a character short of the maximum; edges of fractional rows differ by a pixel.
    private const double TrimSlack = 8;
    private const double EdgeTolerance = 1.5;

    internal static async Task RunAsync(MainWindow window)
    {
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        string originalLanguage = Texts.Language;
        List<string> failures = [];
        Exception? failureBeforeCleanup = null;
        try
        {
            Control anchor = window.FindControl<Button>("ToolsNavigationButton") ?? throw new InvalidOperationException("The footer has no More button.");
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                await UiCatalogChecks.Settle(window, 150);
                foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Application.Current.RequestedThemeVariant = theme;
                    await Program.WaitUntilAsync(() => window.ActualThemeVariant == theme);
                    string prefix = language + "-" + theme.ToString().ToLowerInvariant() + "-";
                    await ContextAsync(window, anchor, prefix + "builder-long", LongBuilder(), mustCap: true, failures);
                    await ContextAsync(window, anchor, prefix + "builder-short", ShortBuilder(), mustCap: false, failures);
                    await ContextAsync(window, anchor, prefix + "catalog-gestures", CatalogBuilder(), mustCap: false, failures);
                    await FlyoutAsync(window, anchor, prefix + "flyout-long-keys", LongFlyout(), mustCap: true, failures);
                    await FlyoutAsync(window, anchor, prefix + "flyout-catalog", CatalogFlyout(), mustCap: false, failures);
                    if (anchor is Button { Flyout: MenuFlyout more })
                        await FlyoutAsync(window, anchor, prefix + "footer-more", more, mustCap: false, failures);
                    await AppMenuAsync(window, prefix + "app-menu", failures);
                }
            }
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                Application.Current!.RequestedThemeVariant = originalTheme;
                Texts.SetLanguage(originalLanguage);
                await UiCatalogChecks.Settle(window, 100);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in ContextMenuLayoutChecks: " + exception));
        }
        if (failures.Count > 0) throw new InvalidOperationException("Context menu layout failed:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private const string VeryLong = "Add or edit the bookmark of this post and keep it with the thread in the reading history of this board…";

    private static ContextMenuBuilder LongBuilder() => new ContextMenuBuilder()
        .Command("gallery.open", () => { })
        .Check("favorite", "Remove from favorites", true, () => { })
        .Command("gallery.open", () => { }, label: VeryLong, entryId: "long-with-key")
        .Item("details", "Show media details", () => { })
        .Section()
        .Item("bookmark", VeryLong, () => { });

    private static ContextMenuBuilder ShortBuilder() => new ContextMenuBuilder()
        .Item("a", "Open", () => { })
        .Item("b", "Copy link", () => { })
        .Command("gallery.open", () => { });

    private static ContextMenuBuilder CatalogBuilder()
    {
        ContextMenuBuilder builder = new();
        foreach (CommandDefinition command in CommandCatalog.All
            .Where(command => CommandCatalog.ShortcutDisplay(command.Id) is { Length: > 0 })
            .OrderByDescending(command => CommandCatalog.ShortcutDisplay(command.Id)!.Length).Take(7))
            builder.Command(command.Id, () => { });
        return builder;
    }

    private static MenuFlyout LongFlyout()
    {
        MenuFlyout flyout = new();
        (string Label, Key Key, KeyModifiers Modifiers)[] rows =
        [
            ("Open in the gallery", Key.Return, KeyModifiers.Shift), (VeryLong, Key.T, KeyModifiers.Control | KeyModifiers.Shift),
            ("Edit the rating", Key.R, KeyModifiers.None), ("Show in the folder of this file", Key.O, KeyModifiers.Control | KeyModifiers.Alt)
        ];
        foreach ((string label, Key key, KeyModifiers modifiers) in rows)
            flyout.Items.Add(new MenuItem { Header = label, InputGesture = new KeyGesture(key, modifiers) });
        return flyout;
    }

    // The same commands as CatalogBuilder, as the menu items a flyout holds (icon, shortcut): no label needs the maximum in English.
    private static MenuFlyout CatalogFlyout()
    {
        MenuFlyout flyout = new();
        foreach (CommandDefinition command in CommandCatalog.All
            .Where(command => CommandCatalog.ShortcutDisplay(command.Id) is { Length: > 0 })
            .OrderByDescending(command => CommandCatalog.ShortcutDisplay(command.Id)!.Length).Take(7))
            flyout.Items.Add(CommandBindings.CreateMenuItem(command.Id, () => { }));
        return flyout;
    }

    private static async Task ContextAsync(MainWindow window, Control anchor, string scene, ContextMenuBuilder builder, bool mustCap, List<string> failures)
    {
        ContextMenu menu = builder.Build() ?? throw new InvalidOperationException(scene + ": the builder made no menu.");
        Console.WriteLine("SMOKE context menu layout opening " + scene);
        menu.Open(anchor);
        try
        {
            await UiCatalogChecks.UntilAsync(() => menu.IsOpen && menu.Bounds.Width > 0, scene + ": the menu did not open", 8);
            await SettledAsync(window, menu);
            Examine(menu, scene, mustCap, failures);
        }
        finally
        {
            menu.Close();
            await UiCatalogChecks.Settle(window, 100);
        }
    }

    private static async Task FlyoutAsync(MainWindow window, Control anchor, string scene, MenuFlyout flyout, bool mustCap, List<string> failures)
    {
        Console.WriteLine("SMOKE context menu layout opening " + scene);
        flyout.ShowAt(anchor);
        try
        {
            await UiCatalogChecks.UntilAsync(() => flyout.IsOpen && flyout.Items.OfType<MenuItem>().FirstOrDefault(item => item.IsVisible) is { Bounds.Width: > 0 }, scene + ": the flyout did not open", 8);
            MenuBase menu = flyout.Items.OfType<MenuItem>().First(item => item.IsVisible).FindAncestorOfType<MenuBase>()
                ?? throw new InvalidOperationException(scene + ": the flyout has no presenter.");
            await SettledAsync(window, menu);
            Examine(menu, scene, mustCap, failures);
        }
        finally
        {
            flyout.Hide();
            await UiCatalogChecks.Settle(window, 100);
        }
    }

    // The header's "⋯" menu is a submenu popup of the menu bar, not a ContextMenu: MenuLayout.IsSubmenuManaged gives its rows the same rule
    // (ST-06). German labels such as "Danbooru-Suche und Suchbeobachtung" and "Downloads und Prüfungen pausieren / fortsetzen" are longer than the menu.
    private static async Task AppMenuAsync(MainWindow window, string scene, List<string> failures)
    {
        MenuItem owner = window.FindControl<MenuItem>("WorkspaceMenuItem") ?? throw new InvalidOperationException(scene + ": the window has no app menu.");
        Console.WriteLine("SMOKE context menu layout opening " + scene);
        owner.IsSubMenuOpen = true;
        try
        {
            await UiCatalogChecks.UntilAsync(() => owner.IsSubMenuOpen && AppMenuRows(owner).Any(row => Part(row, "PART_HeaderPresenter") is { Bounds.Width: > 0 }),
                scene + ": the app menu did not open", 8);
            // MenuLayout looks at the rows once they are laid out: every text label is set to trim, then.
            await UiCatalogChecks.UntilAsync(() => AppMenuRows(owner).All(row => (Part(row, "PART_HeaderPresenter") as ContentPresenter)?.Child is not TextBlock { TextWrapping: TextWrapping.NoWrap } label
                || label.TextTrimming == TextTrimming.CharacterEllipsis), scene + ": the labels of the app menu were not set to trim", 8);
            await UiCatalogChecks.Settle(window, 150);
            ExamineAppMenu(owner, scene, failures);
        }
        finally
        {
            owner.IsSubMenuOpen = false;
            await UiCatalogChecks.Settle(window, 100);
        }
    }

    private static MenuItem[] AppMenuRows(MenuItem owner) => owner.Items.OfType<MenuItem>().Where(row => row.IsEffectivelyVisible).ToArray();

    private static void ExamineAppMenu(MenuItem owner, string scene, List<string> failures)
    {
        List<string> problems = [];
        MenuItem[] rows = AppMenuRows(owner);
        if (rows.Length == 0) problems.Add("no rows");
        // The submenu popup holds the menu's frame inside a margin for its shadow (8 dip each side in Semi), so the popup is 16 dip wider than
        // the menu people see. The shared rule caps the frame, as Examine measures a context menu by its own bounds, not by its popup.
        double popupWidth = rows.Length == 0 || TopLevel.GetTopLevel(rows[0]) is not { } popup ? 0 : popup.ClientSize.Width;
        double menuWidth = rows.Length == 0 ? 0 : rows[0].GetVisualAncestors().OfType<Border>().LastOrDefault()?.Bounds.Width ?? 0;
        if (menuWidth + Tolerance < 224) problems.Add($"the app menu {menuWidth:0.#} is narrower than 224");
        if (menuWidth - Tolerance > 416) problems.Add($"the app menu {menuWidth:0.#} is wider than 416");
        if (popupWidth + Tolerance < menuWidth) problems.Add($"the app menu popup {popupWidth:0.#} is narrower than the menu {menuWidth:0.#}");
        int trimmedRows = 0;
        foreach (MenuItem row in rows)
        {
            if ((Part(row, "PART_HeaderPresenter") as ContentPresenter)?.Child is not TextBlock { TextWrapping: TextWrapping.NoWrap, Text: { Length: > 0 } text } label) continue;
            double natural = MenuLayout.TextWidth(label, text);
            bool trimmed = label.Bounds.Width + Tolerance < natural;
            if (trimmed) trimmedRows++;
            if (trimmed && label.TextTrimming == TextTrimming.None) problems.Add($"label \"{text}\" is cut without an ellipsis");
            if (trimmed && ToolTip.GetTip(row) == null) problems.Add($"trimmed label \"{text}\" has no tooltip");
        }
        // ST-07: the "View" entry wears the eye icon of the work view command, like the other entries of its group.
        MenuItem view = owner.Items.OfType<MenuItem>().FirstOrDefault(row => row.Name == "WorkViewMenuItem") ?? throw new InvalidOperationException(scene + ": the app menu has no View entry.");
        if (view.Icon == null) problems.Add("the View entry has no icon");
        Console.WriteLine($"SMOKE context menu layout {(problems.Count == 0 ? "PASS" : "FAIL")} {scene}: menu {menuWidth:0.#}, popup {popupWidth:0.#}, rows {rows.Length}, trimmed {trimmedRows}");
        foreach (string problem in problems) failures.Add(scene + ": " + problem);
    }

    // The menu is laid out several times while it settles (MenuLayout sets its width after the first pass): wait until its width stays.
    private static async Task SettledAsync(MainWindow window, MenuBase menu)
    {
        double previous = -1;
        int stable = 0;
        DateTime deadline = DateTime.UtcNow.AddSeconds(6);
        while (stable < 6 && DateTime.UtcNow < deadline)
        {
            await UiCatalogChecks.Settle(window, 60);
            double width = menu.Bounds.Width;
            stable = Math.Abs(width - previous) < 0.1 ? stable + 1 : 0;
            previous = width;
        }
    }

    private static void Examine(MenuBase menu, string scene, bool mustCap, List<string> failures)
    {
        List<string> problems = [];
        double menuWidth = menu.Bounds.Width;
        TopLevel? popup = TopLevel.GetTopLevel(menu);
        MenuItem[] items = menu.GetVisualDescendants().OfType<MenuItem>().Where(item => item.IsEffectivelyVisible && item.Bounds.Width > 0).ToArray();
        if (items.Length == 0) problems.Add("no rows");
        if (menuWidth + Tolerance < 224) problems.Add($"menu {menuWidth:0.#} is narrower than 224");
        if (menuWidth - Tolerance > 416) problems.Add($"menu {menuWidth:0.#} is wider than 416");
        if (mustCap && menuWidth + Tolerance < 416 - TrimSlack) problems.Add($"menu {menuWidth:0.#} did not grow to the maximum although a label needs it");
        if (popup != null && popup.ClientSize.Width + Tolerance < menuWidth) problems.Add($"popup {popup.ClientSize.Width:0.#} is narrower than the menu {menuWidth:0.#}");
        List<double> gestureRights = [];
        List<string> geometry = [];
        foreach (MenuItem item in items)
        {
            Control? header = Part(item, "PART_HeaderPresenter");
            Control? gesture = Part(item, "PART_InputGestureText");
            TextBlock? label = (header as ContentPresenter)?.Child as TextBlock;
            string name = label?.Text ?? item.Header?.ToString() ?? "?";
            double origin = item.TranslatePoint(default, menu)?.X ?? 0;
            double inner = origin + item.Bounds.Width - item.Padding.Right - item.BorderThickness.Right;
            if (origin + item.Bounds.Width > menuWidth + Tolerance) problems.Add($"row \"{name}\" runs past the menu edge");
            double gestureLeft = double.NaN, gestureRight = double.NaN;
            if (gesture is TextBlock { Text: { Length: > 0 } keys } keyBlock)
            {
                gestureLeft = keyBlock.TranslatePoint(default, menu)?.X ?? 0;
                gestureRight = gestureLeft + keyBlock.Bounds.Width;
                double natural = MenuLayout.TextWidth(keyBlock, keys);
                if (keyBlock.Bounds.Width + Tolerance < natural) problems.Add($"shortcut \"{keys}\" of \"{name}\" is clipped: {keyBlock.Bounds.Width:0.#} of {natural:0.#}");
                if (gestureRight > inner + Tolerance) problems.Add($"shortcut \"{keys}\" of \"{name}\" ends at {gestureRight:0.#}, past the row's inner edge {inner:0.#}");
                gestureRights.Add(gestureRight);
            }
            if (label is { TextWrapping: TextWrapping.NoWrap, Text: { Length: > 0 } text })
            {
                double natural = MenuLayout.TextWidth(label, text);
                double labelLeft = label.TranslatePoint(default, menu)?.X ?? 0;
                bool trimmed = label.Bounds.Width + Tolerance < natural;
                if (trimmed && menuWidth + Tolerance < 416 - TrimSlack) problems.Add($"label \"{name}\" is clipped at {label.Bounds.Width:0.#} of {natural:0.#} although the menu is not at its maximum");
                if (trimmed && label.TextTrimming == TextTrimming.None) problems.Add($"label \"{name}\" is cut without an ellipsis");
                if (trimmed && ToolTip.GetTip(item) == null) problems.Add($"trimmed label \"{name}\" has no tooltip");
                if (!double.IsNaN(gestureLeft))
                {
                    double room = gestureLeft - (labelLeft + Math.Min(natural, label.Bounds.Width));
                    if (room + Tolerance < Gap) problems.Add($"label \"{name}\" has {room:0.#} dip before its shortcut, not {Gap}");
                }
                geometry.Add($"{name.Substring(0, Math.Min(18, name.Length))}: label {labelLeft:0.#}+{label.Bounds.Width:0.#}/{natural:0.#}, shortcut {gestureLeft:0.#}..{gestureRight:0.#}");
            }
        }
        if (gestureRights.Count > 1 && gestureRights.Max() - gestureRights.Min() > EdgeTolerance)
            problems.Add($"the shortcuts do not end on one right edge: {gestureRights.Min():0.#}..{gestureRights.Max():0.#}");
        Console.WriteLine($"SMOKE context menu layout {(problems.Count == 0 ? "PASS" : "FAIL")} {scene}: menu {menuWidth:0.#}, popup {popup?.ClientSize.Width:0.#}, rows {items.Length}; " + string.Join("; ", geometry));
        Capture(menu, scene);
        foreach (string problem in problems) failures.Add(scene + ": " + problem);
    }

    private static Control? Part(MenuItem item, string name) => item.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name);

    private static void Capture(MenuBase menu, string scene)
    {
        if (menu.Bounds.Width <= 0 || menu.Bounds.Height <= 0) return;
        double scale = TopLevel.GetTopLevel(menu)?.RenderScaling ?? 1;
        using RenderTargetBitmap image = new(PixelSize.FromSize(menu.Bounds.Size, scale), new Vector(96 * scale, 96 * scale));
        image.Render(menu);
        image.Save(Path.Combine(AppContext.BaseDirectory, "smoke-context-menu-layout-" + scene + ".png"), PngBitmapEncoderOptions.Default);
    }
}
