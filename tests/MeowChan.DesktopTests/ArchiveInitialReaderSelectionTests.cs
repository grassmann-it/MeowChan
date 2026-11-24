using System.Reflection;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Gallery viewer construction")]
public sealed class ArchiveInitialReaderSelectionTests
{
    [Fact]
    public async Task InitialEmbeddedReaderSelectsFirstRowWithoutKeyboardFocusAndEnablesFocusedMedia()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAsync(archive, "one");
        archive.Run(page =>
        {
            page.ApplyReaderMediaPreferences(ReaderMediaPreferences.Default with { Sidebar = false, FocusedEnlarge = true });
            ReaderArchiveTestFixture.Load(page, view);
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
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView first = await SeedAsync(archive, "one"), second = await SeedAsync(archive, "two");
        archive.Run(page =>
        {
            ReaderArchiveTestFixture.Load(page, first);
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            posts.SelectedItem = Rows(posts).Last();
            typeof(ArchivePage).GetMethod("RenderThread", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null);
            Assert.Same(Rows(posts).Last(), posts.SelectedItem);
            ReaderArchiveTestFixture.Load(page, second);
            Assert.Same(Rows(posts).First(), posts.SelectedItem);
            Assert.False(posts.IsKeyboardFocusWithin);
        });
    }

    [Fact]
    public async Task ExplicitJumpWinsOverInitialFallbackAndSurvivesAnotherRender()
    {
        using ReaderArchiveTestFixture archive = new();
        ArchivedThreadView view = await SeedAsync(archive, "jump");
        archive.Run(page =>
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

    private static Task<ArchivedThreadView> SeedAsync(ReaderArchiveTestFixture archive, string threadId)
    {
        ResourceId id = new("synthetic-A", "board-A", threadId);
        CapturedPost[] posts = Enumerable.Range(0, 3).Select(i => new CapturedPost(
            "p" + i, i, PostFieldPresence.All, Html: "stored text " + i,
            Attachments: [new CapturedAttachment(0, "first.png", Url: "https://example.invalid/first.png")])).ToArray();
        return archive.CaptureAsync(id, "Initial reader selection", posts);
    }
}
