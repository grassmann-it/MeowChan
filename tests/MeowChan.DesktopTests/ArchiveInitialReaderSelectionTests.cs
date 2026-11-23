using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class ArchiveInitialReaderSelectionTests
{
    [Fact]
    public async Task InitialEmbeddedReaderSelectsFirstRowWithoutKeyboardFocusAndEnablesFocusedMedia()
    {
        ArchivedThreadView view = await SeedAsync("one");
        WithPage(page =>
        {
            page.ApplyReaderMediaPreferences(ReaderMediaPreferences.Default with { Sidebar = false, FocusedEnlarge = true });
            Assert.True(page.LoadThreadAsync(view.Summary.Id, true, view).IsCompletedSuccessfully);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow first = Rows(posts).First();
            Assert.Same(first, posts.SelectedItem);
            Assert.Equal(first.Id, page.CurrentReaderPostId);
            Assert.False(posts.IsKeyboardFocusWithin);
            Assert.False(posts.AutoScrollToSelectedItem);
            Assert.True(posts.GetValue(ReaderPostLayout.FocusScalePercentProperty) > 100);
            Assert.Equal("first.png", first.Attachments[0].Attachment.MediaId);
        });
    }

    [Fact]
    public async Task RebuildingTheListRetainsSelectionButAnotherThreadWithOverlappingIdsStartsAtFirst()
    {
        ArchivedThreadView first = await SeedAsync("one"), second = await SeedAsync("two");
        WithPage(page =>
        {
            Assert.True(page.LoadThreadAsync(first.Summary.Id, true, first).IsCompletedSuccessfully);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            posts.SelectedItem = Rows(posts).Last();
            typeof(ArchivePage).GetMethod("RenderThread", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            Assert.Same(Rows(posts).Last(), posts.SelectedItem);
            Assert.True(page.LoadThreadAsync(second.Summary.Id, true, second).IsCompletedSuccessfully);
            Assert.Same(Rows(posts).First(), posts.SelectedItem);
            Assert.False(posts.IsKeyboardFocusWithin);
        });
    }

    [Fact]
    public async Task ExplicitJumpWinsOverInitialFallbackAndSurvivesAnotherRender()
    {
        ArchivedThreadView view = await SeedAsync("jump");
        WithPage(page =>
        {
            typeof(ArchivePage).GetMethod("ShowCapturedView", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(page, [view, "p2"]);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            Assert.Equal("p2", Assert.IsType<ArchivePostRow>(posts.SelectedItem).Id);
            typeof(ArchivePage).GetMethod("RenderThread", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            Assert.Equal("p2", Assert.IsType<ArchivePostRow>(posts.SelectedItem).Id);
            Assert.False(posts.IsKeyboardFocusWithin);
        });
    }

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
                "p" + i, i, PostFieldPresence.All, Html: "stored text " + i,
                Attachments: [new CapturedAttachment(0, "first.png", Url: "https://example.invalid/first.png")])).ToArray();
            await catalog.CaptureAsync(new(id, "https://example.invalid/not-acquired", "Initial reader selection",
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, Path.Combine(temp.Path, "uncreated-media"), posts));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        return (await reader.ReadThreadAsync(id))!;
    }
}
