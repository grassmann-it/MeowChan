using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Desktop.WorkViews;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class WorkspacePopupToggleTests
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

            Press(window, Key.F, RawInputModifiers.Control);
            TextBox query = Find<TextBox>(window, "DiagnosticTextFilter");
            Assert.True(query.IsKeyboardFocusWithin);
            query.Text = "fixture";
            Press(window, Key.F, RawInputModifiers.Control);
            Assert.False(filters.IsExpanded);
            Assert.Equal("fixture", query.Text);
            Assert.True(Find<DataGrid>(window, "DiagnosticsGrid").IsKeyboardFocusWithin);

            Press(window, Key.F, RawInputModifiers.Control);
            Assert.True(filters.IsExpanded);
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
        ShortcutService.Publish(before.Set("sections.open-navigation", [ShortcutGesture.Parse("Primary+Alt+J")]));
        Window window = Show(editor, 740);
        try
        {
            Expander sections = Find<Expander>(window, "SettingsSections");
            Click(window, sections, y: 12);
            Assert.True(sections.IsExpanded);
            Click(window, sections, y: 12);
            Assert.False(sections.IsExpanded);
            sections.Focus();
            Press(window, Key.J, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.True(sections.IsExpanded);
            PressAtFocus(window, Key.J, RawInputModifiers.Control | RawInputModifiers.Alt);
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
            Button groups = Find<Button>(window, "TaggingGroupManage");
            VerifyPopup(window, groups, () => groups.ContextMenu?.IsOpen == true);
            foreach (string id in new[] { "TaggingStamp", "TaggingEmptyHow", "TaggingHelp" })
            {
                Button opener = Find<Button>(window, id);
                VerifyPopup(window, opener, () => opener.Flyout is { IsOpen: true } flyout && ReferenceEquals(flyout.Target, opener));
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GalleryLayoutAndOverflowCloseAtTheirOpenerAndLayoutAcceptsItsRebinding() => OnUi(async () =>
    {
        ShortcutBindingsSection before = ShortcutService.CurrentSection;
        ShortcutService.Publish(before.Set(GalleryLayoutMenu.OpenCommandId, [ShortcutGesture.Parse("Primary+Alt+J")]));
        await using GalleryViewer viewer = new();
        viewer.ApplyWorkView(WorkView.Advanced);
        viewer.ConfigurePlayback(new GalleryPlaybackPreferences(OverlayControls: false, AutoHideControls: false));
        using TempDirectory temp = new();
        GalleryEntry entry = ImageEntry(temp.Path);
        Window window = Show(viewer, 1280);
        try
        {
            await viewer.ShowAsync(entry, [entry], 1, CancellationToken.None);
            Layout(window);
            Button layout = Find<Button>(window, "GalleryLayoutButton");
            Button more = Find<Button>(window, "GalleryMore");
            VerifyPopup(window, layout, () => viewer.IsLayoutMenuOpen);
            VerifyPopup(window, more, () => viewer.IsOverflowOpen);
            viewer.Focus();
            Press(window, Key.J, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.True(viewer.IsLayoutMenuOpen);
            PressAtFocus(window, Key.J, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.False(viewer.IsLayoutMenuOpen);
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
            Press(window, Key.T, RawInputModifiers.Control);
            Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
            viewer.TagSurface!.FocusField();
            Press(window, Key.T, RawInputModifiers.Control);
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
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAD9sSLAAAAFElEQVR4AWMYBWj1/z+uFpMTmn8AuGMCD9ugRsIAAAAASUVORK5CYII=");
        File.WriteAllBytes(path, png);
        MediaAssetReference asset = new("picture", "picture", "original", file, path, png.Length,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)), "", file, true);
        MediaAssetSnapshot manifest = new(directory, MediaManifestStatus.Available, 1, "fixture", [asset]);
        return new(new(new("fixture", "g", "42"), directory, asset.AssetId), manifest, asset, null, null, false, false);
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

    private static void VerifyPopup(Window window, Button opener, Func<bool> isOpen)
    {
        Assert.False(opener.Focusable);
        Assert.False(isOpen());
        Click(window, opener);
        Assert.True(isOpen());
        Assert.Equal(Texts.T("shortcuts.group.expanded"), AutomationProperties.GetItemStatus(opener));
        Click(window, opener);
        Assert.False(isOpen());
        Click(window, opener);
        Assert.True(isOpen());
        PressAtFocus(window, Key.Escape);
        Assert.False(isOpen());
        Assert.Equal(Texts.T("shortcuts.group.collapsed"), AutomationProperties.GetItemStatus(opener));
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
        Press(root, key, modifiers);
        Layout(window);
    }

    private static void Press(TopLevel root, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        root.KeyPress(key, modifiers, PhysicalKey.None, null);
        root.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Layout(root);
    }

    private static void Layout(TopLevel root)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
    }
}
