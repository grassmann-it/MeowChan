using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// RQ-08: a saved Danbooru original carries the profile's own (private) tags as a loose local file of its post. The provider's
// tags stay claims in the Booru catalog; equal bytes saved for two posts are two occurrences with separate tags.
[Collection("Avalonia global state")]
public sealed class BooruLocalMediaTagHostTests
{
    private static readonly byte[] Bytes = { 0x89, 0x50, 0x4E, 0x47, 7, 7, 7, 7 };
    private static readonly DateTimeOffset Observed = DateTimeOffset.Parse("2026-11-12T12:00:00Z");

    private static BooruPostClaim Claim(string id) => new("danbooru", id, BooruPostClaimPresence.All,
        $"https://danbooru.donmai.us/posts/{id}", CreatedAtUtc: Observed.AddHours(-2), Rating: BooruPostClaimRating.General, Score: 1, FavoriteCount: 1,
        Tags: [new BooruPostClaimTag("provider_tag", BooruPostClaimTagCategory.General)],
        Media: [new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, "https://cdn.donmai.us/original/a.png", ".png", Bytes.Length, 64, 64)]);

    private static async Task SeedPostAsync(string profile, string id, byte[] bytes)
    {
        string directory = Path.Combine(profile, "danbooru", id);
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, id + ".png"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            Entries = new Dictionary<string, object>
            {
                [id + ".png"] = new
                {
                    MediaIdentifier = BooruMediaIdentity.MediaIdentifier(id), Variant = "original",
                    SourceUrl = "https://cdn.donmai.us/original/a.png", FileName = id + ".png", OriginalFileName = id + ".png",
                    Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    Md5 = Convert.ToBase64String(MD5.HashData(bytes))
                }
            }
        }));
        string assetId = (await MediaAssetReader.OpenAsync(directory)).GetPage().Single().AssetId;
        using ProfileLease lease = ProfileLease.Acquire(profile);
        BooruCatalogStore store = new(profile, lease);
        store.UpsertPost(Claim(id), Observed);
        store.LinkLocalAsset("danbooru", id, assetId, Path.Combine(directory, id + ".png"));
    }

    private static async Task<MediaLibrarySnapshot> BuildAsync(DesktopSessionHost host) =>
        await MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync, readBooruCatalog: host.ReadBooruCatalog);

    private static async Task<(MediaLibraryEntry Indexed, GalleryEntry Gallery)> OpenAsync(DesktopSessionHost host, string post)
    {
        MediaLibrarySnapshot snapshot = await BuildAsync(host);
        MediaLibraryEntry indexed = snapshot.Entries.Single(entry => entry.BooruPostId == post);
        GalleryEntry gallery = Assert.Single(await MediaLibraryGallery.ResolveAsync(new[] { indexed }, host.OpenArchiveReaderAsync,
            MediaAssetReader.OpenAsync, default, host.ReadBooruCatalog));
        return (indexed, gallery);
    }

    private static MediaTagSession Session(DesktopSessionHost host, MediaTagSubject subject) =>
        new(subject, host.GetPublishedPrivateTagSnapshot, host.DefineAndSetMediaSubjectTagAsync, host.SetMediaSubjectTagAsync);

    private static string[] SourceTags(DesktopSessionHost host, string post) =>
        BooruMediaProjection.Find(host.ReadBooruCatalog()!, post)!.Tags.Select(tag => tag.Name).ToArray();

    [Fact]
    public async Task APrivateTagIsSetAndRemovedOnASavedDanbooruMediumAndTheProviderTagsStayUntouched()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        await SeedPostAsync(temp.Path, "123", Bytes);
        await using DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => new(temp.Path, Array.Empty<IImageBoard>()));
        await host.StartAsync();
        string[] provider = SourceTags(host, "123");
        (MediaLibraryEntry indexed, GalleryEntry gallery) = await OpenAsync(host, "123");

        // Both views describe the same local subject (gallery entry and media index entry).
        MediaTagSubject.Local fromGallery = Assert.IsType<MediaTagSubject.Local>(MediaTagSubjects.Of(gallery));
        MediaTagSubject.Local fromIndex = Assert.IsType<MediaTagSubject.Local>(MediaTagSubjects.Of(indexed));
        Assert.Equal(fromGallery.Observation, fromIndex.Observation);

        MediaTagSession session = Session(host, fromGallery);
        await session.AddAsync("mine");
        Assert.Null(session.MessageKey);
        TagId mine = Assert.Single(session.Assigned).Id;
        Assert.Equal("mine", host.GetPrivateTagSnapshot().Snapshot!.TagsOf(MediaTagSubjects.Of(indexed)!).Single().Name.Display);
        Assert.Equal(provider, SourceTags(host, "123")); // the provider claim is not overwritten and not merged
        Assert.DoesNotContain(host.GetPrivateTagSnapshot().Snapshot!.Tags, tag => tag.Name.Display == "provider_tag");

        await session.RemoveAsync(mine);
        Assert.Null(session.MessageKey);
        Assert.Empty(session.Assigned);
        Assert.Empty(host.GetPrivateTagSnapshot().Snapshot!.LocalAssignments);
        Assert.Equal(provider, SourceTags(host, "123"));
    }

    [Fact]
    public async Task EqualBytesFromTwoPostsAreTwoOccurrencesWithSeparatePrivateTagsAndSurviveARestart()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        await SeedPostAsync(temp.Path, "201", Bytes);
        await SeedPostAsync(temp.Path, "202", Bytes);
        LocalMediaTagTarget target;
        await using (DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => new(temp.Path, Array.Empty<IImageBoard>())))
        {
            await host.StartAsync();
            (_, GalleryEntry first) = await OpenAsync(host, "201");
            (_, GalleryEntry second) = await OpenAsync(host, "202");
            Assert.Equal(first.Asset.Sha256, second.Asset.Sha256);
            MediaTagSession session = Session(host, MediaTagSubjects.Of(first)!);
            await session.AddAsync("only-first");
            Assert.Null(session.MessageKey);
            target = Assert.IsType<MediaTagSubject.Local>(session.Subject).Expected!.Value;
            // Same bytes, other post: not tagged.
            Assert.Empty(host.GetPrivateTagSnapshot().Snapshot!.TagsOf(MediaTagSubjects.Of(second)!));
            await Session(host, MediaTagSubjects.Of(second)!).AddAsync("only-second");
            Assert.Equal(2, host.GetPrivateTagSnapshot().Snapshot!.LocalRegistry.Occurrences.Count);
        }
        await using (DesktopSessionHost restored = OwnedUiDispatcher.CreateHost(() => new(temp.Path, Array.Empty<IImageBoard>())))
        {
            await restored.StartAsync();
            PrivateTagStoreSnapshot snapshot = restored.GetPrivateTagSnapshot().Snapshot!;
            (MediaLibraryEntry indexed, GalleryEntry first) = await OpenAsync(restored, "201");
            (_, GalleryEntry second) = await OpenAsync(restored, "202");
            Assert.Equal(target, snapshot.ResolveLocal(Assert.IsType<MediaTagSubject.Local>(MediaTagSubjects.Of(first)!).Observation).Target);
            Assert.Equal("only-first", Assert.Single(snapshot.TagsOf(MediaTagSubjects.Of(first)!)).Name.Display);
            Assert.Equal("only-first", Assert.Single(snapshot.TagsOf(MediaTagSubjects.Of(indexed)!)).Name.Display);
            Assert.Equal("only-second", Assert.Single(snapshot.TagsOf(MediaTagSubjects.Of(second)!)).Name.Display);
            Assert.Equal("provider_tag", Assert.Single(SourceTags(restored, "201")));
            // Orphan inspection finds the files again through the Booru catalog: nothing is orphaned.
            LocalMediaTagInspection inspection = (await restored.InspectLocalMediaTagOrphansAsync())!;
            Assert.Equal(0, inspection.OrphanedFiles);
            Assert.Equal(0, inspection.UncheckedFiles);
        }
    }

    [Fact]
    public async Task AChangedSavedFileRefusesNewPrivateTagsButKeepsThemRemovable()
    {
        using TempDirectory temp = new();
        new Config().SaveConfig(temp.Path);
        await SeedPostAsync(temp.Path, "401", Bytes);
        await using DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => new(temp.Path, Array.Empty<IImageBoard>()));
        await host.StartAsync();
        (_, GalleryEntry gallery) = await OpenAsync(host, "401");
        MediaTagSession session = Session(host, MediaTagSubjects.Of(gallery)!);
        await session.AddAsync("first");
        TagId first = Assert.Single(session.Assigned).Id;
        byte[] changed = (byte[])Bytes.Clone();
        changed[4] ^= 0xFF;
        await File.WriteAllBytesAsync(Path.Combine(temp.Path, "danbooru", "401", "401.png"), changed);
        await session.AddAsync("second");
        Assert.Equal("tags.media.error.unavailable", session.MessageKey);
        await session.RemoveAsync(first);
        Assert.Empty(host.GetPrivateTagSnapshot().Snapshot!.LocalAssignments);
    }
}
