using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// Root hook, after the owner's Ready/empty barrier: MEOWCHAN_LANGUAGE_SWITCH_FOOTER_ONLY=1.
// This check uses passive Texts switches only. It submits no URL, opens no source page and
// therefore activates no provider/network path, while proving real MainWindow presentation.
internal static class LanguageSwitchFooterChecks
{
    private sealed record NavigationTarget(string Button, string? MenuItem, string CommandId, string LabelKey);

    // F1: the pages are footer buttons and entries of the footer's More menu (checked in CheckToolsFlyoutAsync); the ⋯ menu has no page entries.
    private static readonly NavigationTarget[] NavigationTargets = MainPageOrder.CommandIds.Select(id =>
        new NavigationTarget(MainPageOrder.ControlStem(id) + "NavigationButton", null, id, "command." + id))
        .Concat(new[] {
            new NavigationTarget("SettingsNavigationButton", "SettingsMenuItem", "navigation.settings", "command.navigation.settings"),
            new NavigationTarget("ToolsNavigationButton", null, "navigation.tools-menu", "command.navigation.tools-menu")
        }).ToArray();

    internal static async Task RunAsync(MainWindow window)
    {
        ComboBox modes = window.FindControl<ComboBox>("WorkspaceModeComboBox")!;
        TextBox input = window.FindControl<TextBox>("UrlInput")!;
        string profile = window.SessionHost.ProfileDirectory;
        string settingsPath = Path.Combine(profile, "settings.json");
        byte[] initialSettings = File.ReadAllBytes(settingsPath);
        string initialDraft = input.Text ?? string.Empty;
        var session = window.SessionHost;
        string initialMode = (modes.SelectedItem as ComboBoxItem)?.Tag as string
            ?? throw new InvalidOperationException("The workspace mode selection is missing.");

        Require(Texts.Language == "en" && Config.LoadConfig(profile).Language == "en",
            "The language-switch footer smoke must begin with the English catalog and preference.");
        Require(window.UrlList.Count == 0 && window.BoardsList.Count == 0,
            "The language-switch footer smoke must run against the harness's empty session.");
        RequireWorkerCNavigationCatalog("en");
        RequireClosedModeSelection(modes);

        // ES and RU switch while the real dropdown is open; FR and the final EN return
        // switch while it is closed. Both paths must replace Avalonia's cached
        // SelectionBoxItemTemplate child without changing selection or preferences.
        try
        {
            foreach (string language in new[] { "es", "fr", "ru", "en" })
            {
                bool switchWhileOpen = language is "es" or "ru";
                if (switchWhileOpen) await OpenModeDropdownAsync(modes);

                Texts.SetLanguage(language);
                string expectedMode = ExpectedModeLabel(modes);
                await Program.WaitUntilAsync(() => Texts.Language == language
                    && VisibleModeSelectionText(modes).SequenceEqual(new[] { expectedMode }));

                if (switchWhileOpen)
                {
                    RequireOpenModeDropdown(modes);
                    Program.CaptureWindow(window, $"smoke-language-footer-{language}-open.png");
                    modes.IsDropDownOpen = false;
                    await Program.WaitUntilAsync(() => !modes.IsDropDownOpen
                        && modes.Items.OfType<ComboBoxItem>().All(item => item.Content is string)
                        && VisibleModeSelectionText(modes).SequenceEqual(new[] { ExpectedModeLabel(modes) }));
                }
                else
                {
                    RequireClosedModeSelection(modes);
                    Program.CaptureWindow(window, $"smoke-language-footer-{language}-closed.png");
                    await OpenModeDropdownAsync(modes);
                    RequireOpenModeDropdown(modes);
                    Program.CaptureWindow(window, $"smoke-language-footer-{language}-open.png");
                    modes.IsDropDownOpen = false;
                    await Program.WaitUntilAsync(() => !modes.IsDropDownOpen
                        && modes.Items.OfType<ComboBoxItem>().All(item => item.Content is string)
                        && VisibleModeSelectionText(modes).SequenceEqual(new[] { ExpectedModeLabel(modes) }));
                }

                RequireClosedModeSelection(modes);
                Program.CaptureWindow(window, $"smoke-language-footer-{language}-post-close.png");
                RequireWorkerCNavigationCatalog(language);
                CheckNavigationCommands(window);
                await CheckToolsFlyoutAsync(window);
                RequireInvariants(window, session, settingsPath, initialSettings, initialDraft, initialMode);

            }

            Require(Texts.Language == "en" && Config.LoadConfig(profile).Language == "en",
                "The final passive switch did not restore English without committing a preference.");
            Console.WriteLine("SMOKE language footer: EN→ES→FR→RU→EN refreshed the closed/open mode selection and Worker-C navigation command labels; passive only, session/draft/settings retained, no URL or source activation.");
        }
        finally
        {
            modes.IsDropDownOpen = false;
            Texts.SetLanguage("en");
        }
    }

    private static void RequireWorkerCNavigationCatalog(string language)
    {
        foreach (string key in NavigationTargets.Select(target => target.LabelKey)
            .Append("command.navigation.about"))
        {
            string translated = Texts.T(key);
            Require(!string.Equals(translated, key, StringComparison.Ordinal),
                $"Worker-C navigation catalog key is missing for {language}: {key}.");
        }
    }

    private static void CheckNavigationCommands(MainWindow window)
    {
        foreach ((string buttonName, string? menuName, string commandId, string labelKey) in NavigationTargets)
        {
            string expected = CommandLabel(commandId, labelKey);
            Button button = window.FindControl<Button>(buttonName)!;
            Require(AutomationProperties.GetName(button) == expected,
                $"{buttonName} kept a stale accessible command label for {Texts.Language}.");
            TextBlock[] visibleLabels = button.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && !string.IsNullOrWhiteSpace(text.Text))
                .ToArray();
            if (visibleLabels.Length != 0)
                Require(visibleLabels.Any(text => text.Text == expected),
                    $"{buttonName} kept a stale visible command label for {Texts.Language}.");

            if (commandId != "navigation.settings" && commandId != "navigation.tools-menu")
                Require(window.FindControl<MenuItem>(MainPageOrder.ControlStem(commandId) + "MenuItem") == null,
                    $"The ⋯ menu lists the page {commandId} again; pages belong to the footer and its More menu.");
            if (menuName != null)
            {
                MenuItem menu = window.FindControl<MenuItem>(menuName)!;
                Require(menu.Header as string == expected && AutomationProperties.GetName(menu) == expected,
                    $"{menuName} kept a stale app-menu command label for {Texts.Language}.");
            }
        }

        CommandDefinition compatibility = CommandCatalog.Get("navigation.plugins");
        Require(CommandLabel("navigation.plugins", "command.navigation.plugins").Length > 0
            && !compatibility.Contexts.Any(context => context is "footer" or "app-menu" or "tools-menu") && compatibility.DefaultGestures.Count == 0
            && MainPageOrder.Digit("navigation.plugins") == null && MainPageOrder.Digit("navigation.status") == 7
            && window.FindControl<Button>("PluginsNavigationButton") == null
            && window.FindControl<MenuItem>("PluginsMenuItem") == null
            && window.FindControl<ContentControl>("PluginsPageHost") == null,
            "The compatibility route reappeared as a workspace or displaced the Status shortcut.");

        string about = CommandLabel("navigation.about", "command.navigation.about");
        Button brand = window.FindControl<Button>("BrandAboutButton")!;
        Require(AutomationProperties.GetName(brand) == about && ToolTip.GetTip(brand) as string == about,
            "The brand About action kept a stale navigation command label.");
    }

    private static async Task CheckToolsFlyoutAsync(MainWindow window)
    {
        Button tools = window.FindControl<Button>("ToolsNavigationButton")!;
        if (!tools.IsEffectivelyVisible) return;
        if (tools.Flyout is not MenuFlyout menu)
            throw new InvalidOperationException("The Tools navigation button has no real menu flyout.");

        menu.ShowAt(tools);
        await Program.WaitUntilAsync(() => menu.IsOpen && menu.Items.OfType<MenuItem>()
            .Any(item => item.IsEffectivelyVisible && item.Bounds.Width > 0));
        Exception? failureBeforeCleanup = null;
        try
        {
            foreach (MenuItem item in menu.Items.OfType<MenuItem>())
            {
                if (item.Tag is not string tag || !tag.StartsWith("nav.", StringComparison.Ordinal)) continue;
                string suffix = tag["nav.".Length..];
                string expected = CommandLabel("navigation." + suffix, "command.navigation." + suffix);
                Require(item.IsEffectivelyVisible && item.Header as string == expected
                    && AutomationProperties.GetName(item) == expected,
                    $"Tools item {tag} kept a stale command label for {Texts.Language}.");
            }
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                menu.Hide();
                await Program.WaitUntilAsync(() => !menu.IsOpen);
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in LanguageSwitchFooterChecks: " + exception));
        }
    }

    private static string CommandLabel(string commandId, string labelKey)
    {
        string translated = Texts.T(labelKey);
        Require(translated != labelKey, "A navigation command label fell back to its key: " + labelKey);
        string expected = CommandCatalog.Label(commandId);
        Require(expected == translated,
            $"CommandCatalog label for {commandId} does not use the centralized {labelKey} translation.");
        return expected;
    }

    private static string ExpectedModeLabel(ComboBox modes) =>
        (modes.SelectedItem as ComboBoxItem)?.Tag is string mode
            ? Texts.T("mode." + mode.ToLowerInvariant())
            : throw new InvalidOperationException("The selected workspace mode has no tag.");

    private static IEnumerable<string> VisibleModeSelectionText(ComboBox modes)
    {
        // Inspect the actual visible selection, not internal presenter identity.
        // The dropdown has its own visual root. Require exactly one label in callers
        // so dropdown options or an extra stale label cannot masquerade as the selection.
        return modes.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible && text.Bounds.Width > 0 && !string.IsNullOrWhiteSpace(text.Text))
            .Select(text => text.Text)
            .OfType<string>();
    }

    private static async Task OpenModeDropdownAsync(ComboBox modes)
    {
        modes.IsDropDownOpen = true;
        await Program.WaitUntilAsync(() => modes.IsDropDownOpen && modes.Items.OfType<ComboBoxItem>()
            .All(item => item.Content is StackPanel)
            && VisibleModeSelectionText(modes).SequenceEqual(new[] { ExpectedModeLabel(modes) }));
    }

    private static void RequireOpenModeDropdown(ComboBox modes)
    {
        Require(modes.IsDropDownOpen, "The workspace mode dropdown closed unexpectedly.");
        string selected = ExpectedModeLabel(modes);
        Require(VisibleModeSelectionText(modes).SequenceEqual(new[] { selected }),
            $"The open workspace mode selection box was stale in {Texts.Language}: expected {selected}.");
        foreach (ComboBoxItem item in modes.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is not string mode) throw new InvalidOperationException("A mode item has no tag.");
            string key = "mode." + mode.ToLowerInvariant();
            string title = Texts.T(key);
            if (item.Content is not StackPanel panel
                || panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text != title)
                throw new InvalidOperationException($"Opened mode {mode} kept a stale title in {Texts.Language}.");
            string accessible = title + ". " + Texts.T(key + "Description") + ". " + Texts.T("mode.sameSession");
            Require(AutomationProperties.GetName(item) == accessible,
                $"Opened mode {mode} kept a stale accessible description in {Texts.Language}.");
        }
    }

    private static void RequireClosedModeSelection(ComboBox modes)
    {
        string expected = ExpectedModeLabel(modes);
        Require(modes.Items.OfType<ComboBoxItem>().All(item => item.Content is string),
            "A closed mode selector retained open detail content.");
        Require(VisibleModeSelectionText(modes).SequenceEqual(new[] { expected }),
            $"The closed workspace mode selection box was stale in {Texts.Language}: expected {expected}; rendered [{string.Join(", ", VisibleModeSelectionText(modes))}].");
        Require(AutomationProperties.GetName(modes) == Texts.T("shell.mode"),
            "The workspace mode selector kept a stale accessible name.");
        if (modes.SelectedItem is not ComboBoxItem selected || selected.Content as string != expected)
            throw new InvalidOperationException($"The closed selected mode item was stale in {Texts.Language}.");
    }

    private static void RequireInvariants(MainWindow window, DesktopSessionHost session, string settingsPath,
        byte[] initialSettings, string initialDraft, string initialMode)
    {
        Require(ReferenceEquals(window.SessionHost, session),
            "A passive language switch replaced the live desktop session.");
        Require(window.UrlList.Count == 0 && window.BoardsList.Count == 0,
            "A passive language/footer switch admitted a watch or board.");
        Require((window.FindControl<TextBox>("UrlInput")!.Text ?? string.Empty) == initialDraft,
            "A passive language/footer switch changed the URL draft.");
        Require((window.FindControl<ComboBox>("WorkspaceModeComboBox")!.SelectedItem as ComboBoxItem)?.Tag as string == initialMode,
            "A passive language/footer switch changed the selected workspace mode.");
        Require(window.SelectedPage == MainWindow.WorkspacePage.Downloads,
            "Opening the mode dropdown or Tools flyout navigated away from Downloads.");
        Require(initialSettings.SequenceEqual(File.ReadAllBytes(settingsPath)),
            "A passive language/footer switch wrote settings.json.");
        Require(Config.LoadConfig(Path.GetDirectoryName(settingsPath)!).Language == "en",
            "A passive language/footer switch committed a language preference.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
