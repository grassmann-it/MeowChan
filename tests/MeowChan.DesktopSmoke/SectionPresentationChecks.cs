using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;

namespace MeowChan.DesktopSmoke;

internal static partial class SectionComparisonChecks
{
    private sealed record PresentationCell(string Language, string Theme, string Mode, int Width, int Height)
    {
        public string Id => Language + "-" + Theme.ToLowerInvariant() + "-" + Mode.ToLowerInvariant();
    }

    // Optional presentation coverage only; never repeats the ten semantic A/B/C tasks.
    private static async Task PresentationMatrix(MainWindow owner, string root)
    {
        string initialMode = Config.LoadConfig().WorkspaceMode;
        try
        {
            foreach (string language in new[] { "en", "de" })
            foreach (string theme in new[] { "Light", "Dark" })
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                // Mode switch and global language/theme are explicit fixture presentation setup.
                await PresentationChecks.SwitchModeAsync(owner, mode);
                Texts.SetLanguage(language);
                Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                await Settle();
                var cell = new PresentationCell(language, theme, mode, mode == "Barebones" ? 640 : 760, mode == "Barebones" ? 480 : 500);
                await Settings(owner, root, "B", "settings-presentation-" + cell.Id, cell);
                Texts.SetLanguage(language); // preceding fixture owns no language preference change
                await Plugins(owner, root, "B", shortPage: false, presentation: cell);
            }
            Console.WriteLine("SECTION_PRESENTATION_COMPLETED: 12 cells, Settings and Plugins production B only; inspect every task-result and screenshot.");
        }
        finally { await PresentationChecks.SwitchModeAsync(owner, initialMode); }
    }

    private static async Task SettingsPresentation(Scenario scenario, SettingsEditor editor, Config config, Func<int> commits)
    {
        scenario.Expect("Production B at actual presentation minimum; localized labels, exact Downloads heading, retained relative path, compact Go focus, then visible local path error without a commit.");
        await scenario.PresentationObservation("initial-settings", new { selectedMode = editor.SelectedWorkspaceMode?.Value });
        Require(editor.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == editor.SettingsTitle && scenario.PresentationVisible(t)),
            "Settings title is not actually localized/rendered.");
        await scenario.OpenContents();
        await scenario.PresentationObservation("minimum-contents", new { geometry = "natural navigation width at the actual window minimum" });
        await scenario.Capture("minimum-contents");
        await scenario.Navigate("settings.downloads");
        TextBox path = editor.FindControl<TextBox>("DownloadPathInput")!;
        const string draft = "relative unfinished ü folder";
        await scenario.Edit(path, draft);
        await scenario.CompactPresentationJump("settings.notifications", captureFocus: false);
        await scenario.CompactPresentationJump("settings.downloads", captureFocus: true);
        Require(path.Text == draft && editor.DownloadPath == draft && commits() == 0 && config.DownloadPath != draft,
            "Presentation navigation lost or committed the actual path draft.");
        await scenario.Edit(path, string.Empty);
        await scenario.Click(editor.FindControl<Button>("SaveSettingsButton")!);
        await Settle();
        TextBlock error = editor.FindControl<TextBlock>("DownloadPathInputError")!;
        Require(path.IsFocused && path.Text == string.Empty && error.IsEffectivelyVisible
            && error.Text == Texts.T("settings.pathInvalid") && scenario.PresentationVisible(error) && commits() == 0,
            "Actual local path error/focus is not visible at this presentation minimum.");
        await scenario.PresentationObservation("path-error", new { path.Text, localError = error.Text, error.Bounds,
            pathFocused = path.IsFocused, config.DownloadPath, commits = commits(), nativePicker = "not-run; existing manual input and local Save validation only" });
        await scenario.Capture("path-error");
        scenario.Result("pass", "retain-provisionally", "Presentation B only; raw path/compact activation/error observed. No native picker or A/B/C decision inferred.");
    }

    private static async Task PluginsPresentation(Scenario scenario, PluginsPage page, string profileRoot, string beforeFiles, Func<int> workerCalls)
    {
        scenario.Expect("Production B at actual minimum; source-b despite duplicate names, retained invalid draft through compact information roundtrip, actual Go focus and exact local error focus; no metadata writes/worker calls.");
        await scenario.PresentationObservation("initial-plugins", new { title = page.FindControl<TextBlock>("PluginsTitle")!.Text });
        Require(page.FindControl<TextBlock>("PluginsTitle")!.Text == Texts.T("nav.plugins")
            && scenario.PresentationVisible(page.FindControl<TextBlock>("PluginsTitle")!), "Plugins title is not actually localized/rendered.");
        await scenario.OpenContents();
        await scenario.PresentationObservation("minimum-contents", new { geometry = "natural navigation width at the actual window minimum" });
        await scenario.Capture("minimum-contents");
        string sourceB = scenario.Controller.Sections.Single(s => s.Id.EndsWith("settings/source/source-b", StringComparison.Ordinal)).Id;
        string information = scenario.Controller.Sections.Single(s => s.Id.EndsWith("/information", StringComparison.Ordinal)).Id;
        var others = page.CurrentDraft!.Sources.Where(p => p.Key != "source-b").ToDictionary(p => p.Key, p => p.Value.EditorFingerprint);
        string plugin = page.CurrentDraft.Plugin.EditorFingerprint;
        await scenario.Navigate(sourceB);
        var group = (Expander)scenario.Controller.Sections.Single(s => s.Id == sourceB).ResolveTarget()!;
        if (!group.IsExpanded) await scenario.Toggle(group);
        var form = (PluginSettingsEditor)group.Content!;
        var limit = (TextBox)form.FindField("limit")!;
        const string raw = "invalid source-b presentation draft";
        await scenario.Edit(limit, raw);
        string fingerprint = page.CurrentDraft.Sources["source-b"].EditorFingerprint;
        await scenario.CompactPresentationJump(information, captureFocus: false);
        await scenario.CompactPresentationJump(sourceB, captureFocus: true);
        Require(limit.Text == raw && page.CurrentDraft.Sources["source-b"].EditorFingerprint == fingerprint,
            "Compact presentation navigation lost the exact source-b draft.");
        await scenario.Click(page.FindControl<Button>("PluginSave")!); await Settle();
        string error = AutomationProperties.GetHelpText(limit) ?? string.Empty;
        Require(limit.IsFocused && scenario.PresentationVisible(limit) && limit.Text == raw && error.Length > LongHelp.Length,
            "Plugin local error did not focus the actual source-b input at the presentation minimum.");
        Require(page.CurrentDraft.Plugin.EditorFingerprint == plugin && others.All(p => page.CurrentDraft.Sources[p.Key].EditorFingerprint == p.Value)
            && HashFiles(profileRoot) == beforeFiles && workerCalls() == 0, "Presentation task changed another scope/persisted metadata or called a worker.");
        await scenario.PresentationObservation("source-b-error", new { raw = limit.Text, errorHelp = error, exactInput = AutomationProperties.GetAutomationId(limit),
            focused = limit.IsFocused, persistedUnchanged = HashFiles(profileRoot) == beforeFiles, workerCalls = workerCalls() });
        await scenario.Capture("source-b-error");
        scenario.Result("pass", "retain-provisionally", "Presentation B only; duplicate-name source identity/raw/error and compact activation observed. Full semantic comparison remains separate.");
    }

    private sealed partial class Scenario
    {
        public bool PresentationVisible(Control target) => InViewport(target);

        public async Task PresentationObservation(string scene, object state)
        {
            Require(_presentation != null, "Missing presentation fixture binding.");
            await Program.WaitUntilAsync(() => Texts.Language == _presentation!.Language
                && Window.ActualThemeVariant.Key?.ToString() == _presentation.Theme
                && PresentationChecks.MatchesRequestedSize(Window, _presentation.Width, _presentation.Height));
            Require(Config.LoadConfig().WorkspaceMode == _presentation!.Mode, "Actual workspace mode differs from the presentation cell.");
            Detail("presentation-observation", new { scene, requested = _presentation, actualMode = Config.LoadConfig().WorkspaceMode,
                actualLanguage = Texts.Language, actualTheme = Window.ActualThemeVariant.Key?.ToString(), actualScale = Window.RenderScaling,
                client = Window.ClientSize.ToString(), frame = Window.FrameSize?.ToString(), minimum = new { Window.MinWidth, Window.MinHeight },
                navigationBounds = Navigation.Bounds.ToString(), maxNavigationWidth = double.IsFinite(Navigation.MaxWidth) ? Navigation.MaxWidth : (double?)null,
                renderedLabels = _page.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrWhiteSpace(t.Text) && InViewport(t))
                    .Select(t => new { t.Name, t.Text, bounds = t.Bounds.ToString(), windowTop = t.TranslatePoint(default, Window)?.Y }).ToArray(),
                focus = Focus(), offset = Scroll.Offset.Y, viewport = Scroll.Viewport.ToString(), state,
                setup = "Mode via existing disposable-profile helper; global language/theme and 540-DIP compact constraint are explicit fixtures; no native Tab/picker/IME/AT claim" });
        }

        public async Task CompactPresentationJump(string id, bool captureFocus)
        {
            // The normal minimum-width scene is recorded first; this separate subscene exercises
            // the real compact projection at an explicitly constrained actual navigation width.
            Navigation.MaxWidth = 540; await Settle();
            await OpenContents(); await Settle();
            Require(Navigation.Bounds.Width < 560, "Actual navigation did not enter compact projection.");
            ComboBox choice = Navigation.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible);
            int targetIndex = Controller.Sections.ToList().FindIndex(s => s.Id == id);
            Require(targetIndex >= 0, "Missing exact compact presentation destination " + id);
            Expect("Select exact compact destination " + id + "; focus actual Go, activate Enter, then focus the actual heading; preserve raw draft.");
            int remaining = Controller.Sections.Count + 2;
            while ((choice.SelectedItem as SectionDescriptor)?.Id != id && remaining-- > 0)
                await PressKey(choice, choice.SelectedIndex > targetIndex ? Key.Up : Key.Down);
            Require((choice.SelectedItem as SectionDescriptor)?.Id == id, "Actual compact keyboard selection did not reach exact ID.");
            Button go = Navigation.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.IsEnabled
                && b.Tag == null && b.Content as string == Texts.T("sections.go"));
            go.Focus(); await Settle(); // explicit input targeting, not a claimed Tab route
            Require(go.IsFocused, "Actual compact Go did not receive focus.");
            await PresentationObservation("compact-go-focused", new { id, selected = (choice.SelectedItem as SectionDescriptor)?.Id,
                actualGoLabel = go.Content?.ToString(), focused = go.IsFocused, focusMethod = "harness Focus() setup; not native Tab navigation" });
            if (captureFocus) await Capture("compact-go-focus");
            await PressKey(go, Key.Enter);
            Control target = Controller.Sections.Single(s => s.Id == id).ResolveTarget() ?? throw new InvalidOperationException("Compact target disappeared.");
            Require(!Navigation.IsExpanded && InViewport(target) && (target.IsFocused || target.IsKeyboardFocusWithin),
                "Compact Go did not jump/focus the actual destination heading.");
            Detail("destination", new { id, compact = true, targetFocused = target.IsFocused || target.IsKeyboardFocusWithin,
                targetTop = target.TranslatePoint(default, Scroll)?.Y, focus = Focus(), offset = Scroll.Offset.Y, state = _state() });
        }
    }
}
