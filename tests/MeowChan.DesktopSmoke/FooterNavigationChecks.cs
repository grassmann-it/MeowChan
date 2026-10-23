using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
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
                        Require(Fits(window, window.FindControl<Button>("ContinueReadingNavigationButton")!),
                            scenario + ": Continue reading is clipped.");
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

    internal static void RequireResumeAction(MainWindow window)
    {
        Button action = window.FindControl<Button>("ContinueReadingNavigationButton")!;
        Require(action.IsEffectivelyVisible && action.IsEffectivelyEnabled,
            "Continue reading must remain available, including when it needs to explain that no position is saved.");
        string expected = Texts.Language == "de" ? "Weiterlesen" : "Continue reading";
        Require(AutomationProperties.GetName(action) == expected
            && action.GetVisualDescendants().OfType<TextBlock>().Any(label => label.IsEffectivelyVisible && label.Text == expected),
            "Continue reading lost its visible localized label or accessible name.");
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
