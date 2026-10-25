using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

/// <summary>Task-based checks on the integrated page, without executing fixture code.</summary>
internal static class PluginUxChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        bool popupReplay = Environment.GetEnvironmentVariable("MEOWCHAN_PLUGIN_UX_POPUP_REPLAY") == "1";
        Require(!popupReplay || Environment.GetEnvironmentVariable("MEOWCHAN_PLUGIN_UX_PRESENTATION") == "1",
            "Popup replay requires the passive presentation selector.");
        if (Environment.GetEnvironmentVariable("MEOWCHAN_PLUGIN_UX_REVIEW") == "1"
            && Environment.GetEnvironmentVariable("MEOWCHAN_PLUGIN_UX_PRESENTATION") == "1")
            throw new InvalidOperationException("Select either the single-cell UX review or the presentation pass, not both.");
        string root = Path.Combine(Config.GetConfigDirectory(), "plugin-ux");
        PluginUxFixture fixture = await PluginUxFixture.CreateAsync(root);
        PluginsPage page = new(); page.Attach(fixture.Services);
        ContentControl host = window.FindControl<ContentControl>("PluginsPageHost")!;
        object? retained = host.Content;
        string language = Texts.Language, mode = Config.LoadConfig().WorkspaceMode;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = window.Width, height = window.Height;
        host.Content = page;
        try
        {
            window.GetLogicalDescendants().OfType<MenuItem>().First(item => Equals(item.Tag, "nav.plugins"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await page.ActivateAsync();
            Require(page.Selected == null && C<SelectionList>(page, "PluginCards").SelectedItem == null,
                "Catalog opening selected a candidate without user intent.");
            if (Environment.GetEnvironmentVariable("MEOWCHAN_PLUGIN_UX_PRESENTATION") == "1")
            {
                foreach (string presentationMode in new[] { "Barebones", "Simple", "Advanced" })
                foreach (string presentationLanguage in new[] { "en", "de" })
                foreach (ThemeVariant presentationTheme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    if (popupReplay && (presentationMode != "Barebones" || presentationLanguage != "de")) continue;
                    await PresentationAsync(window, page, fixture, presentationMode, presentationLanguage, presentationTheme,
                        presentationMode == "Barebones" ? 640 : 760, presentationMode == "Barebones" ? 480 : 500);
                }
                Observation(window, page, fixture, "presentation-result", new { cells = popupReplay ? 2 : 12,
                    interpretation = "Presentation only; semantic tasks were not repeated per cell. Inspect actual captures." });
            }
            else
            {
                // The initial review is exactly one reference cell. Neighbour/minimum
                // cells require the separately selected presentation pass after review.
                await PresentationAsync(window, page, fixture, "Advanced", "en", ThemeVariant.Dark, 1280, 800);
                await DraftAndDisclosureAsync(window, page, fixture, root);
                await MenuIntentAsync(window, page, fixture);
                await MissingAndRetainedAsync(window, page, fixture);
                Observation(window, page, fixture, "task-result", new { result = "pass",
                    interpretation = "Technical task checks passed; retain/adjust/change needs actual image review; human discovery/native AT remain separate." });
            }
            Require(fixture.ForbiddenCalls == 0, "Display, menus or disclosure invoked compiler/worker/site services.");
        }
        catch
        {
            Program.CaptureWindow(window, "smoke-pux-failure.png");
            Observation(window, page, fixture, "task-result", new { result = "fail", interpretation = "No UX acceptance inferred.",
                disclosure = DisclosureState(window, page) });
            throw;
        }
        finally
        {
            page.EndLifetime(); host.Content = retained;
            Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
            await PresentationChecks.SwitchModeAsync(window, mode);
            window.Width = width; window.Height = height;
        }
    }

    private static async Task PresentationAsync(MainWindow window, PluginsPage page, PluginUxFixture fixture,
        string mode, string language, ThemeVariant theme, double width, double height)
    {
        await PresentationChecks.SwitchModeAsync(window, mode);
        Texts.SetLanguage(language); Application.Current!.RequestedThemeVariant = theme;
        window.WindowState = WindowState.Normal; window.Width = width; window.Height = height;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height)
            && Texts.Language == language && window.ActualThemeVariant == theme);
        page.Select(PluginUxFixture.SourceKey);
        C<ScrollViewer>(page, "PluginDetailsPane").Offset = default;
        await Settle();
        string cell = mode.ToLowerInvariant() + "-" + language + "-" + theme.Key!.ToString()!.ToLowerInvariant();
        Expect("Identify this plugin's purpose and find its immediate state action without opening technical details.");
        Require(C<TextBlock>(page, "PluginDescription").Text == PluginUxFixture.Purpose,
            "The original purpose was changed or replaced with an ID-specific host text.");
        Require(InViewport(C<TextBlock>(page, "PluginHeading")) && InViewport(C<TextBlock>(page, "PluginDescription"))
            && InViewport(C<Button>(page, "PluginEnabled")),
            "A normal name, single-sentence purpose or state action is outside the first detail viewport.");
        Require(!C<Expander>(page, "PluginDeveloperExpander").IsExpanded,
            "Technical details opened on initial presentation or a passive presentation change.");
        // A closed ContentPresenter need not have materialized its content yet.
        // Detached controls retain IsEffectivelyVisible=true; they cannot render.
        Require(!RootedVisible(C<Descriptions>(page, "PluginIdentity")),
            "Full build identities are attached and visible in the closed normal overview.");
        Program.CaptureWindow(window, "smoke-pux-overview-" + cell + ".png");
        Observation(window, page, fixture, "overview", new { cell, purpose = C<TextBlock>(page, "PluginDescription").Text,
            purposeInViewport = InViewport(C<TextBlock>(page, "PluginDescription")), actionInViewport = InViewport(C<Button>(page, "PluginEnabled")),
            technicalExpanded = C<Expander>(page, "PluginDeveloperExpander").IsExpanded,
            disclosure = DisclosureState(window, page),
            visibleLabels = VisibleLabels(page) });

        // A real positionless routed request exercises the page's keyboard-context
        // handler; it is not a claim that an OS sent Shift+F10 or Tab.
        SelectionListItem item = (SelectionListItem)C<SelectionList>(page, "PluginCards").SelectedItem!;
        if (!item.IsEffectivelyVisible)
            await ActivateAsync(C<Button>(page, "PluginBack"));
        item.Focus();
        item.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        await Program.WaitUntilAsync(() => page.PluginActionMenu?.IsOpen == true);
        ContextMenu menu = page.PluginActionMenu!;
        Require(menu.Items.OfType<MenuItem>().Any(entry => Equals(entry.Tag, "plugins.disable") && entry.IsEnabled),
            "The selected active plugin has no reachable Disable item in the actual open menu.");
        if (mode == "Barebones" && language == "de" || width > 1000)
            CaptureMenu(window, menu, "smoke-pux-menu-" + cell + ".png");
        menu.Close(); await Settle();
        Require(page.Selected?.Key == PluginUxFixture.SourceKey,
            "Opening/closing the context menu changed the candidate identity.");
        page.Select(PluginUxFixture.SourceKey);
    }

    private static async Task DraftAndDisclosureAsync(MainWindow window, PluginsPage page, PluginUxFixture fixture, string root)
    {
        Expect("Edit an invalid source value, inspect developer details, then save: preserve raw text and focus its local error without executing plugin code.");
        page.Select(PluginUxFixture.SourceKey);
        // Locate the source scope before its initially closed presenter has
        // materialized the editor into the visual tree, then open the real header.
        Expander scope = C<StackPanel>(page, "PluginSettings").Children.OfType<Expander>()
            .Single(group => group.Content is PluginSettingsEditor settings
                && ReferenceEquals(settings.Draft, page.CurrentDraft!.Sources[PluginUxFixture.SourceId]));
        PluginSettingsEditor editor = (PluginSettingsEditor)scope.Content!;
        if (!scope.IsExpanded) await ToggleAsync(scope);
        await Program.WaitUntilAsync(() => RootedVisible(editor));
        TextBox limit = (TextBox)editor.FindField("limit")!;
        await RevealAsync(window, limit); limit.Text = "1.5 unfinished ü";
        Require(C<TextBlock>(page, "PluginDraftSummary").IsVisible
            && C<TextBlock>(page, "PluginDraftSummary").Text == Texts.T("plugins.draft"),
            "The overview does not distinguish the unsaved draft from the active or next-start state.");
        PluginConfigurationDraft draft = page.CurrentDraft!;
        string fingerprint = draft.Sources[PluginUxFixture.SourceId].EditorFingerprint;
        string beforeFiles = HashFiles(root);
        Expander developer = C<Expander>(page, "PluginDeveloperExpander");
        await RequireDeveloperClosedAsync(window, page, fixture, "closed-before-expand");
        await ToggleAsync(developer);
        Require(developer.IsExpanded, "The real developer disclosure did not open with keyboard activation.");
        Descriptions identity = C<Descriptions>(page, "PluginIdentity");
        await Program.WaitUntilAsync(() => DeveloperPresenter(developer) is { IsVisible: true } presenter
            && RootedVisible(presenter) && RootedVisible(identity));
        var values = identity.Items.OfType<DescriptionsItem>().Select(row => (row.Content as SelectableTextBlock)?.Text ?? string.Empty).ToArray();
        Require(values.Contains(PluginUxFixture.SourcePluginId) && values.Contains(fixture.Source.CandidatePath!)
            && values.Any(value => value.Contains(fixture.Source.Artifact!.ExecutionRevision, StringComparison.Ordinal)),
            "Developer details omit complete selectable identity, loader path or execution revision.");
        Button copy = C<Button>(page, "PluginCopyTechnicalDetails");
        await RevealAsync(window, copy);
        bool copied = await ClipboardChecks.CheckIsolatedCopyAsync(window, () => ActivateAsync(copy),
            new[] { PluginUxFixture.SourcePluginId, fixture.Source.CandidatePath!, fixture.Source.Artifact!.ExecutionRevision },
            new[] { "1.5 unfinished ü", "saved label", "next-start label" });
        if (copied) Require(C<TextBlock>(page, "PluginTechnicalCopyStatus").Text == Texts.T("plugins.technicalDetailsCopied"),
            "Successful fixture copy has no accessible localized completion status.");
        await RevealAsync(window, identity.Items.OfType<DescriptionsItem>().First());
        Observation(window, page, fixture, "disclosure-open", DisclosureState(window, page));
        Program.CaptureWindow(window, "smoke-pux-developer-reference.png");
        TextBox executable = C<TextBox>(page, "PluginEditorExecutable");
        executable.Text = "unfinished editor preference ü";
        await ToggleAsync(developer);
        await RequireDeveloperClosedAsync(window, page, fixture, "closed-after-expand");
        Program.CaptureWindow(window, "smoke-pux-developer-closed-reference.png");
        await page.RefreshAsync(); await page.RefreshAsync();
        Require(ReferenceEquals(draft, page.CurrentDraft) && draft.Sources[PluginUxFixture.SourceId].EditorFingerprint == fingerprint
            && HashFiles(root) == beforeFiles && fixture.ForbiddenCalls == 0,
            "Disclosure/status refresh changed raw draft, persisted metadata or executed plugin code.");
        await ToggleAsync(scope);
        await RevealAsync(window, C<Button>(page, "PluginSave"));
        await ActivateAsync(C<Button>(page, "PluginSave"));
        await Program.WaitUntilAsync(() => !page.IsBusy && page.GetVisualDescendants().OfType<PluginSettingsEditor>()
            .Single(e => ReferenceEquals(e.Draft, draft.Sources[PluginUxFixture.SourceId])).FindField("limit")!.IsFocused);
        Require(limit.Text == "1.5 unfinished ü" && InViewport(limit) && scope.IsExpanded
            && AutomationProperties.GetHelpText(limit)?.Contains(Texts.T("plugins.integerError"), StringComparison.Ordinal) == true,
            "Save did not reopen/focus the exact retained source input and its local error.");
        Require(HashFiles(root) == beforeFiles && fixture.ForbiddenCalls == 0,
            "Invalid local Save wrote configuration or called a worker.");
        Program.CaptureWindow(window, "smoke-pux-draft-error-reference.png");
        page.Select(PluginUxFixture.FilterKey); page.Select(PluginUxFixture.SourceKey);
        Require(executable.Text == "unfinished editor preference ü" && ReferenceEquals(draft, page.CurrentDraft),
            "Candidate roundtrip lost global editor preference text or source draft.");
        await fixture.StageSettingsAsync(); await page.RefreshAsync();
        var state = (await fixture.Store.ReadAsync()).Plugins.Single(p => p.PluginId == PluginUxFixture.SourcePluginId);
        Require(state.Active!.ExecutionRevision == state.Pending!.ExecutionRevision
            && state.Active.Configuration.Revision != state.Pending.Configuration.Revision,
            "Settings-only fixture does not represent the same code with a different next-start configuration.");
        Require(page.CurrentDraft?.Sources[PluginUxFixture.SourceId].EditorFingerprint == fingerprint
            && !C<Button>(page, "PluginSave").IsEnabled
            && C<TextBlock>(page, "PluginDraftStatus").Text == Texts.T("plugins.stale"),
            "Independent profile write rebased, saved or discarded the invalid draft instead of exposing stale state.");
        Require(C<TextBlock>(page, "PluginDraftSummary").IsVisible
            && C<TextBlock>(page, "PluginDraftSummary").Text == Texts.T("plugins.stale"),
            "The stale draft disappeared from the overview while saved next-start settings exist.");
        Require(VisibleText(page).Contains(PluginContentPresentation.DescribePending(PluginPendingKind.Configuration), StringComparison.Ordinal),
            "The same-code next-start settings notice disappeared behind technical details.");
        Observation(window, page, fixture, "draft-and-disclosure", new { fingerprint,
            active = state.Active.ExecutionRevision, pending = state.Pending.ExecutionRevision,
            activeConfiguration = state.Active.Configuration.Revision, pendingConfiguration = state.Pending.Configuration.Revision,
            rawRetained = page.CurrentDraft!.Sources[PluginUxFixture.SourceId].Text("limit"), stale = true });
    }

    private static async Task MenuIntentAsync(MainWindow window, PluginsPage page, PluginUxFixture fixture)
    {
        Expect("Open Disable for this source, change its stored state independently, and confirm the old action cannot turn into Enable.");
        page.Select(PluginUxFixture.SourceKey);
        SelectionList list = C<SelectionList>(page, "PluginCards");
        SelectionListItem sourceItem = (SelectionListItem)list.SelectedItem!;
        page.OpenPluginActionMenu(PluginUxFixture.SourceKey, sourceItem);
        await Program.WaitUntilAsync(() => page.PluginActionMenu?.IsOpen == true);
        MenuItem disable = page.PluginActionMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "plugins.disable"));
        var before = await fixture.Store.ReadAsync();
        await fixture.Store.SetEnabledAsync(PluginUxFixture.SourcePluginId, false, before.Revision);
        await page.RefreshAsync();
        long disabledRevision = (await fixture.Store.ReadAsync()).Revision;
        // Simulates an already queued invocation of the captured command after refresh.
        // The real menu may correctly have closed; executing its frozen command must
        // still never invert the intent or retarget the currently selected row.
        disable.Command?.Execute(disable.CommandParameter);
        await Settle();
        var afterStale = await fixture.Store.ReadAsync();
        Require(!afterStale.Plugins.Single(p => p.PluginId == PluginUxFixture.SourcePluginId).Enabled
            && afterStale.Revision == disabledRevision,
            "A stale Disable invocation inverted state or wrote an obsolete target.");
        page.Select(PluginUxFixture.FilterKey);
        Require(!C<Button>(page, "PluginWatches").IsVisible,
            "A pure filter exposes a source/watch action.");
        SelectionListItem filterItem = (SelectionListItem)list.SelectedItem!;
        // Return to the source before right-clicking the different filter row.
        // The hit-tested routed pointer path must target that row, not Selected.
        page.Select(PluginUxFixture.SourceKey);
        await SelectionChecks.PressAsync(window, filterItem, right: true);
        await Program.WaitUntilAsync(() => page.PluginActionMenu?.IsOpen == true);
        Require(page.Selected?.Key == PluginUxFixture.FilterKey,
            "Right-clicking another row retained the old source as action target.");
        MenuItem enable = page.PluginActionMenu!.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "plugins.enable"));
        await ActivateAsync(enable);
        await Program.WaitUntilAsync(() => !page.IsBusy);
        var after = await fixture.Store.ReadAsync();
        Require(after.Plugins.Single(p => p.PluginId == PluginUxFixture.FilterPluginId).Enabled
            && !after.Plugins.Single(p => p.PluginId == PluginUxFixture.SourcePluginId).Enabled,
            "Filter Enable targeted a previous source or failed to execute.");
        Require(page.CurrentDraft == null || !page.CurrentDraft.Dirty,
            "The filter action unexpectedly modified its settings draft.");
        page.Select(PluginUxFixture.SourceKey);
        Require(page.CurrentDraft?.Sources[PluginUxFixture.SourceId].Text("limit") == "1.5 unfinished ü",
            "A quick action on another plugin discarded the retained source draft.");
        sourceItem = (SelectionListItem)list.SelectedItem!;
        page.OpenPluginActionMenu(PluginUxFixture.SourceKey, sourceItem);
        await Program.WaitUntilAsync(() => page.PluginActionMenu?.IsOpen == true);
        ContextMenu escapeMenu = page.PluginActionMenu!;
        escapeMenu.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = escapeMenu, Key = Key.Escape });
        await Program.WaitUntilAsync(() => page.PluginActionMenu?.IsOpen != true && sourceItem.IsFocused);
        Require(page.Selected?.Key == PluginUxFixture.SourceKey && C<Grid>(page, "PluginListPane").IsVisible,
            "Escape both closed the popup and unexpectedly navigated away from its source list.");
        Observation(window, page, fixture, "menu-intent", new { staleDisableDidNotInvert = true,
            filterEnabled = true, sourceEnabled = false, input = "actual menu command; in-process routed activation, not native OS key injection" });
    }

    internal static async Task CheckBusyForeignMenuAsync(MainWindow window, PluginsPage page, PluginManagementServices services)
    {
        Require(page.IsBusy && page.Selected != null, "Busy-menu check requires the real running probe.");
        string selected = page.Selected.Key;
        string temporarySource = Path.Combine(services.PluginDirectory, "pux-busy-" + Guid.NewGuid().ToString("N") + ".cs");
        try
        {
            // Inventory-only input is never compiled. Its temporary file belongs
            // to the disposable real-workflow fixture, not a personal profile.
            await File.WriteAllTextAsync(temporarySource, "// Undescribed inventory input for the busy-menu check.\n");
            await page.RefreshAsync();
            var foreign = (await services.ReadCatalogAsync(CancellationToken.None)).Entries.Single(e => e.LoaderPath == temporarySource);
            page.OpenPluginActionMenu(foreign.Key, C<ComboBox>(page, "PluginCategory"));
            await Program.WaitUntilAsync(() => page.PluginActionMenu?.IsOpen == true);
            Require(page.IsBusy && page.Selected?.Key == selected,
                "Inspecting another row's menu implicitly selected it and cancelled the running probe.");
            ContextMenu menu = page.PluginActionMenu!;
            Require(menu.Items.OfType<MenuItem>().Where(i => i.Tag is string).All(i => !i.IsEnabled),
                "A busy foreign-row menu exposes an executable plugin action.");
            menu.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = menu, Key = Key.Escape });
            await Program.WaitUntilAsync(() => page.PluginActionMenu?.IsOpen != true);
            Require(page.IsBusy && page.Selected?.Key == selected,
                "Closing the busy menu also cancelled the probe or changed its target.");
            Console.WriteLine("PLUGIN_UX_BUSY: real pending probe retained across foreign-row menu and Escape; explicit Deactivate performs the later cancellation.");
        }
        finally
        {
            File.Delete(temporarySource);
            await page.RefreshAsync();
        }
    }

    private static async Task MissingAndRetainedAsync(MainWindow window, PluginsPage page, PluginUxFixture fixture)
    {
        Expect("Distinguish an undescribed file, a failed update with retained description, and a missing file; do not invent a successful current version.");
        page.Select(PluginUxFixture.UnknownKey);
        Require(C<TextBlock>(page, "PluginDescription").Text == Texts.T("plugins.noDescription")
            && !C<Button>(page, "PluginEnabled").IsEnabled,
            "An undescribed inventory file was presented as a described enabled plugin.");
        await fixture.FailSourceAsync(missing: false); await page.RefreshAsync(); page.Select(PluginUxFixture.SourceKey);
        Require(C<TextBlock>(page, "PluginDescription").Text == PluginUxFixture.Purpose
            && C<TextBlock>(page, "PluginDescriptionOrigin").IsEffectivelyVisible
            && !string.IsNullOrWhiteSpace(C<TextBlock>(page, "PluginDescriptionOrigin").Text)
            && !C<Control>(page, "PluginVersionChip").IsVisible,
            "A retained description lost either its original text or its visible provenance.");
        Require(!C<Button>(page, "PluginSelectCandidate").IsEnabled,
            "The failed candidate can be selected as a valid next-start build.");
        Require(RootedVisible(C<TextBlock>(page, "PluginAttention"))
            && C<TextBlock>(page, "PluginAttention").Text?.Contains(page.Selected!.Candidate!.Problem!, StringComparison.Ordinal) == true
            && !C<Expander>(page, "PluginDeveloperExpander").IsExpanded,
            "The failed candidate's actual reason requires opening developer details.");
        C<ScrollViewer>(page, "PluginDetailsPane").Offset = default;
        await Settle(); Program.CaptureWindow(window, "smoke-pux-retained-failure-reference.png");
        await fixture.FailSourceAsync(missing: true); await page.RefreshAsync();
        Require(page.Selected?.Key == PluginUxFixture.SourceKey && page.Selected.Candidate?.IsPresent == false,
            "Missing candidate was silently replaced by a different plugin/candidate.");
        Require((await fixture.Store.ReadAsync()).Plugins.Any(p => p.PluginId == PluginUxFixture.SourcePluginId),
            "Missing candidate deleted its saved profile identity.");
        Observation(window, page, fixture, "missing-and-retained", new { unknown = PluginUxFixture.UnknownKey,
            retainedPurpose = PluginUxFixture.Purpose, missingKey = page.Selected?.Key, visibleLabels = VisibleLabels(page) });
    }

    private static T C<T>(PluginsPage page, string name) where T : Control => page.FindControl<T>(name)!;
    private static bool RootedVisible(Control control) => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;
    private static ContentPresenter? DeveloperPresenter(Expander expander) => expander.GetVisualDescendants()
        .OfType<ContentPresenter>().SingleOrDefault(presenter => presenter.Name == "PART_ContentPresenter"
            && ReferenceEquals(presenter.TemplatedParent, expander));
    private static ToggleButton ExpanderHeader(Expander expander)
    {
        expander.ApplyTemplate();
        return expander.GetVisualDescendants().OfType<ToggleButton>()
            .First(header => header.GetVisualAncestors().OfType<Expander>().FirstOrDefault() == expander);
    }
    private static async Task RequireDeveloperClosedAsync(MainWindow window, PluginsPage page, PluginUxFixture fixture, string phase)
    {
        Expander developer = C<Expander>(page, "PluginDeveloperExpander");
        ToggleButton header = ExpanderHeader(developer);
        await RevealAsync(window, header);
        // The actual Semi template hides this presenter, with no content-height
        // animation. A visible header plus hidden presenter proves collapse even
        // when the technical content would otherwise lie below the viewport.
        await Program.WaitUntilAsync(() => !developer.IsExpanded && InViewport(header)
            && DeveloperPresenter(developer) is { IsVisible: false } presenter && presenter.IsAttachedToVisualTree()
            && !RootedVisible(C<Descriptions>(page, "PluginIdentity")));
        Require((header.IsFocused || header.Focus()) && !C<Button>(page, "PluginCopyTechnicalDetails").Focus() && header.IsFocused,
            "Closed technical details retained a focusable copy action or lost their reachable header.");
        Observation(window, page, fixture, phase, DisclosureState(window, page));
    }
    private static object DisclosureState(Window window, PluginsPage page)
    {
        Expander developer = C<Expander>(page, "PluginDeveloperExpander");
        // Diagnostics must also survive failures before a template exists.
        ToggleButton? header = developer.GetVisualDescendants().OfType<ToggleButton>()
            .FirstOrDefault(toggle => toggle.GetVisualAncestors().OfType<Expander>().FirstOrDefault() == developer);
        return new { developer.IsExpanded, header = header == null ? null : State(header),
            presenters = developer.GetVisualDescendants().OfType<ContentPresenter>()
                .Where(presenter => presenter.Name == "PART_ContentPresenter" && ReferenceEquals(presenter.TemplatedParent, developer))
                .Select(State).ToArray(),
            identity = State(C<Descriptions>(page, "PluginIdentity")), copy = State(C<Button>(page, "PluginCopyTechnicalDetails")) };
        object State(Control control) => new { type = control.GetType().Name, control.Name,
            attached = control.IsAttachedToVisualTree(), rootedInFixture = ReferenceEquals(TopLevel.GetTopLevel(control), window),
            control.IsVisible, control.IsEffectivelyVisible, bounds = control.Bounds.ToString(),
            origin = control.TranslatePoint(default, window)?.ToString(),
            ancestors = control.GetVisualAncestors().OfType<Control>().Select(parent => new { type = parent.GetType().Name,
                parent.Name, parent.IsVisible, parent.IsEffectivelyVisible, bounds = parent.Bounds.ToString(),
                parent.ClipToBounds, clip = parent.Clip?.Bounds.ToString() }).ToArray() };
    }
    private static async Task ToggleAsync(Expander expander)
    {
        ToggleButton header = ExpanderHeader(expander);
        await RevealAsync(TopLevel.GetTopLevel(expander) as Window ?? throw new InvalidOperationException("Missing fixture window."), header);
        await ActivateAsync(header);
    }
    private static async Task ActivateAsync(Control target)
    {
        target.Focus();
        Key key = target is ToggleButton ? Key.Space : Key.Enter;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = target, Key = key });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Source = target, Key = key });
        await Settle();
    }
    private static bool InViewport(Control target)
    {
        ScrollViewer? scroll = target.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Point? location = scroll == null ? null : target.TranslatePoint(default, scroll);
        return RootedVisible(target) && target.Bounds.Width > 0 && target.Bounds.Height > 0
            && scroll != null && location is { } point && point.Y >= -1
            && point.Y + target.Bounds.Height <= scroll.Viewport.Height + 1
            && point.X >= -1 && point.X + target.Bounds.Width <= scroll.Viewport.Width + 1;
    }
    private static Task RevealAsync(Window window, Control target) => Program.WaitUntilAsync(() =>
    { window.UpdateLayout(); target.BringIntoView(); window.UpdateLayout(); return InViewport(target); });
    private static async Task Settle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    private static string HashFiles(string root) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|",
        Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))))));
    private static string[] VisibleLabels(PluginsPage page) => page.GetVisualDescendants().OfType<TextBlock>()
        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Text!).ToArray();
    private static string VisibleText(PluginsPage page) => string.Join("\n", VisibleLabels(page));
    private static void Expect(string intent) => Console.WriteLine("PLUGIN_UX_EXPECT " + intent);
    private static void Observation(MainWindow window, PluginsPage page, PluginUxFixture fixture, string task, object data)
    {
        Control? focus = TopLevel.GetTopLevel(page)?.FocusManager?.GetFocusedElement() as Control;
        Console.WriteLine("PLUGIN_UX " + JsonSerializer.Serialize(new { task, language = Texts.Language,
            mode = Config.LoadConfig().WorkspaceMode, theme = window.ActualThemeVariant.Key?.ToString(), window.RenderScaling,
            client = window.ClientSize.ToString(), frame = window.FrameSize?.ToString(), pageBounds = page.Bounds.ToString(),
            viewport = C<ScrollViewer>(page, "PluginDetailsPane").Viewport.ToString(), offset = C<ScrollViewer>(page, "PluginDetailsPane").Offset.ToString(),
            key = page.Selected?.Key, focus = focus?.Name ?? focus?.GetType().Name, focusVisible = focus?.IsEffectivelyVisible,
            forbiddenCalls = fixture.ForbiddenCalls, data }));
    }
    private static void CaptureMenu(Window owner, ContextMenu menu, string name)
    {
        PopupRoot root = TopLevel.GetTopLevel(menu) as PopupRoot
            ?? throw new InvalidOperationException("Menu capture requires its actual separate PopupRoot.");
        root.UpdateLayout();
        double scaling = root.RenderScaling;
        Require(menu.IsOpen && root.IsEffectivelyVisible && root.ClientSize.Width > 0 && root.ClientSize.Height > 0,
            "Open menu has no rendered popup viewport.");
        Rect viewport = new(root.ClientSize);
        Rect InRoot(Visual visual) => new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(root)
            ?? throw new InvalidOperationException("Menu visual is not attached to its popup root."));
        bool Fits(Rect bounds) => bounds.Width > 0 && bounds.Height > 0 && bounds.X >= -1 && bounds.Y >= -1
            && bounds.Right <= viewport.Right + 1 && bounds.Bottom <= viewport.Bottom + 1;
        var rows = menu.Items.OfType<MenuItem>().Select(item => new { Item = item, Bounds = InRoot(item) }).ToArray();
        Console.WriteLine("PLUGIN_UX_POPUP_GEOMETRY " + JsonSerializer.Serialize(new { name,
            root = root.GetType().Name, rootClient = root.ClientSize.ToString(), actualScale = scaling,
            ownerScale = owner.RenderScaling, menu = menu.Bounds.ToString(), menuInRoot = InRoot(menu).ToString(),
            menuToRoot = menu.TransformToVisual(root)?.ToString(), menuTransform = menu.RenderTransform?.Value.ToString(),
            popupTransform = root.Transform?.Value.ToString(),
            rows = rows.Select(row => new { id = row.Item.Tag?.ToString(), bounds = row.Bounds.ToString(), row.Item.IsEffectivelyVisible }).ToArray() }));
        Require(rows.Length == 5 && rows.All(row => row.Item.IsEffectivelyVisible && Fits(row.Bounds))
            && Fits(InRoot(menu)), "Popup viewport does not contain all five actual menu rows.");
        for (int index = 1; index < rows.Length; index++)
            Require(rows[index].Bounds.Top >= rows[index - 1].Bounds.Bottom - 1,
                "Actual popup menu rows overlap or are out of order.");
        PixelSize pixels = new((int)Math.Ceiling(root.ClientSize.Width * scaling),
            (int)Math.Ceiling(root.ClientSize.Height * scaling));
        // Keep raster DPI neutral and apply DIP-to-device scaling exactly once.
        // A VisualBrush preserves the attached popup visual and its coordinate tree.
        using RenderTargetBitmap bitmap = new(pixels, new Vector(96, 96));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        using (context.PushTransform(Matrix.CreateScale(scaling, scaling)))
            context.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, viewport);
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, name));
        if (Environment.GetEnvironmentVariable("MEOWCHAN_PLUGIN_UX_POPUP_REPLAY") == "1")
        {
            // Controlled DPI probe: identical actual root and pixel extent, no
            // explicit transform. Its content should occupy the unscaled DIP extent.
            using RenderTargetBitmap probe = new(pixels, new Vector(96, 96));
            probe.Render(root);
            probe.Save(Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(name) + "-probe-96dpi.png"));
        }
        Console.WriteLine("PLUGIN_UX_MENU " + JsonSerializer.Serialize(new { name, actualScale = scaling,
            ownerScale = owner.RenderScaling, root = root.GetType().Name, rootBounds = root.Bounds.ToString(),
            rootClient = root.ClientSize.ToString(), rootTransform = root.RenderTransform?.Value.ToString(),
            popupTransform = root.Transform?.Value.ToString(), menu = menu.Bounds.ToString(),
            menuInRoot = InRoot(menu).ToString(), menuToRoot = menu.TransformToVisual(root)?.ToString(),
            menuTransform = menu.RenderTransform?.Value.ToString(), imagePixels = bitmap.PixelSize.ToString(),
            rasterDpi = bitmap.Dpi.ToString(), explicitDipToPixel = scaling,
            rootScreenOrigin = root.PointToScreen(default).ToString(),
            ancestors = menu.GetVisualAncestors().Select(visual => new { type = visual.GetType().Name,
                bounds = visual.Bounds.ToString(), transform = visual.RenderTransform?.Value.ToString(),
                toRoot = visual.TransformToVisual(root)?.ToString(), visual.ClipToBounds }).ToArray(),
            items = rows.Select(row => new { id = row.Item.Tag?.ToString(), row.Item.IsEnabled,
                row.Item.IsEffectivelyVisible, bounds = row.Bounds.ToString(),
                expectedRasterPixels = new Rect(row.Bounds.X * scaling, row.Bounds.Y * scaling,
                    row.Bounds.Width * scaling, row.Bounds.Height * scaling).ToString(),
                expectedProbePixels = row.Bounds.ToString(),
                name = AutomationProperties.GetName(row.Item), header = row.Item.Header?.ToString() }).ToArray(),
            capture = "Actual PopupRoot at neutral 96 DPI with one explicit DIP-to-pixel transform; original five-row viewport unchanged." }));
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
