using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

/// <summary>Uses the existing isolated smoke window; no watches, HTTP, or additional native windows.</summary>
internal static partial class RulesPageChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        // Earlier preference fixtures deliberately show failure toasts. Dismiss those before
        // recording this independent editor; this does not dismiss failures of the rule checks.
        foreach (NotificationCard notification in window.GetVisualDescendants().OfType<NotificationCard>().ToArray())
            notification.Close();
        await Program.WaitUntilAsync(() => !window.GetVisualDescendants().OfType<NotificationCard>()
            .Any(notification => notification.IsEffectivelyVisible));
        MediaRuleSet originalRules = window.SessionHost.MediaRules;
        Config originalConfig = Config.LoadConfig();
        RulesPage page = window.RulesPage;
        int originalNewScopeKind = page.NewScopeKind;
        SettingsEditor settings = window.SettingsEditor;
        TextBox intake = window.FindControl<TextBox>("UrlInput")!;
        string? originalIntake = intake.Text;
        string originalSettingsDraft = settings.DownloadPath;
        WindowState originalState = window.WindowState;
        await WaitLayoutAsync(window, () => originalState != WindowState.Normal ||
            Math.Abs(window.Bounds.Width - window.Width) <= 1 && Math.Abs(window.Bounds.Height - window.Height) <= 1);
        double originalWidth = window.Width;
        double originalHeight = window.Height;
        MediaRuleScope board = MediaRuleScope.ForBoard("4chan", "g");
        MediaRuleScope thread = MediaRuleScope.ForThread("4chan", "g", "999");
        const string retainedUrl = "https://unsupported.invalid/retained-rules-draft";
        const string retainedSettings = "retained rules settings draft";
        try
        {
            Texts.SetLanguage("en");
            await PresentationChecks.SwitchModeAsync(window, "Advanced");
            await OpenRulesAsync(window);
            await RuleExtensionsLifetimeChecks.RunAsync(window);
            page.EditScope(MediaRuleScope.Global);
            await FlushAsync();
            RequireScopeSelection(page, MediaRuleScope.Global);
            page.FindControl<CheckBox>("RuleEnabledInput")!.IsChecked = true;
            page.FindControl<ComboBox>("RuleTypesModeInput")!.SelectedIndex = (int)MediaTypeSelectionMode.Selected;
            (await WaitForRuleExtensionsEditorAsync(window, page)).Text = "JPG, WebM";
            page.FindControl<ComboBox>("RuleSizeModeInput")!.SelectedIndex = (int)MediaSizeLimitMode.Limited;
            page.FindControl<TextBox>("RuleMegabytesInput")!.Text = "1.5";
            Require(page.ExtensionsText.Contains(".jpg", StringComparison.Ordinal) && page.ExtensionsText.Contains(".webm", StringComparison.Ordinal),
                "The first focused edit was not synchronously attached to the rule tag draft: " + page.ExtensionsText);
            Console.WriteLine("SMOKE rules before global save: types=" + page.TypesMode + "; raw=" + page.ExtensionsText
                + "; sizeMode=" + page.SizeMode + "; MB=" + page.MegabytesText);
            await SaveThroughButtonAsync(window, page);
            MediaRuleSet savedGlobal = window.SessionHost.MediaRules;
            EffectiveMediaRule global = new MediaRuleResolver(savedGlobal).Resolve(MediaRuleScope.Global);
            Require(global.Types.Extensions.SequenceEqual(new[] { ".jpg", ".webm" }) && global.SizeLimit.Bytes == 1_500_000,
                "The global Rules editor did not commit normalized types and decimal MB through the session host. Actual: mode=" + global.Types.Mode
                + "; extensions=" + string.Join(",", global.Types.Extensions) + "; bytes=" + global.SizeLimit.Bytes
                + "; draft=" + page.ExtensionsText + "; draftMB=" + page.MegabytesText + "; revision=" + savedGlobal.Revision);
            Require(File.ReadAllText(Path.Combine(Config.GetConfigDirectory(), "media_rules.json")).Contains(savedGlobal.Revision, StringComparison.Ordinal),
                "The visible saved rule revision was not present in the isolated profile.");

            page.EditScope(board);
            await FlushAsync();
            RequireScopeSelection(page, board);
            page.FindControl<CheckBox>("RuleEnabledInput")!.IsChecked = true;
            page.FindControl<ComboBox>("RuleTypesModeInput")!.SelectedIndex = (int)MediaTypeSelectionMode.Selected;
            (await WaitForRuleExtensionsEditorAsync(window, page)).Text = "PNG";
            page.FindControl<ComboBox>("RuleSizeModeInput")!.SelectedIndex = (int)MediaSizeLimitMode.Inherit;
            await FlushAsync();
            Require(page.EffectiveTypesLabel == ".png" && page.TypesOriginLabel.Contains("4chan/g", StringComparison.Ordinal)
                && page.SizeOriginLabel.Contains("Global", StringComparison.Ordinal),
                "The preview did not distinguish the board type override from its inherited size origin.");
            await SaveThroughButtonAsync(window, page);
            EffectiveMediaRule inherited = window.SessionHost.ResolveRules(board);
            Require(inherited.TypesOrigin.Scope == board && inherited.SizeLimitOrigin.Scope == MediaRuleScope.Global
                && inherited.SizeLimit.Bytes == 1_500_000, "The saved board rule did not resolve fields independently.");

            page.FindControl<CheckBox>("RuleEnabledInput")!.IsChecked = false;
            await FlushAsync();
            Require(page.TypesOriginLabel.Contains("Global", StringComparison.Ordinal)
                && page.EffectiveTypesLabel.Contains(".webm", StringComparison.Ordinal),
                "Disabling the draft did not preview inheritance from its parent.");
            await SaveThroughButtonAsync(window, page);
            Require(!window.SessionHost.MediaRules.Rules.Single(rule => rule.Scope == board).Enabled
                && window.SessionHost.ResolveRules(board).TypesOrigin.Scope == MediaRuleScope.Global,
                "Disabling a saved rule did not restore inherited values.");
            page.FindControl<CheckBox>("RuleEnabledInput")!.IsChecked = true;
            await SaveThroughButtonAsync(window, page);
            RequireScopeSelection(page, board);

            // Picking a scope is separate from opening it. Refreshing translated labels
            // must retain that choice and keep the current editor untouched until Open.
            ComboBox scopes = page.FindControl<ComboBox>("RulesScopeList")!;
            scopes.SelectedItem = page.ScopeChoices.Single(choice => choice.Scope == MediaRuleScope.Global);
            page.FindControl<ComboBox>("NewRuleScopeKindInput")!.SelectedIndex = (int)MediaRuleScopeKind.Thread;
            Texts.SetLanguage("de");
            await FlushAsync();
            RequireScopeSelection(page, MediaRuleScope.Global);
            Require(page.NewScopeKind == (int)MediaRuleScopeKind.Thread,
                "Changing language altered the pending new-scope kind.");
            Require(page.EffectiveTypesLabel == ".png", "Selecting a scope changed the editor before Open.");
            Click(page.FindControl<Button>("OpenSavedRuleButton")!);
            await FlushAsync();
            RequireScopeSelection(page, MediaRuleScope.Global);
            Require(page.EffectiveTypesLabel.Contains(".webm", StringComparison.Ordinal), "Open ignored the selected global scope.");
            scopes.SelectedItem = page.ScopeChoices.Single(choice => choice.Scope == board);
            Click(page.FindControl<Button>("OpenSavedRuleButton")!);
            Texts.SetLanguage("en");
            await FlushAsync();
            RequireScopeSelection(page, board);
            Require(page.EffectiveTypesLabel == ".png", "Open did not return to the selected board scope.");

            window.WindowState = WindowState.Normal;
            double overviewWidth = Math.Max(760, Math.Min(1440,
                Math.Floor((window.Screens.ScreenFromWindow(window)?.WorkingArea.Width ?? 3200) / window.RenderScaling) - 48));
            double overviewHeight = Math.Max(500, Math.Min(920,
                Math.Floor((window.Screens.ScreenFromWindow(window)?.WorkingArea.Height ?? 2000) / window.RenderScaling) - 64));
            window.Width = overviewWidth;
            window.Height = overviewHeight;
            await WaitLayoutAsync(window, () => Math.Abs(window.Bounds.Width - overviewWidth) <= 1
                && Math.Abs(window.Bounds.Height - overviewHeight) <= 1 && Fits(window, page.FindControl<Button>("SaveRuleButton")!));
            Program.CaptureWindow(window, "smoke-rules-board-inheritance.png");

            page.EditScope(thread);
            await FlushAsync();
            RequireScopeSelection(page, thread);
            page.FindControl<ComboBox>("RuleTypesModeInput")!.SelectedIndex = (int)MediaTypeSelectionMode.Selected;
            (await WaitForRuleExtensionsEditorAsync(window, page)).Text = "image/jpeg";
            page.FindControl<ComboBox>("RuleSizeModeInput")!.SelectedIndex = (int)MediaSizeLimitMode.Limited;
            page.FindControl<TextBox>("RuleMegabytesInput")!.Text = "not-a-number";
            await FlushAsync();
            Require(page.HasValidationError && !page.CanSave, "Invalid rule values did not block save.");
            string revisionBeforeInvalidDraft = window.SessionHost.MediaRules.Revision;
            Click(page.FindControl<Button>("SaveRuleButton")!);
            await FlushAsync();
            Require(window.SessionHost.MediaRules.Revision == revisionBeforeInvalidDraft,
                "A disabled or invalid Rules save still changed durable rules.");

            intake.Text = retainedUrl;
            settings.DownloadPath = retainedSettings;
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                foreach (string language in new[] { "en", "de" })
                {
                    Click(window.FindControl<Button>("DownloadsNavigationButton")!);
                    Click(window.FindControl<Button>("SettingsNavigationButton")!);
                    await Program.WaitUntilAsync(() => window.SettingsPageVisible);
                    Texts.SetLanguage(language);
                    await FlushAsync();
                    await OpenRulesAsync(window);
                    RequireScopeSelection(page, thread);
                    Require(ReferenceEquals(page, window.RulesPage) && page.ExtensionsText == "image/jpeg"
                        && page.MegabytesText == "not-a-number" && page.HasValidationError
                        && page.TypesMode == (int)MediaTypeSelectionMode.Selected
                        && page.SizeMode == (int)MediaSizeLimitMode.Limited,
                        "Page, language or mode navigation discarded or normalized an invalid rule draft.");
                    Require(intake.Text == retainedUrl && settings.DownloadPath == retainedSettings,
                        "Rules navigation discarded a Downloads or Settings draft.");
                    Button rulesNavigation = window.FindControl<Button>("RulesNavigationButton")!;
                    Require(!rulesNavigation.IsVisible || (mode == "Advanced" && rulesNavigation.IsEffectivelyEnabled
                        && Fits(window, rulesNavigation)), "Direct Rules navigation was outside Advanced, clipped or disabled.");
                    Button toolsNavigation = window.FindControl<Button>("ToolsNavigationButton")!;
                    MenuFlyout toolsMenu = toolsNavigation.Flyout as MenuFlyout
                        ?? throw new InvalidOperationException("Tools has no navigation menu.");
                    MenuItem[] rulesOverflow = toolsMenu.Items.OfType<MenuItem>()
                        .Where(item => item.IsVisible && item.Tag as string == "nav.rules").ToArray();
                    Require(rulesOverflow.Length == (rulesNavigation.IsVisible ? 0 : 1),
                        "Tools duplicated direct Rules navigation or lost its hidden destination.");
                    Require(rulesNavigation.IsVisible || (toolsNavigation.IsVisible && toolsNavigation.IsEffectivelyEnabled
                        && Fits(window, toolsNavigation) && rulesOverflow[0].IsEnabled),
                        "Hidden Rules navigation did not retain a usable Tools route.");
                    MenuItem rulesMenu = window.FindControl<MenuItem>("RulesMenuItem")!;
                    Require(rulesMenu.IsVisible && rulesMenu.IsEnabled,
                        "Rules navigation did not retain its normal app-menu route.");
                    RequireIconLabel(page.FindControl<Button>("OpenSavedRuleButton")!, page.OpenLabel);
                    RequireIconLabel(page.FindControl<Button>("OpenNewRuleScopeButton")!, page.OpenLabel);
                    RequireIconLabel(page.FindControl<Button>("DiscardRuleButton")!, page.DiscardLabel);
                    RequireIconLabel(page.FindControl<Button>("SaveRuleButton")!, page.SaveLabel);
                    double width = mode == "Barebones" ? 640 : 760;
                    double height = mode == "Barebones" ? 480 : 500;
                    window.WindowState = WindowState.Normal;
                    window.Width = width;
                    window.Height = height;
                    ScrollViewer scroll = page.FindControl<ScrollViewer>("RulesScroll")!;
                    try
                    {
                        await WaitLayoutAsync(window, () => PresentationChecks.MatchesRequestedSize(window, width, height)
                            && Fits(window, page.FindControl<Button>("SaveRuleButton")!)
                            && Fits(window, page.FindControl<Button>("DiscardRuleButton")!)
                            && scroll.Viewport.Height > 40 && ContentFitsHorizontally(page, scroll));
                    }
                    catch
                    {
                        Console.WriteLine($"SMOKE rules layout failure: mode={mode}; language={language}; requested={width}x{height}; actual={window.Bounds}; minimum={window.MinWidth}x{window.MinHeight}; state={window.WindowState}; viewport={scroll.Viewport}; extent={scroll.Extent}.");
                        foreach (string name in new[] { "SaveRuleButton", "DiscardRuleButton", "RulesScroll", "RulesContent", "RuleExtensionsInput" })
                        {
                            Control control = page.FindControl<Control>(name)!;
                            Console.WriteLine($"SMOKE rules control: {name}; bounds={control.Bounds}; width={control.Width}; desired={control.DesiredSize}; origin={control.TranslatePoint(new Point(0, 0), window)}; visible={control.IsEffectivelyVisible}; fits={Fits(window, control)}.");
                        }
                        Program.CaptureWindow(window, "smoke-rules-layout-failure.png");
                        throw;
                    }
                    scroll.Offset = new Vector(0, scroll.Extent.Height);
                    await FlushAsync();
                    Program.CaptureWindow(window, $"smoke-rules-{mode.ToLowerInvariant()}-{language}-minimum.png");
                }
            }

            Texts.SetLanguage("en");
            await FlushAsync();
            page.EditScope(board);
            await FlushAsync();
            Require(page.EffectiveTypesLabel == ".png" && !page.HasValidationError,
                "The saved board editor was contaminated by another scope's invalid draft.");
            RequireScopeSelection(page, board);
            page.EditScope(thread);
            await FlushAsync();
            RequireScopeSelection(page, thread);
            Require(page.ExtensionsText == "image/jpeg" && page.MegabytesText == "not-a-number",
                "Switching rule scopes silently discarded the previous scope's draft.");

            // Exercise locale formatting independently of a failed parse, without committing the draft.
            page.ExtensionsText = "PNG";
            page.MegabytesText = "0.000001";
            await FlushAsync();
            Texts.SetLanguage("de");
            await FlushAsync();
            RequireModeSelections(page);
            Require(page.MegabytesText == "0,000001" && page.CanSave,
                "Changing language did not preserve the exact one-byte decimal MB draft.");
            Texts.SetLanguage("en");
            await FlushAsync();
            RequireModeSelections(page);
            Require(page.MegabytesText == "0.000001", "The size draft lost precision on the language roundtrip.");

            await CheckOriginNavigationAsync(window);
            await CheckFailedSaveInSameWindowAsync(window);
            await CheckUrsaDraftWorkflowAsync(window);
        }
        finally
        {
            // The page intentionally has no bulk-delete command. Cleanup uses the same real host
            // command as the page callback, never direct edits to a leased profile file.
            MediaRuleSet current = window.SessionHost.MediaRules;
            await window.SessionHost.SaveRulesAsync(originalRules.Rules, current.Revision);
            page.RefreshConfirmedRules();
            page.DiscardCurrentDraft();
            page.EditScope(MediaRuleScope.Global);
            page.DiscardCurrentDraft();
            page.NewScopeKind = originalNewScopeKind;
            settings.DownloadPath = originalSettingsDraft;
            intake.Text = originalIntake;
            Texts.SetLanguage(originalConfig.Language);
            await PresentationChecks.SwitchModeAsync(window, originalConfig.WorkspaceMode);
            window.WindowState = WindowState.Normal;
            window.Width = originalWidth;
            window.Height = originalHeight;
            window.WindowState = originalState;
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        }
        Require(SameCatalog(window.SessionHost.MediaRules, originalRules),
            "The Rules smoke did not restore the original catalog.");
        Console.WriteLine("SMOKE rules: durable global/board rules, field origins, disabled inheritance, retained invalid drafts, locale precision, guarded save failures and compact EN/DE layouts passed.");
    }

    private static void ClickParentBreadcrumb(RulesPage page)
    {
        var parent = page.FindControl<Ursa.Controls.Breadcrumb>("RuleScopeBreadcrumb")!.Items.OfType<Ursa.Controls.BreadcrumbItem>()
            .Single(item => Equals(item.Tag, page.ParentScope));
        Click(parent);
    }

    private static async Task CheckOriginNavigationAsync(MainWindow window)
    {
        await OpenRulesAsync(window);
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!;
        object? retained = host.Content;
        MediaRuleScope source = MediaRuleScope.ForSource("unknown-provider");
        MediaRuleScope board = MediaRuleScope.ForBoard("unknown-provider", "unknown-board");
        MediaRuleScope thread = MediaRuleScope.ForThread("unknown-provider", "unknown-board", "unknown-thread");
        MediaRuleSet confirmed = new MediaRuleSet("origins-1", new[]
        {
            new MediaRule(MediaRuleScope.Global, sizeLimit: new MediaSizeLimit(MediaSizeLimitMode.Limited, 2_000_000)),
            new MediaRule(source, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { "jpg" })),
            new MediaRule(board, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { "png" }))
        });
        int writes = 0;
        RulesPage fixture = new RulesPage(() => confirmed, (_, _) => { writes++; return Task.FromResult(confirmed); });
        host.Content = fixture;
        try
        {
            fixture.IsAvailable = true;
            fixture.EditScope(thread);
            fixture.RuleEnabled = true; // Retain a dirty inherited thread draft independently of its parent drafts.
            fixture.NewSourceId = "manual-source";
            fixture.NewBoardId = "manual-board";
            fixture.NewThreadId = "manual-thread";
            Require(fixture.CanOpenTypesOrigin && fixture.CanOpenSizeOrigin, "Inherited values lost their origin actions.");
            Click(fixture.FindControl<Button>("OpenTypesOriginButton")!);
            Require(fixture.ActiveScope == board, "Types origin did not open the actual board rule.");
            fixture.ExtensionsText = "gif";
            ClickParentBreadcrumb(fixture);
            Require(fixture.ActiveScope == source, "Board parent navigation did not open the source scope.");
            fixture.ExtensionsText = "webm";
            Click(fixture.FindControl<Button>("OpenTypesOriginButton")!);
            Require(fixture.ActiveScope == source && fixture.ExtensionsText == "webm", "Source-origin navigation discarded its own draft.");
            fixture.EditScope(thread);
            Click(fixture.FindControl<Button>("OpenSizeOriginButton")!);
            Require(fixture.ActiveScope == MediaRuleScope.Global && !fixture.CanOpenParent,
                "Size origin did not open Global independently of the type origin.");
            fixture.EditScope(board);
            Require(fixture.ExtensionsText == "gif", "Origin navigation lost the board draft.");
            fixture.EditScope(source);
            Require(fixture.ExtensionsText == "webm", "Origin navigation lost the source draft.");
            fixture.EditScope(thread);
            fixture.TypesMode = (int)MediaTypeSelectionMode.Selected;
            fixture.ExtensionsText = "avif";
            Click(fixture.FindControl<Button>("OpenTypesOriginButton")!);
            Require(fixture.ActiveScope == thread && fixture.ExtensionsText == "avif", "Thread-origin navigation opened a parent instead.");
            Require(fixture.NewSourceId == "manual-source" && fixture.NewBoardId == "manual-board" && fixture.NewThreadId == "manual-thread",
                "Origin navigation lost manually entered unknown object identities.");
            confirmed = new MediaRuleSet("origins-2", confirmed.Rules.Where(rule => rule.Scope != source)
                .Append(new MediaRule(source, types: MediaTypeSelection.All)));
            ClickParentBreadcrumb(fixture);
            ClickParentBreadcrumb(fixture);
            Require(fixture.ActiveScope == source && fixture.HasValidationError && !fixture.CanSave && fixture.ExtensionsText == "webm",
                "Parent navigation erased an externally conflicting draft or its stale marker.");
            fixture.EditScope(thread);
            fixture.EditScope(source);
            Require(fixture.HasValidationError && fixture.ExtensionsText == "webm", "Returning through scopes cleared a stale conflict.");
            confirmed = MediaRuleSet.Default;
            fixture.EditScope(MediaRuleScope.Global);
            Require(!fixture.CanOpenTypesOrigin && !fixture.CanOpenSizeOrigin && !fixture.CanOpenParent,
                "Built-in defaults were exposed as invented stored rules.");
            Click(fixture.FindControl<Button>("OpenTypesOriginButton")!);
            Require(fixture.ActiveScope == MediaRuleScope.Global && writes == 0, "Origin navigation wrote rules or followed a default value.");
            confirmed = new MediaRuleSet("origins-3", new[]
            {
                new MediaRule(MediaRuleScope.Global, sizeLimit: new MediaSizeLimit(MediaSizeLimitMode.Limited, 2_000_000)),
                new MediaRule(board, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { "png" }))
            });
            fixture.EditScope(thread);
            fixture.DiscardCurrentDraft();
            Avalonia.Styling.ThemeVariant? originalTheme = window.RequestedThemeVariant;
            try
            {
                foreach (Avalonia.Styling.ThemeVariant theme in new[] { Avalonia.Styling.ThemeVariant.Dark, Avalonia.Styling.ThemeVariant.Light })
                {
                    window.RequestedThemeVariant = theme;
                    foreach (string language in new[] { "en", "de" })
                    {
                        Texts.SetLanguage(language);
                        await FlushAsync();
                        ScrollViewer scroll = fixture.FindControl<ScrollViewer>("RulesScroll")!;
                        scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height - 60));
                        await FlushAsync();
                        Program.CaptureWindow(window, "smoke-rule-origins-" + theme.Key.ToString()!.ToLowerInvariant() + "-" + language + ".png");
                    }
                }
            }
            finally { window.RequestedThemeVariant = originalTheme; }
            Require(writes == 0, "Rule origin or parent navigation performed an automatic save.");
        }
        finally
        {
            fixture.EndLifetime();
            host.Content = retained;
            Texts.SetLanguage("en");
        }
        Console.WriteLine("SMOKE rule origins: field-specific Global/Source/Board/Thread, parent scopes, dirty and stale draft retention, unknown IDs, defaults and zero writes passed.");
    }

    private static async Task CheckFailedSaveInSameWindowAsync(MainWindow window)
    {
        await OpenRulesAsync(window);
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!;
        object? retained = host.Content;
        MediaRuleSet baseline = window.SessionHost.MediaRules;
        TaskCompletionSource<MediaRuleSet> pendingSave = new TaskCompletionSource<MediaRuleSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        List<bool> busyChanges = new List<bool>();
        RulesPage fixture = new RulesPage(() => baseline, (rules, revision) =>
        {
            Require(revision == baseline.Revision && rules.Any(rule => rule.Scope == MediaRuleScope.Global),
                "The save callback received no global rule or the wrong baseline revision.");
            writes++;
            return pendingSave.Task;
        });
        fixture.BusyChanged += (_, _) => busyChanges.Add(fixture.IsBusy);
        host.Content = fixture;
        try
        {
            fixture.IsAvailable = true;
            fixture.EditScope(MediaRuleScope.Global);
            fixture.TypesMode = (int)MediaTypeSelectionMode.Selected;
            fixture.ExtensionsText = "GIF";
            fixture.IsAvailable = false;
            await fixture.SaveCurrentAsync();
            Require(writes == 0, "An unavailable Rules page issued a write.");
            fixture.IsAvailable = true;
            Task save = fixture.SaveCurrentAsync();
            Require(fixture.IsBusy && !fixture.IsEditable && !fixture.CanSave,
                "Saving did not disable the rule editor and save action.");
            await fixture.SaveCurrentAsync();
            Require(writes == 1, "An in-flight rule write was submitted twice.");
            fixture.IsAvailable = false;
            pendingSave.SetException(new IOException("Synthetic rule commit failure"));
            await save;
            fixture.IsAvailable = true;
            await FlushAsync();
            Require(!fixture.IsBusy && fixture.HasError && fixture.ErrorMessage?.Contains("Synthetic rule commit failure", StringComparison.Ordinal) == true
                && fixture.ExtensionsText == "GIF" && window.SessionHost.MediaRules.Revision == baseline.Revision,
                "A failed commit hid its error, activated rules, or discarded the retained draft.");
            Require(busyChanges.SequenceEqual(new[] { true, false }), "BusyChanged did not bracket the rule write.");
            await WaitLayoutAsync(window, () => Fits(window, fixture.FindControl<Button>("SaveRuleButton")!)
                && Fits(window, fixture.FindControl<Button>("DiscardRuleButton")!)
                && fixture.FindControl<TextBlock>("RuleActionError")!.IsEffectivelyVisible);
            Program.CaptureWindow(window, "smoke-rules-save-failure.png");
        }
        finally
        {
            if (!pendingSave.Task.IsCompleted) pendingSave.TrySetException(new IOException("Rules smoke cleanup"));
            fixture.EndLifetime();
            host.Content = retained;
        }
    }

    private static async Task SaveThroughButtonAsync(MainWindow window, RulesPage page)
    {
        await FlushAsync();
        Require(page.CanSave, "A valid changed rule did not enable Save.");
        string revision = window.SessionHost.MediaRules.Revision;
        Click(page.FindControl<Button>("SaveRuleButton")!);
        await Program.WaitUntilAsync(() => !page.IsBusy && (page.HasError || window.SessionHost.MediaRules.Revision != revision));
        Require(!page.HasError && window.SessionHost.MediaRules.Revision != revision,
            "A Rules save did not commit: " + page.ErrorMessage);
    }

    private static async Task OpenRulesAsync(MainWindow window)
    {
        window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Rules && window.RulesPage.IsEditable);
    }

    private static async Task<TextBox> WaitForRuleExtensionsEditorAsync(Window window, RulesPage page)
    {
        var tags = page.FindControl<MeowChan.Desktop.Controls.RuleExtensionsInput>("RuleExtensionsInput")!;
        TextBox? input = null;
        try
        {
            // The Selected mode reveals a previously hidden TagInput. Its inner template
            // is materialized by layout, rather than by the SelectedIndex assignment.
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                input = tags.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(value => value.IsAttachedToVisualTree()
                    && value.IsEffectivelyVisible && value.IsEffectivelyEnabled && value.Bounds.Width > 0 && value.Bounds.Height > 0);
                if (input == null) return false;
                input.BringIntoView(); window.UpdateLayout();
                if (!Fits(window, input)) return false;
                foreach (ScrollViewer scroll in input.GetVisualAncestors().OfType<ScrollViewer>())
                {
                    Point? point = input.TranslatePoint(default, scroll);
                    if (point == null || point.Value.Y < -1 || point.Value.Y + input.Bounds.Height > scroll.Bounds.Height + 1) return false;
                }
                input.Focus(NavigationMethod.Tab);
                return ReferenceEquals(window.FocusManager?.GetFocusedElement(), input);
            });
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("The visible rule extension editor did not materialize as an attached, enabled, on-screen, focusable TextBox. "
                + "Scope=" + page.ActiveScope + "; mode=" + page.TypesMode + "; selectionVisible=" + page.HasTypeSelection
                + "; tagAttached=" + tags.IsAttachedToVisualTree() + "; tagVisible=" + tags.IsEffectivelyVisible
                + "; tagEnabled=" + tags.IsEffectivelyEnabled + "; textBoxes=" + tags.GetVisualDescendants().OfType<TextBox>().Count()
                + "; tagBounds=" + tags.Bounds, exception);
        }
        return input!;
    }

    private static Task WaitLayoutAsync(Window window, Func<bool> condition) => Program.WaitUntilAsync(() =>
    {
        window.UpdateLayout();
        return condition();
    });
    private static async Task FlushAsync() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static void RequireScopeSelection(RulesPage page, MediaRuleScope expected)
    {
        RuleScopeChoice? selected = page.FindControl<ComboBox>("RulesScopeList")!.SelectedItem as RuleScopeChoice;
        Require(selected != null && selected.Scope == expected && page.SelectedScopeChoice == selected
            && page.ScopeChoices.Contains(selected),
            "The visible scope choice and editor selection model diverged while refreshing rule scopes.");
        RequireModeSelections(page);
    }
    private static void RequireModeSelections(RulesPage page)
    {
        RequireModeSelection(page, "NewRuleScopeKindInput", page.NewScopeKind, page.ScopeKindChoices);
        RequireModeSelection(page, "RuleTypesModeInput", page.TypesMode, page.TypesModeChoices);
        RequireModeSelection(page, "RuleSizeModeInput", page.SizeMode, page.SizeModeChoices);
    }
    private static void RequireModeSelection(RulesPage page, string name, int expected, IReadOnlyList<string> labels)
    {
        ComboBox choice = page.FindControl<ComboBox>(name)!;
        Require(choice.SelectedIndex == expected && choice.SelectedItem as string == labels[expected],
            $"{name} lost its visible selection after a rule scope or language change: "
            + $"index={choice.SelectedIndex}, item='{choice.SelectedItem ?? "<null>"}', "
            + $"expected index={expected}, item='{labels[expected]}'.");
    }
    private static bool SameCatalog(MediaRuleSet actual, MediaRuleSet expected) =>
        actual.Rules.Count == expected.Rules.Count && expected.Rules.All(rule =>
            actual.Rules.Any(candidate => candidate.Scope == rule.Scope && candidate.Enabled == rule.Enabled
                && candidate.Types.Mode == rule.Types.Mode && candidate.Types.Extensions.SequenceEqual(rule.Types.Extensions)
                && candidate.SizeLimit == rule.SizeLimit));
    private static void RequireIconLabel(Button button, string expected)
    {
        Require(button.Content is StackPanel panel && panel.Children.Count == 2
            && panel.Children[0] is Control
            && panel.Children[1] is TextBlock label && label.Text == expected,
            $"{button.Name} lost its icon or translated action label.");
    }

    private static bool Fits(Window window, Control control)
    {
        Point? point = control.TranslatePoint(new Point(0, 0), window);
        return control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0 && point.HasValue
            && point.Value.X >= -1 && point.Value.Y >= -1 && point.Value.X + control.Bounds.Width <= window.Bounds.Width + 1
            && point.Value.Y + control.Bounds.Height <= window.Bounds.Height + 1;
    }
    private static bool ContentFitsHorizontally(RulesPage page, ScrollViewer scroll)
    {
        // Avalonia can retain the previous wider Extent after a hidden page is resized.
        // Check the arranged content and all its visible controls against the viewport.
        StackPanel content = page.FindControl<StackPanel>("RulesContent")!;
        return content.Bounds.Width > 0 && content.Bounds.Width <= scroll.Viewport.Width + 1
            && content.GetVisualDescendants().OfType<Control>()
                .Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0)
                .All(control => control.TranslatePoint(new Point(0, 0), content) is Point point
                    && point.X >= -1 && point.X + control.Bounds.Width <= scroll.Viewport.Width + 1);
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
