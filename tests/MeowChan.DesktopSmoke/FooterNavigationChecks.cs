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
        ("Rules", MainWindow.WorkspacePage.Rules), ("Plugins", MainWindow.WorkspacePage.Plugins),
        ("Diagnostics", MainWindow.WorkspacePage.Diagnostics), ("Settings", MainWindow.WorkspacePage.Settings),
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
        ("Archive", "de") => "Archiv", ("Rules", "de") => "Regeln", ("Sources", "de") => "Quellen",
        ("Diagnostics", "de") => "Diagnose", ("Settings", "de") => "Einstellungen", _ => name
    };

    private static void RequireToolsLabel(MainWindow window, Button tools, string language)
    {
        if (!tools.IsVisible || window.Bounds.Width < 640) return;
        string expected = language == "de" ? "Werkzeuge" : "Tools";
        Require(tools.GetVisualDescendants().OfType<TextBlock>()
                .Any(label => label.IsEffectivelyVisible && label.Text == expected),
            "Tools must retain its visible localized label at the supported workspace minimum width.");
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
                    Require(intersection.Width <= 1 || intersection.Height <= 1, "Header controls overlap: " + x.Name + "/" + y.Name);
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
