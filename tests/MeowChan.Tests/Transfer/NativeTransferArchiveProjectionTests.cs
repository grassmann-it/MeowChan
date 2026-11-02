using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Transfer;
using MeowChan.Tests.GoalFixtures;

namespace MeowChan.Tests.Transfer;

public sealed class NativeTransferArchiveProjectionTests
{
    [Fact]
    public async Task SyntheticExistingArchiveProjectsAllPosts404AndPartialFieldsWithoutOperationalPaths()
    {
        await using Gp02SyntheticArchiveBaseline fixture = await Gp02SyntheticArchiveBaseline.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.SourceB.ProfileDirectory);
        ArchivedThreadView view = (await reader.ReadThreadAsync(Gp02SyntheticArchiveBaseline.ThreadId))!;
        TransferThread projected = NativeTransferArchiveProjection.From(view);
        Assert.Equal(view.Summary.Id, projected.Id);
        Assert.Equal(ThreadSourceState.NotFound, projected.SourceState);
        Assert.Equal(view.Summary.FirstSeenAt, projected.FirstSeenAtUtc);
        Assert.Equal(view.Summary.LastCapturedAt, projected.CapturedAtUtc);
        Assert.Equal(view.Summary.Revision, projected.CaptureRevision);
        Assert.Null(projected.DecimalPostOrder); // do not infer a contract from sorted output
        Assert.Equal(view.PostCount, projected.Posts.Length);
        Assert.Equal(fixture.SourceB.ExpectedPostIds, projected.Posts.Select(p => p.Metadata.PostId));
        foreach (TransferPost post in projected.Posts)
        {
            CapturedPost before = view.Posts.Single(p => p.PostId == post.Metadata.PostId);
            Assert.Equal(before.Fields, post.Metadata.Fields); Assert.Equal(before.Html, post.Metadata.Html);
            Assert.Equal(before.Ordinal, post.Metadata.Ordinal);
            foreach (TransferAttachment attachment in post.Attachments)
            {
                CapturedAttachment old = before.Attachments!.Single(a => a.Position == attachment.Metadata.Position);
                Assert.Equal(old with { PlannedPath = null }, attachment.Metadata);
                Assert.Null(attachment.PayloadSha256); Assert.Null(attachment.History);
                Assert.Equal(TransferContentStatus.Unknown, attachment.Status); // no fake media success from metadata
            }
        }
        string json = Encoding.UTF8.GetString(NativeTransferCatalogSerializer.Serialize(new(DateTimeOffset.UtcNow, [projected])));
        Assert.DoesNotContain(fixture.SourceB.ProfileDirectory, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.SourceB.MediaDirectory, json, StringComparison.Ordinal);
        foreach (string field in new[] { "plannedPath", "mediaDirectory", "captureEnabled", "watch", "grants", "profileId" })
            Assert.DoesNotContain('"' + field + '"', json, StringComparison.Ordinal);
    }
    [Theory]
    [InlineData(null)] [InlineData(false)] [InlineData(true)]
    public void StoredNullableSortContractIsNotReplacedBySourceFallback(bool? order)
    {
        ResourceId id = new("4chan", "b", "7001"); DateTimeOffset at = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        ArchiveThreadSummary summary = new(id, "https://boards.4chan.org/b/thread/7001", "synthetic", "/local/synthetic-only",
            ThreadSourceState.Archived, false, at, at, 9, 4);
        ArchivedThreadView view = new(summary, [new("10", 0, PostFieldPresence.None), new("2", 1, PostFieldPresence.None)], decimalPostOrder: order);
        TransferThread projected = NativeTransferArchiveProjection.From(view);
        Assert.Equal(order, projected.DecimalPostOrder);
        Assert.Equal(view.Posts.Select(p => p.PostId), projected.Posts.Select(p => p.Metadata.PostId));
        Assert.Null(projected.Posts[0].Metadata.Subject);
    }
}
