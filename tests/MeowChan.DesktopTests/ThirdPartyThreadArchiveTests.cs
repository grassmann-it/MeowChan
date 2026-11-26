using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using Semi.Avalonia.DataGrid;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class ThirdPartyThreadArchiveTests
{
    private const string ActionId = "location.open-third-party-archive";
    private const string SourceId = "location.open-source";
    private static readonly ResourceId Supported = new("4chan", "g", "7300");
    private static readonly Uri Target = new("https://desuarchive.org/g/thread/7300/");

    [Theory]
    [InlineData("g", "desuarchive.org")]
    [InlineData("tg", "archive.4plebs.org")]
    [InlineData("vg", "arch.b4k.dev")]
    [InlineData("vt", "archive.palanq.win")]
    [InlineData("wg", "archived.moe")]
    public void KnownBoardsResolveToTheirCanonicalHttpsThreadPage(string board, string host) =>
        Assert.Equal(new Uri($"https://{host}/{board}/thread/7300/"),
            ThirdPartyThreadArchiveMenu.TargetFor(new ResourceId("4chan", board, "7300")));

    [Theory]
    [InlineData("8kun", "g", "7300")]
    [InlineData("4chan", "unmapped", "7300")]
    [InlineData("4chan", "g", null)]
    [InlineData("4chan", "g", "")]
    [InlineData("4chan", "g", "7300?redirect=https://other.invalid")]
    [InlineData("4chan", "g", "７３００")]
    public void UnmappedOrInvalidIdentitiesDoNotResolve(string source, string board, string? thread) =>
        Assert.Null(ThirdPartyThreadArchiveMenu.TargetFor(new ResourceId(source, board, thread)));

    [Fact]
    public void DownloadsOpenOnlyTheMenuTargetOnExplicitActivation() => WithWindow(fixture =>
    {
        List<Uri> opened = [];
        fixture.Window.ThirdPartyArchiveLauncherForTests = opened.Add;
        DesktopThreadEntry first = DownloadWindowFixture.Thread(1, url: "https://fixture.invalid/irrelevant");
        DesktopThreadEntry second = DownloadWindowFixture.Thread(2);
        fixture.ShowThreads([first with { Snapshot = first.Snapshot with { Id = Supported } }, second]);
        DataGrid grid = Find<DataGrid>(fixture.Window, "ThreadsDataGrid");
        fixture.Select(1);
        ContextMenu menu = OpenListMenu(fixture, grid);
        MenuItem action = AssertArchiveAction(menu);
        Assert.Empty(opened);
        menu.Close();

        fixture.Select(2);
        action.Command!.Execute(action.CommandParameter);
        Assert.Empty(opened);
        ContextMenu unsupported = OpenListMenu(fixture, grid);
        AssertAbsent(unsupported);
        unsupported.Close();

        fixture.Select(1);
        ActivateByKeyboard(fixture, AssertArchiveAction(OpenListMenu(fixture, grid)));
        Assert.Equal(Target, Assert.Single(opened));
        return Task.CompletedTask;
    });

    [Fact]
    public void ArchiveTableResolvesTheLeadAndKeepsTheTipWhenTheMenuRefreshes() => WithWindow(fixture =>
    {
        List<Uri> opened = [];
        fixture.Window.ThirdPartyArchiveLauncherForTests = opened.Add;
        ArchivePage page = GoToArchive(fixture);
        page.ArchiveLayoutPreference = "Table";
        DataGrid grid = Find<DataGrid>(page, "ArchiveThreadTable");
        ArchiveThreadRow supported = new(View(Supported).Summary);
        ArchiveThreadRow unsupported = new(View(Supported with { Board = "unmapped" }).Summary);
        grid.ItemsSource = new[] { supported, unsupported };
        grid.SelectedItem = unsupported;
        fixture.Layout();
        ContextMenu otherMenu = OpenListMenu(fixture, grid);
        AssertAbsent(otherMenu);
        otherMenu.Close();

        grid.SelectedItem = supported;
        ContextMenu menu = OpenListMenu(fixture, grid);
        MenuItem action = AssertArchiveAction(menu);
        Assert.Empty(opened);
        Texts.SetLanguage("de");
        try
        {
            CommandBindings.Refresh(action);
            Assert.Equal("Im Drittarchiv öffnen", action.Header);
            Assert.Equal("Öffnet desuarchive.org im Standardbrowser. MeowChan selbst ruft dort nichts ab.", ToolTip.GetTip(action));
            Assert.Equal(ToolTip.GetTip(action), AutomationProperties.GetHelpText(action));
            action.Command!.Execute(action.CommandParameter);
            Assert.Equal(Target, Assert.Single(opened));
        }
        finally
        {
            menu.Close();
            Texts.SetLanguage("en");
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReaderOpensTheCurrentThreadOnlyFromItsThreadMenu(bool detached) => WithWindow(async fixture =>
    {
        List<Uri> opened = [];
        fixture.Window.ThirdPartyArchiveLauncherForTests = opened.Add;
        ArchivePage page = GoToArchive(fixture);
        await page.LoadThreadAsync(Supported, preparedThread: View(Supported));
        if (detached) page.OpenReaderRequested?.Invoke(null);
        fixture.Layout();

        ContextMenu? shown = null;
        page.ShowThreadMenu = (menu, origin) =>
        {
            shown = menu;
            menu.Open(origin);
        };
        ListBox posts = Find<ListBox>(fixture.Window, "ArchivePosts");
        Assert.True(posts.IsKeyboardFocusWithin || posts.Focus());
        Press(fixture, Assert.IsType<KeyGesture>(CommandBindings.Gesture("reader.thread-menu")));
        ContextMenu threadMenu = Assert.IsType<ContextMenu>(shown);
        MenuItem action = AssertArchiveAction(threadMenu);
        Assert.Empty(opened);
        ActivateByKeyboard(fixture, action);
        Assert.Equal(Target, Assert.Single(opened));

        ResourceId unsupported = Supported with { Source = "8kun" };
        await page.LoadThreadAsync(unsupported, preparedThread: View(unsupported));
        AssertAbsent(page.BuildReaderThreadMenu());
        Assert.Single(opened);
    });

    [Fact]
    public void MacArchiveActionAddsNoShortcutToTheExistingMenuGestures()
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(platform: ShortcutPlatform.MacOS);
        Assert.DoesNotContain(CommandCatalog.All, command => command.Id == ActionId);
        Assert.Equal(new KeyGesture(Key.F10, KeyModifiers.Shift),
            Assert.Single(bindings.For("menu.context-open")).Resolve(ShortcutPlatform.MacOS));
        Assert.Equal(new KeyGesture(Key.J, KeyModifiers.Meta | KeyModifiers.Shift),
            Assert.Single(bindings.For("reader.thread-menu")).Resolve(ShortcutPlatform.MacOS));
    }

    private static ArchivedThreadView View(ResourceId id) => new(
        new ArchiveThreadSummary(id, "https://fixture.invalid/irrelevant", "Fixture", "", ThreadSourceState.NotFound,
            true, DateTimeOffset.UnixEpoch, null, 1, 1),
        [new CapturedPost(id.Thread!, 1, PostFieldPresence.All, Html: "fixture post")], decimalPostOrder: false);

    private static void WithWindow(Func<DownloadWindowFixture, Task> test) => DownloadWindowFixture.Run(test,
        () => Application.Current!.Styles.Insert(1, new DataGridSemiTheme()));

    private static ArchivePage GoToArchive(DownloadWindowFixture fixture)
    {
        var navigation = Find<Button>(fixture.Window, "ArchiveNavigationButton");
        navigation.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        fixture.Layout();
        ArchivePage page = fixture.Window.ArchivePage;
        Assert.True(page.IsEffectivelyVisible);
        return page;
    }

    private static ContextMenu OpenListMenu(DownloadWindowFixture fixture, DataGrid grid)
    {
        Assert.True(grid.Focus());
        Press(fixture, new KeyGesture(Key.F10, KeyModifiers.Shift));
        ContextMenu menu = Assert.IsType<ContextMenu>(grid.ContextMenu);
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuItem AssertArchiveAction(ContextMenu menu)
    {
        Control[] entries = menu.Items.OfType<Control>().ToArray();
        var action = Assert.IsType<MenuItem>(Assert.Single(entries, item => AutomationId(item) == ActionId));
        int source = Array.IndexOf(entries, Assert.Single(entries, item => AutomationId(item) == SourceId));
        Assert.Same(action, entries[source + 1]);
        Assert.Equal("Open in third-party archive", action.Header);
        Assert.Equal(action.Header, AutomationProperties.GetName(action));
        Assert.Equal("Opens desuarchive.org in your default browser. MeowChan itself requests nothing there.", ToolTip.GetTip(action));
        Assert.Equal(ToolTip.GetTip(action), AutomationProperties.GetHelpText(action));
        Assert.Equal("external-link", MenuIcons.KeyOf(action));
        Assert.Null(action.InputGesture);
        Assert.True(action.Command?.CanExecute(action.CommandParameter));
        return action;
    }

    private static void AssertAbsent(ContextMenu menu) =>
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => AutomationId(item) == ActionId);

    private static void ActivateByKeyboard(DownloadWindowFixture fixture, MenuItem item)
    {
        Assert.True(item.Focus());
        Press(fixture, new KeyGesture(Key.Enter));
    }

    private static void Press(DownloadWindowFixture fixture, KeyGesture gesture)
    {
        MainWindow window = fixture.Window;
        TopLevel receiver = window.FocusManager?.GetFocusedElement() is Visual focused
            ? TopLevel.GetTopLevel(focused) ?? window : window;
        receiver.KeyPress(gesture.Key, (RawInputModifiers)gesture.KeyModifiers, PhysicalKey.None, null);
        (receiver.PlatformImpl != null ? receiver : window).KeyRelease(
            gesture.Key, (RawInputModifiers)gesture.KeyModifiers, PhysicalKey.None, null);
        fixture.Layout();
    }

    private static T Find<T>(Visual root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => AutomationId(control) == id);

    private static string? AutomationId(Control control) =>
        ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId();
}
