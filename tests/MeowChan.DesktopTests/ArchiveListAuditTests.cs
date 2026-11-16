using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>
/// The audit of the archive's thread list (16.11.2026): every entry point of the result list and table that was shown but did nothing.
/// The real page is built without a window; its handlers are driven with real routed events on the real controls.
/// </summary>
[Collection("Gallery viewer construction")]
public sealed class ArchiveListAuditTests
{
    private static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

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

    private static void Set(ArchivePage page, string field, object value) =>
        typeof(ArchivePage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static ArchiveThreadRow Row(string thread)
    {
        ThreadSourceState state = ThreadSourceState.NotFound;
        return new ArchiveThreadRow(new ArchiveThreadSummary(new ResourceId("synthetic", "b", thread), "", "", "",
            state, true, DateTimeOffset.UnixEpoch, null, 1, 1));
    }

    /// <summary>A page whose table holds one selected thread, ready for the list's keys.</summary>
    private static (DataGrid Table, ArchiveThreadRow Row) WithSelectedThread(ArchivePage page)
    {
        ArchiveThreadRow row = Row("1");
        DataGrid table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
        table.ItemsSource = new[] { row };
        Set(page, "_available", true);
        ((HashSet<ResourceId>)typeof(ArchivePage).GetField("_archiveSelectionIds", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!).Add(row.Summary.Id);
        Set(page, "_archiveLeadId", row.Summary.Id);
        return (table, row);
    }

    private static KeyEventArgs Press(Key key, KeyModifiers modifiers, Control source) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = source };

    // ---- the column header of the thread table --------------------------------------------------------------------------------

    [Fact]
    public void ARightClickOnAColumnHeaderOfTheThreadTableOpensTheColumnMenuInsteadOfBeingSwallowedByTheRowMenu() => WithPage(page =>
    {
        DataGrid table = page.FindControl<DataGrid>("ArchiveThreadTable")!;
        List<ContextMenu> shown = new();
        page.ShowColumnMenu = (menu, _) => shown.Add(menu);
        DataGridColumnHeader header = new() { Content = "no such column" };
        ContextRequestedEventArgs request = new() { RoutedEvent = Control.ContextRequestedEvent, Source = header };
        // The selection handler is the first one on the grid (RaiseEvent would reset the source of the request, so the handler is called as the grid does).
        typeof(ArchivePage).GetMethod("OnArchiveContextRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, new object?[] { table, request });
        Assert.True(request.Handled);
        // The thread table's selection handler is the first one on the grid; it used to mark the request handled, so the header's own handler
        // (registered after it) never ran and the right-click on a header did nothing at all.
        ContextMenu menu = Assert.Single(shown);
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "archive.columnsFitAll"));
        Assert.Contains(menu.Items.OfType<MenuItem>(), item => Equals(item.Tag, "archive.columnsReset"));
    });

    [Fact]
    public void AHeaderPathIsRecognisedAndABodyPathIsNot()
    {
        DataGridColumnHeader header = new();
        Assert.True(ArchivePage.IsColumnHeaderPath(new object[] { new TextBlock(), header, new DataGrid() }));
        Assert.False(ArchivePage.IsColumnHeaderPath(new object[] { new TextBlock(), new DataGridRow(), new DataGrid() }));
    }

    // ---- a button inside a thread row (thumbnail, star) ---------------------------------------------------------------------------

    [Fact]
    public void ARightClickOnARowsThumbnailOrStarBelongsToTheRowButTextFieldsHeadersAndScrollBarsKeepTheirOwn()
    {
        DataGrid table = new();
        ListBox list = new();
        Assert.True(ArchivePage.IsRowButtonPath(new object[] { new Border(), new Button(), new DataGridRow(), table }, table));
        Assert.True(ArchivePage.IsRowButtonPath(new object[] { new Button(), new Grid(), new ListBoxItem(), list }, list));
        Assert.True(ArchivePage.IsRowButtonPath(new object[] { new ArchiveThreadFavoriteButton(), new DataGridRow(), table }, table));
        // No button: the ordinary list path answers. No row: the blank area has its own rule. A path that never reaches the list is none of ours.
        Assert.False(ArchivePage.IsRowButtonPath(new object[] { new TextBlock(), new DataGridRow(), table }, table));
        Assert.False(ArchivePage.IsRowButtonPath(new object[] { new Button(), table }, table));
        Assert.False(ArchivePage.IsRowButtonPath(new object[] { new Button(), new DataGridRow(), new DataGrid() }, table));
        // A button the header holds, a scroll bar's button and an editor keep their behaviour.
        Assert.False(ArchivePage.IsRowButtonPath(new object[] { new Button(), new DataGridColumnHeader(), table }, table));
        Assert.False(ArchivePage.IsRowButtonPath(new object[] { new Button(), new ScrollBar(), new DataGridRow(), table }, table));
        Assert.False(ArchivePage.IsRowButtonPath(new object[] { new Button(), new TextBox(), new DataGridRow(), table }, table));
    }

    [Fact]
    public void TheRowPressAndTheRowRequestShareTheRowButtonRule()
    {
        string selection = Source("Desktop/Views/Archive/ArchivePage.Selection.cs");
        // The press opens the row menu for a button part of a row (the list's own check refuses buttons), and the page-level request handler
        // (registered before the media menus' handler) answers the request of its release, so the open thread's media menu never answers it.
        Assert.Contains("IsRowButtonPath(pressed.GetSelfAndVisualAncestors(), list)", selection);
        Assert.Contains("AddHandler(ContextRequestedEvent, OnThreadRowContextRequested, RoutingStrategies.Tunnel);", selection);
        string page = Source("Desktop/Views/Archive/ArchivePage.axaml.cs");
        Assert.True(page.IndexOf("InitializeArchiveSelection();", StringComparison.Ordinal) < page.IndexOf("InitializeArchiveMediaCommands();", StringComparison.Ordinal),
            "the row handler must be registered before the media menus' handler");
    }

    // ---- the keys of the list ----------------------------------------------------------------------------------------------------

    [Fact]
    public void DeleteOnAnArchivedThreadThatIsNotWatchedSaysWhyInsteadOfDoingNothing() => WithPage(page =>
    {
        (DataGrid table, ArchiveThreadRow row) = WithSelectedThread(page);
        List<IReadOnlyList<ResourceId>> removed = new();
        page.RemoveSelectedWatches = ids => { removed.Add(ids); return Task.CompletedTask; };
        page.CanRemoveWatch = _ => false;
        KeyEventArgs delete = Press(Avalonia.Input.Key.Delete, KeyModifiers.None, table);
        table.RaiseEvent(delete);
        Assert.True(delete.Handled);
        Assert.Empty(removed);
        Assert.Equal(Texts.T("archive.command.noWatch"), page.FindControl<TextBlock>("ArchiveMessage")!.Text);

        // A watched thread is removed as before, and says nothing of its own.
        page.FindControl<TextBlock>("ArchiveMessage")!.Text = string.Empty;
        page.CanRemoveWatch = id => id == row.Summary.Id;
        table.RaiseEvent(Press(Avalonia.Input.Key.Delete, KeyModifiers.None, table));
        Assert.Equal(row.Summary.Id, Assert.Single(Assert.Single(removed)));
        Assert.Equal(string.Empty, page.FindControl<TextBlock>("ArchiveMessage")!.Text);
    });

    [Fact]
    public void CheckNowAndRetryMediaKeysReachTheShellOnceTheWatchRulesAreWired() => WithPage(page =>
    {
        (DataGrid table, ArchiveThreadRow row) = WithSelectedThread(page);
        List<(IReadOnlyList<ResourceId> Ids, bool Retry)> checks = new();
        page.CheckSelectedWatches = (ids, retry) => { checks.Add((ids, retry)); return Task.CompletedTask; };
        page.CanCheckWatch = id => id == row.Summary.Id;
        page.CanRetryWatch = id => id == row.Summary.Id;
        table.RaiseEvent(Press(Avalonia.Input.Key.F5, KeyModifiers.None, table));
        table.RaiseEvent(Press(Avalonia.Input.Key.F5, KeyModifiers.Shift, table));
        Assert.Equal(new[] { false, true }, checks.Select(check => check.Retry));
        Assert.All(checks, check => Assert.Equal(row.Summary.Id, Assert.Single(check.Ids)));
    });

    // ---- the shell wires every callback the list reads ------------------------------------------------------------------------------

    // Seams of the tests and callbacks that have their own fallback or are set by the page itself.
    private static readonly HashSet<string> NotWiredByTheShell = new(StringComparer.Ordinal)
    {
        "ExternalLinkLauncher", "CopyPostReference", "GetBoardSuggestionSource", "MediaTagServices", "ReadArchiveMediaInventoryAsync",
        "ThreadFindIndexFactory", "ShowColumnMenu", "ShowThreadMenu"
    };

    [Fact]
    public void EveryCallbackOfTheArchivePageIsAssignedBySomeoneOutsideThePage()
    {
        string repository = RepositoryRoot();
        string[] pageFiles = Directory.GetFiles(Path.Combine(repository, "Desktop/Views/Archive"), "ArchivePage*.cs");
        Regex callback = new(@"internal\s+(?:Func|Action)<[^;{]*?>\??\s+(\w+)\s*\{\s*get;\s*set;\s*\}", RegexOptions.Compiled);
        string[] names = pageFiles.SelectMany(file => callback.Matches(File.ReadAllText(file)).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(names.Length > 40, "the scan found the page's callbacks");
        string[] others = Directory.GetFiles(Path.Combine(repository, "Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !pageFiles.Contains(file)).ToArray();
        string everythingElse = string.Join("\n", others.Select(File.ReadAllText));
        string[] unassigned = names.Where(name => !NotWiredByTheShell.Contains(name) && !name.EndsWith("ForTests", StringComparison.Ordinal)
                && !name.StartsWith("Before", StringComparison.Ordinal)
                && !Regex.IsMatch(everythingElse, @"\b" + name + @"\s*=[^=>]"))
            .ToArray();
        // CanCheckWatch and CanRetryWatch were never assigned in the product: "Check now" and "Retry missing media" of the list's menu (F5, Shift+F5)
        // were disabled for every thread.
        Assert.True(unassigned.Length == 0, "Callbacks of the archive page that nothing assigns (the entries that use them are dead): " + string.Join(", ", unassigned));
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));
}
