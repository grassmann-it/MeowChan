using System.Security.Cryptography;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruOccurrenceSourceTests
{
    private const string PostId = "42";
    private static readonly DateTimeOffset Captured = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task IdenticalOriginalsFromTwoInstancesKeepDistinctLabelsAfterReload()
    {
        using TempDirectory temp = new();
        MediaLibraryEntry north = Original(temp.Path, BooruMediaIdentity.Thread(new BooruPostKey("example-booru", "north", PostId)), "first"u8);
        MediaLibraryEntry south = Original(temp.Path, BooruMediaIdentity.Thread(new BooruPostKey("example-booru", "south", PostId)), "first"u8);

        IReadOnlyList<MediaLibraryEntry> entries = await ReloadAsync(temp.Path, [north, south]);
        MediaLibraryTile tile = Assert.Single(Tiles(entries));
        Assert.Equal(2, tile.ProviderPostCount);
        AssertLabels(tile, "example-booru · north · Post #42", "example-booru · south · Post #42");
    }

    [Fact]
    public void DifferentOriginalsWithTheSamePostIdStayOnSeparateTiles()
    {
        using TempDirectory temp = new();
        MediaLibraryEntry north = Original(temp.Path, BooruMediaIdentity.Thread(new BooruPostKey("example-booru", "north", PostId)), "first"u8);
        MediaLibraryEntry south = Original(temp.Path, BooruMediaIdentity.Thread(new BooruPostKey("example-booru", "south", PostId)), "other"u8);

        IReadOnlyList<MediaLibraryTile> tiles = Tiles([north, south]);

        Assert.Equal(2, tiles.Count);
        Assert.Equal(north, Assert.Single(Assert.Single(tiles, tile => tile.Entry == north).Occurrences));
        Assert.Equal(south, Assert.Single(Assert.Single(tiles, tile => tile.Entry == south).Occurrences));
    }

    [Fact]
    public async Task LegacyDanbooruKeepsItsSourceAfterReload()
    {
        using TempDirectory temp = new();
        MediaLibraryEntry legacy = Original(temp.Path, new ResourceId("danbooru", "posts", PostId), "first"u8);
        MediaLibraryEntry scoped = Original(temp.Path, BooruMediaIdentity.Thread(new BooruPostKey("example-booru", "north", PostId)), "first"u8);

        IReadOnlyList<MediaLibraryEntry> entries = await ReloadAsync(temp.Path, [legacy, scoped]);

        AssertLabels(Assert.Single(Tiles(entries)), "Danbooru · default · Post #42", "example-booru · north · Post #42");
    }

    [Theory]
    [InlineData("en", false, "Occurrences: 2 · Booru posts: 2. Tags stay with each occurrence.")]
    [InlineData("en", true, "Occurrences: 3 · threads: 1 · Booru posts: 2. Tags stay with each occurrence.")]
    [InlineData("de", false, "Fundstellen: 2 · Booru-Posts: 2. Tags bleiben bei jeder Fundstelle gespeichert.")]
    [InlineData("de", true, "Fundstellen: 3 · Threads: 1 · Booru-Posts: 2. Tags bleiben bei jeder Fundstelle gespeichert.")]
    public void SummaryCountsPostsFromBothBooruInstances(string language, bool includeThread, string expected)
    {
        using TempDirectory temp = new();
        MediaLibraryEntry e621 = Original(temp.Path, BooruMediaIdentity.Thread(new BooruPostKey("e621", "e621", PostId)), "first"u8);
        MediaLibraryEntry e926 = Original(temp.Path, BooruMediaIdentity.Thread(new BooruPostKey("e621", "e926", PostId)), "first"u8);
        MediaLibraryEntry archived = Original(temp.Path, new ResourceId("4chan", "b", "123"), "first"u8) with { BooruPostId = null };
        MediaLibraryTile tile = Assert.Single(Tiles(includeThread ? [e621, e926, archived] : [e621, e926]));

        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            string previousLanguage = Texts.Language;
            Texts.SetLanguage(language);
            try
            {
                MediaOccurrencesWindow window = new(tile, _ => string.Empty, _ => false, _ => { });
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    TextBlock summary = Assert.Single(window.GetVisualDescendants().OfType<TextBlock>(),
                        text => Peer(text).GetAutomationId() == "MediaOccurrencesSummary");
                    Assert.True(summary.IsEffectivelyVisible);
                    Assert.Equal(expected, summary.Text);
                }
                finally { window.Close(); }
            }
            finally { Texts.SetLanguage(previousLanguage); }
        });
    }

    private static MediaLibraryEntry Original(string directory, ResourceId thread, ReadOnlySpan<byte> bytes) =>
        new(thread, "Post #42", 0, Captured, Captured, null, null, null, null, null, directory, "asset-" + thread.Board,
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "original", "original", "42.png", SavedMediaKind.Image,
            MediaLibraryPresence.Present, Unassigned: true, Historical: false, LocalSlot: "42.png") { BooruPostId = PostId };

    private static IReadOnlyList<MediaLibraryTile> Tiles(IReadOnlyList<MediaLibraryEntry> entries) =>
        MediaLibraryGrouping.Build(entries, entries, MediaLibrarySort.ByThread, includeMissing: false);

    private static async Task<IReadOnlyList<MediaLibraryEntry>> ReloadAsync(string directory, IReadOnlyList<MediaLibraryEntry> entries)
    {
        string path = Path.Combine(directory, MediaLibraryIndexCache.FileName);
        MediaLibrarySnapshot snapshot = new(entries, [], [], complete: true);
        MediaLibraryIndexCacheWrite saved = await MediaLibraryIndexCache.SaveAsync(path, snapshot, MediaLibraryIndexCache.DefaultLimitBytes);
        Assert.Equal(MediaLibraryIndexCacheWriteStatus.Written, saved.Status);
        MediaLibraryIndexCacheRead loaded = Assert.IsType<MediaLibraryIndexCacheRead>(await MediaLibraryIndexCache.LoadAsync(path));
        return loaded.Snapshot.Entries;
    }

    private static void AssertLabels(MediaLibraryTile tile, params string[] expectedLabels) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        MediaOccurrencesWindow window = new(tile, _ => string.Empty, _ => false, _ => { });
        try
        {
            window.Show();
            window.UpdateLayout();
            StackPanel[] rows = window.GetVisualDescendants().OfType<StackPanel>()
                .Where(row => Peer(row).GetAutomationId() == "MediaOccurrence").ToArray();
            Assert.Equal(expectedLabels.Length, rows.Length);
            foreach (string expected in expectedLabels)
            {
                StackPanel row = Assert.Single(rows, row => Peer(row).GetName()?.StartsWith(expected + Environment.NewLine, StringComparison.Ordinal) == true);
                TextBlock heading = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => Peer(text).GetAutomationId() == "MediaOccurrenceWhere");
                Button action = Assert.Single(row.GetVisualDescendants().OfType<Button>(), button => Peer(button).GetAutomationId() == "MediaOccurrenceShow");
                Assert.True(heading.IsEffectivelyVisible);
                Assert.Equal(expected, heading.Text);
                Assert.Equal(expected, Peer(heading).GetName());
                Assert.Equal("Show in thread, " + expected, Peer(action).GetName());
            }
        }
        finally { window.Close(); }
    });

    private static AutomationPeer Peer(Control control) => ControlAutomationPeer.CreatePeerForElement(control);
}
