using System.Text;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ArchiveUrlTextExportPlanTests
{
    private static readonly ResourceId Id = new("4chan", "b", "123");
    private const string Url = "https://boards.4chan.org/b/thread/123";

    private static ArchiveThreadSummary Summary(ResourceId id, string url) => new(id, url,
        "PRIVATE TITLE", "/private/profile/media", ThreadSourceState.NotFound, false,
        DateTimeOffset.UnixEpoch, null, 0, 1);

    [Theory]
    [InlineData("4chan", "b", "123", Url)]
    [InlineData("8kun", "test", "456", "https://8kun.top/test/res/456.html")]
    [InlineData("4chan", "b", "01", "https://boards.4chan.org/b/thread/01")]
    public void SavedOfflineMetadataNeedsNoPostsOrProvider(string source, string board, string thread, string url)
    {
        ResourceId id = new(source, board, thread);
        var plan = ArchiveUrlTextExportPlan.TryCreate(id, Summary(id, url), out var error);
        Assert.Equal(ArchiveUrlTextExportPlanError.None, error);
        Assert.NotNull(plan);
        Assert.Equal(id, plan.Id);
        Assert.Equal(url, plan.Url);
        Assert.Equal(Encoding.UTF8.GetBytes(url + "\n"), plan.CopyUtf8());
        Assert.Equal(url.Length + 1, plan.ByteCount);
    }

    [Theory]
    [InlineData("https://boards.4chan.org/b/thread/0123")]
    [InlineData("https://boards.4chan.org/a/thread/123")]
    [InlineData("https://8kun.top/b/res/123.html")]
    [InlineData("https://boards.4chan.org/b/thread/124")]
    public void ValidCorePageMustBindExactIdentity(string url)
    {
        Assert.Null(ArchiveUrlTextExportPlan.TryCreate(Id, Summary(Id, url), out var error));
        Assert.Equal(ArchiveUrlTextExportPlanError.PageIdentityMismatch, error);
    }

    [Theory]
    [InlineData("https://BOARDS.4chan.org/b/thread/123")]
    [InlineData("HTTPS://boards.4chan.org/b/thread/123")]
    [InlineData("https://boards.4chan.org/B/thread/123")]
    [InlineData("https://boards.4chan.org/b/thread/123#p123")]
    [InlineData("https://boards.4chan.org/b/thread/123?secret=PRIVATE")]
    [InlineData("https://PRIVATE:secret@boards.4chan.org/b/thread/123")]
    [InlineData("https://boards.4chan.org:443/b/thread/123")]
    [InlineData("https://boards.4channel.org/b/thread/123")]
    [InlineData("https://boards.4chan.org/b/")]
    [InlineData("https://boards.4chan.org/b/thread/123/title")]
    [InlineData("https://boards.4chan.org/b/thread/%31%32%33")]
    [InlineData("https://boards.4chan.org/b/../b/thread/123")]
    [InlineData("https://boards.4chan.org/b/thread/123\\PRIVATE")]
    [InlineData(Url + "\n" + Url)]
    [InlineData(Url + "\r")]
    [InlineData("\uFEFF" + Url)]
    [InlineData(" https://boards.4chan.org/b/thread/123")]
    [InlineData("https://i.4cdn.org/b/123.jpg")]
    [InlineData("https://8kun.top/b/res/123.html#123")]
    public void RejectsWithoutRetainingRawDiagnostics(string url)
    {
        Assert.Null(ArchiveUrlTextExportPlan.TryCreate(Id, Summary(Id, url), out var error));
        Assert.NotEqual(ArchiveUrlTextExportPlanError.None, error);
        Assert.DoesNotContain("PRIVATE", error.ToString());
        Assert.DoesNotContain("https", error.ToString());
    }

    [Fact]
    public void SummaryAndSelectionMustMatchBeforeUrlValidation()
    {
        foreach (var other in new[] { new ResourceId("4chan", "b", "0123"),
            new ResourceId("4chan", "B", "123"), new ResourceId("8kun", "b", "123") })
        {
            Assert.Null(ArchiveUrlTextExportPlan.TryCreate(Id, Summary(other, Url), out var error));
            Assert.Equal(ArchiveUrlTextExportPlanError.IdentityMismatch, error);
        }
    }

    [Fact]
    public void MissingAndUnsupportedIdentitiesAreRejected()
    {
        foreach (var id in new ResourceId?[] { null, new("4CHAN", "b", "123"),
            new("unknown", "b", "123"), new("4chan", "b"), new("4chan", "", "123"),
            new("4chan", new string('b', 257), "123"), new("4chan", "b", new string('1', 257)) })
            Assert.Null(ArchiveUrlTextExportPlan.TryCreate(id, Summary(Id, Url), out _));
        Assert.Null(ArchiveUrlTextExportPlan.TryCreate(Id, null, out _));
        Assert.Null(ArchiveUrlTextExportPlan.TryCreate(Id, Summary(Id, ""), out _));
    }

    [Fact]
    public void StrictUnicodeAndPreallocationLengthBound()
    {
        foreach (string invalid in new[] { Url + "\uD800", Url + "\uDC00" })
        {
            Assert.Null(ArchiveUrlTextExportPlan.TryCreate(Id, Summary(Id, invalid), out var error));
            Assert.Equal(ArchiveUrlTextExportPlanError.InvalidUnicode, error);
        }
        foreach (string oversized in new[] { new string('x', 8193), new string('\uD800', 8193) })
        {
            Assert.Null(ArchiveUrlTextExportPlan.TryCreate(Id, Summary(Id, oversized), out var error));
            Assert.Equal(ArchiveUrlTextExportPlanError.LimitExceeded, error);
        }
        Assert.Null(ArchiveUrlTextExportPlan.TryCreate(Id, Summary(Id, new string('x', 8192)), out var bounded));
        Assert.Equal(ArchiveUrlTextExportPlanError.InvalidPage, bounded);
    }

    [Theory]
    [InlineData(ThreadSourceState.Active)]
    [InlineData(ThreadSourceState.Archived)]
    [InlineData(ThreadSourceState.NotFound)]
    public void SourceStateDoesNotGrantOrBlockPureSavedPagePlan(ThreadSourceState state)
    {
        Assert.NotNull(ArchiveUrlTextExportPlan.TryCreate(Id,
            Summary(Id, Url) with { SourceState = state }, out _));
    }

    [Fact]
    public void MaximumCoreIdentityLengthsRemainAccepted()
    {
        ResourceId id = new("8kun", new string('b', 256), new string('1', 256));
        string url = $"https://8kun.top/{id.Board}/res/{id.Thread}.html";
        var plan = ArchiveUrlTextExportPlan.TryCreate(id, Summary(id, url), out var error);
        Assert.NotNull(plan);
        Assert.Equal(ArchiveUrlTextExportPlanError.None, error);
        Assert.Equal(Encoding.UTF8.GetBytes(url + "\n"), plan.CopyUtf8());
    }

    [Fact]
    public void CopiesCannotMutateSnapshotAndPrivateSummaryFieldsAreAbsent()
    {
        var summary = Summary(Id, Url);
        var plan = ArchiveUrlTextExportPlan.TryCreate(Id, summary, out _)!;
        byte[] first = plan.CopyUtf8();
        byte[] second = plan.CopyUtf8();
        Assert.NotSame(first, second);
        Array.Fill(first, (byte)0);
        Assert.Equal(Encoding.UTF8.GetBytes(Url + "\n"), second);
        Assert.Equal(second, plan.CopyUtf8());
        summary = summary with { Url = "PRIVATE", Title = "CHANGED" };
        Assert.Equal(Url, plan.Url);
        Assert.DoesNotContain("PRIVATE", Encoding.UTF8.GetString(plan.CopyUtf8()));
        Assert.DoesNotContain("profile", Encoding.UTF8.GetString(plan.CopyUtf8()));
        Assert.DoesNotContain("PRIVATE", plan.ToString()!);
    }
}
