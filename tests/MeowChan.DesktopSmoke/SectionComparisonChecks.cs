using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopSmoke;

/// <summary>Test-only projections of the actual integrated pages; no replacement form/store.</summary>
internal static partial class SectionComparisonChecks
{
    private const string Candidate = "section-comparison-candidate";
    private static readonly string LongHelp = string.Join(" ", Enumerable.Repeat("Long help / lange Hilfe with Unicode ü and a complete accessible explanation.", 5));
    private static string FixtureHash(string task, PresentationCell? presentation = null) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
        version = 1, task, settings = new { timer = 60, workspace = presentation?.Mode ?? "Advanced", language = presentation?.Language ?? "en", theme = presentation?.Theme ?? "Dark", path = "shared disposable comparison path" },
        plugin = task.StartsWith("plugin", StringComparison.Ordinal) ? Description(task == "plugin-short") : null
    }))).ToLowerInvariant();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly List<string> ContractFailures = new();

    internal static async Task RunAsync(MainWindow owner)
    {
        ContractFailures.Clear();
        string root = Path.Combine(Config.GetConfigDirectory(), "section-comparison");
        Directory.CreateDirectory(root);
        string initialLanguage = Texts.Language;
        ThemeVariant? initialTheme = Application.Current!.RequestedThemeVariant;
        try
        {
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SECTION_PRESENTATION_ONLY") == "1")
            {
                await PresentationMatrix(owner, root);
                return;
            }
            Texts.SetLanguage("en"); Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SECTION_COMPARISON_INTERACTIVE") == "1")
            {
                await Settings(owner, root, "B", "settings-manual");
                await Plugins(owner, root, "B", shortPage: false);
                return;
            }
            // Reverse ordering for task 2 rather than training the same order for every task.
            foreach (string task in new[] { "settings-raw-roundtrip", "settings-budgets-error" })
            foreach (string variant in task.EndsWith("roundtrip") ? new[] { "A", "B", "C" } : new[] { "C", "B", "A" })
                await Settings(owner, root, variant, task);
            foreach (string variant in new[] { "B", "C", "A" }) await Plugins(owner, root, variant, shortPage: false);
            await Plugins(owner, root, "A", shortPage: true);
            Require(ContractFailures.Count == 0, "Section comparison collected all ten task results; contract failures: " + string.Join(", ", ContractFailures));
            Console.WriteLine("SECTION_COMPARISON_COMPLETED: read every JSON result; native AT/IME and human preference remain open.");
        }
        finally { Texts.SetLanguage(initialLanguage); Application.Current.RequestedThemeVariant = initialTheme; }
    }

    private static async Task Settings(Window owner, string root, string variant, string task, PresentationCell? presentation = null)
    {
        Config config = Config.LoadConfig(Path.Combine(root, task + "-" + variant));
        config.WorkspaceMode = presentation?.Mode ?? "Advanced"; config.Language = presentation?.Language ?? "en"; config.Theme = presentation?.Theme ?? "Dark";
        config.DownloadPath = Path.Combine(root, "fixture-download");
        int commits = 0;
        var editor = new SettingsEditor(config, _ => { commits++; throw new IOException("Comparison commit is deliberately unavailable"); });
        using var scenario = new Scenario(owner, editor, variant, task,
            editor.FindControl<SectionNavigationControl>("SettingsSections")!, editor.FindControl<ScrollViewer>("SettingsScrollViewer")!,
            () => Private<SectionNavigationController>(editor, "_sectionNavigation"),
            () => new { timer = editor.FindControl<Avalonia.Controls.NumericUpDown>("RecheckTimerInput")!.Text,
                timerValue = editor.RecheckTimer, path = editor.DownloadPath, configTimer = config.RecheckTimer,
                configPath = config.DownloadPath, commits, editor.HasError }, presentation);
        scenario.Window.Show(owner); await Settle();
        try
        {
            await scenario.PrepareAsync();
            if (presentation != null) { await SettingsPresentation(scenario, editor, config, () => commits); return; }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SECTION_COMPARISON_INTERACTIVE") == "1") { await scenario.WaitForManualClose(); return; }
            scenario.Expect("Same actual integrated SettingsEditor, all 8 section IDs, top offset, clean draft; no commits.");
            Require(scenario.Controller.Sections.Count == 8 && scenario.Controller.Sections.Any(s => s.Id == "settings.notifications") && scenario.Controller.Sections.Any(s => s.Id == "settings.shortcuts"),
                "Comparison requires integrated Notifications and Shortcuts.");
            if (task.EndsWith("roundtrip"))
            {
                await scenario.Navigate("settings.downloads");
                var timer = editor.FindControl<Avalonia.Controls.NumericUpDown>("RecheckTimerInput")!;
                timer.ApplyTemplate();
                TextBox text = timer.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
                await scenario.Edit(text, "invalid timer comparison");
                await scenario.Edit(editor.FindControl<TextBox>("DownloadPathInput")!, "relative unfinished ü folder");
                foreach (string id in new[] { "settings.shortcuts", "settings.notifications", "settings.downloads" })
                {
                    await scenario.Navigate(id);
                    Require(timer.Text == "invalid timer comparison" && editor.DownloadPath == "relative unfinished ü folder" && commits == 0,
                        "Navigation lost raw text or committed settings.");
                }
                await scenario.Edit(text, "120");
                await scenario.Edit(editor.FindControl<TextBox>("DownloadPathInput")!, config.DownloadPath);
                Require(config.RecheckTimer == 60 && commits == 0, "Correction saved settings implicitly.");
            }
            else
            {
                await scenario.Navigate("settings.worker-budgets");
                Expander budgets = editor.FindControl<Expander>("SettingsWorkerBudgetsHeading")!;
                if (!budgets.IsExpanded) await scenario.Toggle(budgets);
                await scenario.Navigate("settings.updates");
                await scenario.Navigate("settings.worker-budgets");
                var workers = editor.FindControl<Avalonia.Controls.NumericUpDown>("ProductionWorkersInput")!;
                workers.ApplyTemplate();
                TextBox raw = workers.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
                await scenario.Edit(raw, "invalid workers comparison");
                await scenario.Toggle(budgets);
                await scenario.Click(editor.FindControl<Button>("SaveSettingsButton")!);
                await Settle();
                Require(raw.IsFocused && raw.Text == "invalid workers comparison" && budgets.IsExpanded && commits == 0,
                    "Save error did not focus the actual retained editor.");
                await scenario.Navigate("settings.updates");
                Require(raw.Text == "invalid workers comparison", "Error navigation replaced raw draft.");
            }
            if (variant == "B" && task.EndsWith("roundtrip")) await ResizeGate(scenario);
            await scenario.Capture("result");
            scenario.Result("pass", "retain-provisionally", "B remains default unless C has a measured advantage on both long tasks; no human preference inferred.");
        }
        catch (Exception error) { scenario.Detail("exception", new { detail = error.ToString() }); await scenario.Capture("failure"); scenario.Result("fail", "undecided", error.Message); if (presentation != null) throw; }
        finally { editor.EndLifetime(); }
    }

    private static async Task Plugins(Window owner, string root, string variant, bool shortPage, PresentationCell? presentation = null)
    {
        string task = presentation != null ? "plugin-presentation-" + presentation.Id : shortPage ? "plugin-short" : "plugin-scope-error";
        string profileRoot = Path.Combine(root, task + "-" + variant);
        Directory.CreateDirectory(profileRoot);
        var store = new PluginProfileStore(profileRoot);
        PluginDescription description = Description(shortPage);
        string hash = new('a', 64);
        var artifact = new PluginArtifact(hash, new string('b', 64), Path.Combine(profileRoot, "never-executed.dll"), hash, hash, hash, Array.Empty<PluginArtifactResource>());
        await store.ObserveCandidateAsync(new PluginCandidateObservation(Candidate, description.PluginId, artifact, description,
            CandidatePath: Path.Combine(profileRoot, "fixture.cs")), 0);
        await using var compiler = new PluginCompilationService(new(Path.Combine(profileRoot, "never-launched"), profileRoot, Path.Combine(profileRoot, "cache")));
        int workerCalls = 0;
        var services = new PluginManagementServices(store, compiler, new PluginInstallationInventory(), profileRoot,
            (_, _) => throw new InvalidOperationException("Comparison must not describe a worker"),
            (_, _, _) => { workerCalls++; throw new InvalidOperationException("Comparison must reject invalid input before worker execution"); },
            (_, _, _, _, _) => throw new InvalidOperationException("Comparison must not query a site"));
        var page = new PluginsPage(); page.Attach(services);
        using var scenario = new Scenario(owner, page, variant, task,
            null, page.FindControl<ScrollViewer>("PluginDetailsPane")!,
            null,
            () => new { candidate = page.Selected?.Key, sources = page.CurrentDraft?.Sources.ToDictionary(p => p.Key, p => p.Value.EditorFingerprint),
                plugin = page.CurrentDraft?.Plugin.EditorFingerprint, workerCalls, profile = HashFiles(profileRoot),
                sourceNames = page.Selected?.Description?.Sources.Select(s => new { s.SourceId, s.Name }).ToArray() }, presentation, plugin: page);
        scenario.Window.Show(owner); await page.ActivateAsync(); page.Select(Candidate); await Settle();
        try
        {
            await scenario.PrepareAsync();
            string beforeFiles = HashFiles(profileRoot);
            if (presentation != null) { await PluginsPresentation(scenario, page, profileRoot, beforeFiles, () => workerCalls); return; }
            if (Environment.GetEnvironmentVariable("MEOWCHAN_SECTION_COMPARISON_INTERACTIVE") == "1") { await scenario.WaitForManualClose(); return; }
            if (!shortPage)
            {
                var otherFingerprints = page.CurrentDraft!.Sources.Where(pair => pair.Key != "source-b")
                    .ToDictionary(pair => pair.Key, pair => pair.Value.EditorFingerprint, StringComparer.Ordinal);
                string pluginFingerprint = page.CurrentDraft.Plugin.EditorFingerprint;
                scenario.Expect("Three sources source-a/source-b/source-c; a/b share a display name. Change only source-b/limit, then read information/grants; invalid Save must focus source-b and preserve every draft/file.");
                string sourceB = scenario.Targets.Single(s => s.Id.EndsWith("settings/source/source-b", StringComparison.Ordinal)).Id;
                await scenario.Navigate(sourceB);
                var group = (Expander)scenario.Targets.Single(s => s.Id == sourceB).ResolveTarget()!;
                if (!group.IsExpanded) await scenario.Toggle(group);
                var form = (PluginSettingsEditor)group.Content!;
                TextBox limit = (TextBox)form.FindField("limit")!;
                PluginSettingsDraft sourceDraft = page.CurrentDraft!.Sources["source-b"];
                string fieldId = PluginSettingsEditor.FieldIdentity(SettingScope.Source, "source-b", "limit");
                Require(ReferenceEquals(form.Draft, sourceDraft) && AutomationProperties.GetAutomationId(limit) == fieldId,
                    "Plugin task did not resolve the actual source-b limit editor.");
                await scenario.Edit(limit, "invalid source-b draft");
                string fingerprint = page.CurrentDraft!.Sources["source-b"].EditorFingerprint;
                foreach (string suffix in new[] { "/information", "/grants" })
                    await scenario.Navigate(scenario.Targets.Single(s => s.Id.EndsWith(suffix, StringComparison.Ordinal)).Id);
                await scenario.Navigate(sourceB);
                Require(page.CurrentDraft.Sources["source-b"].EditorFingerprint == fingerprint && workerCalls == 0,
                    "Scope navigation changed a draft or called a worker.");
                await scenario.ScrollToControl(page.FindControl<Button>("PluginSave")!);
                await scenario.Click(page.FindControl<Button>("PluginSave")!); await Settle();
                Require(limit.IsFocused && limit.Text == "invalid source-b draft", "Plugin Save error did not focus exact source-b editor.");
                string validationError = Texts.T("plugins.integerError");
                string errorHelp = AutomationProperties.GetHelpText(limit) ?? string.Empty;
                Require(errorHelp.StartsWith(LongHelp + Environment.NewLine, StringComparison.Ordinal)
                    && errorHelp.Contains(validationError, StringComparison.Ordinal),
                    "Plugin Save did not expose fixture help and validation error on the exact source-b field.");
                string visibleErrorBeforeLanguage = errorHelp[(LongHelp.Length + Environment.NewLine.Length)..];
                await scenario.Edit(limit, "another unfinished source-b draft");
                fingerprint = page.CurrentDraft.Sources["source-b"].EditorFingerprint;
                // Passive refresh is a fixture event, not counted as user navigation.
                scenario.Expect("Refresh preserves the exact source-b raw value, error, scope and focus.");
                await page.RefreshAsync(); await Settle(); scenario.Observe("status-refresh (fixture event)");
                Require(page.CurrentDraft.Sources["source-b"].EditorFingerprint == fingerprint && limit.IsFocused
                    && AutomationProperties.GetHelpText(limit) == errorHelp, "Passive refresh lost draft/error/focus.");
                TextBox limitBeforeLanguage = limit;
                string refreshLabelBeforeLanguage = AutomationProperties.GetName(page.FindControl<Button>("PluginsRefresh")!)
                    ?? throw new InvalidOperationException("Plugin refresh has no accessible name before language change.");
                Texts.SetLanguage("de"); await Settle(); scenario.Observe("language-change (fixture event)");
                scenario.Reproject();
                // Localize rebuilds views while retaining scoped drafts and cached validation messages.
                // Resolve the current target by stable scope/field identity; the detached old view cannot own focus.
                group = (Expander)scenario.Targets.Single(s => s.Id == sourceB).ResolveTarget()!;
                form = (PluginSettingsEditor)group.Content!;
                limit = (TextBox)form.FindField("limit")!;
                bool sameCandidate = page.Selected?.Key == Candidate;
                bool sameDraft = ReferenceEquals(page.CurrentDraft.Sources["source-b"], sourceDraft) && ReferenceEquals(form.Draft, sourceDraft);
                bool sameField = AutomationProperties.GetAutomationId(limit) == fieldId;
                bool currentAttached = ReferenceEquals(TopLevel.GetTopLevel(limit), scenario.Window);
                bool retainedRaw = sourceDraft.EditorFingerprint == fingerprint && limit.Text == "another unfinished source-b draft";
                bool currentFocused = limit.IsFocused && ReferenceEquals(scenario.Window.FocusManager?.GetFocusedElement(), limit);
                string currentHelp = AutomationProperties.GetHelpText(limit) ?? string.Empty;
                bool retainedHelp = currentHelp == errorHelp;
                bool visibleError = limit.Parent is Panel fieldContent && fieldContent.Children.OfType<TextBlock>()
                    .Any(text => text.Classes.Contains("workspace-error") && text.IsEffectivelyVisible && text.Text == visibleErrorBeforeLanguage);
                string refreshLabel = AutomationProperties.GetName(page.FindControl<Button>("PluginsRefresh")!)
                    ?? throw new InvalidOperationException("Plugin refresh has no accessible name after language change.");
                bool localizedChrome = Texts.Language == "de" && refreshLabel == Texts.T("plugins.refresh")
                    && refreshLabel != refreshLabelBeforeLanguage
                    && Equals(page.FindControl<Expander>("PluginInfoExpander")!.Header, Texts.T("plugins.information"));
                scenario.Detail("language-change-guards", new { sourceB, fieldId, sameCandidate, sameDraft, sameField, currentAttached,
                    retainedRaw, currentFocused, retainedHelp, visibleError, localizedChrome, group.IsExpanded,
                    currentVisible = limit.IsEffectivelyVisible, viewInstanceChanged = !ReferenceEquals(limitBeforeLanguage, limit),
                    oldViewAttached = TopLevel.GetTopLevel(limitBeforeLanguage) != null, helpBefore = errorHelp, helpAfter = currentHelp,
                    validationError, visibleErrorBeforeLanguage, refreshLabelBeforeLanguage, refreshLabel,
                    note = "Fixture metadata help and cached validation message are retained verbatim; application chrome is localized." });
                Require(sameCandidate && sameDraft && sameField && currentAttached, "Live language change lost candidate/scoped draft/current field identity.");
                Require(retainedRaw, "Live language change lost source-b raw text or fingerprint.");
                Require(currentFocused && group.IsExpanded && limit.IsEffectivelyVisible, "Live language change did not focus the current visible source-b field.");
                Require(retainedHelp && visibleError, "Live language change lost exact field help or visible cached validation error.");
                Require(localizedChrome, "Live language change did not update actual plugin chrome to German.");
                Require(page.CurrentDraft.Plugin.EditorFingerprint == pluginFingerprint
                    && otherFingerprints.All(pair => page.CurrentDraft.Sources[pair.Key].EditorFingerprint == pair.Value),
                    "Source-b task changed the plugin or a similarly named source draft.");
                // Actual narrow Back button returns to same selected candidate; no page switch method counted.
                scenario.Window.Width = 760; scenario.Window.Height = 500; await Settle();
                // The page reacts to the native size change; the Back button exists once it has switched presentation.
                await Program.WaitUntilAsync(() => Math.Abs(scenario.Window.ClientSize.Width - 760) <= 1 && page.FindControl<Button>("PluginBack")!.IsEffectivelyVisible);
                await scenario.ScrollToControl(page.FindControl<Button>("PluginBack")!);
                await scenario.Click(page.FindControl<Button>("PluginBack")!);
                Require(page.FindControl<Grid>("PluginListPane")!.IsEffectivelyVisible && page.Selected?.Key == Candidate,
                    "Narrow Back changed candidate or failed to show list.");
                string beforeTransitionFiles = HashFiles(profileRoot);
                scenario.Detail("navigation-persistence-gate", new { beforeFiles, beforeTransitionFiles, unchanged = beforeFiles == beforeTransitionFiles });
                Require(beforeFiles == beforeTransitionFiles, "Navigation or invalid Save changed persisted plugin metadata.");
                // Remove a Source through a controlled catalog fixture transition while the old item exists.
                scenario.Window.Width = 1100; scenario.Window.Height = 760;
                await Program.WaitUntilAsync(() => Math.Abs(scenario.Window.ClientSize.Width - 1100) <= 1 && !page.FindControl<Button>("PluginBack")!.IsVisible);
                page.Select(Candidate); await Settle();
                string? navigatedSource = null;
                page.OpenSourceWatches = source => navigatedSource = source;
                await scenario.OpenContents();
                ContextMenu oldMenu = page.PluginActionMenu!;
                MenuItem oldSource = oldMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "plugins.watches"))
                    .Items.OfType<MenuItem>().Single(item => item.Header as string == "Same source name · source-b");
                Require(oldSource.Command?.CanExecute(oldSource.CommandParameter) == true,
                    "Source-b removal fixture did not start with a current source action.");
                var removed = description with { Sources = description.Sources.Where(source => source.SourceId != "source-b").ToArray() };
                PluginProfileSnapshot previous = await store.ReadAsync();
                await store.ObserveCandidateAsync(new PluginCandidateObservation(Candidate, description.PluginId, artifact, removed,
                    CandidatePath: Path.Combine(profileRoot, "fixture.cs")), previous.Revision);
                await page.RefreshAsync(); await Settle(); scenario.Reproject();
                scenario.Observe("source-removed (fixture transition; old source action must not resolve to source-a)");
                bool staleResolverInvalidated = oldSource.Command?.CanExecute(oldSource.CommandParameter) == false;
                string beforeStaleAction = HashFiles(profileRoot);
                // Already queued command from the old revision: still must not act, even after its menu closes.
                oldSource.Command!.Execute(oldSource.CommandParameter); await Settle();
                bool removedCannotNavigate = navigatedSource == null && HashFiles(profileRoot) == beforeStaleAction;
                await scenario.OpenContents();
                MenuItem watches = page.PluginActionMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "plugins.watches"));
                bool stillOffered = watches.Items.OfType<MenuItem>().Any(item => (item.Header as string)?.EndsWith(" · source-b", StringComparison.Ordinal) == true);
                bool retainedRemovedDraft = ReferenceEquals(page.CurrentDraft?.Sources.GetValueOrDefault("source-b"), sourceDraft)
                    && sourceDraft.EditorFingerprint == fingerprint;
                scenario.Detail("source-removal-observation", new { stillOffered, staleResolverInvalidated, removedCannotNavigate,
                    retainedRemovedDraft, oldMenuClosed = !oldMenu.IsOpen,
                    explanation = "Dirty settings remain a draft; current watch menu and captured revisioned source commands must invalidate removed targets, never retarget the duplicate name." });
                Require(!stillOffered && staleResolverInvalidated && removedCannotNavigate && !oldMenu.IsOpen,
                    "Removed source remains offered, its captured command is current, or an obsolete command navigated/wrote metadata.");
                Require(retainedRemovedDraft, "Source target invalidation replaced or changed the retained dirty source-b draft.");
                page.PluginActionMenu.Close();
            }
            else
            {
                scenario.Expect("Short no-parameter page, A headings only; measure actual scroller extent and available viewport without extra navigation.");
                scenario.Detail("short-page", new { settingsScopes = page.CurrentDraft?.Sources.Count, extent = scenario.Scroll.Extent.Height,
                    viewport = scenario.Scroll.Viewport.Height, navigationOffered = page.FindControl<SectionNavigationControl>("PluginSections") != null });
            }
            // Catalog-removal fixture deliberately writes profile metadata; normal navigation before that point is captured independently.
            scenario.Detail("persistence", new { beforeFiles, afterFiles = HashFiles(profileRoot), workerCalls,
                note = "Source-removal transition explicitly writes candidate metadata, never a settings commit." });
            Require(workerCalls == 0, "Navigation/error task unexpectedly executed a worker.");
            await scenario.Capture("result"); scenario.Result("pass", "retain-provisionally", "Technical task only; source-retention row and all focus/raw observations must be reviewed independently.");
        }
        catch (Exception error) { scenario.Detail("exception", new { detail = error.ToString() }); await scenario.Capture("failure"); scenario.Result("fail", "undecided", error.Message); if (presentation != null) throw; }
        finally { page.EndLifetime(); Texts.SetLanguage("en"); }
    }

    private static async Task ResizeGate(Scenario scenario)
    {
        // Controlled geometry/focus setup is a regression fixture, never a user action count.
        scenario.Navigation.Width = 640; scenario.Navigation.IsExpanded = true; await Settle();
        Button link = scenario.Navigation.GetVisualDescendants().OfType<Button>().First(b => b.Tag is string && b.IsEffectivelyVisible);
        string id = (string)link.Tag!; link.Focus();
        scenario.Navigation.Width = 540; await Settle();
        ComboBox combo = scenario.Navigation.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible);
        bool compact = combo.IsFocused && scenario.Navigation.SelectedSectionId == id;
        scenario.Navigation.Width = 640; await Settle();
        bool wide = scenario.Navigation.GetVisualDescendants().OfType<Button>().Any(b => Equals(b.Tag, id) && b.IsFocused && b.IsEffectivelyVisible);
        Control? outside = scenario.Window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => !t.GetVisualAncestors().Contains(scenario.Navigation));
        outside?.Focus();
        scenario.Navigation.Width = 540; await Settle();
        bool external = outside == null || outside.IsFocused;
        scenario.Detail("breakpoint-focus-regression", new { id, compact, wide, external, fixture = "actual Navigation.Width 640→540→640→540; focus setup not counted as a UI navigation step" });
        scenario.Navigation.Width = double.NaN; scenario.Navigation.IsExpanded = false;
        Require(compact && wide && external, "Breakpoint transition hid focused section control or stole external focus.");
    }

    private static PluginDescription Description(bool shortPage = false)
    {
        SettingsSchema Fields(SettingScope scope, int count) => new("1", Enumerable.Range(0, count).Select(i => i == 0
            ? new SettingDefinition("limit", scope, SettingKind.Integer, "Limit / Grenze", LongHelp, true, new("limit", SettingKind.Integer, Integer: 10), Minimum: 10, Maximum: 100)
            : new SettingDefinition("field-" + i, scope, SettingKind.Text, "Long parameter / langer Parameter " + i, LongHelp,
                Default: new("field-" + i, SettingKind.Text, Text: "initial " + i))).ToArray());
        SourceDescription Source(string id, string name) => new(id, name, new Uri("https://" + id + ".example.test/"), SourceCapabilities.ThreadRead,
            new[] { new UrlRoute(SourceResourceKind.Thread, id + ".example.test", "/{board}/thread/{thread}", IsCanonical: true) },
            new[] { new HttpHostDeclaration(id + ".example.test", HttpHostPurpose.Metadata) }, Fields(SettingScope.Source, shortPage ? 0 : 9));
        return new("fixture.sections", "Integrated plugin sections fixture", "1.0.0", SdkContract.Version,
            shortPage ? "Short page without configurable parameters" : "Actual PluginsPage with three scopes and duplicate source names",
            shortPage ? new[] { Source("short-source", "Short source without parameters") } : new[] { Source("source-a", "Same source name"), Source("source-b", "Same source name"), Source("source-c", "Third source") },
            shortPage ? Array.Empty<FilterDescription>() : new[] { new FilterDescription("fixture-filter", "Fixture grants", LongHelp,
                FilterDataAccess.CandidateMetadata, Fields(SettingScope.FilterRule, 0), new[] { "image/png" }) }, Fields(SettingScope.Plugin, shortPage ? 0 : 8),
            new[] { new InformationEntry("fixture", "Fixture information", InformationKind.Text, shortPage ? "Short" : LongHelp) });
    }
    private static T Private<T>(object owner, string name) where T : class => (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!);
    private static string HashFiles(string root) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
        Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))))).ToLowerInvariant();
    private static async Task Settle() { await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded); await Task.Delay(30); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed partial class Scenario : IDisposable
    {
        public Window Window { get; }
        private readonly SectionNavigationControl? _navigation;
        public SectionNavigationControl Navigation => _navigation ?? throw new InvalidOperationException("Plugin details have no section navigator.");
        private readonly PluginsPage? _plugin;
        public IReadOnlyList<SectionDescriptor> Targets => _plugin == null ? Controller.Sections : PluginTargets();
        public ScrollViewer Scroll { get; }
        public SectionNavigationController Controller => _controller?.Invoke() ?? throw new InvalidOperationException("Plugin details have no section controller.");
        private readonly Control _page;
        private readonly Func<SectionNavigationController>? _controller;
        private readonly Func<object> _state;
        private readonly object? _originalContent;
        private readonly string _task;
        private readonly PresentationCell? _presentation;
        private readonly List<object> _actions = new();
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TextBox _prediction = new() { PlaceholderText = "Prediction before next action / Vorhersage", MinWidth = 240 };
        private string _variant;
        private int _screenshot;
        private bool _changingContent;
        private TestAnchor? _anchor;
        public Scenario(Window owner, Control page, string variant, string task, SectionNavigationControl? navigation,
            ScrollViewer scroll, Func<SectionNavigationController>? controller, Func<object> state, PresentationCell? presentation = null, PluginsPage? plugin = null)
        {
            _page = page; _variant = variant; _task = task; _navigation = navigation; _plugin = plugin; Scroll = scroll;
            _controller = controller; _state = state; _originalContent = navigation?.Content; _presentation = presentation;
            Window = new Window { Title = "URSA17 " + task + " " + variant, Width = presentation?.Width ?? 1100, Height = presentation?.Height ?? 760,
                MinWidth = presentation?.Width ?? 640, MinHeight = presentation?.Height ?? 480 };
            var toolbar = new WrapPanel();
            foreach (string choice in new[] { "A", "B", "C" })
            {
                string captured = choice;
                var button = new Button { Content = choice, Margin = new Thickness(4) };
                button.Click += (_, _) => { _variant = captured; Reproject(); Detail("manual-projection", new { variant = captured, formInstancePreserved = true, state = _state() }); };
                toolbar.Children.Add(button);
            }
            toolbar.Children.Add(_prediction);
            var observe = new Button { Content = "Record observation", Margin = new Thickness(4) };
            observe.Click += async (_, _) => { Expect(_prediction.Text ?? "(no human prediction entered)"); Observe("manual observer checkpoint"); await Capture("manual"); };
            toolbar.Children.Add(observe);
            var instructions = new TextBlock { Text = task + (plugin == null
                ? ": A headings; B product contents; C Anchor."
                : ": A actual headings; B shipped action-menu commands; C keyboard action menu. No plugin section navigator.")
                + " Same form/draft/errors; predictions before actions. Native AT remains separate.", TextWrapping = TextWrapping.Wrap };
            if (presentation == null)
            {
                var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
                root.Children.Add(toolbar); Grid.SetRow(instructions, 1); root.Children.Add(instructions); Grid.SetRow(page, 2); root.Children.Add(page); Window.Content = root;
            }
            else Window.Content = page;
            if (navigation != null) navigation.SizeChanged += (_, _) => ApplyWidth();
            Window.Closed += (_, _) => _closed.TrySetResult();
        }
        public async Task PrepareAsync()
        {
            Reproject();
            if (_plugin != null)
            {
                Require(_plugin.FindControl<SectionNavigationControl>("PluginSections") == null,
                    "The removed plugin section navigator was recreated by the fixture.");
                await Settle(); Scroll.Offset = default; await Settle();
                Observe("fixture ready: actual plugin menu/expanders; no replacement section navigator");
                return;
            }
            Navigation.IsExpanded = false;
            await Settle();
            Scroll.Offset = default; // identical start-state fixture setup, never counted as user scroll
            await Settle();
            Observe("fixture ready: top offset / identical draft");
            if (_variant != "A")
                Require(Navigation.StyleKey == typeof(Expander) && Navigation.GetVisualDescendants().OfType<ToggleButton>().Any(),
                    "Actual product section navigation has no materialized Expander header/template.");
        }
        public Task WaitForManualClose() => _closed.Task;
        public void Reproject()
        {
            // Plugin variants retain all original tasks but use the shipped headings/menu routes, never an invented navigator.
            if (_plugin != null) return;
            if (_changingContent) return;
            _changingContent = true;
            try
            {
                // Variant A withholds the contents without writing the product's own IsVisible: the page
                // re-evaluates that flag on every layout pass, and a competing writer re-queues it forever.
                Navigation.IsVisible = true;
                WithholdNavigation(_variant == "A");
                if (_variant != "C") { Navigation.Content = _originalContent; _anchor = null; return; }
                var anchor = new TestAnchor { TargetContainer = Scroll, Navigate = Navigation.Navigate };
                foreach (SectionDescriptor section in Controller.Sections)
                {
                    if (section.ResolveTarget() is { } target) Ursa.Controls.Anchor.SetId(target, section.Id);
                    anchor.Items.Add(new Ursa.Controls.AnchorItem { Header = section.Title, AnchorId = section.Id, Focusable = true });
                }
                _anchor = anchor;
                ApplyWidth();
                anchor.Loaded += (_, _) => anchor.InvalidatePositions();
            }
            finally { _changingContent = false; }
        }
        private void WithholdNavigation(bool withheld)
        {
            if (withheld)
            {
                Navigation.Height = 0; Navigation.Opacity = 0; Navigation.IsEnabled = false; Navigation.IsHitTestVisible = false;
                Navigation.ClipToBounds = true;
                return;
            }
            Navigation.ClearValue(Layoutable.HeightProperty); Navigation.ClearValue(Visual.OpacityProperty);
            Navigation.ClearValue(InputElement.IsEnabledProperty); Navigation.ClearValue(InputElement.IsHitTestVisibleProperty);
            Navigation.ClearValue(Visual.ClipToBoundsProperty);
        }
        private void ApplyWidth()
        {
            if (_variant != "C" || _anchor == null) return;
            object? content = Navigation.Bounds.Width < 560 ? _originalContent : _anchor;
            if (!ReferenceEquals(Navigation.Content, content)) Navigation.Content = content;
            _anchor.InvalidatePositions();
        }
        public void Expect(string intent) => Detail("prediction-before-action", new { intent, state = _state(), offset = Scroll.Offset.Y });
        public async Task OpenContents()
        {
            if (_plugin != null)
            {
                _plugin.PluginActionMenu?.Close();
                await Click(_plugin.FindControl<Button>("PluginMoreActions")!);
                await Program.WaitUntilAsync(() => _plugin.PluginActionMenu?.IsOpen == true);
                return;
            }
            await ScrollToControl(Navigation);
            if (!Navigation.IsExpanded) await Toggle(Navigation);
        }
        public async Task Navigate(string id)
        {
            if (_plugin != null) { await NavigatePlugin(id); return; }
            SectionDescriptor section = Controller.Sections.Single(s => s.Id == id);
            Expect("Reach exact section " + id + "; keep same draft and do not commit.");
            if (_variant == "A")
            {
                Control baseline = section.ResolveTarget() ?? throw new InvalidOperationException("Missing baseline target " + id);
                // Wide Settings show one category page at a time. Without the contents control, the reader
                // reaches a heading on a hidden page by choosing that page, never by the control under comparison.
                bool Reachable() => baseline.IsAttachedToVisualTree() && baseline.IsEffectivelyVisible && baseline.Bounds.Width > 0 && baseline.Bounds.Height > 0;
                if (!Reachable())
                {
                    section.Open?.Invoke();
                    // A page that was hidden has no template or layout until the next layout pass.
                    await Program.WaitUntilAsync(() => { Window.UpdateLayout(); return Reachable(); });
                    await Settle();
                }
                await ScrollToControl(baseline);
            }
            else
            {
                await OpenContents(); await Settle();
                if (_variant == "C" && _anchor?.IsEffectivelyVisible == true)
                {
                    Ursa.Controls.AnchorItem item = _anchor.Items.OfType<Ursa.Controls.AnchorItem>().Single(i => i.AnchorId == id);
                    await PressKey(item, Key.Enter);
                }
                else
                {
                    Button? link = Navigation.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Equals(b.Tag, id) && b.IsEffectivelyVisible);
                    if (link != null) await Click(link);
                    else
                    {
                        ComboBox choice = Navigation.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEffectivelyVisible);
                        await PressKey(choice, Key.Home);
                        int limit = Controller.Sections.Count + 2;
                        while ((choice.SelectedItem as SectionDescriptor)?.Id != id && limit-- > 0) await PressKey(choice, Key.Down);
                        Require((choice.SelectedItem as SectionDescriptor)?.Id == id, "Compact keyboard driver could not select exact section; no property selection substituted.");
                        Button go = Navigation.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Tag == null && b.IsEnabled && b.Content as string == Texts.T("sections.go"));
                        await Click(go);
                    }
                }
                await Settle();
            }
            Control target = section.ResolveTarget() ?? throw new InvalidOperationException("Section removed while navigating " + id);
            Require(InViewport(target), "Section did not enter actual viewport: " + id);
            if (_variant != "A") Require(target.IsFocused || target.IsKeyboardFocusWithin, "Navigation did not focus exact target: " + id);
            Detail("destination", new { id, focus = Focus(), targetFocused = target.IsFocused || target.IsKeyboardFocusWithin,
                offset = Scroll.Offset.Y, targetTop = target.TranslatePoint(default, Scroll)?.Y, viewport = Scroll.Viewport.Height, state = _state() });
            if (_presentation == null) await Capture("destination-" + Uri.EscapeDataString(id));
        }
        private IReadOnlyList<SectionDescriptor> PluginTargets()
        {
            PluginsPage page = _plugin!;
            var targets = new List<SectionDescriptor>();
            // Resolve actual materialized expanders by draft identity, not by a translated/duplicate source name.
            foreach (Expander group in page.FindControl<StackPanel>("PluginSettings")!.Children.OfType<Expander>())
            {
                if (group.Content is not PluginSettingsEditor editor) continue;
                string? scope = ReferenceEquals(editor.Draft, page.CurrentDraft?.Plugin) ? string.Empty
                    : page.CurrentDraft?.Sources.FirstOrDefault(pair => ReferenceEquals(pair.Value, editor.Draft)).Key;
                if (scope == null) continue;
                string id = "settings/" + (scope.Length == 0 ? "plugin" : "source/" + Uri.EscapeDataString(scope));
                string captured = id;
                targets.Add(new(id, group.Header?.ToString() ?? id, () => PluginTargetsCore(captured)));
            }
            foreach ((string suffix, string name) in new[] { ("information", "PluginInfoExpander"), ("grants", "PluginGrantExpander"), ("developer", "PluginDeveloperExpander") })
                if (page.FindControl<Expander>(name) is { IsVisible: true } expander)
                    targets.Add(new("plugin/" + suffix, expander.Header?.ToString() ?? suffix, () => page.FindControl<Expander>(name)));
            return targets;
        }
        private Control? PluginTargetsCore(string id)
        {
            foreach (Expander group in _plugin!.FindControl<StackPanel>("PluginSettings")!.Children.OfType<Expander>())
            {
                if (group.Content is not PluginSettingsEditor editor) continue;
                string? scope = ReferenceEquals(editor.Draft, _plugin.CurrentDraft?.Plugin) ? string.Empty
                    : _plugin.CurrentDraft?.Sources.FirstOrDefault(pair => ReferenceEquals(pair.Value, editor.Draft)).Key;
                if (scope != null && id == "settings/" + (scope.Length == 0 ? "plugin" : "source/" + Uri.EscapeDataString(scope))) return group;
            }
            return null;
        }
        private async Task NavigatePlugin(string id)
        {
            _plugin!.PluginActionMenu?.Close();
            SectionDescriptor target = Targets.Single(section => section.Id == id);
            Expect("Reach actual plugin detail " + id + " via headings or the shipped action menu; retain every draft and file.");
            string? action = id.StartsWith("settings/", StringComparison.Ordinal) ? "plugins.show-settings"
                : id.EndsWith("/grants", StringComparison.Ordinal) ? "plugins.show-grants"
                : id.EndsWith("/developer", StringComparison.Ordinal) ? "plugins.show-developer-tools" : null;
            if (_variant != "A" && action != null)
            {
                await OpenContents();
                MenuItem item = _plugin!.PluginActionMenu!.Items.OfType<MenuItem>().Single(menu => Equals(menu.Tag, action));
                Require(item.IsEffectivelyEnabled && item.Command?.CanExecute(item.CommandParameter) == true,
                    "Actual plugin menu target is disabled: " + action);
                if (_variant == "C") await PressKey(item, Key.Enter);
                else
                {
                    // Command-backed MenuItem; disclosed in-process invocation, not an OS click.
                    // Like normal menu dispatch, retire the popup before entering the background-page routing guard.
                    _plugin!.PluginActionMenu!.Close(); await Settle();
                    item.Command!.Execute(item.CommandParameter); await Settle();
                    Action(new { kind = "MenuCommand", command = action, input = "actual materialized MenuItem.Command" });
                }
                await Program.WaitUntilAsync(() => _plugin.PluginActionMenu?.IsOpen != true);
                if (action == "plugins.show-settings")
                {
                    Expander firstScope = _plugin.FindControl<StackPanel>("PluginSettings")!.Children.OfType<Expander>().First();
                    Require(firstScope.IsExpanded && (firstScope.IsFocused || firstScope.IsKeyboardFocusWithin),
                        "The plugin Settings menu did not expose and focus its actual first scope.");
                }
                if (action == "plugins.show-grants" || action == "plugins.show-developer-tools")
                    Require(target.ResolveTarget() is { } menuTarget && (menuTarget.IsFocused || menuTarget.IsKeyboardFocusWithin),
                        "Plugin menu did not focus its actual detail target.");
            }
            Control control = target.ResolveTarget() ?? throw new InvalidOperationException("Plugin detail disappeared: " + id);
            await ScrollToControl(control);
            if (control is Expander expander)
            {
                if (!expander.IsExpanded) await Toggle(expander);
                // The menu opens the first settings scope. Other scopes and Information are ordinary expanders now;
                // targeting their real header is explicit harness setup, never reported as automatic navigator focus.
                ToggleButton header = expander.GetVisualDescendants().OfType<ToggleButton>().First(toggle =>
                    ReferenceEquals(toggle.GetVisualAncestors().OfType<Expander>().FirstOrDefault(), expander));
                header.Focus(); await Settle();
            }
            Require(InViewport(control) && (control.IsFocused || control.IsKeyboardFocusWithin), "Plugin heading/scope is not visible and focusable: " + id);
            Detail("destination", new { id, route = _variant == "A" ? "headings" : "plugin action menu + actual scope expander",
                focusSetup = "harness header targeting is not native Tab or automatic scope navigation", focus = Focus(), offset = Scroll.Offset.Y, state = _state() });
            if (_presentation == null) await Capture("destination-" + Uri.EscapeDataString(id));
        }
        public async Task ScrollToControl(Control control)
        {
            Require(control.IsEffectivelyVisible, "Control is hidden: " + control.Name);
            for (int i = 0; i < 100 && !InViewport(control); i++)
            {
                Point? point = control.TranslatePoint(default, Scroll);
                if (point == null) break;
                double before = Scroll.Offset.Y;
                double distance = point.Value.Y;
                bool far = Math.Abs(distance) > Scroll.Viewport.Height * 0.85;
                if (far) await PressKey(Scroll, distance < 0 ? Key.PageUp : Key.PageDown);
                else await WheelScroll(distance < 0);
                if (Math.Abs(Scroll.Offset.Y - before) < 0.1)
                    throw new InvalidOperationException("Actual routed page/wheel input did not move this scroller; no fabricated scroll count or direct offset replacement.");
            }
            Require(InViewport(control), "Control unreachable by bounded routed page/wheel scroll: " + control.Name
                + $" [bounds={control.Bounds} inScroller={control.GetVisualAncestors().Contains(Scroll)} atScroller={control.TranslatePoint(default, Scroll)}"
                + $" atWindow={control.TranslatePoint(default, Window)} viewport={Scroll.Viewport} offset={Scroll.Offset} extent={Scroll.Extent} window={Window.Bounds}]");
        }
        private bool InViewport(Control target)
        {
            if (!target.IsEffectivelyVisible) return false;
            Point? point = target.TranslatePoint(default, Scroll);
            // Outside the content scroller (e.g. Settings header/footer) is visible in the real window.
            if (!target.GetVisualAncestors().Contains(Scroll))
            {
                Point? windowPoint = target.TranslatePoint(default, Window);
                return windowPoint is { } p && p.Y >= 0 && p.Y + Math.Min(target.Bounds.Height, 32) <= Window.Bounds.Height;
            }
            return point is { } q && q.Y >= -1 && q.Y + Math.Min(target.Bounds.Height, 32) <= Scroll.Viewport.Height + 1;
        }
        public async Task Toggle(Expander expander)
        {
            await ScrollToControl(expander);
            ToggleButton? header = expander.GetVisualDescendants().OfType<ToggleButton>()
                .FirstOrDefault(toggle => ReferenceEquals(toggle.GetVisualAncestors().OfType<Expander>().FirstOrDefault(), expander));
            Require(header != null, "No actual Expander template header: " + expander.GetType().Name + "; styleKey=" + expander.StyleKey);
            bool before = expander.IsExpanded;
            await PressKey(header!, Key.Space);
            Require(expander.IsExpanded != before, "Actual Expander header did not toggle through keyboard activation.");
        }
        public async Task Click(Button button)
        {
            Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "Attempt to activate hidden/disabled control " + button.Name);
            await ScrollToControl(button);
            string? before = Focus(); double offset = Scroll.Offset.Y;
            button.Focus(); // input targeting setup is disclosed, never counted as a Tab/focus-navigation success
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Action(new { kind = "Click", control = button.Name, tag = button.Tag?.ToString(), input = "in-process routed Button.ClickEvent", focusTargeting = "harness Focus() (not a Tab step)",
                focusBefore = before, focusAfter = Focus(), offsetBefore = offset, offsetAfter = Scroll.Offset.Y });
        }
        public async Task PressKey(Control target, Key key)
        {
            string? before = Focus(); double offset = Scroll.Offset.Y;
            target.Focusable = true; target.Focus();
            var down = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = target, Key = key };
            target.RaiseEvent(down);
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Source = target, Key = key });
            await Settle();
            Action(new { kind = "KeyPress", control = target.Name ?? target.GetType().Name, key = key.ToString(), input = "in-process routed KeyDown+KeyUp", focusTargeting = "harness Focus() (not a Tab step)",
                handled = down.Handled, focusBefore = before, focusAfter = Focus(), offsetBefore = offset, offsetAfter = Scroll.Offset.Y });
        }
        private async Task WheelScroll(bool up)
        {
            Require(Scroll.Presenter is ScrollContentPresenter { IsEffectivelyVisible: true }, "Actual ScrollContentPresenter is unavailable for routed wheel input.");
            var presenter = (ScrollContentPresenter)Scroll.Presenter!;
            Point? position = presenter.TranslatePoint(new Point(presenter.Bounds.Width / 2, presenter.Bounds.Height / 2), Window);
            Require(position != null, "Scroll presenter has no actual window position.");
            string? before = Focus(); double offset = Scroll.Offset.Y;
            using var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, true);
            var wheel = new PointerWheelEventArgs(presenter, pointer, Window, position!.Value, 0,
                PointerPointProperties.None, KeyModifiers.None, new Vector(0, up ? 1 : -1));
            presenter.RaiseEvent(wheel);
            await Settle();
            Action(new { kind = "Wheel", control = presenter.Name ?? presenter.GetType().Name, direction = up ? "up" : "down", delta = wheel.Delta.ToString(),
                input = "in-process routed PointerWheelChanged on actual ScrollContentPresenter (not OS pointer input)",
                handled = wheel.Handled, focusBefore = before, focusAfter = Focus(), offsetBefore = offset, offsetAfter = Scroll.Offset.Y });
        }
        public async Task Edit(TextBox editor, string raw)
        {
            await ScrollToControl(editor);
            string? before = editor.Text;
            editor.Focus(); editor.Text = raw; // explicit single in-process edit; not OS typing/IME
            await Settle();
            Action(new { kind = "Edit", control = editor.Name, rawBefore = before, rawAfter = editor.Text, input = "in-process TextBox.Text edit", focusAfter = Focus() });
        }
        private string? Focus() => (Window.FocusManager?.GetFocusedElement() as Control) is { } focus
            ? (focus.Name ?? focus.GetType().Name) + " [automationId=" + AutomationProperties.GetAutomationId(focus) + "]" : null;
        private void Action(object action) { _actions.Add(action); Detail("actual-action", new { actionIndex = _actions.Count, action, state = _state() }); }
        public void Observe(string note) => Detail("observation", new { note, focus = Focus(), offset = Scroll.Offset.Y, extent = Scroll.Extent.Height,
            viewport = Scroll.Viewport.Height, horizontalExtent = Scroll.Extent.Width, horizontalViewport = Scroll.Viewport.Width,
            navigationWidth = _navigation?.Bounds.Width, contentBounds = _page.Bounds.ToString(),
            headings = Targets.Select(section => new { section.Id, section.Title, bounds = section.ResolveTarget()?.Bounds.ToString(),
                top = section.ResolveTarget()?.TranslatePoint(default, Scroll)?.Y }).ToArray(),
            clipping = "unassessed; inspect captured images and actual bounds", state = _state() });
        public void Detail(string kind, object data) => Console.WriteLine("SECTION_COMPARISON " + JsonSerializer.Serialize(new {
            package = 17, task = _task, variant = _variant, presentation = _presentation, fixture_digest = FixtureHash(_task, _presentation),
            source_digest = Environment.GetEnvironmentVariable("MEOWCHAN_COMPARISON_SOURCE_DIGEST") ?? "not supplied; bind external source manifest",
            observer_kind = "harness", os = Environment.OSVersion.ToString(), renderScaling = Window.RenderScaling,
            language = Texts.Language, theme = Window.ActualThemeVariant.Key?.ToString(), window = Window.Bounds.ToString(), kind, data,
            human_judgment_open = true, native_at_ime_open = true }, JsonOptions));
        public async Task Capture(string suffix) { await Settle(); Program.CaptureWindow(Window, "smoke-section-comparison-" + _task + "-" + _variant + "-" + suffix + "-" + _screenshot++ + ".png"); }
        public void Result(string result, string interpretation, string note)
        {
            Detail("task-result", new { result, interpretation, note,
                actualActionCount = _actions.Count, actual_actions = _actions, finalState = _state(), keyboardTabRoute = "not-run; input-target Focus() is separately disclosed" });
            // Only actual task failures affect process status; an alternative preference is not a contract failure.
            if (result == "fail") ContractFailures.Add(_task + ":" + _variant);
        }
        public void Dispose() { if (Window.IsVisible) Window.Close(); }
    }

    private sealed class TestAnchor : Ursa.Controls.Anchor
    {
        protected override Type StyleKeyOverride => typeof(Ursa.Controls.Anchor);
        public Action<string>? Navigate { get; init; }
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Ursa.Controls.AnchorItem? item = e.Source as Ursa.Controls.AnchorItem ?? (e.Source as Visual)?.FindAncestorOfType<Ursa.Controls.AnchorItem>();
            if (item?.AnchorId is { } id) { e.Handled = true; Navigate?.Invoke(id); }
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            Ursa.Controls.AnchorItem? item = e.Source as Ursa.Controls.AnchorItem ?? (e.Source as Visual)?.FindAncestorOfType<Ursa.Controls.AnchorItem>();
            if ((e.Key is Key.Enter or Key.Space) && item?.AnchorId is { } id) { e.Handled = true; Navigate?.Invoke(id); }
            else base.OnKeyDown(e);
        }
    }
}
