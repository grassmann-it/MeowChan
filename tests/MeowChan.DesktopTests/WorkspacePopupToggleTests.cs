using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Plugins;
using MeowChan.Desktop.Updates;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;
using MeowChan.Rules;
using MeowChan.Storage;
using MeowChan.Tags;
using Xunit.Abstractions;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class WorkspacePopupToggleTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("RulesMoreButton")]
    [InlineData("DiagnosticMoreButton")]
    [InlineData("SettingsMoreButton")]
    [InlineData("PluginsMoreButton")]
    public void WorkspaceMoreClosesOnTheSecondPointerClickAndEscape(string id) => OnUi(async () =>
    {
        await using RulesPage rules = new() { IsAvailable = true };
        DiagnosticsPage diagnostics = new();
        SettingsEditor settings = new(new Config());
        using PluginsPage plugins = new();
        rules.ApplyWorkView(WorkView.Barebones, true);
        rules.EditScope(MediaRuleScope.Global);
        diagnostics.ApplyWorkView(WorkView.Barebones);
        plugins.ApplyWorkView(WorkView.Barebones, true);
        Control page = id switch
        {
            "RulesMoreButton" => rules,
            "DiagnosticMoreButton" => diagnostics,
            "SettingsMoreButton" => settings,
            _ => plugins
        };
        Window window = Show(page);
        try
        {
            Button opener = Find<Button>(window, id);
            VerifyPopup(window, opener, () => MenuFor(window, opener) != null);
        }
        finally
        {
            window.Close();
            diagnostics.EndLifetime();
            settings.EndLifetime();
        }
    });

    [Fact]
    public void RulesTemplatesKeepTheirCheckedMenuEntryAndCanBeHiddenAgain() => OnUi(async () =>
    {
        await using RulesPage page = new() { IsAvailable = true };
        page.ApplyWorkView(WorkView.Barebones, true);
        page.EditScope(MediaRuleScope.Global);
        Window window = Show(page);
        try
        {
            Button opener = Find<Button>(window, "RulesMoreButton");
            foreach (bool wasOpen in new[] { false, true, false })
            {
                Click(window, opener);
                ContextMenu menu = Assert.IsType<ContextMenu>(MenuFor(window, opener));
                MenuItem templates = Assert.Single(menu.Items.OfType<MenuItem>(),
                    item => AutomationProperties.GetAutomationId(item) == "rules.templates");
                Assert.Equal(wasOpen, templates.IsChecked);
                Click(Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(templates)), templates);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void DiagnosticsFilterTogglePreservesTheQueryAndReturnsToResults() => OnUi(() =>
    {
        DiagnosticsPage page = new();
        page.ApplyWorkView(WorkView.Barebones);
        Window window = Show(page);
        try
        {
            Expander filters = Find<Expander>(window, "DiagnosticFiltersSection");
            Assert.False(filters.IsExpanded);
            Click(window, filters, y: 12);
            Assert.True(filters.IsExpanded);
            Click(window, filters, y: 12);
            Assert.False(filters.IsExpanded);

            PressCommand(window, "input.focus-diagnostics-search");
            TextBox query = Find<TextBox>(window, "DiagnosticTextFilter");
            Assert.True(query.IsKeyboardFocusWithin);
            query.Text = "fixture";
            PressCommand(window, "input.focus-diagnostics-search");
            Assert.False(filters.IsExpanded);
            Assert.Equal("fixture", query.Text);
            Assert.True(Find<DataGrid>(window, "DiagnosticsGrid").IsKeyboardFocusWithin);

            PressCommand(window, "input.focus-diagnostics-search");
            Assert.True(filters.IsExpanded);
            TextPresenter presenter = Assert.Single(query.GetVisualDescendants().OfType<TextPresenter>());
            presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, "candidate");
            PressCommand(window, "input.focus-diagnostics-search");
            PressAtFocus(window, Key.Escape);
            Assert.True(filters.IsExpanded && query.IsKeyboardFocusWithin);
            Assert.Equal("fixture", query.Text);
            presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, null);
            Press(window, Key.Escape);
            Assert.False(filters.IsExpanded);
            Assert.Equal("fixture", query.Text);
        }
        finally { window.Close(); page.EndLifetime(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void SettingsCompactSectionsCloseByClickReboundShortcutAndEscape() => OnUi(() =>
    {
        SettingsEditor editor = new(new Config());
        ShortcutBindingsSection before = ShortcutService.CurrentSection;
        ShortcutService.Publish(before.Set("sections.open-navigation", [ShortcutGesture.Parse("Primary+Shift+J")]));
        Window window = Show(editor, 740);
        try
        {
            Expander sections = Find<Expander>(window, "SettingsSections");
            Click(window, sections, y: 12);
            Assert.True(sections.IsExpanded);
            Click(window, sections, y: 12);
            Assert.False(sections.IsExpanded);
            sections.Focus();
            PressCommand(window, "sections.open-navigation");
            Assert.True(sections.IsExpanded);
            PressCommand(window, "sections.open-navigation");
            Assert.False(sections.IsExpanded);
            editor.OpenSectionNavigation();
            Press(window, Key.Escape);
            Assert.False(sections.IsExpanded);
        }
        finally
        {
            window.Close();
            editor.EndLifetime();
            ShortcutService.Publish(before);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void SettingsMoreShortcutReachesExportAndResetAndTheConfirmationReturnsFocusToSettings() => OnUi(() =>
    {
        SettingsEditor editor = new(new Config());
        Window window = Show(editor);
        try
        {
            Button opener = Find<Button>(window, "SettingsMoreButton");
            Assert.False(opener.Focusable);
            editor.FocusSearch();
            PressCommand(window, SettingsEditor.MoreActionsCommandId);
            ContextMenu menu = Assert.IsType<ContextMenu>(MenuFor(window, opener));
            MenuItem export = Find<MenuItem>(menu, "settings.transfer.export");
            Assert.True(export.IsFocused && export.IsEnabled);
            PressCommand(window, SettingsEditor.MoreActionsCommandId);
            Assert.False(menu.IsOpen);

            PressCommand(window, SettingsEditor.MoreActionsCommandId);
            menu = Assert.IsType<ContextMenu>(MenuFor(window, opener));
            MenuItem reset = Find<MenuItem>(menu, "settings.reset.all");
            Assert.True(reset.Focus());
            PressAtFocus(window, Key.Enter);
            Assert.False(menu.IsOpen);
            Button cancel = Assert.Single(window.OpenedPopups.SelectMany(popup =>
                    popup.Child?.GetVisualDescendants().OfType<Button>() ?? []),
                button => AutomationProperties.GetAutomationId(button) == "settings.reset.cancel");
            IInputElement? focused = window.FocusManager?.GetFocusedElement();
            string? focusedId = focused is StyledElement element ? AutomationProperties.GetAutomationId(element) : null;
            Assert.True(cancel.IsFocused,
                $"Reset cancellation focus: type={focused?.GetType().Name ?? "<none>"}, AutomationId={focusedId ?? "<none>"}");
            PressAtFocus(window, Key.Enter);
            Assert.False(cancel.IsEffectivelyVisible);
            Assert.True(editor.IsKeyboardFocusWithin);
            Assert.False(opener.IsKeyboardFocusWithin);
        }
        finally { window.Close(); editor.EndLifetime(); }
        return Task.CompletedTask;
    });

    [Fact]
    public void TaggingGroupStampAndEmptyHelpUseTheirOwnOpeningButtons() => OnUi(async () =>
    {
        using TempDirectory profile = new();
        new Config { DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1 }.SaveConfig(profile.Path);
        await using DesktopSessionHost session = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        await session.StartAsync();
        using TaggingPage page = new(session);
        Window window = Show(page, 1440);
        try
        {
            await page.ActivateAsync();
            Layout(window);
            ToggleButton more = Assert.IsAssignableFrom<ToggleButton>(page.Actions.More);
            Click(window, more);
            Button groupOpener = page.GroupManageButton;
            VerifyPopup(window, groupOpener, () => groupOpener.ContextMenu?.IsOpen == true,
                logRoute: output.WriteLine, reopenParent: () => Click(window, more));
            more.IsChecked = false;
            Layout(window);

            ComboBox groups = Find<ComboBox>(window, "tagging.group");
            Assert.True(groups.Focus());
            PressCommand(window, "menu.context-open");
            ContextMenu menu = Assert.Single(window.OpenedPopups.Select(popup => popup.Child).OfType<ContextMenu>());
            MenuItem create = Find<MenuItem>(menu, "tagging.group.new");
            Assert.True(create.Focus());
            PressCommand(window, "menu.context-open");
            Assert.False(menu.IsOpen);
            foreach (string id in new[] { "tagging.stamp", "TaggingEmptyHow", "TaggingHelp" })
            {
                Button opener = Find<Button>(window, id);
                VerifyPopup(window, opener, () => opener.Flyout is { IsOpen: true } flyout && ReferenceEquals(flyout.Target, opener));
            }
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1280)]
    [InlineData(760)]
    public void GalleryLayoutAndMoreReturnToThePictureAndAcceptKeyboardInput(int width) => OnUi(async () =>
    {
        ShortcutBindingsSection before = ShortcutService.CurrentSection;
        ShortcutService.Publish(before.Set(GalleryLayoutMenu.OpenCommandId, [ShortcutGesture.Parse("Primary+Shift+J")]));
        await using GalleryViewer viewer = new();
        viewer.ApplyWorkView(WorkView.Advanced);
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayControls: false, AutoHideControls: false));
        using TempDirectory temp = new();
        GalleryEntry entry = ImageEntry(temp.Path);
        Window window = Show(viewer, width);
        try
        {
            await viewer.ShowAsync(entry, [entry], 1, CancellationToken.None);
            Layout(window);
            Assert.Contains(viewer.GetVisualDescendants().OfType<Image>(), image => image.IsEffectivelyVisible
                && image.Source is Bitmap picture && picture.PixelSize == new PixelSize(17, 11));
            Button layout = Find<Button>(window, "GalleryLayoutButton");
            Button more = Find<Button>(window, "GalleryMore");
            bool layoutOnBar = width == 1280;
            Assert.Equal(layoutOnBar, layout.IsEffectivelyVisible);
            Assert.False(layout.Focusable);
            if (layoutOnBar)
            {
                VerifyPopup(window, layout, () => viewer.IsLayoutMenuOpen, returnFocus: viewer);
            }
            VerifyPopup(window, more, () => viewer.IsOverflowOpen, returnFocus: viewer);

            Button layoutOpener = layoutOnBar ? layout : more;
            PressCommand(window, GalleryLayoutMenu.OpenCommandId);
            Assert.Equal(layoutOnBar, viewer.IsLayoutMenuOpen);
            Assert.Equal(!layoutOnBar, viewer.IsOverflowOpen);
            Assert.Equal(!layoutOnBar, viewer.LayoutSection.IsEffectivelyVisible);
            Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(layoutOpener));
            PressCommand(window, GalleryLayoutMenu.OpenCommandId);
            Assert.False(viewer.IsLayoutMenuOpen);
            Assert.False(viewer.IsOverflowOpen);
            Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(layoutOpener));
            Assert.Same(viewer, window.FocusManager?.GetFocusedElement());

            PressAtFocus(window, Key.F10, RawInputModifiers.Shift);
            ContextMenu media = Assert.IsType<ContextMenu>(viewer.MediaMenu);
            Assert.True(media.IsOpen);
            MenuItem openMore = Find<MenuItem>(media, "gallery.overflow.label");
            Assert.Equal(Texts.T("gallery.overflow.label"), AutomationProperties.GetName(openMore));
            for (int step = 0; !openMore.IsFocused && step < media.ItemCount; step++)
                PressAtFocus(window, Key.Down);
            Assert.True(openMore.IsFocused);
            PressAtFocus(window, Key.Enter);
            Assert.False(media.IsOpen);
            Assert.True(viewer.IsOverflowOpen);
            ToggleButton fit = Find<ToggleButton>(viewer.OverflowMenuContent, "gallery.fit");
            Assert.True(fit.IsEffectivelyEnabled);
            Assert.Same(fit, window.FocusManager?.GetFocusedElement());
            Assert.True(viewer.OverflowMenuContent.IsKeyboardFocusWithin);

            ToggleButton widthFit = Find<ToggleButton>(viewer.OverflowMenuContent, "gallery.fit-width");
            PressAtFocus(window, Key.Down);
            Assert.Same(widthFit, window.FocusManager?.GetFocusedElement());
            PressAtFocus(window, Key.Enter);
            Assert.True(viewer.IsOverflowOpen);
            Assert.True(widthFit.IsChecked);
            Assert.False(fit.IsChecked);

            PressAtFocus(window, Key.Down);
            Assert.Same(Find<ToggleButton>(viewer.OverflowMenuContent, "gallery.fit-height"), window.FocusManager?.GetFocusedElement());
            PressAtFocus(window, Key.Down);
            Button zoom = Find<Button>(viewer.OverflowMenuContent, "gallery.zoom-in");
            Assert.True(zoom.IsEffectivelyEnabled);
            Assert.Same(zoom, window.FocusManager?.GetFocusedElement());
            PressAtFocus(window, Key.Escape);
            Assert.False(viewer.IsOverflowOpen);
            Assert.Same(viewer, window.FocusManager?.GetFocusedElement());
        }
        finally
        {
            window.Close();
            ShortcutService.Publish(before);
        }
    });

    [Fact]
    public void GalleryThreadTagSectionClosesEvenAfterFocusLeavesItsField() => OnUi(async () =>
    {
        using TempDirectory temp = new();
        GalleryEntry entry = ImageEntry(temp.Path);
        await using GalleryViewer viewer = new();
        Window window = Show(viewer, 1280);
        try
        {
            await viewer.ShowAsync(entry, [entry], 1, CancellationToken.None);
            viewer.ShowTagPanel(new UnavailableGalleryTags(entry));
            Layout(window);
            Button opener = Find<Button>(window, "GalleryThreadTagsToggle");
            Assert.False(opener.Focusable);
            Click(window, opener);
            Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
            Click(window, opener);
            Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(opener));
            PressCommand(window, "tags.quick.thread-section");
            Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
            viewer.TagSurface!.FocusField();
            PressCommand(window, "tags.quick.thread-section");
            Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(opener));
            Click(window, opener);
            Press(window, Key.Escape);
            Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(opener));
            Assert.True(viewer.IsTagPanelOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void MoreCancelsAnInFlightTagSuggestionListAndEscapeClosesTheNextList() => OnUi(async () =>
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = QuickTagSurfaceTests.Entry("a", 'a');
        Assert.True(PrivateTagName.TryCreate("fixture", out PrivateTagName? name, out _));
        store.DefineAndSetMediaSubjects(name!.Display, [new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!)], null, CancellationToken.None);
        using QuickTagSession session = new(QuickTagTarget.For(QuickTagSurfaceTests.Single(store, entry)));
        using QuickTagSurface surface = new(session, name: "TestQuickTag") { FocusOnAttach = false };
        Window window = Show(surface);
        try
        {
            Button more = Find<Button>(window, "TestQuickTagMore");
            IdentitySuggestionBox input = Find<IdentitySuggestionBox>(window, "TestQuickTagInput");
            Assert.True(more.IsEffectivelyVisible && !more.Focusable);
            Assert.Empty(surface.Shown);
            surface.FocusField();
            Assert.True(input.IsKeyboardFocusWithin);
            PressAtFocus(window, Key.Tab);
            Assert.False(input.IsKeyboardFocusWithin);
            TaskCompletionSource<IdentitySuggestion[]> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            input.Candidates = (_, _) => pending.Task;
            Click(window, more);
            Assert.True(input.HasSuggestionList);
            Click(window, more);
            Assert.False(input.HasSuggestionList);
            pending.SetResult([new IdentitySuggestion("fixture", null, "fixture")]);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(input.IsDropDownOpen);

            input.Candidates = (_, _) => Task.FromResult<IdentitySuggestion[]>([new IdentitySuggestion("fixture", null, "fixture")]);
            Click(window, more);
            Assert.True(input.IsDropDownOpen);
            Press(window, Key.Escape);
            Assert.False(input.IsDropDownOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void TheFirstMoreClickFromAnAssignedChipListsUnassignedTagsInTheEmptyField() => OnUi(async () =>
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagStore store = new(temp.Path, lease);
        MediaLibraryEntry entry = QuickTagSurfaceTests.Entry("a", 'a');
        MediaLibraryEntry elsewhere = QuickTagSurfaceTests.Entry("b", 'b');
        store.DefineAndSetMediaSubjects("assigned", [new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(entry)!)], null, CancellationToken.None);
        store.DefineAndSetMediaSubjects("browsable", [new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(elsewhere)!)], null, CancellationToken.None);
        using QuickTagSession session = new(QuickTagTarget.For(QuickTagSurfaceTests.Single(store, entry)));
        // The surface's own provider: an empty field lists tags only for an explicit request.
        using QuickTagSurface surface = new(session, name: "TestQuickTag") { FocusOnAttach = false };
        Window window = Show(surface);
        try
        {
            Button more = Find<Button>(window, "TestQuickTagMore");
            IdentitySuggestionBox input = Find<IdentitySuggestionBox>(window, "TestQuickTagInput");
            surface.FocusField();
            Layout(window);
            Assert.True(input.IsKeyboardFocusWithin && string.IsNullOrEmpty(input.Text) && !input.HasSuggestionList);
            PressAtFocus(window, Key.Up);
            Assert.True(surface.FocusZone() == QuickTagFocus.Assigned && !input.IsKeyboardFocusWithin,
                $"The assigned chip takes the focus first, the zone is {surface.FocusZone()}.");

            Click(window, more);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
            while (!input.IsDropDownOpen && !deadline.IsCancellationRequested)
            {
                await Task.Delay(10);
                Layout(window);
            }
            IdentitySuggestion[] listed = input.ItemsSource?.OfType<IdentitySuggestion>().ToArray() ?? [];
            Assert.True(input.IsDropDownOpen && input.IsKeyboardFocusWithin,
                $"The first click lists tags: open={input.IsDropDownOpen}, pending={input.HasSuggestionList}, " +
                $"field focused={input.IsKeyboardFocusWithin}, zone={surface.FocusZone()}, rows={listed.Length}.");
            Assert.Contains(listed, row => row.Value == "browsable");
            Assert.DoesNotContain(listed, row => row.Value == "assigned");

            Click(window, more);
            Assert.False(input.HasSuggestionList);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void EscapeAndTheSectionShortcutStayWithAComposingThreadTagField() => OnUi(async () =>
    {
        using TempDirectory temp = new();
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        PrivateTagStore store = new(profile.Path, lease);
        GalleryEntry entry = ImageEntry(temp.Path);
        await using GalleryViewer viewer = new();
        Window window = Show(viewer, 1280);
        try
        {
            await viewer.ShowAsync(entry, [entry], 1, CancellationToken.None);
            viewer.ShowTagPanel(new StoreGalleryTags(entry, store));
            Layout(window);
            Button opener = Find<Button>(window, "GalleryThreadTagsToggle");
            var disclosure = Assert.IsAssignableFrom<IExpandCollapseProvider>(ControlAutomationPeer.CreatePeerForElement(opener));
            Assert.False(opener.Focusable);
            Assert.Equal(ExpandCollapseState.Collapsed, disclosure.ExpandCollapseState);
            Click(window, opener);
            Assert.Equal(ExpandCollapseState.Expanded, disclosure.ExpandCollapseState);
            Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
            IdentitySuggestionBox field = Assert.Single(viewer.GetVisualDescendants().OfType<IdentitySuggestionBox>(),
                box => box.Name == "GalleryTagPanelThreadInput");
            Assert.True(field.IsEffectivelyVisible && field.IsEffectivelyEnabled && field.IsKeyboardFocusWithin,
                $"The thread tag field is editable and focused: visible={field.IsEffectivelyVisible}, enabled={field.IsEffectivelyEnabled}, " +
                $"focused={field.IsKeyboardFocusWithin}.");
            field.Text = "draft";
            Layout(window);
            TextPresenter presenter = Assert.Single(field.GetVisualDescendants().OfType<TextPresenter>());
            presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, "candidate");

            PressAtFocus(window, Key.Escape);
            Assert.True(viewer.IsTagPanelOpen, "Escape belongs to the composition: the tag panel stays open.");
            Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
            Assert.True(field.IsEffectivelyVisible && field.IsKeyboardFocusWithin);
            Assert.Equal("draft", field.Text);
            PressCommand(window, "tags.quick.thread-section");
            Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
            Assert.True(viewer.IsTagPanelOpen && field.IsKeyboardFocusWithin);

            presenter.SetCurrentValue(TextPresenter.PreeditTextProperty, null);
            field.Text = string.Empty;
            Layout(window);
            for (int press = 0; press < 3 && AutomationProperties.GetItemStatus(opener) == Texts.T("shortcuts.group.expanded"); press++)
                PressAtFocus(window, Key.Escape); // a suggestion list, if one opened, closes first
            Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(opener));
            Assert.Equal(ExpandCollapseState.Collapsed, disclosure.ExpandCollapseState);
            Assert.True(viewer.IsTagPanelOpen, "Without a composition Escape folds only the thread section.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public void PluginDetailActionsAndTheSourceChooserCloseOnTheSecondPointerClick() => OnUi(async () =>
    {
        using TempDirectory temp = new();
        PluginManagementServices services = await TwoSourcePluginAsync(Path.Combine(temp.Path, "plugin-fixture"));
        SourcesPage sources = new();
        PluginsPage plugins = sources.PluginManagement;
        plugins.Attach(services);
        plugins.ApplyWorkView(WorkView.Advanced, true);
        Window window = Show(sources, 1280);
        try
        {
            await sources.ActivateAsync();
            sources.SelectSource(TwoSourcePluginSource + "-a");
            Layout(window);
            bool MenuOpen() => plugins.PluginActionMenu is { IsOpen: true };
            string State() => $"menu open={MenuOpen()}, selected={plugins.Selected?.Key ?? "<none>"}, filter={plugins.Filter}, " +
                $"anchor={(plugins.PluginActionMenu?.PlacementTarget as Control)?.Name ?? "<none>"}";

            // Sources overview: "Actions" of the selected source's plugin.
            Button sourceMore = Find<Button>(window, "SourcePluginMore");
            Assert.True(sourceMore.IsEffectivelyVisible && sourceMore.IsEffectivelyEnabled, "SourcePluginMore is offered: " + State());
            VerifyThreeClicks(sourceMore);
            PressAtFocus(window, Key.Escape);
            Assert.False(MenuOpen(), State());
            Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(sourceMore));

            // Plugin detail: "Actions" beside the detail buttons.
            plugins.SetFilter(SourcesPluginsFilter.AllPlugins);
            plugins.Select(TwoSourcePluginKey);
            Layout(window);
            Button more = Find<Button>(window, "PluginMoreActions");
            Assert.True(more.IsEffectivelyVisible && more.IsEffectivelyEnabled, "PluginMoreActions is offered: " + State());
            VerifyThreeClicks(more);
            PressAtFocus(window, Key.Escape);
            Assert.False(MenuOpen(), State());
            Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(more));

            // "Show in Plugins" of a plugin with two sources opens the source chooser.
            Button watches = Find<Button>(window, "PluginWatches");
            Assert.True(watches.IsEffectivelyVisible && watches.IsEffectivelyEnabled, "PluginWatches is offered: " + State());
            VerifyThreeClicks(watches, () => Assert.Equal(2, plugins.PluginActionMenu!.Items.OfType<MenuItem>().Count()));

            PressCommand(window, "plugins.watches");
            Assert.False(MenuOpen(), State());
            Click(window, watches);
            Assert.True(MenuOpen(), State());

            // The press that dismisses the chooser is not the Actions button's own: its click opens the other menu.
            Click(window, more);
            Assert.True(MenuOpen() && ReferenceEquals(plugins.PluginActionMenu!.PlacementTarget, more), State());
            PressAtFocus(window, Key.Escape);
            Assert.False(MenuOpen(), State());

            void VerifyThreeClicks(Button opener, Action? verifyMenu = null)
            {
                Assert.False(opener.Focusable);
                Assert.False(MenuOpen());
                SourcesPluginsFilter filter = plugins.Filter;
                Rect Bounds() => new(opener.TranslatePoint(default, window)!.Value, opener.Bounds.Size);
                Rect original = Bounds();
                Point point = original.Center;
                int clicks = 0;
                void CountClick(object? sender, RoutedEventArgs e) => clicks++;
                opener.AddHandler(Button.ClickEvent, CountClick, handledEventsToo: true);
                try
                {
                    window.MouseMove(point);
                    for (int click = 1; click <= 3; click++)
                    {
                        window.MouseDown(point, MouseButton.Left);
                        Layout(window);
                        Assert.Equal(original, Bounds());
                        window.MouseUp(point, MouseButton.Left);
                        Layout(window);
                        Assert.Equal(click, clicks);
                        Assert.True(opener.IsEffectivelyVisible, State());
                        Assert.Equal(original, Bounds());
                        Assert.Equal(filter, plugins.Filter);
                        Assert.Equal(TwoSourcePluginKey, plugins.Selected?.Key);
                        bool open = click != 2;
                        Assert.Equal(open, MenuOpen());
                        Assert.Equal(Texts.T(open ? "shortcuts.group.expanded" : "shortcuts.group.collapsed"),
                            AutomationProperties.GetItemStatus(opener));
                        if (open)
                        {
                            Assert.Same(opener, plugins.PluginActionMenu!.PlacementTarget);
                            verifyMenu?.Invoke();
                        }
                    }
                }
                finally { opener.RemoveHandler(Button.ClickEvent, CountClick); }
            }
        }
        finally
        {
            window.Close();
            sources.EndLifetime();
        }
    });

    [Fact]
    public void RetargetingTheDetailButtonDuringDismissalOpensTheNewPluginsMenu() => OnUi(async () =>
    {
        using TempDirectory temp = new();
        PluginManagementServices services = await TwoSourcePluginAsync(temp.Path, includeOneSource: true);
        SourcesPage page = new();
        PluginsPage plugins = page.PluginManagement;
        plugins.Attach(services);
        plugins.ApplyWorkView(WorkView.Advanced, true);
        Window window = Show(page, 1280);
        try
        {
            await page.ActivateAsync();
            plugins.SetFilter(SourcesPluginsFilter.AllPlugins);
            plugins.Select(TwoSourcePluginKey);
            Layout(window);
            Button more = Find<Button>(window, "PluginMoreActions");
            Click(window, more);
            ContextMenu original = Assert.IsType<ContextMenu>(plugins.PluginActionMenu);
            Assert.True(original.IsOpen);
            Point point = more.TranslatePoint(new Rect(more.Bounds.Size).Center, window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            Layout(window);
            plugins.Select(OneSourcePluginKey);
            Layout(window);
            Assert.False(original.IsOpen);
            Assert.Equal(OneSourcePluginKey, plugins.Selected?.Key);
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            ContextMenu replacement = Assert.IsType<ContextMenu>(plugins.PluginActionMenu);
            Assert.True(replacement.IsOpen);
            Assert.NotSame(original, replacement);
            Assert.Same(more, replacement.PlacementTarget);
            MenuItem entry = Assert.Single(replacement.Items.OfType<MenuItem>(),
                item => AutomationProperties.GetAutomationId(item) == "plugins.watches");
            Assert.Empty(entry.Items);
            window.MouseUp(default, MouseButton.Left);
            Layout(window);
            PressAtFocus(window, Key.Escape);
            Assert.False(replacement.IsOpen);

            Button watches = Find<Button>(window, "PluginWatches");
            Assert.False(watches.Focusable);
            List<string> opened = [];
            plugins.OpenSourceWatches = opened.Add;
            await plugins.ExecuteSelectedPluginActionAsync("plugins.watches");
            Assert.Equal(TwoSourcePluginSource + "-c", Assert.Single(opened));
        }
        finally
        {
            window.Close();
            page.EndLifetime();
        }
    });

    [Theory]
    [InlineData(ShortcutPlatform.MacOS, KeyModifiers.Meta)]
    [InlineData(ShortcutPlatform.Windows, KeyModifiers.Control)]
    [InlineData(ShortcutPlatform.Linux, KeyModifiers.Control)]
    public void ShowPluginSourcesHasAPlatformShortcut(ShortcutPlatform platform, KeyModifiers modifiers)
    {
        var bindings = EffectiveShortcutBindings.Build(platform: platform);
        using var context = EffectiveShortcutBindings.EnterKeyContext("plugins");
        Assert.True(bindings.Matches(new KeyEventArgs { Key = Key.J, KeyModifiers = modifiers }, "plugins.watches"));
    }

    [Theory]
    [InlineData("input.focus-diagnostics-search", "Primary+F")]
    [InlineData("sections.open-navigation", "Primary+Alt+J")]
    [InlineData("gallery.layout-menu", "Primary+Alt+J")]
    [InlineData("tags.quick.thread-section", "Primary+T")]
    [InlineData("plugins.actions", "Primary+Alt+J")]
    public void ToggleGesturesResolveToCommandOnMac(string command, string binding)
    {
        ShortcutGesture gesture = ShortcutGesture.Parse(binding);
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set(command, [gesture]), ShortcutPlatform.MacOS);
        ShortcutGesture effective = Assert.Single(bindings.For(command));
        Assert.True(effective.Resolve(bindings.Platform).KeyModifiers.HasFlag(KeyModifiers.Meta));
        Assert.False(effective.Resolve(bindings.Platform).KeyModifiers.HasFlag(KeyModifiers.Control));
    }

    private static GalleryEntry ImageEntry(string directory)
    {
        const string file = "picture.png";
        string path = Path.Combine(directory, file);
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");
        File.WriteAllBytes(path, png);
        MediaAssetReference asset = new("picture", "picture", "original", file, path, png.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)), "", file, true);
        MediaAssetSnapshot manifest = new(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
        return new(new(new("fixture", "g", "42"), directory, asset.AssetId), manifest, asset, null, null, false, false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PluginDetailMoreIsFullyClickableAtMinimumWindowSizeInGerman(bool dark) => OnUi(async () =>
    {
        Texts.SetLanguage("de");
        using TempDirectory temp = new();
        PluginManagementServices services = await TwoSourcePluginAsync(Path.Combine(temp.Path, "plugin-fixture"));
        SourcesPage sources = new();
        PluginsPage plugins = sources.PluginManagement;
        plugins.Attach(services);
        plugins.ApplyWorkView(WorkView.Barebones, true);
        Window window = new()
        {
            Width = 640,
            Height = 480,
            RequestedThemeVariant = dark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light,
            Content = new Border { Padding = new Thickness(0, 48, 0, 78), Child = sources },
            Classes = { MotionPolicy.OffClass }
        };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await sources.ActivateAsync();
            sources.SelectSource(TwoSourcePluginSource + "-a");
            plugins.SelectSourcePlugin(TwoSourcePluginKey, details: true);
            Layout(window);
            Assert.True(plugins.ShowingSourcePluginDetails);
            Button opener = Find<Button>(window, "PluginMoreActions");
            Assert.True(opener.IsEffectivelyEnabled);
            Assert.False(opener.Focusable);

            var scroll = Find<ScrollViewer>(window, "PluginDetailsPane");
            var footer = Find<Border>(window, "PluginDraftFooter");
            var reasons = Find<SelectableTextBlock>(window, "PluginDraftReasons");
            // Padding audit §2, rank 11: the standard card footer gives 12 dip after the divider.
            Assert.Equal(12, footer.Padding.Top);
            try
            {
                await BooruTestSupport.WaitAsync(() =>
                {
                    opener.BringIntoView();
                    window.UpdateLayout();
                    return opener.IsEffectivelyVisible && opener.Bounds.Height > 0
                        && FullyInside(window, window.ClientSize)
                        && opener.GetVisualAncestors().OfType<ScrollViewer>().All(ancestor => FullyInside(ancestor, ancestor.Viewport));
                }, "the full plugin More actions button inside the detail viewport");
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"{error.Message}. Button={opener.Bounds.Size} at {opener.TranslatePoint(default, scroll)}, "
                    + $"viewport={scroll.Viewport}, offset={scroll.Offset}, footer={footer.Bounds.Size}, reasons={reasons.Bounds.Size}.", error);
            }

            var save = Find<Button>(window, "PluginSave");
            var discard = Find<Button>(window, "PluginDiscard");
            Assert.False(string.IsNullOrWhiteSpace(reasons.Text));
            double pinnedY = footer.TranslatePoint(default, window)!.Value.Y;
            foreach (double offset in new[] { 0d, scroll.ScrollBarMaximum.Y })
            {
                scroll.Offset = new Vector(0, offset);
                Layout(window);
                Assert.Equal(offset, scroll.Offset.Y, 1);
                Assert.Equal(pinnedY, footer.TranslatePoint(default, window)!.Value.Y, 1);
                foreach (Control control in new Control[] { save, discard, reasons })
                {
                    Assert.True(control.IsEffectivelyVisible && control.Bounds.Height > 0);
                    Point top = control.TranslatePoint(default, window)!.Value;
                    Point bodyEnd = scroll.TranslatePoint(new Point(0, scroll.Bounds.Height), window)!.Value;
                    Assert.True(top.Y >= bodyEnd.Y - 1 && top.Y + control.Bounds.Height <= window.ClientSize.Height + 1,
                        $"{control.Name} at {top} with height {control.Bounds.Height}, body ends at {bodyEnd}");
                }
            }
            opener.BringIntoView();
            Layout(window);
            Click(window, opener);
            ContextMenu menu = Assert.IsType<ContextMenu>(plugins.PluginActionMenu);
            Assert.True(menu.IsOpen);
            Assert.Single(menu.Items.OfType<MenuItem>(), item => AutomationProperties.GetAutomationId(item) == "plugins.watches");

            bool FullyInside(Visual surface, Size size) => opener.TranslatePoint(default, surface) is { } point
                && point.X >= -1 && point.Y >= -1 && point.X + opener.Bounds.Width <= size.Width + 1
                && point.Y + opener.Bounds.Height <= size.Height + 1;
        }
        finally { window.Close(); }
    });

    private const string TwoSourcePluginKey = "toggle-two-sources";
    private const string OneSourcePluginKey = "toggle-one-source";
    private const string TwoSourcePluginSource = "toggle-source";

    // Passive catalog data over the real profile store: nothing is compiled, described or started.
    private static async Task<PluginManagementServices> TwoSourcePluginAsync(string root, bool includeOneSource = false)
    {
        Directory.CreateDirectory(root);
        PluginProfileStore store = new(root);
        SettingsSchema empty = new("1", Array.Empty<SettingDefinition>());
        SourceDescription Source(string suffix)
        {
            string id = TwoSourcePluginSource + "-" + suffix, host = id + ".example.test";
            return new(id, "Fixture source " + suffix, new Uri("https://" + host + "/"),
                SourceCapabilities.ThreadRead | SourceCapabilities.OriginalMedia,
                new[] { new UrlRoute(SourceResourceKind.Thread, host, "/{board}/thread/{thread}", IsCanonical: true) },
                new[] { new HttpHostDeclaration(host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia) }, empty);
        }
        PluginDescription description = new("toggle.two-sources", "Two fixture sources", "1.0.0", SdkContract.Version,
            "Reads two fixture sites.", new[] { Source("a"), Source("b") }, Array.Empty<FilterDescription>(), empty,
            Array.Empty<InformationEntry>());
        List<PluginCandidateObservation> observations = [];
        List<PluginInstallationCandidate> installations = [];
        Add(TwoSourcePluginKey, description, 'a');
        if (includeOneSource)
            Add(OneSourcePluginKey, description with {
                PluginId = "toggle.one-source", Name = "One fixture source", Sources = [Source("c")]
            }, 'b');
        await store.ApplyStartupAsync(observations, 0);
        return new(store, new UnusedCompiler(), new FixedInventory(installations.ToArray()),
            root, (_, _) => throw new InvalidOperationException("The menu test described a worker."),
            (_, _, _) => throw new InvalidOperationException("The menu test started a worker."),
            (_, _, _, _, _) => throw new InvalidOperationException("The menu test probed a site."));

        void Add(string key, PluginDescription plugin, char digit)
        {
            string hash = new(digit, 64), path = Path.Combine(root, key + ".cs");
            PluginArtifact artifact = new(hash, hash, Path.Combine(root, hash + ".dll"), hash, hash, hash, Array.Empty<PluginArtifactResource>());
            observations.Add(new(key, plugin.PluginId, artifact, plugin, CandidatePath: path));
            installations.Add(new(key, path, PluginCandidateKind.Source, 100, Array.Empty<PluginBuildDiagnostic>()));
        }
    }

    private sealed class FixedInventory(params PluginInstallationCandidate[] candidates) : IPluginInstallationInventory
    {
        public Task<PluginInventory> ScanAsync(string directory, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PluginInventory(candidates, false, Array.Empty<PluginBuildDiagnostic>()));
    }

    private sealed class UnusedCompiler : IPluginCompilationService
    {
        private static InvalidOperationException Fail() => new("The menu test invoked compilation or cache maintenance.");
        public Task<PluginBuildResult> BuildAsync(string sourcePath, IReadOnlyList<PluginResourceInput>? resources = null,
            CancellationToken cancellationToken = default) => throw Fail();
        public Task<PluginArtifact?> FindArtifactAsync(string executionRevision, CancellationToken cancellationToken = default) => throw Fail();
        public Task<PluginCacheCleanupResult> CleanupAsync(IReadOnlySet<string> retainedExecutionRevisions,
            IReadOnlySet<string> retainedCompilationFingerprints, TimeSpan minimumAge,
            CancellationToken cancellationToken = default) => throw Fail();
    }

    // Tags of the medium and of its thread over a real store, so the thread tag field is editable.
    private sealed class StoreGalleryTags(GalleryEntry entry, PrivateTagStore store) : IGalleryTagPanelOwner
    {
        private readonly MediaTagSubject _subject = new MediaTagSubject.Bound(MediaLibraryFilterEngine.TagTarget(QuickTagSurfaceTests.Entry("a", 'a'))!);
        public IReadOnlyList<GalleryEntry> Entries { get; } = [entry];
        public GalleryTagPanelPosition? Position => null;
        public TagRecentList Recent { get; } = new();
        public QuickTagOptions Options => QuickTagOptions.Default;
        public TagContextOptions ContextOptions => TagContextOptions.Default;
        public MediaTagSubject? SubjectOf(GalleryEntry selected, out string? unavailableReason)
        {
            unavailableReason = null;
            return _subject;
        }
        public IQuickTagTarget? OpenTarget(GalleryEntry selected, out string? unavailableReason)
        {
            unavailableReason = null;
            return QuickTagTarget.For(QuickTagSurfaceTests.Single(store, QuickTagSurfaceTests.Entry("a", 'a')));
        }
        public IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason) =>
            OpenTarget(Entries[0], out unavailableReason);
        public BooruCatalogSnapshot? ReadSourceTags() => null;
        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => new(thread,
            () => new PrivateTagLoadSnapshot(PrivateTagLoadState.Ready, store.ReadSnapshot(), null),
            (_, _) => throw new NotSupportedException(), (_, _, _, _) => throw new NotSupportedException());
        public void OpenAdvanced(GalleryEntry selected) => throw new NotSupportedException();
    }

    private sealed class UnavailableGalleryTags(GalleryEntry entry) : IGalleryTagPanelOwner
    {
        public IReadOnlyList<GalleryEntry> Entries { get; } = [entry];
        public GalleryTagPanelPosition? Position => null;
        public TagRecentList Recent { get; } = new();
        public QuickTagOptions Options => QuickTagOptions.Default;
        public TagContextOptions ContextOptions => TagContextOptions.Default;
        public MediaTagSubject? SubjectOf(GalleryEntry selected, out string? unavailableReason)
        {
            unavailableReason = "No tag store";
            return null;
        }
        public IQuickTagTarget? OpenTarget(GalleryEntry selected, out string? unavailableReason)
        {
            unavailableReason = "No tag store";
            return null;
        }
        public IQuickTagTarget? OpenSelectionTarget(IReadOnlyList<MediaTagSubject> subjects, out string? unavailableReason) =>
            OpenTarget(Entries[0], out unavailableReason);
        public BooruCatalogSnapshot? ReadSourceTags() => null;
        public GalleryThreadTagModel OpenThreadTags(ResourceId thread) => throw new NotSupportedException();
        public void OpenAdvanced(GalleryEntry selected) => throw new NotSupportedException();
    }

    private static void OnUi(Func<Task> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        TransientPopupPolicy.Install();
        BooruComponentDispatcher.Wait(test());
    });

    private static Window Show(Control page, double width = 1100)
    {
        Window window = new() { Width = width, Height = 850, Content = page, Classes = { MotionPolicy.OffClass } };
        window.Show();
        window.PlatformImpl!.Activated?.Invoke();
        Layout(window);
        return window;
    }

    private static void VerifyPopup(Window window, Button opener, Func<bool> isOpen,
        Action<string>? logRoute = null, Action? reopenParent = null, Control? returnFocus = null)
    {
        Assert.False(opener.Focusable);
        Assert.False(isOpen());
        Click(TopLevel.GetTopLevel(opener) ?? window, opener);
        Assert.True(isOpen());
        Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
        string route = TracePopupClick(window, opener);
        logRoute?.Invoke(route);
        Assert.False(isOpen(), route);
        if (returnFocus != null) Assert.Same(returnFocus, window.FocusManager?.GetFocusedElement());
        if (!opener.IsAttachedToVisualTree()) reopenParent?.Invoke();
        Click(TopLevel.GetTopLevel(opener) ?? window, opener);
        Assert.True(isOpen());
        PressAtFocus(window, Key.Escape);
        Assert.False(isOpen());
        Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(opener));
        if (returnFocus != null) Assert.Same(returnFocus, window.FocusManager?.GetFocusedElement());
    }

    private static string TracePopupClick(Window window, Button opener)
    {
        Popup? before = AnchoredPopup(window.OpenedPopups);
        List<string> route = [PopupState("Before", before)];
        int clicks = 0, opened = 0, closed = 0;
        window.AddHandler(InputElement.PointerPressedEvent, LogPointer,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(InputElement.PointerReleasedEvent, LogPointer,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        opener.AddHandler(Button.ClickEvent, LogClick, handledEventsToo: true);
        if (before != null)
        {
            before.Opened += OnOpened;
            before.Closed += OnClosed;
        }
        try
        {
            Click(TopLevel.GetTopLevel(opener) ?? window, opener);
            Popup? after = AnchoredPopup(window.OpenedPopups);
            if (after != null && !ReferenceEquals(after, before)) opened++;
            route.Add(PopupState("After", after));
            route.Add($"Click={clicks}, Opened={opened}, Closed={closed}");
        }
        finally
        {
            window.RemoveHandler(InputElement.PointerPressedEvent, LogPointer);
            window.RemoveHandler(InputElement.PointerReleasedEvent, LogPointer);
            opener.RemoveHandler(Button.ClickEvent, LogClick);
            if (before != null)
            {
                before.Opened -= OnOpened;
                before.Closed -= OnClosed;
            }
        }
        return string.Join(Environment.NewLine, route);

        void LogPointer(object? sender, PointerEventArgs e) => LogEvent(e);

        void LogClick(object? sender, RoutedEventArgs e)
        {
            clicks++;
            LogEvent(e);
        }

        void LogEvent(RoutedEventArgs e) =>
            route.Add($"{e.RoutedEvent?.Name} {e.Route}: Source={e.Source?.GetType().Name ?? "<none>"}, Handled={e.Handled}");

        void OnOpened(object? sender, EventArgs e) => opened++;
        void OnClosed(object? sender, EventArgs e) => closed++;

        Popup? AnchoredPopup(IEnumerable<Popup> popups)
        {
            foreach (Popup popup in popups)
            {
                if (popup.Child is ToolTip) continue;
                if (ReferenceEquals(popup.PlacementTarget, opener)) return popup;
                if (AnchoredPopup(popup.OpenedPopups) is { } nested) return nested;
            }
            return null;
        }

        string PopupState(string phase, Popup? popup) =>
            $"{phase}: OpenedPopups.Count={window.OpenedPopups.Count}, OverlayPopupHost={popup?.IsUsingOverlayLayer}, " +
            $"popup.Child={popup?.Child?.GetType().Name ?? "<none>"}, opener.Attached={opener.IsAttachedToVisualTree()}, " +
            $"opener.Bounds={BoundsInWindow(opener)}, menu.Bounds={BoundsInWindow(popup?.Child)}";

        string BoundsInWindow(Visual? visual)
        {
            if (visual == null) return "<none>";
            if (!visual.IsAttachedToVisualTree() || visual.TranslatePoint(default, window) is not { } topLeft ||
                visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), window) is not { } bottomRight)
                return "<nicht eingehängt>";
            return new Rect(topLeft, bottomRight).ToString();
        }
    }

    private static ContextMenu? MenuFor(Window window, Button opener) => window.OpenedPopups
        .Where(popup => ReferenceEquals(popup.PlacementTarget, opener))
        .Select(popup => popup.Child).OfType<ContextMenu>().SingleOrDefault();

    private static T Find<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(),
            control => AutomationProperties.GetAutomationId(control) == id);

    private static void Click(TopLevel root, Control control, double? y = null)
    {
        Assert.True(control.IsEffectivelyVisible);
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, y ?? control.Bounds.Height / 2), root)!.Value;
        root.MouseMove(point);
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        Layout(root);
    }

    private static void PressAtFocus(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        TopLevel root = window.FocusManager?.GetFocusedElement() is Visual focused
            ? TopLevel.GetTopLevel(focused) ?? window : window;
        Press(root, key, modifiers, window);
        Layout(window);
    }

    internal static void PressCommand(Window window, string commandId)
    {
        KeyGesture gesture = Assert.IsType<KeyGesture>(CommandBindings.Gesture(commandId));
        PressAtFocus(window, gesture.Key, (RawInputModifiers)gesture.KeyModifiers);
    }

    private static void Press(TopLevel root, Key key, RawInputModifiers modifiers = RawInputModifiers.None, Window? fallback = null)
    {
        root.KeyPress(key, modifiers, PhysicalKey.None, null);
        TopLevel receiver = root.PlatformImpl != null ? root : Assert.IsType<Window>(fallback);
        receiver.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Layout(receiver);
    }

    private static void Layout(TopLevel root)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
    }
}
