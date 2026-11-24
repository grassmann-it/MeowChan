using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Stats;
using MeowChan.Desktop.Updates;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class FooterDisplayInteractionTests
{
    [Fact]
    public async Task OlderProfilesUseDefaultsAndRestartKeepsOnlyThatProfilesChoices()
    {
        using TempDirectory profile = new();
        using TempDirectory otherProfile = new();
        File.WriteAllText(Path.Combine(profile.Path, "settings.json"), JsonSerializer.Serialize(new
        {
            RecheckTimer = 120, FirstRunSetupVersion = 1, DownloadPath = Path.Combine(profile.Path, "media"),
        }));
        FooterDisplayPreferences defaults = new(false, false, true);
        Assert.Equal(defaults, FooterDisplayPreferences.FromConfig(Config.LoadConfig(profile.Path)));
        Assert.Equal(defaults, FooterDisplayPreferences.FromConfig(Config.LoadConfig(otherProfile.Path)));

        await using (DesktopSessionHost host = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1")))
        {
            await host.StartAsync();
            Config draft = host.Config.Clone();
            draft.ShowFooterDisk = true;
            await host.SaveViewPreferencesAsync(config => { config.ShowFooterNetwork = true; config.ShowFooterStatus = false; });
            await host.ApplySettingsAsync(draft);
            Assert.Equal(new FooterDisplayPreferences(true, true, false), FooterDisplayPreferences.FromConfig(host.Config));
            Assert.Equal(new FooterDisplayPreferences(true, true, false),
                FooterDisplayPreferences.FromConfig(SettingsTransfer.CreatePortableSnapshot(host.Config)));
        }

        await using (DesktopSessionHost restarted = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1")))
        {
            await restarted.StartAsync();
            Assert.Equal(new FooterDisplayPreferences(true, true, false), FooterDisplayPreferences.FromConfig(restarted.Config));
        }
        Assert.Equal(defaults, FooterDisplayPreferences.FromConfig(Config.LoadConfig(otherProfile.Path)));
    }

    [Fact]
    public void FooterGesturesControlIndependentDisplaysAndPreserveChildActions() => WithWindow(async (window, session, source, clock) =>
    {
        Border navigation = Find<Border>(window, "WorkspaceNavigation");
        Border strip = Find<Border>(window, "WorkspaceStatusBar");
        TextBlock network = Find<TextBlock>(window, "FooterNetworkRate");
        TextBlock disk = Find<TextBlock>(window, "FooterDiskRate");
        TextBlock status = Find<TextBlock>(window, "WorkspaceStatus");
        TextBlock counts = Find<TextBlock>(window, "WorkspaceCounts");
        TextBlock warning = Find<TextBlock>(window, "WorkspaceStoreWarning");
        ShrinkRowPanel row = Assert.IsType<ShrinkRowPanel>(strip.Child);
        Assert.Collection(row.Children,
            item => Assert.Same(status, item),
            item => Assert.Equal("UpdateNoticeButton", Assert.IsType<Button>(item).Name),
            item => Assert.Same(warning, Assert.IsType<Button>(item).Content),
            item => Assert.Same(counts, item),
            item => Assert.Same(network, item),
            item => Assert.Same(disk, item));
        Assert.All(new[] { status, counts, warning, network, disk },
            item => Assert.Same(TextTrimming.CharacterEllipsis, item.TextTrimming));
        Assert.True(status.IsVisible && counts.IsVisible);
        Assert.False(network.IsVisible || disk.IsVisible);
        Assert.Equal(IoStatsVisibility.None, window.IoStatsClaim);
        double defaultCountsRight = BoundsIn(counts, window).Right;

        Click(window, FreeSpace(strip, window), MouseButton.Right);
        AssertChecks(strip, false, false, true);
        Choose(strip, "footer.network");
        await BooruTestSupport.WaitAsync(() => network.IsVisible, "network in footer");
        Layout(window);
        Assert.True(network.Bounds.Width > 0);
        Assert.True(BoundsIn(counts, window).Right < defaultCountsRight);
        Assert.False(disk.IsVisible);
        Assert.Equal(IoStatsVisibility.Window, window.IoStatsClaim);
        source.Add("fixture", net: 2 * 1024 * 1024);
        clock.Advance(TimeSpan.FromSeconds(1));
        await BooruTestSupport.WaitAsync(() => network.Text?.Contains("2 MiB/s", StringComparison.Ordinal) == true, "network sample in footer");

        Point freeNavigation = FreeSpace(navigation, window);
        Click(window, freeNavigation, MouseButton.Left, RawInputModifiers.Control);
        Assert.Null(navigation.ContextMenu);
        window.UseMacFooterContextClick = true;
        Click(window, freeNavigation, MouseButton.Left, RawInputModifiers.Control);
        AssertChecks(navigation, true, false, true);
        Choose(navigation, "footer.disk");
        await BooruTestSupport.WaitAsync(() => disk.IsVisible, "disk in footer");
        Assert.True(network.IsVisible && counts.IsVisible);
        source.Add("fixture", diskRead: 4 * 1024 * 1024);
        clock.Advance(TimeSpan.FromSeconds(1));
        await BooruTestSupport.WaitAsync(() => disk.Text?.Contains("4 MiB/s", StringComparison.Ordinal) == true, "disk sample in footer");
        Assert.Equal(IoStatsDisplay.HeaderGraphOff, session.Config.StatsHeaderGraph);

        foreach (double width in new[] { 760d, 1400d })
        {
            BooruReviewTestPlatform.Resize(window, width);
            Rect available = BoundsIn(strip, window);
            Rect[] visible = new[] { status, counts, network, disk }.Select(item => BoundsIn(item, window)).ToArray();
            Assert.All(visible, bounds => Assert.True(bounds.Width > 0 && available.Contains(bounds)));
            for (int index = 1; index < visible.Length; index++)
                Assert.True(visible[index - 1].Right <= visible[index].Left);
        }

        OpenWithKey(window, navigation, Key.F10, PhysicalKey.F10, RawInputModifiers.Shift);
        AssertChecks(navigation, true, true, true);
        Choose(navigation, "footer.status");
        await BooruTestSupport.WaitAsync(() => !status.IsVisible && !counts.IsVisible, "status and counts hidden");
        Layout(window);
        Assert.True(network.IsVisible && disk.IsVisible);
        Assert.Equal(BoundsIn(strip, window).Left + strip.Padding.Left, BoundsIn(network, window).Left, precision: 1);
        Assert.True(navigation.Bounds.Height > 0);

        Button media = Find<Button>(window, "MediaNavigationButton");
        Point mediaCenter = BoundsIn(media, window).Center;
        Click(window, mediaCenter, MouseButton.Right);
        Assert.Null(navigation.ContextMenu);
        Assert.Equal(MainWindow.WorkspacePage.Downloads, window.SelectedPage);
        ContextMenu childMenu = new() { Items = { new MenuItem { Header = "Child action" } } };
        media.ContextMenu = childMenu;
        Click(window, mediaCenter, MouseButton.Right);
        Assert.True(childMenu.IsOpen);
        Assert.Null(navigation.ContextMenu);
        childMenu.Close();
        media.ContextMenu = null;
        Click(window, mediaCenter, MouseButton.Left);
        Assert.Equal(MainWindow.WorkspacePage.Media, window.SelectedPage);

        window.MediaPageForTests!.SetFocusMode(true, announce: false);
        Layout(window);
        Assert.False(strip.IsVisible || navigation.IsVisible);
        Assert.Equal(IoStatsVisibility.None, window.IoStatsClaim);
        window.MediaPageForTests.SetFocusMode(false, announce: false);
        Layout(window);
        Assert.True(strip.IsVisible && navigation.IsVisible);
        Assert.Equal(IoStatsVisibility.Window, window.IoStatsClaim);

        var unavailable = new GalleryEntryKey(new(new ResourceId("offline", "gallery", "1"), session.ProfileDirectory, "missing"), null, null);
        await window.OpenMediaGalleryAsync(Array.Empty<GalleryEntry>(), unavailable);
        Layout(window);
        Assert.True(Find<ContentControl>(window, "GalleryHost").IsVisible);
        Assert.Equal(IoStatsVisibility.None, window.IoStatsClaim);
        Click(window, FreeSpace(navigation, window), MouseButton.Right);
        Assert.Null(navigation.ContextMenu);
    });

    [Fact]
    public void EmptyFooterCanBeReopenedInEveryLanguageAndResetThroughSettings() => WithWindow(async (window, session, _, _) =>
    {
        Border navigation = Find<Border>(window, "WorkspaceNavigation");
        Border strip = Find<Border>(window, "WorkspaceStatusBar");
        Grid root = Find<Grid>(window, "WorkspaceRoot");
        OpenWithKey(window, navigation, Key.Apps, PhysicalKey.ContextMenu);
        Choose(navigation, "footer.status");
        await BooruTestSupport.WaitAsync(() => !strip.IsVisible, "empty strip collapsed");
        Layout(window);
        Assert.Equal(0, root.RowDefinitions[2].ActualHeight);
        Assert.True(navigation.Bounds.Height > 0);
        Assert.Equal(new FooterDisplayPreferences(false, false, false), FooterDisplayPreferences.FromConfig(Config.LoadConfig(session.ProfileDirectory)));

        foreach (string code in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(code);
            Layout(window);
            Assert.Equal(code, Texts.Language);
            OpenWithKey(window, navigation, Key.F10, PhysicalKey.F10, RawInputModifiers.Shift);
            AssertChecks(navigation, false, false, false);
            Assert.Equal(Texts.T("settings.footer.displays"), AutomationProperties.GetName(navigation.ContextMenu!));
            Assert.Equal(new[] { IoStatsDisplay.NetworkTitle(), IoStatsDisplay.DiskTitle(), Texts.T("settings.footer.statusLines") },
                navigation.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()));
            navigation.ContextMenu!.Close();
            Layout(window);
        }
        Texts.SetLanguage("en");

        // With the status display disabled, a failed preference write still reaches
        // the shell's regular error UI and leaves the confirmed choice in place.
        session.BeforeFinalWriteIoForTests = unit =>
        {
            if (unit == "view") throw new IOException("Footer preference fixture");
        };
        try
        {
            Click(window, FreeSpace(navigation, window), MouseButton.Right);
            Choose(navigation, "footer.status");
            Control banner = Find<Control>(window, "ShellActionErrorBanner");
            await BooruTestSupport.WaitAsync(() => banner.IsVisible, "footer preference error banner");
            Assert.False(strip.IsVisible);
        }
        finally { session.BeforeFinalWriteIoForTests = null; }

        Find<Button>(window, "SettingsNavigationButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SettingsEditor editor = window.SettingsEditor;
        Assert.True(window.SettingsPageVisible);
        Assert.False(editor.ShowFooterStatus);
        editor.ResetDraft(SettingsEditor.SettingsResetScope.Interface);
        Assert.True(editor.ShowFooterStatus);
        Assert.False(editor.ShowFooterNetwork || editor.ShowFooterDisk);
        Find<Button>(editor, "SaveSettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await BooruTestSupport.WaitAsync(() => session.Config.ShowFooterStatus && strip.IsVisible && !editor.IsBusy, "reset footer displays saved");
        Assert.Equal(new FooterDisplayPreferences(false, false, true), FooterDisplayPreferences.FromConfig(Config.LoadConfig(session.ProfileDirectory)));
    });

    private static void WithWindow(Func<MainWindow, DesktopSessionHost, IoFakeSource, IoManualClock, Task> test) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "media"), Language = "en", WorkspaceMode = "Advanced",
                FirstRunSetupVersion = 1, WindowWidth = 1400, WindowHeight = 740, Animations = "Off",
                StatsHeaderGraph = IoStatsDisplay.HeaderGraphOff, StatsTrayTooltip = false, StatsTrayMenu = false,
                StatsSampleIntervalMs = 1000, ShowErrorNotifications = false,
                ShortcutBindings = new ShortcutBindingsSection().Set("menu.context-open", [ShortcutGesture.Parse("Alt+F10")])
            }.SaveConfig(profile.Path);
            try { BooruComponentDispatcher.Wait(Exercise()); }
            finally { Texts.Initialize(); Texts.SetLanguage("en"); }

            async Task Exercise()
            {
                IoManualClock clock = new(DateTimeOffset.UnixEpoch);
                IoFakeSource source = new();
                using IoStatsHub hub = new(source, clock);
                await using DesktopSessionHost session = new(profile.Path, [],
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"), ioStats: hub);
                await using MainWindow window = new(session) { Classes = { MotionPolicy.OffClass } };
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await window.StartupTimingRestoreTask;
                Assert.True(session.State == DesktopSessionState.Ready, session.StartupError);
                Layout(window);
                await test(window, session, source, clock);
            }
        });

    private static void OpenWithKey(Window window, Control anchor, Key key, PhysicalKey physicalKey,
        RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Assert.True(anchor.Focus(NavigationMethod.Tab));
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Layout(window);
        Assert.True(anchor.ContextMenu?.IsOpen);
    }

    private static void AssertChecks(Control anchor, params bool[] states)
    {
        Assert.True(anchor.ContextMenu?.IsOpen);
        MenuItem[] items = anchor.ContextMenu!.Items.OfType<MenuItem>().ToArray();
        Assert.All(items, item => Assert.Equal(MenuItemToggleType.CheckBox, item.ToggleType));
        Assert.Equal(new[] { "footer.network", "footer.disk", "footer.status" }, items.Select(item => item.Tag as string));
        Assert.Equal(states, items.Select(item => item.IsChecked));
    }

    private static void Choose(Control anchor, string id)
    {
        MenuItem item = Assert.Single(anchor.ContextMenu!.Items.OfType<MenuItem>(), item => Equals(item.Tag, id));
        TopLevel top = TopLevel.GetTopLevel(item)!;
        Click(top, BoundsIn(item, top).Center, MouseButton.Left);
    }

    private static Point FreeSpace(Control control, Window window) =>
        control.TranslatePoint(new Point(6, control.Bounds.Height / 2), window)!.Value;

    private static Rect BoundsIn(Control control, Visual root) =>
        new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);

    private static T Find<T>(Control parent, string name) where T : Control => parent.FindControl<T>(name)!;

    private static void Click(TopLevel top, Point point, MouseButton button, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        top.MouseDown(point, button, modifiers);
        top.MouseUp(point, button, modifiers);
        Layout(top);
    }

    private static void Layout(TopLevel top)
    {
        Dispatcher.UIThread.RunJobs();
        top.UpdateLayout();
    }
}
