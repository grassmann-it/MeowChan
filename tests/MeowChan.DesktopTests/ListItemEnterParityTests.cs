using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Shortcuts;

namespace MeowChan.DesktopTests;

// Sweep S1 (16 Nov 2026): a focused ListBoxItem consumes Enter and Space (any modifier) before a bubble handler of its list sees them (Avalonia 12), so a list
// whose primary action hangs on a bubble KeyDown is dead once focus sits on a row. Every test raises the key on the row's own container with a bare ListBox
// template, the route the keyboard takes, never on the list itself. The headless lists below each run the very action their double click or button runs.
[Collection("Localization")]
public sealed class ListItemEnterParityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    // ---- ListRowActions.OnActivate: the one call of the dialogs (Bookmarks, Saved searches, Reader groups, Favourites, Filename search) ----------------

    [Fact]
    public void EnterOnAFocusedRowRunsThePrimaryActionOnThatRowAndShiftOrCtrlEnterDoNot() => Run(() =>
    {
        int runs = 0;
        ListBox list = Realize(new ListBox { ItemsSource = new[] { "one", "two", "three" }, SelectionMode = SelectionMode.Single });
        ListRowActions.OnActivate(list, () => runs++);
        list.SelectedIndex = 0;

        Press(Container(list, 2), Key.Enter);

        Assert.Equal(1, runs);
        Assert.Equal(2, list.SelectedIndex); // a focus-only move leaves another row selected: the focused row is the one that is meant
        Press(Container(list, 1), Key.Enter, KeyModifiers.Shift);
        Press(Container(list, 1), Key.Enter, KeyModifiers.Control);
        Press(Container(list, 1), Key.Space);
        Assert.Equal(1, runs);
    });

    [Fact]
    public void EnterWithTheListItselfFocusedRunsItOnTheSelectedRowOnlyAndNeverOnNoRow() => Run(() =>
    {
        int runs = 0;
        ListBox list = Realize(new ListBox { ItemsSource = new[] { "one", "two" }, SelectionMode = SelectionMode.Single });
        ListRowActions.OnActivate(list, () => runs++);

        Press(list, Key.Enter);
        Assert.Equal(0, runs); // nothing selected: no hidden "the old selection opens"
        list.SelectedIndex = 1;
        Press(list, Key.Enter);
        Assert.Equal(1, runs);
    });

    [Fact]
    public void AKeyFromAnInteractivePartOfARowIsNotARowKey() => Run(() =>
    {
        ListBox list = Realize(new ListBox { ItemsSource = new[] { "one" }, SelectionMode = SelectionMode.Single });
        // The guard OnActivate runs: a text field below a row keeps Enter, the row itself does not.
        Assert.True(ItemActivation.IsOwnedByInteractivePart(new TextBox(), null));
        Assert.False(ItemActivation.IsOwnedByInteractivePart(Container(list, 0), list));
    });

    [Fact]
    public void TheListsOfTheSweepRegisterBothGesturesWithOneCall()
    {
        string root = RepoRoot();
        foreach (string file in new[]
        {
            "Desktop/Views/Collections/PostBookmarksDialog.axaml.cs", "Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml.cs",
            "Desktop/Views/Archive/ArchivePage.Favorites.cs", "Desktop/Views/Archive/FilenameSearchDialog.cs"
        })
        {
            string text = File.ReadAllText(Path.Combine(root, file));
            Assert.Contains("ListRowActions.OnActivate(", text);
            Assert.DoesNotContain("ListRowActions.OnDoubleClick(", text);
        }
        string groups = File.ReadAllText(Path.Combine(root, "Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs"));
        Assert.Contains("ListRowActions.OnActivate(C<ListBox>(\"Groups\")", groups);
        Assert.Contains("ListRowActions.OnActivate(C<ListBox>(\"GroupCandidates\")", groups);
        Assert.DoesNotContain("ListRowActions.OnDoubleClick(", groups);
    }

    // ---- "Show full history…": Enter after an arrow key ----------------------------------------------------------------------------------------------

    [Fact]
    public void EnterOnTheSecondHistoryRowAfterAnArrowKeyOpensThatRow() => Run(() =>
    {
        List<LocalReadingPosition> chosen = new();
        LocalReadingHistoryEntry[] entries = [Entry(1), Entry(2), Entry(3)];
        ListBox list = Realize(ReadingHistoryWindow.BuildList(entries, Now, chosen.Add));
        list.SelectedIndex = 1;

        Press(Container(list, 1), Key.Enter);

        Assert.Equal(entries[1].Position, Assert.Single(chosen));
    });

    [Fact]
    public void EnterOnAFocusedHistoryRowOpensTheFocusedRowEvenWhenAnotherIsSelectedAndAModifierDoesNot() => Run(() =>
    {
        List<LocalReadingPosition> chosen = new();
        LocalReadingHistoryEntry[] entries = [Entry(1), Entry(2)];
        ListBox list = Realize(ReadingHistoryWindow.BuildList(entries, Now, chosen.Add));
        list.SelectedIndex = 0;

        Press(Container(list, 1), Key.Enter, KeyModifiers.Control);
        Assert.Empty(chosen);
        Press(Container(list, 1), Key.Enter);

        Assert.Equal(entries[1].Position, Assert.Single(chosen));
    });

    // ---- the shortcut table ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EnterOnACommandRowOfTheShortcutTableStartsRecordingAndSpaceOrEnterOnAGroupHeaderFoldsIt() => Run(() =>
    {
        ShortcutBindingsEditor editor = new();
        try
        {
            editor.ApplyContext(ShortcutHelpContext.Settings);
            ListBox list = Realize(editor.ActionList);
            Root root = new() { Child = editor };
            try
            {
                Assert.Empty(editor.CollapsedGroups);
                int header = list.Items.OfType<ListBoxItem>().ToList().FindIndex(item => item.Tag is not string);
                Assert.True(header >= 0);
                Press(Container(list, header), Key.Space);
                Assert.Single(editor.CollapsedGroups);
                header = list.Items.OfType<ListBoxItem>().ToList().FindIndex(item => item.Tag is not string);
                Press(Container(list, header), Key.Enter);
                Assert.Empty(editor.CollapsedGroups);

                int index = list.Items.OfType<ListBoxItem>().ToList().FindIndex(item => item.Tag as string == "gallery.fit");
                Assert.True(index >= 0);
                Assert.False(editor.IsRecording);
                Press(Container(list, index), Key.Enter);
                Assert.Equal("gallery.fit", editor.SelectedCommandId);
                Assert.True(editor.IsRecording, "Enter on the focused command row must start recording its new shortcut.");
            }
            finally { root.Child = null; }
        }
        finally { editor.Detach(); }
    });

    // ---- the filter picker -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void EnterOnAFocusedEntryOfTheFilterPickerPicksItAndShiftEnterPicksItAsExclusion() => Run(() =>
    {
        List<MediaFilterTerm> picked = new();
        FilterTermPicker picker = new() { PickedTerm = term => { picked.Add(term); return null; } };
        ListBox list = (ListBox)typeof(FilterTermPicker).GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!;
        FilterTermChoice first = new(MediaFilterField.Board, "/g/", new MediaFilterTerm(MediaFilterField.Board, "g"));
        FilterTermChoice second = new(MediaFilterField.Board, "/a/", new MediaFilterTerm(MediaFilterField.Board, "a"));
        list.ItemsSource = new object[] { first, second };
        Realize(list);
        list.SelectedIndex = 0;

        Press(Container(list, 1), Key.Enter);
        Press(Container(list, 0), Key.Enter, KeyModifiers.Shift);

        Assert.Equal(2, picked.Count);
        Assert.Equal(new MediaFilterTerm(MediaFilterField.Board, "a"), picked[0]);
        Assert.Equal(new MediaFilterTerm(MediaFilterField.Board, "g", Exclude: true), picked[1]);
    });

    // ---- the context key on a list ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AContextHostOnAListSeesTheKeyOfAFocusedRowInTheTunnelPhase() => Run(() =>
    {
        ListBox list = Realize(new ListBox { ItemsSource = new[] { "one" }, SelectionMode = SelectionMode.Single });
        using ContextMenuHost host = new(list, () => null);
        list.SelectedIndex = 0;

        // The menu itself needs a rooted list; that the key arrived (and was taken) is what the tunnel registration guarantees.
        KeyEventArgs args = Press(Container(list, 0), Key.Apps);

        Assert.True(args.Handled);
    });

    // ---- helpers ---------------------------------------------------------------------------------------------------------------------------------

    private static LocalReadingHistoryEntry Entry(int thread) =>
        new(new LocalReadingPosition("4chan", "g", thread.ToString(), "1", 0, null, "Thread " + thread), Now);

    /// <summary>The bare presenter of the product's theme: containers exist without a theme and without a layout pass, and are the sources of the key.</summary>
    private static ListBox Realize(ListBox list)
    {
        FuncTemplate<Panel?> panel = new(() => new StackPanel());
        list.ItemsPanel = panel;
        list.Template = new FuncControlTemplate<ListBox>((_, scope) =>
        {
            ItemsPresenter presenter = new() { Name = "PART_ItemsPresenter", ItemsPanel = panel };
            presenter.RegisterInNameScope(scope);
            return presenter;
        });
        list.ApplyTemplate();
        foreach (ItemsPresenter presenter in list.GetVisualDescendants().OfType<ItemsPresenter>()) presenter.ApplyTemplate();
        return list;
    }

    private static Control Container(ListBox list, int index) => list.ContainerFromIndex(index)
        ?? throw new InvalidOperationException("The row " + index + " has no container.");

    private static KeyEventArgs Press(Control source, Key key, KeyModifiers modifiers = KeyModifiers.None)
    {
        KeyEventArgs args = new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = source };
        source.RaiseEvent(args);
        for (int attempt = 0; attempt < 20; attempt++) Dispatcher.UIThread.RunJobs();
        return args;
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string caller = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(caller)!, "../.."));

    private static void Run(Action test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<Avalonia.Platform.ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication app = new();
        AvaloniaLocator.CurrentMutable.Bind<Application>().ToConstant(app);
        app.Initialize();
        test();
    });

    private sealed class TokenApplication : Application
    {
        public override void Initialize() =>
            Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MeowChan/")) { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
    }

    private sealed class Root : Decorator, ILogicalRoot;
}
