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
                        Require(Fits(window, window.FindControl<ComboBox>("WorkspaceModeComboBox")!),
                            scenario + ": the footer view switch is clipped.");

                        MenuItem[] visibleItems = menu.Items.OfType<MenuItem>().Where(item => item.IsVisible).ToArray();
                        string[] expectedHidden = Destinations.Where(destination => !Button(window, destination.Name).IsVisible)
                            .Select(destination => "nav." + destination.Name.ToLowerInvariant()).Order().ToArray();
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
                            Require(!tools.IsVisible && Destinations.All(destination => Button(window, destination.Name).IsVisible),
                                scenario + ": wide Advanced does not expose every destination directly, including Plugins.");
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
                        await SettleAsync(window);
                        RequireLanguage(window, language);
                        RequireToolsLabel(window, tools, language);
                        RequireNoOverlap(window);
                        Program.CaptureWindow(window, $"smoke-footer-{mode.ToLowerInvariant()}-{language}-{(wide ? "wide" : "minimum")}.png");
                        Console.WriteLine("SMOKE footer navigation: " + scenario + "; each destination has one usable footer/Tools route.");
                    }
                }
            }
        }
        finally
        {
            menu.Hide();
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
        string[] separatorNames = { "FooterSeparatorWork", "FooterSeparatorSetUp", "FooterSeparatorObserve" };
        var groups = MeowChan.Desktop.Commands.MainPageOrder.Groups;
        for (int group = 0; group < separatorNames.Length; group++)
        {
            Control separator = footer.Children.Single(control => control.Name == separatorNames[group]);
            bool groupVisible = group == 0 || groups[group].Any(id => Button(window, MeowChan.Desktop.Commands.MainPageOrder.ControlStem(id)).IsVisible);
            Require(separator.IsVisible == groupVisible, $"Footer separator after group {group} is {(separator.IsVisible ? "shown" : "hidden")} but its group is {(groupVisible ? "visible" : "empty")}.");
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

    /// <summary>The ⋯ menu lists every main page, Downloads included, in footer order and in the same groups.</summary>
    private static void RequireWorkspaceMenuGroups(MainWindow window)
    {
        MenuItem menu = window.FindControl<MenuItem>("WorkspaceMenuItem")!;
        var ids = MeowChan.Desktop.Commands.MainPageOrder.CommandIds;
        string[] tags = ids.Select(MeowChan.Desktop.Commands.MainPageOrder.Tag).ToArray();
        MenuItem[] pages = menu.Items.OfType<MenuItem>().Where(item => item.Tag is string tag && tags.Contains(tag)).ToArray();
        Require(pages.Select(item => (string)item.Tag!).SequenceEqual(tags), "The ⋯ menu does not list every page in footer order.");
        var groups = MeowChan.Desktop.Commands.MainPageOrder.Groups;
        for (int group = 0; group + 1 < groups.Count; group++)
        {
            int last = menu.Items.IndexOf(pages.Single(item => (string)item.Tag! == MeowChan.Desktop.Commands.MainPageOrder.Tag(groups[group][^1])));
            int first = menu.Items.IndexOf(pages.Single(item => (string)item.Tag! == MeowChan.Desktop.Commands.MainPageOrder.Tag(groups[group + 1][0])));
            Require(first == last + 2 && menu.Items[last + 1] is Separator, $"The ⋯ menu does not set group {group} apart with a separator.");
        }
        Require(pages.All(item => item.IsVisible), "A page of the ⋯ menu is hidden.");
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
