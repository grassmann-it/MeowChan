using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Tests.GoalFixtures;

public sealed class Gp02SyntheticArchiveBaselineTests
{
    [Fact]
    public async Task SyntheticOverlapBaselineSurvivesTwoReaderReopensAndWriterReopen()
    {
        await using Gp02SyntheticArchiveBaseline fixture = await Gp02SyntheticArchiveBaseline.CreateAsync();

        for (int reopen = 0; reopen < 2; reopen++)
        {
            if (reopen == 1)
            {
                await ReopenWriterWithoutWritingAsync(fixture.SourceA);
                await ReopenWriterWithoutWritingAsync(fixture.SourceB);
            }

            await using ArchiveReader readerA = await ArchiveReader.OpenAsync(fixture.SourceA.ProfileDirectory);
            await using ArchiveReader readerB = await ArchiveReader.OpenAsync(fixture.SourceB.ProfileDirectory);
            await AssertCurrentBaselineAsync(readerA, fixture.SourceA);
            await AssertCurrentBaselineAsync(readerB, fixture.SourceB);
        }
    }

    [Fact]
    public async Task SameThreadSnapshotsRemainSeparatedWithoutInventingACrossProfileImport()
    {
        await using Gp02SyntheticArchiveBaseline fixture = await Gp02SyntheticArchiveBaseline.CreateAsync();
        await using ArchiveReader readerA = await ArchiveReader.OpenAsync(fixture.SourceA.ProfileDirectory);
        await using ArchiveReader readerB = await ArchiveReader.OpenAsync(fixture.SourceB.ProfileDirectory);

        ArchivedThreadView? rawA = await readerA.ReadThreadAsync(Gp02SyntheticArchiveBaseline.ThreadId);
        ArchivedThreadView? rawB = await readerB.ReadThreadAsync(Gp02SyntheticArchiveBaseline.ThreadId);
        Assert.NotNull(rawA);
        Assert.NotNull(rawB);
        ArchivedThreadView viewA = rawA!;
        ArchivedThreadView viewB = rawB!;

        string[] idsA = [.. viewA.GetPosts(0, 500).Select(post => post.PostId)];
        string[] idsB = [.. viewB.GetPosts(0, 500).Select(post => post.PostId)];
        Assert.Equal(fixture.SourceA.ExpectedPostIds, idsA);
        Assert.Equal(fixture.SourceB.ExpectedPostIds, idsB);

        Assert.Equal(
            Enumerable.Range(15, 6).Select(Number).ToArray(),
            idsA.Intersect(idsB, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("35", idsA);
        foreach (int number in Enumerable.Range(21, 10))
            Assert.DoesNotContain(Number(number), idsA);
        foreach (int number in Enumerable.Range(1, 14))
            Assert.DoesNotContain(Number(number), idsB);
        foreach (int number in Enumerable.Range(31, 4))
            Assert.DoesNotContain(Number(number), idsB);

        ArchiveThreadPage pageA = await readerA.ListThreadsAsync(0, 500);
        ArchiveThreadPage pageB = await readerB.ListThreadsAsync(0, 500);
        Assert.Single(pageA.Items);
        Assert.Single(pageB.Items);
        Assert.NotEqual(fixture.SourceA.MediaDirectory, fixture.SourceB.MediaDirectory);
        Assert.NotSame(viewA, viewB);
    }

    private static async Task ReopenWriterWithoutWritingAsync(Gp02SyntheticSource source)
    {
        using ProfileLease lease = ProfileLease.Acquire(source.ProfileDirectory);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(source.ProfileDirectory, lease);
        Assert.True(await catalog.GetCaptureEnabledAsync(source.Snapshot.Id));
    }

    private static async Task AssertCurrentBaselineAsync(ArchiveReader reader, Gp02SyntheticSource source)
    {
        ArchiveThreadPage page = await reader.ListThreadsAsync(0, 500);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(source.Snapshot.Id, Assert.Single(page.Items).Id);

        ArchivedThreadView? raw = await reader.ReadThreadAsync(source.Snapshot.Id);
        Assert.NotNull(raw);
        ArchivedThreadView view = raw!;
        IReadOnlyList<CapturedPost> posts = view.GetPosts(0, 500);

        Assert.Equal(source.Snapshot.Id, view.Summary.Id);
        Assert.Equal(source.ExpectedPostIds, posts.Select(post => post.PostId));
        Assert.Equal(posts.Count, view.Summary.PostCount);
        Assert.Equal(source.Snapshot.SourceState, view.Summary.SourceState);
        Assert.Equal(source.Snapshot.Title, view.Summary.Title);
        Assert.Equal(source.Snapshot.CapturedAt, view.Summary.LastCapturedAt);
        Assert.Equal(source.Snapshot.MediaDirectory, view.Summary.MediaDirectory);
        Assert.Equal(source.Snapshot.MediaDirectory, Assert.Single(await reader.GetMediaDirectoriesAsync(source.Snapshot.Id)));

        foreach (CapturedPost expected in source.Snapshot.Posts)
        {
            CapturedPost stored = posts.Single(post => post.PostId == expected.PostId);
            Assert.Equal(expected.Ordinal, stored.Ordinal);
            Assert.Equal(expected.Fields, stored.Fields);
            Assert.Equal(expected.PostedAt, stored.PostedAt);
            Assert.Equal(expected.Subject, stored.Subject);
            Assert.Equal(expected.Author, stored.Author);
            Assert.Equal(expected.Html, stored.Html);
            Assert.Equal(expected.IsDeleted, stored.IsDeleted);
            Assert.Equal(
                (expected.Attachments ?? []).Select(WithoutObservedRevisionKey),
                (stored.Attachments ?? []).Select(WithoutObservedRevisionKey));
            Assert.All(stored.Attachments ?? [], attachment =>
                Assert.False(string.IsNullOrWhiteSpace(attachment.AttachmentRevisionKey)));
        }

        foreach (Gp02SyntheticMediaFile file in source.MediaFiles)
        {
            Assert.True(File.Exists(file.Path));
            Assert.Equal(file.Bytes.ToArray(), await File.ReadAllBytesAsync(file.Path));
        }
        foreach (string missing in source.MissingOriginalPaths)
            Assert.False(File.Exists(missing));
    }

    private static CapturedAttachment WithoutObservedRevisionKey(CapturedAttachment attachment) =>
        attachment with { AttachmentRevisionKey = null };

    private static string Number(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
