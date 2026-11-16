using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using System.Reflection;

namespace MeowChan.DesktopTests;

/// <summary>
/// Sweep S2 (16.11.2026), findings F1, F2, F3 and F5: a menu that a click handler builds and assigns to <c>X.ContextMenu</c> must not stay on
/// X after it closed (a later right-click or Menu key would open it unrebuilt, with the old thread, table, scope or checkmarks); the speed menu
/// opened by its command must be composed first (<c>ContextMenu.Open(control)</c> does not raise <c>Opening</c>); a right-click on a Downloads
/// column header is no empty-area click and keeps the row selection.
/// The archive page is built without a window and its handlers are driven for real. The video player, the main window and the scope chip need
/// a platform (LibVLC, the shell, an open popup), so their contract is checked on the source; the behaviour is the headless smoke.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class MenuAttachmentAndSpeedRefreshTests
{
    private static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static string Body(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, startMarker);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, endMarker);
        return source[start..end];
    }

    private sealed class TokenApplication : Application
    {
        public override void Initialize() =>
            Styles.Add(new StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
    }

    private static void WithPage(Action<ArchivePage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
        ArchivePage page = new();
        try { test(page); }
        finally { (page as IDisposable)?.Dispose(); }
    });

    private static void Invoke(ArchivePage page, string method, object sender) =>
        typeof(ArchivePage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, new object?[] { sender, new RoutedEventArgs() });

    private static void Close(ContextMenu menu) => menu.RaiseEvent(new RoutedEventArgs(MenuBase.ClosedEvent) { Source = menu });

    // ---- F2: the reader's Thread menu -------------------------------------------------------------------------------------

    [Fact]
    public void TheReaderThreadMenuStaysOnItsButtonWhileOpenAndIsGoneOnceClosedSoNoLaterOpenReusesTheOldThread() => WithPage(page =>
    {
        List<ContextMenu> shown = new();
        page.ShowThreadMenu = (menu, _) => shown.Add(menu);
        Button thread = new();

        Invoke(page, "OnReaderThreadMenuClick", thread);
        ContextMenu first = Assert.Single(shown);
        Assert.Same(first, thread.ContextMenu); // attached while open: the toolbar overflow keeps its popup open while the button's menu is open

        Close(first);
        Assert.Null(thread.ContextMenu); // another thread opened in the reader: a right-click must not bring the first one's captured menu back

        // The next click composes a fresh menu for what is open now.
        Invoke(page, "OnReaderThreadMenuClick", thread);
        Assert.Equal(2, shown.Count);
        Assert.NotSame(first, shown[1]);
        Assert.Same(shown[1], thread.ContextMenu);
    });

    [Fact]
    public void TheClosingOfAnEarlierThreadMenuDoesNotDetachALaterOne() => WithPage(page =>
    {
        List<ContextMenu> shown = new();
        page.ShowThreadMenu = (menu, _) => shown.Add(menu);
        Button thread = new();
        Invoke(page, "OnReaderThreadMenuClick", thread);
        Invoke(page, "OnReaderThreadMenuClick", thread); // a second open before the first one's Closed arrives
        Assert.Equal(2, shown.Count);
        Close(shown[0]);
        Assert.Same(shown[1], thread.ContextMenu);
        Close(shown[1]);
        Assert.Null(thread.ContextMenu);
    });

    // ---- F3: the Archive "Columns" buttons --------------------------------------------------------------------------------

    [Theory]
    [InlineData("ArchiveThreadColumnsButton")]
    [InlineData("ArchiveMediaColumnsButton")]
    public void AnArchiveColumnsMenuIsDetachedFromItsButtonWhenItClosesBecauseItsChecksDescribeTheTableOfThatMoment(string buttonName) => WithPage(page =>
    {
        List<ContextMenu> shown = new();
        page.ShowColumnMenu = (menu, _) => shown.Add(menu);
        Button button = page.FindControl<Button>(buttonName)!;
        Assert.NotNull(button);

        Invoke(page, "OnArchiveColumnsClick", button);
        ContextMenu menu = Assert.Single(shown);
        Assert.Same(menu, button.ContextMenu);
        Close(menu);
        Assert.Null(button.ContextMenu);
    });

    // ---- F3: the Downloads "Columns" button and the scope chip (source contract) --------------------------------------------

    [Fact]
    public void TheDownloadsColumnsMenuDetachesItselfOnCloseSoItIsNotReopenedForAnotherTab()
    {
        string body = Body(Source("Desktop/Shell/MainWindow.Columns.cs"), "private void OnDownloadsColumnsClick(", "// A right-click on a column header");
        int attach = body.IndexOf("button.ContextMenu = menu;", StringComparison.Ordinal);
        int detach = body.IndexOf("menu.Closed += (_, _) => { if (ReferenceEquals(button.ContextMenu, menu)) button.ContextMenu = null; };", StringComparison.Ordinal);
        int open = body.IndexOf("menu.Open(button);", StringComparison.Ordinal);
        Assert.True(attach >= 0 && detach > attach && open > detach, "attach, then detach on Closed, then open");
    }

    [Fact]
    public void TheScopeChipsMenuDetachesItselfOnCloseSoNoStaleScopeOrCarryStateIsKept()
    {
        string body = Body(Source("Desktop/Controls/SearchScopeChip.cs"), "public void ShowMenu()", "protected override void OnAttachedToVisualTree");
        int closed = body.IndexOf("menu.Closed +=", StringComparison.Ordinal);
        int detach = body.IndexOf("if (ReferenceEquals(ContextMenu, menu)) ContextMenu = null;", StringComparison.Ordinal);
        int attach = body.IndexOf("ContextMenu = menu;", StringComparison.Ordinal);
        int open = body.IndexOf("menu.Open(", StringComparison.Ordinal);
        Assert.True(closed >= 0 && detach > closed, "the Closed handler clears the attachment");
        Assert.True(attach > detach && open > attach, "the attachment stays while the menu is open");
    }

    // ---- F1: the speed menu composes before it opens ----------------------------------------------------------------------

    [Fact]
    public void TheSpeedMenuOpenedByItsCommandIsComposedFirstBecauseOpenDoesNotRaiseOpening()
    {
        string source = Source("Desktop/GalleryVideoPlayer.Transport.cs");
        // The checkmark, the labels (language, decimal separator) and the enabled state are written by RefreshRateItems only.
        Assert.Contains("_speedMenu.Opening += (_, _) => RefreshRateItems(_speedMenuRates);", source);
        string body = Body(source, "internal void OpenSpeedMenu()", "private bool CanSaveFrame");
        int refresh = body.IndexOf("RefreshRateItems(_speedMenuRates);", StringComparison.Ordinal);
        int open = body.IndexOf("_speedMenu.Open(_speed);", StringComparison.Ordinal);
        Assert.True(refresh >= 0 && open > refresh, "RefreshRateItems(_speedMenuRates) must run before _speedMenu.Open(_speed)");
    }

    // ---- F5: a right-click on a Downloads column header keeps the selection ------------------------------------------------

    [Fact]
    public void ADownloadsColumnHeaderIsRecognisedAsNoEmptyAreaAndARowOrNothingIsNot()
    {
        Assert.True(MainWindow.IsDownloadColumnHeaderSource(new DataGridColumnHeader()));
        Assert.False(MainWindow.IsDownloadColumnHeaderSource(new DataGridRow()));
        Assert.False(MainWindow.IsDownloadColumnHeaderSource(new Border()));
        Assert.False(MainWindow.IsDownloadColumnHeaderSource(null));
    }

    [Fact]
    public void TheDownloadsPressOnAHeaderLeavesTheSelectionAloneAndStillSwallowsTheReleaseRequest()
    {
        string body = Body(Source("Desktop/Shell/MainWindow.Selection.cs"), "private void SelectDownloadContextRow(", "private readonly HashSet<Control> _downloadPointerContextHandled");
        int flag = body.IndexOf("_downloadPointerContextHandled.Add(grid);", StringComparison.Ordinal);
        int header = body.IndexOf("if (IsDownloadColumnHeaderSource(e.Source))", StringComparison.Ordinal);
        int clear = body.IndexOf("grid.SelectedItems.Clear();", StringComparison.Ordinal);
        Assert.True(flag >= 0 && header > flag, "the release ContextRequested of the header press stays swallowed");
        Assert.True(clear > header, "the header branch returns before the empty-area branch clears the selection");
        string branch = body[header..clear];
        Assert.Contains("return;", branch);
        Assert.DoesNotContain("e.Handled = true", branch); // the header's own handler (registered next) answers the press
    }
}
