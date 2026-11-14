using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class FooterNavigationChecks
{
    // Booru is no entry of this table: it is hidden in the footer by default (F1), needs the Danbooru composition and is covered through the More
    // entry BooruMoreMenuItem wherever the fixture offers it (BooruInMore).
    private static readonly (string Name, MainWindow.WorkspacePage Page)[] Destinations =
    [
        ("Downloads", MainWindow.WorkspacePage.Downloads), ("Archive", MainWindow.WorkspacePage.Archive),
        ("Media", MainWindow.WorkspacePage.Media), ("Tagging", MainWindow.WorkspacePage.Tagging), ("Rules", MainWindow.WorkspacePage.Rules),
        ("Status", MainWindow.WorkspacePage.Status), ("Settings", MainWindow.WorkspacePage.Settings),
        ("Sources", MainWindow.WorkspacePage.Sources)
    ];

    internal static async Task RunAsync(MainWindow window)
    {
        string modeBefore = window.SessionHost.Config.WorkspaceMode;
        string languageBefore = window.SessionHost.Config.Language;
        double widthBefore = window.Width, heightBefore = window.Height;
        Button tools = window.FindControl<Button>("ToolsNavigationButton")!;
        MenuFlyout menu = tools.Flyout as MenuFlyout
            ?? throw new InvalidOperationException("Tools has no navigation menu.");
        try
        {
            // Reverse the second traversal to cover both directions of every mode transition.
            foreach (string language in new[] { "en", "de" })
            {
                await PersistLanguageAsync(window, language);
                foreach (string mode in language == "en"
                    ? new[] { "Barebones", "Simple", "Advanced" }
                    : new[] { "Advanced", "Simple", "Barebones" })
                {
                    await PresentationChecks.SwitchModeAsync(window, mode);
                    await SettleAsync(window);
                    RequireSelection(window, tools);
                    foreach (bool wide in new[] { false, true })
                    {
                        Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
                            ?? throw new InvalidOperationException("Footer navigation has no screen.");
                        double frameWidth = Math.Max(0, (window.FrameSize?.Width ?? window.ClientSize.Width) - window.ClientSize.Width);
                        double available = Math.Floor(screen.WorkingArea.Width / window.RenderScaling - frameWidth);
                        double requested = wide ? Math.Min(1400, available) : window.MinWidth;
                        window.Width = requested;
                        window.Height = Math.Max(window.MinHeight, wide ? 600 : window.MinHeight);
                        await Program.WaitUntilAsync(() => Math.Abs(window.Bounds.Width - requested) <= 1);
                        await SettleAsync(window);
                        string scenario = $"{mode}/{language}/{(wide ? "wide" : "minimum")}/{window.Bounds.Width}";
                        RequireLanguage(window, language);
                        RequireSelection(window, tools);
                        RequireResumeAction(window);
                        RequireGroupSeparators(window, tools);
                        RequireWorkspaceMenuGroups(window);
                        RequireFooterOrderAndDigits(window);
                        Require(Fits(window, window.FindControl<ComboBox>("WorkspaceModeComboBox")!),
                            scenario + ": the footer view switch is clipped.");

                        MenuItem[] visibleItems = menu.Items.OfType<MenuItem>().Where(item => item.IsVisible).ToArray();
                        bool booruInMore = BooruInMore(window);
                        string[] expectedHidden = Destinations.Where(destination => !Button(window, destination.Name).IsVisible)
                            .Select(destination => "nav." + destination.Name.ToLowerInvariant())
                            .Concat(booruInMore ? ["nav.booru"] : []).Order().ToArray();
                        Require(tools.IsVisible == (expectedHidden.Length > 0), scenario + ": Tools does not match the hidden destinations.");
                        if (tools.IsVisible)
                        {
                            Require(Fits(window, tools), scenario + ": Tools is clipped.");
                            RequireToolsLabel(window, tools, language);
                            Require(visibleItems.Select(item => item.Tag as string ?? "<untagged>").Order().SequenceEqual(expectedHidden),
                                scenario + ": Tools duplicates a visible footer target or loses a hidden destination.");
                            await OpenMenuAsync(menu, tools);
                            Require(visibleItems.All(item => item.IsEffectivelyVisible && item.IsEffectivelyEnabled && item.Bounds.Width > 0),
                                scenario + ": a Tools destination cannot be used.");
                            menu.Hide();
                            await Program.WaitUntilAsync(() => !menu.IsOpen);
                        }
                        if (wide && mode == "Advanced" && window.Bounds.Width >= 1280)
                            Require(!tools.IsVisible == !booruInMore && Destinations.All(destination => Button(window, destination.Name).IsVisible),
                                scenario + ": wide Advanced does not expose every destination directly, including Plugins (Booru stays in More until the footer shows it).");
                        foreach ((string name, MainWindow.WorkspacePage page) in Destinations)
                        {
                            Button direct = Button(window, name);
                            if (direct.IsVisible)
                            {
                                Require(Fits(window, direct) && direct.IsEffectivelyEnabled,
                                    scenario + ": direct " + name + " is clipped or disabled.");
                                Require(AutomationProperties.GetName(direct) == DestinationLabel(name, language),
                                    scenario + ": direct " + name + " lost its localized accessible name.");
                                direct.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                            }
                            else
                            {
                                await OpenMenuAsync(menu, tools);
                                MenuItem item = visibleItems.Single(item => item.Tag as string == "nav." + name.ToLowerInvariant());
                                Require(item.Header as string == DestinationLabel(name, language),
                                    scenario + ": hidden " + name + " lost its localized label.");
                                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                                await Program.WaitUntilAsync(() => !menu.IsOpen);
                            }
                            await Program.WaitUntilAsync(() => window.SelectedPage == page);
                            RequireLanguage(window, language);
                            RequireSelection(window, tools);
                        }
                        if (booruInMore)
                        {
                            // The page hidden in the footer by default is reached through its More entry, never through a ⋯ entry.
                            await SmokeNavigation.GoToAsync(window, MainWindow.WorkspacePage.Booru);
                            Require(window.SelectedPage == MainWindow.WorkspacePage.Booru && tools.Classes.Contains("selected"),
                                scenario + ": Booru through More did not select the More button.");
                            Button(window, "Downloads").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                            await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads);
                        }
                        await SettleAsync(window);
                        RequireLanguage(window, language);
                        RequireToolsLabel(window, tools, language);
                        RequireNoOverlap(window);
                        Program.CaptureWindow(window, $"smoke-footer-{mode.ToLowerInvariant()}-{language}-{(wide ? "wide" : "minimum")}.png");
                        Console.WriteLine("SMOKE footer navigation: " + scenario + "; each destination has one usable footer/Tools route.");
                    }
                }
            }
            await CheckConfiguredLayoutAsync(window);
        }
        finally
        {
            menu.Hide();
            await RestoreFooterLayoutAsync(window);
            await PersistLanguageAsync(window, languageBefore);
            await PresentationChecks.SwitchModeAsync(window, modeBefore);
            window.Width = widthBefore; window.Height = heightBefore;
            Button(window, "Downloads").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await SettleAsync(window);
        }
    }

    internal static async Task PersistLanguageAsync(MainWindow window, string language)
    {
        // Mode preference saves and opening Settings refresh Texts from the
        // persisted config. Exercise the same language choice a user saves.
        window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SettingsPageVisible);
        SettingsEditor settings = window.SettingsEditor;
        settings.FindControl<ComboBox>("LanguageInput")!.SelectedItem = settings.LanguageChoices.Single(choice => choice.Code == language);
        settings.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Program.WaitUntilAsync(() => !settings.IsBusy && Config.LoadConfig().Language == language
            && window.SessionHost.Config.Language == language && Texts.Language == language
            && window.FindControl<Button>("AddUrlButton")!.IsEnabled);
        Button(window, "Downloads").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
        await SettleAsync(window);
        RequireLanguage(window, language);
    }

    internal static void RequireLanguage(MainWindow window, string expected)
    {
        Require(Texts.Language == expected && window.SessionHost.Config.Language == expected && Config.LoadConfig().Language == expected,
            $"Footer scenario expected persisted {expected}, but active language is {Texts.Language} and session language is {window.SessionHost.Config.Language}.");
    }

    private static string DestinationLabel(string name, string language) => (name, language) switch
    {
        ("Archive", "de") => "Archiv", ("Media", "de") => "Medien", ("Tagging", "de") => "Taggen", ("Rules", "de") => "Regeln", ("Sources", _) => "Plugins", // the merged page is plain "Plugins" in both languages (14.11.2026)
        ("Settings", "de") => "Einstellungen", _ => name
    };

    private static void RequireToolsLabel(MainWindow window, Button tools, string language)
    {
        if (!tools.IsVisible || window.Bounds.Width < 640) return;
        string expected = language == "de" ? "Mehr" : "More";
        Require(tools.GetVisualDescendants().OfType<TextBlock>()
                .Any(label => label.IsEffectivelyVisible && label.Text == expected),
            "More must retain its visible localized label at the supported workspace minimum width.");
    }

    internal static Task WaitForResumeHitAsync(MainWindow window) => Program.WaitUntilAsync(() =>
    {
        // Layout can lead the rendered hit-test scene after resize or revealing
        // the book. Observe natural presentation readiness; do not render/repair it.
        Button action = window.FindControl<Button>("ContinueReadingButton")!;
        return action.IsEffectivelyVisible && action.IsEffectivelyEnabled
            && action.IsMeasureValid && action.IsArrangeValid
            && ReferenceEquals(TopLevel.GetTopLevel(action), window)
            && action.Bounds is { Width: > 0, Height: > 0 }
            && action.TranslatePoint(default, window) is { } origin
            && window.InputHitTest(new Rect(origin, action.Bounds.Size).Center) is Visual hit
            && hit.GetSelfAndVisualAncestors().Contains(action);
    });

    internal static void RequireResumeAction(MainWindow window, bool expectedVisible = false)
    {
        Button action = window.FindControl<Button>("ContinueReadingButton")!;
        StackPanel footer = window.FindControl<StackPanel>("WorkspaceNavigationButtons")!;
        Require(!footer.Children.Contains(action)
            && !footer.Children.Any(control => control.Name?.Contains("ContinueReading") == true),
            "Continue reading is still presented as a footer destination.");
        Require(action.IsVisible == expectedVisible, "Unexpected header reading action visibility.");
        Grid header = window.FindControl<Grid>("WorkspaceHeader")!;
        Control[] headerControls = new[] { header.Children[0] }.Concat(new[] {
            "WorkspaceSubtitle", "TransferSpeed", "ContinueReadingButton", "PauseButton", "FullscreenButton",
            "ThemeToggleButton", "WorkspaceMenuItem", "WindowMinimizeButton", "WindowMaximizeButton", "WindowCloseButton"
        }.Select(name => window.FindControl<Control>(name)!))
            .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0).ToArray();
        if (expectedVisible)
        {
            Require(action.IsEffectivelyVisible && action.IsEffectivelyEnabled && Fits(window, action),
                "The available header reading action is disabled or clipped.");
            Button pause = window.FindControl<Button>("PauseButton")!;
            Require(action.Parent is StackPanel utilities && utilities.Children.IndexOf(pause) == utilities.Children.IndexOf(action) + 1,
                "The header book action is not directly left of Pause.");
            foreach (Control control in headerControls) Require(Fits(window, control), "Header control is clipped: " + control.Name);
            for (int first = 0; first < headerControls.Length; first++)
                for (int second = first + 1; second < headerControls.Length; second++)
                {
                    Control x = headerControls[first], y = headerControls[second];
                    Rect intersection = new Rect(x.TranslatePoint(default, window)!.Value, x.Bounds.Size)
                        .Intersect(new Rect(y.TranslatePoint(default, window)!.Value, y.Bounds.Size));
                    Require(intersection.Width <= 1 || intersection.Height <= 1, "Header controls overlap: " + x.Name + "/" + y.Name
                        + " (" + window.SessionHost.Config.WorkspaceMode + ", " + Texts.Language + ", client " + window.ClientSize + "; "
                        + string.Join("; ", headerControls.Select(control => (control.Name ?? "brand-group") + "@" + control.TranslatePoint(default, window) + "/" + control.Bounds.Size)) + ")");
                }
            Point a = action.TranslatePoint(default, window)!.Value;
            Point b = pause.TranslatePoint(default, window)!.Value;
            Rect box = new(a, action.Bounds.Size);
            Rect overlap = box.Intersect(new Rect(b, pause.Bounds.Size));
            Require((overlap.Width <= 1 || overlap.Height <= 1)
                && window.InputHitTest(box.Center) is Visual hit && hit.GetSelfAndVisualAncestors().Contains(action),
                "The book action overlaps Pause or cannot receive a real pointer hit.");
        }
        string expected = Texts.T("resume.action");
        Require(action.Content is Border { OpacityMask: not null } && AutomationProperties.GetName(action) == expected
            && ToolTip.GetTip(action) is string tip && tip.Contains(expected),
            "Continue reading lost its book icon, localized tooltip or accessible name.");
        MenuItem menu = window.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.OfType<MenuItem>()
            .Single(item => item.Header as string == expected);
        Require(menu.IsEnabled, "Continue reading must remain available in the app menu for empty-state explanation.");
        Console.WriteLine("SMOKE reading header " + System.Text.Json.JsonSerializer.Serialize(new {
            expected_visible = expectedVisible, action.IsVisible, action.IsEffectivelyVisible, action.IsEnabled,
            scale = window.RenderScaling, client = window.ClientSize.ToString(), window_bounds = window.Bounds.ToString(),
            header_controls = headerControls.Select(control => new { name = control.Name ?? "brand-group",
                bounds = control.Bounds.ToString(), origin = control.TranslatePoint(default, window)?.ToString(),
                root = TopLevel.GetTopLevel(control)?.GetType().Name, control.IsEffectivelyVisible }).ToArray(),
            mode = window.SessionHost.Config.WorkspaceMode, language = Texts.Language,
            bounds = action.Bounds.ToString(), footer_duplicate = footer.Children.Contains(action),
            name = AutomationProperties.GetName(action), tooltip = ToolTip.GetTip(action)?.ToString(),
            menu_enabled = menu.IsEnabled, focus = (window.FocusManager?.GetFocusedElement() as Control)?.Name
        }));
    }

    private static Button Button(MainWindow window, string name) => window.FindControl<Button>(name + "NavigationButton")!;

    private static void RequireSelection(MainWindow window, Button tools)
    {
        foreach ((string name, MainWindow.WorkspacePage page) in Destinations)
            Require(Button(window, name).Classes.Contains("selected") == (window.SelectedPage == page),
                "Footer selection disagrees with the open " + window.SelectedPage + " page: " + name);
        bool selectedHidden = Destinations.Any(destination => destination.Page == window.SelectedPage && !Button(window, destination.Name).IsVisible);
        Require(tools.Classes.Contains("selected") == selectedHidden, "Tools selection disagrees with its hidden destinations after a route, resize or mode change.");
    }

    private static async Task OpenMenuAsync(MenuFlyout menu, Button tools)
    {
        menu.ShowAt(tools);
        await Program.WaitUntilAsync(() => menu.IsOpen && menu.Items.OfType<MenuItem>().Any(item => item.IsEffectivelyVisible && item.Bounds.Width > 0));
    }

    private static async Task SettleAsync(MainWindow window)
    {
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static bool Fits(MainWindow window, Control control)
    {
        Point? point = control.TranslatePoint(default, window);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && point is { } origin
            && origin.X >= -1 && origin.Y >= -1
            && origin.X + control.Bounds.Width <= window.Bounds.Width + 1
            && origin.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }

    /// <summary>Footer groups (Downloads, Archive, Media, Tagging, Booru | Rules, Sources, Plugins | Status | More, Settings):
    /// a separator shows exactly while the group in front of it has a visible button, never two in a row, never one at an edge,
    /// and the More flyout sets its own groups apart the same way.</summary>
    private static void RequireGroupSeparators(MainWindow window, Button tools)
    {
        StackPanel footer = window.FindControl<StackPanel>("WorkspaceNavigationButtons")!;
        // Every page button is followed by its separator slot (ArrangeFooterButtons): a slot is shown behind a shown button when the next shown
        // button belongs to another group, and behind the last shown one; a hidden or reduced page has no separator.
        var ids = window.FooterLayout.Order;
        bool[] shown = ids.Select(id => Button(window, MeowChan.Desktop.Commands.MainPageOrder.ControlStem(id)).IsVisible).ToArray();
        for (int index = 0; index < ids.Count; index++)
        {
            string stem = MeowChan.Desktop.Commands.MainPageOrder.ControlStem(ids[index]);
            int at = footer.Children.IndexOf(footer.Children.Single(control => control.Name == stem + "NavigationButton"));
            Control separator = footer.Children[at + 1];
            Require(separator is Border, "No separator slot follows the footer button " + stem + ".");
            int next = Enumerable.Range(index + 1, ids.Count - index - 1).Cast<int?>().FirstOrDefault(candidate => shown[candidate!.Value]) ?? -1;
            bool expected = shown[index] && (next < 0 || MeowChan.Desktop.Commands.MainPageOrder.Group(ids[index]) != MeowChan.Desktop.Commands.MainPageOrder.Group(ids[next]));
            Require(separator.IsVisible == expected, $"Footer separator behind {stem} is {(separator.IsVisible ? "shown" : "hidden")} but should be {(expected ? "shown" : "hidden")}.");
        }
        Control[] visible = footer.Children.Where(control => control.IsVisible).ToArray();
        Require(visible[0] is Button && visible[^1] is Button, "A footer separator sits at the edge of the footer.");
        for (int index = 1; index < visible.Length; index++)
            Require(!(visible[index] is Border && visible[index - 1] is Border), "Two footer separators are adjacent.");
        if (tools.Flyout is MenuFlyout flyout)
        {
            // Each flyout separator needs a visible destination before and after it.
            Control[] items = flyout.Items.OfType<Control>().Where(item => item.IsVisible).ToArray();
            if (items.Length > 0)
            {
                Require(items[0] is MenuItem && items[^1] is MenuItem, "A More separator sits at the edge of the menu.");
                for (int index = 1; index < items.Length; index++)
                    Require(!(items[index] is Separator && items[index - 1] is Separator), "Two More separators are adjacent.");
            }
        }
    }

    /// <summary>
    /// F1 (14.11.2026): the ⋯ menu lists no pages. Downloads, Archive, Media, Tagging, Booru, Rules, Plugins and Status are the footer buttons the
    /// profile chose, the footer's More menu and the Ctrl/Cmd+digit shortcuts; the ⋯ menu keeps the tools and the application and starts with a tool.
    /// </summary>
    private static void RequireWorkspaceMenuGroups(MainWindow window)
    {
        MenuItem menu = window.FindControl<MenuItem>("WorkspaceMenuItem")!;
        var ids = MeowChan.Desktop.Commands.MainPageOrder.CommandIds;
        string[] tags = ids.Select(MeowChan.Desktop.Commands.MainPageOrder.Tag).ToArray();
        Require(!menu.Items.OfType<MenuItem>().Any(item => item.Tag is string tag && tags.Contains(tag)), "The ⋯ menu still lists a page.");
        foreach (string id in ids)
            Require(window.FindControl<MenuItem>(MeowChan.Desktop.Commands.MainPageOrder.ControlStem(id) + "MenuItem") == null,
                "The ⋯ menu still has the entry " + MeowChan.Desktop.Commands.MainPageOrder.ControlStem(id) + "MenuItem.");
        Require(window.FindControl<Separator>("PagesGroupSeparatorWork") == null && window.FindControl<Separator>("PagesGroupSeparatorSetUp") == null
            && window.FindControl<Separator>("MenuPagesToolsSeparator") == null, "A separator of the removed page group is still in the ⋯ menu.");
        Require(menu.Items.Count > 0 && menu.Items[0] is not Separator, "The ⋯ menu starts with a separator.");
        foreach (string name in new[] { "ManualImportMenuItem", "PauseMenuItem", "SettingsMenuItem", "ShortcutHelpMenuItem", "AboutMenuItem", "ExitMenuItem" })
            Require(menu.Items.OfType<MenuItem>().Any(item => item.Name == name), "The ⋯ menu lost its entry " + name + ".");
        Require(window.FindControl<CheckBox>("ShowBooruToolsToggle") == null && menu.Items.OfType<MenuItem>().All(item => item.Name != "BooruToolsMenuItem"),
            "The Show Booru switch is still in the footer or the ⋯ menu (it is Settings > Appearance > Footer since F1).");
    }

    /// <summary>
    /// The footer buttons stand in the profile's order and Ctrl/Cmd+1, 2, 3 ... follow the pages the footer shows from the left, the hidden pages
    /// (Booru by default) after them. The numbering is the layout's, so a narrow window moving a button under More does not renumber.
    /// </summary>
    private static void RequireFooterOrderAndDigits(MainWindow window)
    {
        var layout = window.FooterLayout;
        StackPanel footer = window.FindControl<StackPanel>("WorkspaceNavigationButtons")!;
        string[] stems = layout.Order.Select(MeowChan.Desktop.Commands.MainPageOrder.ControlStem).ToArray();
        string[] childOrder = footer.Children.OfType<Button>().Select(button => button.Name ?? string.Empty)
            .Where(name => stems.Any(stem => name == stem + "NavigationButton")).ToArray();
        Require(childOrder.SequenceEqual(stems.Select(stem => stem + "NavigationButton")),
            "The footer buttons are not in the order of the layout: " + string.Join(", ", childOrder));
        string[] shownDirectly = stems.Where(stem => footer.Children.OfType<Button>().Single(button => button.Name == stem + "NavigationButton").IsVisible).ToArray();
        Require(shownDirectly.SequenceEqual(shownDirectly.OrderBy(stem => Array.IndexOf(stems, stem))), "The shown footer buttons are not in layout order.");
        Require(layout.IsVisible("navigation.downloads") && Button(window, "Downloads").IsVisible, "Downloads left the footer.");
        // Booru is off in a default layout, so it has no direct button; the setting never forces one a work view or the width removed.
        if (layout.IsDefault) Require(!Button(window, "Booru").IsVisible, "Booru is shown in the default footer.");
        foreach (string id in layout.Order)
            if (!layout.IsVisible(id)) Require(!Button(window, MeowChan.Desktop.Commands.MainPageOrder.ControlStem(id)).IsVisible, id + " is hidden by the layout but its button is shown.");
        // Digits: visible footer pages from the left, then the hidden ones.
        var numbering = layout.Numbering;
        for (int index = 0; index < numbering.Count && index < MeowChan.Desktop.Commands.MainPageOrder.MaximumDigit; index++)
            Require(MeowChan.Desktop.Commands.MainPageOrder.Digit(numbering[index]) == index + 1,
                numbering[index] + " does not own Ctrl/Cmd+" + (index + 1) + " (owns " + MeowChan.Desktop.Commands.MainPageOrder.Digit(numbering[index]) + ").");
        if (layout.IsDefault)
            Require(numbering.SequenceEqual(new[] { "navigation.downloads", "navigation.archive", "navigation.media", "navigation.tagging",
                "navigation.rules", "navigation.sources", "navigation.status", "navigation.booru" }),
                "The default footer numbering is not Downloads, Archive, Media, Tagging, Rules, Plugins, Status, then Booru: " + string.Join(", ", numbering));
        // The tooltip of a shown button names the digit it owns.
        foreach (string id in layout.Order.Where(layout.IsVisible))
        {
            Button button = Button(window, MeowChan.Desktop.Commands.MainPageOrder.ControlStem(id));
            string? shortcut = MeowChan.Desktop.Commands.CommandCatalog.ShortcutDisplay(id);
            Require(shortcut != null && ToolTip.GetTip(button) is string tip && tip.Contains(shortcut, StringComparison.Ordinal),
                "The footer tooltip of " + id + " does not name its digit shortcut (" + shortcut + ").");
        }
    }

    private static bool BooruInMore(MainWindow window) => window.FindControl<MenuItem>("BooruMoreMenuItem") is { IsVisible: true };

    /// <summary>The footer setting at work: hiding Media removes its button, puts it in More and renumbers; the page keeps its shortcut.</summary>
    private static async Task CheckConfiguredLayoutAsync(MainWindow window)
    {
        await window.SetFooterPageVisibleAsync("media", false);
        await Program.WaitUntilAsync(() => !window.FooterLayout.IsVisible("navigation.media"));
        await SettleAsync(window);
        Button tools = window.FindControl<Button>("ToolsNavigationButton")!;
        MenuFlyout menu = (MenuFlyout)tools.Flyout!;
        Require(!Button(window, "Media").IsVisible && tools.IsVisible
            && menu.Items.OfType<MenuItem>().Any(item => item.Tag as string == "nav.media" && item.IsVisible),
            "Hiding Media in the footer layout did not move it into More.");
        RequireFooterOrderAndDigits(window);
        Require(MeowChan.Desktop.Commands.MainPageOrder.Digit("navigation.media") > MeowChan.Desktop.Commands.MainPageOrder.Digit("navigation.status"),
            "A page hidden from the footer did not move behind the shown ones in the numbering.");
        await SmokeNavigation.GoToAsync(window, MainWindow.WorkspacePage.Media);
        Require(tools.Classes.Contains("selected"), "More is not selected while a page it holds is open.");
        Button(window, "Downloads").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Downloads);
        await RestoreFooterLayoutAsync(window);
        RequireFooterOrderAndDigits(window);
        Console.WriteLine("SMOKE footer navigation: a page hidden by the footer setting moved into More and behind the shown pages in the digits; restored.");
    }

    private static async Task RestoreFooterLayoutAsync(MainWindow window)
    {
        if (!window.FooterLayout.IsVisible("navigation.media")) await window.SetFooterPageVisibleAsync("media", true);
        await Program.WaitUntilAsync(() => window.FooterLayout.IsVisible("navigation.media"));
    }

    private static void RequireNoOverlap(MainWindow window)
    {
        Control[] controls = window.FindControl<StackPanel>("WorkspaceNavigationButtons")!.Children.Where(control => control.IsVisible)
            .Append(window.FindControl<ComboBox>("WorkspaceModeComboBox")!).ToArray();
        for (int first = 0; first < controls.Length; ++first)
            for (int second = first + 1; second < controls.Length; ++second)
            {
                Point a = controls[first].TranslatePoint(default, window)!.Value;
                Point b = controls[second].TranslatePoint(default, window)!.Value;
                Rect overlap = new Rect(a, controls[first].Bounds.Size).Intersect(new Rect(b, controls[second].Bounds.Size));
                Require(overlap.Width <= 1 || overlap.Height <= 1,
                    "Footer controls overlap: " + controls[first].Name + " and " + controls[second].Name);
            }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
