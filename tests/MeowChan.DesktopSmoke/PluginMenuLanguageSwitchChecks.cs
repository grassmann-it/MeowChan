using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Passive, integrated coverage for materialized plugin surfaces during a language
/// reload. The page is real, but the fixture's compiler, worker and site delegates
/// all fail if presentation accidentally invokes them.
/// </summary>
internal static class PluginMenuLanguageSwitchChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string initialLanguage = Texts.Language;
        string root = Path.Combine(Config.GetConfigDirectory(), "plugin-menu-language");
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(root);
        SourcesPage sources = MergedSourcesSmoke.Create(fixture.Services, out PluginsPage page);
        ContentControl host = window.FindControl<ContentControl>("SourcesPageHost")
            ?? throw new InvalidOperationException("The plugin page host is missing.");
        object? retained = host.Content;
        host.Content = sources;

        try
        {
            await ActivatePageAsync(window, page);
            page.Select(PluginUxFixture.SourceKey);
            await PluginSettingsFormChecks.OpenManagedScopeAsync(page, SettingScope.Source);
            await WaitForSourceEditorAsync(page, "source selection");

            TextBox rawLimit = SourceLimit(page);
            const string rawValue = "1.5 unfinished ü";
            rawLimit.Text = rawValue;
            var draft = page.CurrentDraft!;
            Require(draft.Sources[PluginUxFixture.SourceId].Text("limit") == rawValue
                && draft.Sources[PluginUxFixture.SourceId].Dirty,
                "The fixture did not produce the dirty raw source draft required by the smoke.");

            // Produce the status through the actual copy command. Both success and a
            // platform clipboard failure are intentionally non-empty transient status;
            // the language handler must clear either one.
            page.OpenPluginDeveloperDetails();
            TextBlock copyStatus = page.FindControl<TextBlock>("PluginTechnicalCopyStatus")!;
            Button copy = page.FindControl<Button>("PluginCopyTechnicalDetails")!;
            await Program.WaitUntilAsync(() => copy.IsAttachedToVisualTree()
                && copy.IsEffectivelyVisible && copy.Bounds.Width > 0 && copy.Bounds.Height > 0);
            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !string.IsNullOrEmpty(copyStatus.Text));
            string statusBeforeSwitch = copyStatus.Text!;

            foreach (string language in new[] { "es", "fr", "ru" })
            {
                ContextMenu oldMenu = await OpenMenuAsync(page);
                string oldMenuLanguage = Texts.Language;
                AssertFreshLabels(oldMenu, oldMenuLanguage);

                // Exercise the ordinary language-change path first. Only after its
                // popup closes and a new menu is realized do we test catalog reload.
                Texts.SetLanguage(language);
                await WaitForSourceEditorAsync(page, $"{language} language change");
                await Program.WaitUntilAsync(() => Texts.Language == language
                    && !oldMenu.IsOpen
                    && string.IsNullOrEmpty(copyStatus.Text)
                    && SourceLimit(page).Text == rawValue
                    && page.CurrentDraft?.Sources[PluginUxFixture.SourceId].Text("limit") == rawValue);

                Require(!oldMenu.IsOpen && !page.IsDiscardConfirmationOpen,
                    $"The old plugin popup remained open after the {language} reload.");
                Require(string.IsNullOrEmpty(copyStatus.Text),
                    $"Transient technical-copy status was retained after the {language} reload: {copyStatus.Text}");
                Require(statusBeforeSwitch.Length > 0,
                    "The pre-switch technical-copy status unexpectedly became empty.");
                Require(page.CurrentDraft?.Sources[PluginUxFixture.SourceId].Dirty == true
                    && SourceLimit(page).Text == rawValue,
                    $"The dirty raw draft was not retained after the {language} reload.");

                ContextMenu freshMenu = await OpenMenuAsync(page);
                Require(!ReferenceEquals(freshMenu, oldMenu),
                    $"The {language} menu reused the closed popup instance.");
                AssertFreshLabels(freshMenu, language);

                // Initialize ONLY: no pending SetLanguage notification can mask a
                // language-code-only menu invalidation guard. This menu was opened
                // after the preceding language-change handler finished rendering.
                Texts.Initialize();
                await Program.WaitUntilAsync(() => Texts.Language == language && !freshMenu.IsOpen);
                await WaitForSourceEditorAsync(page, $"{language} same-language catalog reload");
                ContextMenu reloadedMenu = await OpenMenuAsync(page);
                Require(!ReferenceEquals(reloadedMenu, freshMenu),
                    $"The {language} same-language catalog reload reused the closed popup instance.");
                AssertFreshLabels(reloadedMenu, language);
                Require(page.CurrentDraft?.Sources[PluginUxFixture.SourceId].Dirty == true
                    && SourceLimit(page).Text == rawValue,
                    $"The dirty raw draft was not retained after the {language} same-language reload.");
                reloadedMenu.Close();
                await Program.WaitUntilAsync(() => !reloadedMenu.IsOpen);

                // The root language handler also owns discard confirmation. Open the
                // real confirmation from the dirty draft and prove a following reload
                // cancels it without discarding the raw value.
                page.RequestDiscardSelectedDraft();
                await Program.WaitUntilAsync(() => page.IsDiscardConfirmationOpen);
                Texts.Initialize();
                await Program.WaitUntilAsync(() => Texts.Language == language
                    && !page.IsDiscardConfirmationOpen
                    && page.CurrentDraft?.Sources[PluginUxFixture.SourceId].Text("limit") == rawValue);
                await WaitForSourceEditorAsync(page, $"{language} discard-confirmation reload");
                Require(!page.IsDiscardConfirmationOpen && SourceLimit(page).Text == rawValue
                    && page.CurrentDraft?.Sources[PluginUxFixture.SourceId].Dirty == true,
                    $"Discard confirmation or raw draft was not preserved correctly during {language} reload.");
            }

            Require(fixture.ForbiddenCalls == 0,
                "Plugin menu/language presentation invoked compiler, worker or site services.");
            Console.WriteLine("SMOKE plugin menu language: passive real-menu ES/FR/RU reloads closed stale popups, refreshed labels, cleared transient copy status, cancelled discard confirmation and retained the dirty raw draft.");
        }
        finally
        {
            sources.EndLifetime();
            host.Content = retained;
            Texts.SetLanguage(initialLanguage);
        }
    }

    private static async Task ActivatePageAsync(MainWindow window, PluginsPage page)
    {
        MergedSourcesSmoke.OpenPlugins(window);
        await page.ActivateAsync();
        await Program.WaitUntilAsync(() => page.IsAttachedToVisualTree() && page.IsEffectivelyVisible);
    }

    private static async Task WaitForSourceEditorAsync(PluginsPage page, string phase)
    {
        try
        {
            await Program.WaitUntilAsync(() => page.Selected?.Key == PluginUxFixture.SourceKey
                && SourceEditor(page)?.FindField("limit") is TextBox limit
                && limit.IsAttachedToVisualTree());
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                $"The source settings UI was not ready after {phase}: "
                + $"selected={page.Selected?.Key}, currentDraft={page.CurrentDraft != null}, "
                + $"sourceEditor={SourceEditor(page) != null}.", exception);
        }

        // Keep a targeted diagnostic even if the draft/editor disappears after the wait.
        _ = SourceLimit(page);
    }

    private static PluginSettingsEditor? SourceEditor(PluginsPage page)
    {
        var draft = page.CurrentDraft;
        if (draft == null || !draft.Sources.TryGetValue(PluginUxFixture.SourceId, out var source))
            return null;
        return page.GetVisualDescendants().OfType<PluginSettingsEditor>()
            .SingleOrDefault(value => ReferenceEquals(value.Draft, source));
    }

    private static TextBox SourceLimit(PluginsPage page)
    {
        if (page.CurrentDraft == null)
            throw new InvalidOperationException("The current plugin draft is missing while locating the source limit field.");
        PluginSettingsEditor editor = SourceEditor(page)
            ?? throw new InvalidOperationException("The source PluginSettingsEditor for the current draft is not materialized.");
        return editor.FindField("limit") as TextBox
            ?? throw new InvalidOperationException("The source limit TextBox is missing.");
    }

    private static SelectionListItem SelectedRow(PluginsPage page)
    {
        SelectionList list = page.FindControl<SelectionList>("PluginCards")
            ?? throw new InvalidOperationException("The plugin selection list is missing.");
        return list.SelectedItem as SelectionListItem
            ?? throw new InvalidOperationException("The selected plugin row is missing.");
    }

    private static async Task<ContextMenu> OpenMenuAsync(PluginsPage page)
    {
        page.OpenPluginActionMenu(PluginUxFixture.SourceKey, SelectedRow(page));
        await Program.WaitUntilAsync(() => page.PluginActionMenu?.IsOpen == true);
        ContextMenu menu = page.PluginActionMenu
            ?? throw new InvalidOperationException("The reopened plugin action menu is missing.");
        await Program.WaitUntilAsync(() => menu.IsOpen && new[] { "plugins.details", "plugins.disable" }
            .All(command => menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, command)
                && item.IsAttachedToVisualTree() && item.IsEffectivelyVisible
                && item.Bounds.Width > 0 && item.Bounds.Height > 0)));
        return menu;
    }

    private static void AssertFreshLabels(ContextMenu menu, string language)
    {
        Require(menu.Items.OfType<MenuItem>().Any(item => Equals(item.Tag, "plugins.details")),
            $"The {language} plugin menu has no Details action.");
        foreach (string command in new[] { "plugins.details", "plugins.disable" })
        {
            MenuItem item = menu.Items.OfType<MenuItem>().SingleOrDefault(value => Equals(value.Tag, command))
                ?? throw new InvalidOperationException($"The {language} plugin menu has no {command} action.");
            string expected = Texts.T(command);
            string actual = HeaderText(item);
            Require(actual == expected && item.IsEffectivelyVisible && item.Bounds.Width > 0,
                $"The {language} plugin menu label is stale for {command}: expected '{expected}', got '{actual}'.");
            Require(Avalonia.Automation.AutomationProperties.GetName(item) == expected,
                $"The {language} plugin menu accessibility label is stale for {command}.");
        }
    }

    private static string HeaderText(MenuItem item) => item.Header switch
    {
        string text => text,
        TextBlock block => block.Text ?? string.Empty,
        _ => string.Empty
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
