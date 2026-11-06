using MeowChan;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopTests;

// Real synthetic catalog + actual pure host resolver. No Window, Show replacement or native claim.
public sealed class FilenameSearchHostNavigationTests
{
    private static readonly ResourceId Origin = new("synthetic-A", "board-A", "1000");
    private static readonly ResourceId Target = new("synthetic-B", "board-B", "2000");
    private const string Post = "p250";
    private const string Name = "w21-original-claim.png";

    [Fact]
    public async Task OtherStoredThreadAndOffscreenPostResolveByExactResourceNotCurrentThreadOrFirstPost()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        ArchiveFilenamePage results = await reader.SearchFilenamesAsync(new(Name));
        Assert.Equal(2L, results.TotalCount);
        ArchiveFilenameHit hit = Assert.Single(results.Items.Where(row => row.Thread == Target));
        ArchivedThreadView target = (await reader.ReadThreadAsync(hit.Thread))!;
        ArchivedThreadView origin = (await reader.ReadThreadAsync(Origin))!;
        Assert.Equal(Post, hit.Post);
        Assert.DoesNotContain(target.GetPosts(0, 50), post => post.PostId == Post);
        Assert.Equal(250, ArchivePage.ResolveFilenameHitPostIndex(target, hit));
        Assert.Equal(Target, target.Summary.Id);
        CapturedPost selected = Assert.Single(target.GetPosts(250, 1));
        Assert.Equal(hit.Post, selected.PostId);
        CapturedAttachment attachment = Assert.Single(selected.Attachments!);
        Assert.Equal(hit.Position, attachment.Position);
        Assert.Equal(hit.Variant, attachment.Variant);
        Assert.Equal(hit.OriginalName, attachment.OriginalFileName);
        Assert.Equal(hit.AttachmentRevisionKey, attachment.AttachmentRevisionKey);
        Assert.Equal(251, origin.PostCount);
        Assert.Equal("p000", Assert.Single(origin.GetPosts(0, 1)).PostId);
        Assert.Equal(250, origin.FindPostIndex(Post));
        Assert.Equal(-1, ArchivePage.ResolveFilenameHitPostIndex(origin, hit)); // same Post/name, other resource
        Assert.Equal(-1, ArchivePage.ResolveFilenameHitPostIndex(target, hit with { Post = "not-stored" }));
    }

    [Fact]
    public async Task NullOrMissingTargetsAreNotInventedAsFirstPostSelections()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        var hit = Assert.Single((await reader.SearchFilenamesAsync(new(Name))).Items.Where(row => row.Thread == Target));
        var view = (await reader.ReadThreadAsync(Target))!;
        Assert.Equal(-1, ArchivePage.ResolveFilenameHitPostIndex(view, null));
        Assert.Equal(-1, ArchivePage.ResolveFilenameHitPostIndex(null, hit));
        Assert.Equal(-1, ArchivePage.ResolveFilenameHitPostIndex(null, null));
        Assert.Equal(-1, ArchivePage.ResolveFilenameHitPostIndex(
            await reader.ReadThreadAsync(Target with { Thread = "absent" }), hit));
        Assert.Equal("p000", Assert.Single(view.GetPosts(0, 1)).PostId);
        Assert.Equal(-1, ArchivePage.ResolveFilenameHitPostIndex(view, hit with { Post = "P250" }));
        Assert.Equal(250, view.FindPostIndex(hit.Post)); // rejection did not mutate the snapshot
    }

    [Fact]
    public async Task EveryCapturedIdentityComponentMustStillMatchTheFreshSnapshot()
    {
        using TempDirectory temp = new();
        await SeedAsync(temp.Path);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(temp.Path);
        var hit = Assert.Single((await reader.SearchFilenamesAsync(new(Name))).Items.Where(row => row.Thread == Target));
        var view = (await reader.ReadThreadAsync(Target))!;
        foreach (var mismatch in new[]
        {
            hit with { Thread = Target with { Source = "other-source" } },
            hit with { Thread = Target with { Board = "other-board" } },
            hit with { Thread = Target with { Thread = "other-thread" } },
            hit with { Post = "p249" }, hit with { Position = hit.Position + 1 },
            hit with { Variant = "original" }, hit with { OriginalName = "changed.png" },
            hit with { AttachmentRevisionKey = "not-the-captured-key" },
            hit with { CatalogThreadRevision = hit.CatalogThreadRevision + 1 },
            hit with { PostIsDeleted = !hit.PostIsDeleted },
            hit with { AttachmentIsDeleted = !hit.AttachmentIsDeleted }
        }) Assert.Equal(-1, ArchivePage.ResolveFilenameHitPostIndex(view, mismatch));
        Assert.Equal(250, ArchivePage.ResolveFilenameHitPostIndex(view, hit));
    }

    private static async Task SeedAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        foreach (var id in new[] { Origin, Target })
        {
            const int count = 251;
            var posts = Enumerable.Range(0, count).Select(i => new CapturedPost(
                "p" + i.ToString("D3"), i, PostFieldPresence.All,
                Html: "synthetic stored text", Attachments: i == count - 1
                    ? new[] { new CapturedAttachment(3, "synthetic-media", "preview",
                        Url: "https://example.invalid/not-acquired", OriginalFileName: Name,
                        PlannedPath: Path.Combine(profile, "uncreated-media", "not-opened.png")) } : Array.Empty<CapturedAttachment>())).ToArray();
            await catalog.CaptureAsync(new(id, "https://example.invalid/not-acquired", "Synthetic navigation",
                ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, Path.Combine(profile, "uncreated-media"), posts));
        }
    }
}
