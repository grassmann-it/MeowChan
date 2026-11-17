using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

// The pointer contract of a post card in the reader (PT-03: the post menu with a "Copy" for a selected text; PT-02 and the press itself run
// in the smoke, which has real cards) and the Primary+F routing of the embedded reader (RD-02), on a real ArchivePage without a window.
[Collection("Gallery viewer construction")]
public sealed class ReaderPostPointerAndFindKeyTests : IDisposable
{
    public ReaderPostPointerAndFindKeyTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly KeyModifiers Primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    [Fact]
    public void ABodyTextHasNoThemeFlyoutAndNamesItsSelectionInPlainText()
    {
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            ReaderBodyTextBlock body = new() { Text = "alpha beta gamma" };
            // The theme's own "Copy" flyout would answer a right click before the post card sees it (PT-03).
            Assert.Null(body.ContextFlyout);
            Assert.Equal(string.Empty, body.SelectedPlainText);
            body.SelectionStart = 6; body.SelectionEnd = 10;
            Assert.Equal("beta", body.SelectedPlainText);
            body.SelectionStart = 16; body.SelectionEnd = 11; // dragged backwards
            Assert.Equal("gamma", body.SelectedPlainText);
        });
    }

    [Fact]
    public async Task ThePostMenuLeadsWithCopyForASelectionThenASeparatorThenTheUnchangedPostMenu()
    {
        ArchivedThreadView view = await SeedAsync("menu");
        WithPage(page =>
        {
            Assert.True(page.LoadThreadAsync(view.Summary.Id, true, view).IsCompletedSuccessfully);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow row = Rows(posts).First();
            List<string> copied = new();
            page.CopyTextForTests = text => { copied.Add(text); return Task.CompletedTask; };

            Control[] plain = page.BuildPostMenu(row, posts).Items.Cast<Control>().ToArray();
            Assert.Equal("post.copy-text", Assert.IsType<MenuItem>(plain[0]).Tag);
            Assert.DoesNotContain(plain, item => item is MenuItem { Tag: "post.copy-selection" });

            Control[] selected = page.BuildPostMenu(row, posts, "stored").Items.Cast<Control>().ToArray();
            MenuItem copy = Assert.IsType<MenuItem>(selected[0]);
            Assert.Equal("post.copy-selection", copy.Tag);
            Assert.Equal("Copy", copy.Header);
            Assert.IsType<Separator>(selected[1]);
            // The post menu follows as it is without a selection: same entries, same order.
            Assert.Equal(plain.Select(Describe), selected.Skip(2).Select(Describe));

            copy.Command!.Execute(null);
            Assert.Equal("stored", Assert.Single(copied)); // the selection, not the post text
        });
    }

    [Fact]
    public async Task PrimaryFWithTheFocusInTheEmbeddedReaderOpensItsFindAndElsewhereLeavesTheKeyToTheArchiveSearch()
    {
        ArchivedThreadView view = await SeedAsync("find");
        WithPage(page =>
        {
            StackPanel bar = page.FindControl<StackPanel>("ReaderFindBar")!;
            // Controls the page itself puts into the pane (no theme needed): the status line, and the find bar with its field.
            TextBlock status = page.FindControl<TextBlock>("ArchiveReaderStatus")!;
            // No thread: nothing to find in, the key is the archive search's.
            Assert.False(page.TryOpenReaderFindByKey(Press(status)));
            Assert.False(bar.IsVisible);

            Assert.True(page.LoadThreadAsync(view.Summary.Id, true, view).IsCompletedSuccessfully);
            Assert.False(page.IsBusy);

            // Inside the embedded reader pane: the key opens the find bar.
            bar.IsVisible = false;
            Assert.True(page.TryOpenReaderFindByKey(Press(status)));
            Assert.True(bar.IsVisible);

            // The archive list and the archive search fields: the key stays with them.
            foreach (Control outside in new Control[] { page.FindControl<TextBox>("ArchiveSearchText")!, page.FindControl<ListBox>("ArchiveThreads")! })
            {
                bar.IsVisible = false;
                Assert.False(page.TryOpenReaderFindByKey(Press(outside)), outside.Name);
                Assert.False(bar.IsVisible, outside.Name);
            }

            // Another key is not the find key. Inside the find bar itself the key asks for the bar again: it stays open and takes the key.
            bar.IsVisible = false;
            Assert.False(page.TryOpenReaderFindByKey(Press(status, Key.G)));
            Assert.False(bar.IsVisible);
            bar.IsVisible = true;
            Assert.True(page.TryOpenReaderFindByKey(Press(bar)));
            Assert.True(bar.IsVisible);

            // The large reader's pane takes the same route (the window asks first there too).
            typeof(ArchivePage).GetField("_largeReader", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, true);
            bar.IsVisible = false;
            Assert.True(page.TryOpenReaderFindByKey(Press(status)));
            Assert.True(bar.IsVisible);

            // A dialog over the reader owns the keys.
            bar.IsVisible = false;
            page.FindControl<Border>("ArchiveExportDialog")!.IsVisible = true;
            Assert.False(page.TryOpenReaderFindByKey(Press(status)));
            Assert.False(bar.IsVisible);
        });
    }

    [Fact]
    public void TheWindowAsksTheReaderBeforeFocusingTheArchiveSearch()
    {
        string shell = Source("Desktop/Shell/MainWindow.Fullscreen.cs");
        int reader = shell.IndexOf("_archivePage?.TryOpenReaderFindByKey(e) == true", StringComparison.Ordinal);
        int search = shell.IndexOf("_archivePage?.FocusArchiveTextSearch()", StringComparison.Ordinal);
        Assert.True(reader > 0 && search > reader, "The reader's find must come before the archive search in the window's key chain.");
    }

    private static KeyEventArgs Press(Control source, Key key = Key.F) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = Primary, Source = source };

    private static string Describe(Control control) => control is MenuItem item ? (item.Tag as string ?? "") : control.GetType().Name;

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static IEnumerable<ArchivePostRow> Rows(ListBox posts) => (IEnumerable<ArchivePostRow>)posts.ItemsSource!;

    private static void WithPage(Action<ArchivePage> test)
    {
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable scope = AvaloniaLocator.EnterScope();
            AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
            TokenApplication.Bind();
            ArchivePage page = new(_ => Task.FromResult<ArchiveReader>(null!), (_, _) => Task.CompletedTask, _ => { });
            try { page.IsReadable = true; test(page); }
            finally { (page as IDisposable)?.Dispose(); }
        });
    }

    private static async Task<ArchivedThreadView> SeedAsync(string threadId)
    {
        ResourceId id = new("synthetic-A", "board-A", threadId);
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease))
        {
            CapturedPost[] posts = Enumerable.Range(0, 3).Select(i => new CapturedPost(
                "p" + i, i, PostFieldPresence.All, Html: "stored text " + i)).ToArray();
            await catalog.CaptureAsync(new(id, "https://example.invalid/not-acquired", "Reader pointer and find",
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, Path.Combine(temp.Path, "uncreated-media"), posts));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        return (await reader.ReadThreadAsync(id))!;
    }
}
