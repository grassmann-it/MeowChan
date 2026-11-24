using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.Desktop.Stats;
using MeowChan.Desktop.Updates;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginSdk;
using Xunit.Sdk;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BuiltMenuIconAuditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChecksKeepTheirActionMaskAndNestedChoicesDeclareExceptions(bool isChecked) => Run(() =>
    {
        var choices = new ContextMenuBuilder().Radio("order", "first", "First", true, () => { });
        var builder = new ContextMenuBuilder().Check("enabled", "Enabled", isChecked, () => { }, icon: "activity")
            .Submenu("sort", "Sort", choices.ToModel(), icon: "list");
        MenuItem[] items = builder.Build()!.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(isChecked, items[0].IsChecked);
        MenuIconAudit.Verify("test", items, new MenuIconAudit.Exception("test", "sort/first", "Sort value."));
    });

    [Theory]
    [InlineData("missing", null, null, "missing action icon")]
    [InlineData("blank", null, " ", "missing exception reason")]
    [InlineData("stale", "activity", "Previously absent.", "stale exceptions")]
    [InlineData("exists", "activity", "Previously absent.", "exception has an icon now")]
    [InlineData("unknown", "does-not-exist", null, "unknown mask")]
    public void BadEntriesAndExceptionsReportTheirAddress(string kind, string? key, string? reason, string message) => Run(() =>
    {
        var menu = new ContextMenuBuilder().Item("row", "Action", () => { }, icon: key).Build()!;
        MenuIconAudit.Exception[] exceptions = reason == null ? []
            : [new("test", kind == "stale" ? "old-row" : "row", reason)];
        var failure = Assert.ThrowsAny<XunitException>(() => MenuIconAudit.Verify("test", menu.Items.OfType<Control>(), exceptions));
        Assert.Contains("test", failure.Message);
        Assert.Contains(kind == "stale" ? "old-row" : "row", failure.Message);
        Assert.Contains(message, failure.Message);
    });

    [Fact]
    public void AShownFlyoutRequiresTheActionMaskEvenWithAnUncheckedMark() => Run(() =>
    {
        string? key = null;
        bool isChecked = false;
        var panel = new LayoutMenuPanel("FixtureLayout",
            () => new("", [new(LayoutMenuRowKind.Toggle, "overlay", "Overlay") { Checked = isChecked, IconKey = key }]),
            _ => isChecked = !isChecked);
        var flyout = new Flyout { Content = panel };
        var anchor = new Button { Content = "Layout", Flyout = flyout };
        using var window = new HostWindow(anchor);
        panel.Refresh();
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        var failure = Assert.ThrowsAny<XunitException>(() => MenuIconAudit.Verify("fixture-layout", panel.Buttons));
        Assert.Contains("fixture-layout/overlay", failure.Message);

        key = "columns";
        foreach (bool value in new[] { false, true })
        {
            isChecked = value;
            panel.Refresh();
            Dispatcher.UIThread.RunJobs();
            MenuIconAudit.Verify("fixture-layout", panel.Buttons);
        }
        flyout.Hide();
    });

    [Fact]
    public void HeaderGraphAndFollowSystemShowActionAndChoiceRowsAcrossLanguages() => Run(() =>
    {
        foreach (var language in Texts.Languages)
        {
            Texts.SetLanguage(language.Code);
            foreach (string preference in new[] { "System", "Dark" })
            {
                string? selected = null;
                var menu = HeaderThemeMenu.Compose(preference, true, value => selected = value).Build()!;
                MenuIconAudit.Verify("follow-system", menu.Items.OfType<Control>());
                MenuItem row = Assert.Single(menu.Items.OfType<MenuItem>());
                Assert.Equal(preference == "System", row.IsChecked);
                Assert.Equal(Texts.T("shell.theme.followSystem"), row.Header);
                Assert.Equal("monitor", MenuIcons.KeyOf(row));
                row.Command!.Execute(null);
                Assert.Equal(preference == "System" ? "Dark" : "System", selected);
            }
        }
        var graph = HeaderIoMenu.Compose(null, _ => { }, () => { }, () => { }).Build()!;
        MenuIconAudit.Verify("header-io", graph.Items.OfType<Control>(), IoStatsDisplay.HeaderGraphChoices
            .Select(value => new MenuIconAudit.Exception("header-io", HeaderIoMenu.EntryId(value), "Graph arrangement value.")).ToArray());
    });

    [Fact]
    public void ShellTaglineAndTrayRetainIconsAndActions() => Run(() => BooruComponentDispatcher.Wait(ExerciseShellAsync()));

    private static async Task ExerciseShellAsync()
    {
        using TempDirectory profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "Downloads"), Language = "en", Theme = "Dark",
            FirstRunSetupVersion = 1, HeaderTagline = TaglinePicker.ModeFixed, HeaderTaglineFixedId = "cats",
            AutomaticUpdateChecks = false
        }.SaveConfig(profile.Path);
        using var updates = new UpdateServiceTests.FakeHandler { Body = UpdateServiceTests.Release("9.9.9") };
        await using DesktopSessionHost session = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, new HttpClient(updates), disableEnvironment: () => null));
        await using MainWindow window = new(session);
        window.Show();
        await window.StartupTimingRestoreTask;
        BooruReviewTestPlatform.Resize(window, 1700);
        var subtitle = window.FindControl<TextBlock>("WorkspaceSubtitle")!;
        var menu = subtitle.ContextMenu!;
        MenuIconAudit.Verify("tagline", menu.Items.OfType<Control>());
        MenuItem another = Assert.Single(menu.Items.OfType<MenuItem>(), row => Equals(row.Tag, "tagline.another"));
        Assert.Equal("shuffle", MenuIcons.KeyOf(another));
        Assert.True(another.IsEnabled);
        Assert.True(another.Command!.CanExecute(null));
        string before = subtitle.Text!;
        another.Command.Execute(null);
        Assert.NotEqual(before, subtitle.Text);

        MenuItem appMenu = window.FindControl<MenuItem>("WorkspaceMenuItem")!;
        MenuItem theme = Assert.Single(appMenu.Items.OfType<MenuItem>(), row => row.Name == "ThemeMenuItem");
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Assert.Equal("sun", MenuIcons.KeyOf(theme));
        window.RequestedThemeVariant = ThemeVariant.Light;
        Assert.Equal("moon", MenuIcons.KeyOf(theme));
        Assert.Equal(Texts.T("shell.switchToDark"), theme.Header);

        // Exercise the window's native projection with an in-process tray, regardless of system tray support.
        TrayIcon tray = (TrayIcon?)Field(typeof(MainWindow), "_trayIcon").GetValue(window) ?? new TrayIcon();
        if (tray.Menu == null)
        {
            Field(typeof(MainWindow), "_trayIcon").SetValue(window, tray);
            typeof(MainWindow).GetMethod("ConfigureTrayIcon", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [tray]);
        }
        var stats = (TrayStatsPresenter)Field(typeof(MainWindow), "_trayStats").GetValue(window)!;
        var update = (NativeMenuItem)Field(typeof(MainWindow), "_trayUpdateItem").GetValue(window)!;
        Assert.False(update.IsVisible);
        Assert.NotNull(update.Icon);
        Assert.Null(update.Gesture);
        await session.Updates.CheckAsync();
        await BooruTestSupport.WaitAsync(() => update.IsVisible, "available update in the tray");
        Assert.Equal(Texts.T("updates.available", "9.9.9"), update.Header);
        Texts.SetLanguage("de");
        await BooruTestSupport.WaitAsync(() => Equals(update.Header, Texts.T("updates.available", "9.9.9")), "localized tray reminder");
        await BooruTestSupport.WaitAsync(() => Equals(another.Header, Texts.T("shell.tagline.menuAnother")), "localized tagline");
        var settings = Assert.Single(menu.Items.OfType<MenuItem>(), row => Equals(row.Tag, "tagline.settings"));
        Assert.Equal(CommandCatalog.Label("navigation.settings"), settings.Header);
        var ids = new Dictionary<NativeMenuItem, string>
        {
            [Item("_trayOpenItem")] = "app.show",
            [Item("_trayFolderItem")] = "location.open-folder",
            [Item("_clipboardMenuItem")] = "watch.import-clipboard",
            [Item("_trayPauseItem")] = "session.pause",
            [update] = "updates.check",
            [Item("_trayStatusItem")] = "navigation.status",
            [Item("_traySettingsItem")] = "navigation.settings",
            [Item("_trayExitItem")] = "app.quit",
            [stats.RatesItem] = "rate",
            [stats.DiskItem] = "disk",
            [stats.DetailItem] = "detail"
        };
        MenuIconAudit.VerifyNative("tray", tray.Menu!.Items.OfType<NativeMenuItem>()
                .Select((item, index) => (ids.GetValueOrDefault(item, "row." + index), item)),
            new("tray", "rate", "Live rate summary and a link to statistics."),
            new("tray", "disk", "Live disk summary and a link to statistics."),
            new("tray", "detail", "Live activity summary and a link to statistics."));

        NativeMenuItem Item(string field) => Assert.IsType<NativeMenuItem>(Field(typeof(MainWindow), field).GetValue(window));
    }

    [Fact]
    public void PluginWatchSourcesUseTheSameMenuAndDispatchFromBothEntrances() => Run(() =>
    {
        PluginsPage page = new();
        using var window = new HostWindow(page);
        try
        {
            var schema = new SettingsSchema("1", []);
            SourceDescription Alpha(string id) => new(id, id, new Uri("https://fixture.invalid"), SourceCapabilities.None, [], [], schema);
            var description = new PluginDescription("fixture", "Fixture", "1", "1", "", [Alpha("alpha"), Alpha("beta")], [], schema, []);
            var candidate = new PluginCandidateSnapshot("fixture", "fixture", null, description, true, null);
            var entry = new PluginCatalogEntry("fixture", "fixture", candidate.CandidateId, null, description, null, candidate, []);
            Field(typeof(PluginsPage), "_catalog").SetValue(page, new PluginCatalogSnapshot(new(0, [], []), [entry], []));
            Field(typeof(PluginsPage), "_selectedKey").SetValue(page, entry.Key);
            List<string> opened = [];
            page.OpenSourceWatches = opened.Add;
            Button origin = page.FindControl<Button>("PluginsAdd")!;
            page.OpenPluginActionMenu(entry.Key, origin);
            ContextMenu actionMenu = Assert.IsType<ContextMenu>(page.PluginActionMenu);
            MenuIconAudit.Verify("plugins", actionMenu.Items.OfType<Control>(),
                new MenuIconAudit.Exception("plugins", "row.3/row.0", "Explanation of the unavailable activation action."));
            MenuItem watches = Assert.Single(actionMenu.Items.OfType<MenuItem>(), row => Equals(row.Tag, "plugins.watches"));
            MenuItem[] sources = watches.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(2, sources.Length);
            foreach (MenuItem source in sources) Assert.Equal(MenuIcons.KeyOf(watches), MenuIcons.KeyOf(source));
            sources[1].Command!.Execute(null);
            Assert.Equal(new[] { "beta" }, opened);

            BooruComponentDispatcher.Wait(page.ExecuteSelectedPluginActionAsync("plugins.watches"));
            ContextMenu sourcesMenu = Assert.IsType<ContextMenu>(page.PluginActionMenu);
            MenuIconAudit.Verify("plugin-sources", sourcesMenu.Items.OfType<Control>());
            MenuItem first = Assert.IsType<MenuItem>(sourcesMenu.Items[0]);
            Assert.Equal("alpha · alpha", Assert.IsType<TextBlock>(first.Header).Text);
            first.Command!.Execute(null);
            Assert.Equal(new[] { "beta", "alpha" }, opened);
        }
        finally { window.Close(); page.EndLifetime(); }
    });

    private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        test();
    });

    private sealed class HostWindow : Window, IDisposable
    {
        internal HostWindow(Control content)
        {
            Content = content;
            Width = 1600;
            Height = 900;
            Show();
            Activate();
            Dispatcher.UIThread.RunJobs();
            UpdateLayout();
        }

        public void Dispose()
        {
            if (IsVisible) Close();
        }
    }
}
