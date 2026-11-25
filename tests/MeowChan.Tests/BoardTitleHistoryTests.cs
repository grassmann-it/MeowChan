using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class BoardTitleHistoryTests
{
    [Fact]
    public async Task ReadsOnlyTitledThreadsForTheExactBoardOrderedByFirstSight()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using var catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        DateTimeOffset origin = DateTimeOffset.UnixEpoch;
        ResourceId untitled = new("4chan", "p", "1");
        ResourceId older = new("4chan", "p", "2");
        ResourceId latest = new("4chan", "p", "3");
        await Capture(older, "/rpt/ #2", origin);
        await Capture(latest, "/rpt/ #3", origin.AddDays(1));
        await Capture(older, "/rpt/ #2", origin.AddDays(2));
        await Capture(untitled, string.Empty, origin.AddDays(3), [new("1", 0, PostFieldPresence.Html, Html: "/rpt/ only in the opening post")]);
        await Capture(new("4chan", "g", "3"), "Other board", origin.AddDays(4));
        await Capture(new("8kun", "p", "3"), "Other source", origin.AddDays(4));

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        var missingSubject = Assert.IsType<ArchivedThreadView>(await reader.ReadThreadAsync(untitled));
        Assert.Equal(string.Empty, missingSubject.Summary.Title);
        Assert.Single(missingSubject.Posts);
        Assert.Equal(latest.Thread, Assert.Single(await reader.ReadBoardTitleHistoryAsync("4chan", "p", 1)).Thread);
        var history = await reader.ReadBoardTitleHistoryAsync("4chan", "p", 50);
        Assert.Equal(new[] { latest.Thread, older.Thread }, history.Select(row => row.Thread));
        Assert.Equal("/rpt/ #2", history[1].Title);
        Assert.Equal(origin, history[1].FirstSeenAt);
        Assert.Equal(origin.AddDays(2), history[1].LastCapturedAt);
        Assert.Empty(await reader.ReadBoardTitleHistoryAsync("4chan", "missing", 1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadBoardTitleHistoryAsync("4chan", "p", 50, canceled.Token));

        Task Capture(ResourceId id, string title, DateTimeOffset when, CapturedPost[]? posts = null) =>
            catalog.CaptureAsync(new(id, $"https://example.invalid/{id.Board}/thread/{id.Thread}", title,
                ThreadSourceState.Archived, when, profile.Path, posts ?? []));
    }
}
