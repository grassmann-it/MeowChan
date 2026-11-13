using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

// AP-01b: one resolver for gallery, archive, media page state/filter and selection; revalidation and orphans.
public sealed class LocalMediaTagResolverTests
{
    private static readonly ResourceId Loose = new("src", "g", "300");
    private static readonly byte[] Same = { 9, 8, 7 };
    private static readonly byte[] Other = { 1, 1, 2, 3, 5 };

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>An old (version 1) manifest: files only, no attachment bindings, as legacy captures left them.</summary>
    internal static async Task WriteLegacyManifestAsync(string directory, params (string Name, string MediaId, byte[] Bytes)[] files)
    {
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new();
        foreach ((string name, string media, byte[] bytes) in files)
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes);
            entries[name] = new { MediaIdentifier = media, Variant = "original", SourceUrl = "https://offline.invalid/" + name, FileName = name,
                OriginalFileName = name, Length = bytes.Length, Sha256 = Hex(bytes), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new { Version = 1, Entries = entries }));
    }

    /// <summary>A thread with two media directories: equal bytes twice in one, and the same slot again in the other.</summary>
    private static async Task<(string First, string Second)> SeedLooseAsync(string profile)
    {
        string first = Path.Combine(profile, "media", "loose"), second = Path.Combine(profile, "media", "loose-old");
        await WriteLegacyManifestAsync(first, ("a.png", "m1", Same), ("b.png", "m1", Same), ("c.webm", "m3", Other));
        await WriteLegacyManifestAsync(second, ("a.png", "m1", Same));
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        await catalog.RegisterThreadAsync(new(Loose, "https://offline.invalid/300", "Loose", first, true));
        await catalog.RegisterThreadAsync(new(Loose, "https://offline.invalid/300", "Loose", second, true));
        return (first, second);
    }

    private static Func<CancellationToken, Task<ArchiveReader>> Reader(string profile) => token => ArchiveReader.OpenAsync(profile, token);

    private static async Task<IReadOnlyList<GalleryEntry>> GalleryAsync(string profile, ResourceId thread)
    {
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile, default);
        ArchivedThreadView view = (await reader.ReadThreadAsync(thread))!;
        List<MediaAssetSnapshot> manifests = new();
        foreach (string directory in await reader.GetMediaDirectoriesAsync(thread)) manifests.Add(await MediaAssetReader.OpenAsync(directory));
        return GallerySelection.Build(view, manifests, allSavedVersions: true, includeOtherMedia: true).Entries;
    }

    private static LocalMediaObservation Observation(MediaTagSubject? subject) => Assert.IsType<MediaTagSubject.Local>(subject).Observation;

    [Fact]
    public async Task LooseLegacyFilesResolveToTheSameLocalSubjectInTheGalleryTheIndexAndAfterACacheRoundTrip()
    {
        using TempDirectory temp = new();
        (string first, string second) = await SeedLooseAsync(temp.Path);
        IReadOnlyList<GalleryEntry> gallery = await GalleryAsync(temp.Path, Loose);
        MediaLibrarySnapshot index = await MediaLibraryIndexTests.BuildAsync(temp.Path);
        string cache = Path.Combine(temp.Path, "cache", MediaLibraryIndexCache.FileName);
        MediaLibraryIndexCache.Save(cache, index);
        MediaLibrarySnapshot cached = MediaLibraryIndexCache.Load(cache)!;

        Assert.Equal(4, gallery.Count);
        Assert.All(gallery, entry => Assert.True(entry.LocalTagEligible));
        foreach (GalleryEntry entry in gallery)
        {
            LocalMediaObservation fromGallery = Observation(MediaTagSubjects.Of(entry, out GalleryMediaTagUnavailable reason));
            Assert.Equal(GalleryMediaTagUnavailable.None, reason);
            Assert.Null(GalleryMediaTagTarget.Resolve(entry, out _)); // The bound resolver itself is unchanged.
            MediaLibraryEntry indexed = Assert.Single(index.Entries, item => item.Directory == entry.Manifest.Directory && item.AssetId == entry.Asset.AssetId);
            Assert.Equal(entry.Asset.RequestedFileName, indexed.LocalSlot);
            Assert.Equal(fromGallery, Observation(MediaTagSubjects.Of(indexed)));
            MediaLibraryEntry fromCache = Assert.Single(cached.Entries, item => item.Directory == indexed.Directory && item.AssetId == indexed.AssetId);
            Assert.Equal(indexed, fromCache);
            Assert.Equal(fromGallery, Observation(MediaTagSubjects.Of(fromCache)));
        }
        // Equal bytes: two slots of one manifest, and the same slot (same manifest-local asset ID) in another directory.
        LocalMediaObservation a1 = Observation(MediaTagSubjects.Of(gallery.Single(e => e.Manifest.Directory == first && e.Asset.RequestedFileName == "a.png")));
        LocalMediaObservation b1 = Observation(MediaTagSubjects.Of(gallery.Single(e => e.Manifest.Directory == first && e.Asset.RequestedFileName == "b.png")));
        LocalMediaObservation a2 = Observation(MediaTagSubjects.Of(gallery.Single(e => e.Manifest.Directory == second)));
        Assert.Equal(a1.Sha256, b1.Sha256);
        Assert.NotEqual(a1.AssetId, b1.AssetId);
        Assert.Equal(a1.AssetId, a2.AssetId);
        Assert.NotEqual(a1.Locator, a2.Locator);
    }

    [Fact]
    public async Task TaggingOneOfThreeEqualByteFilesShowsOnlyOnItInStateFilterCountsAndSelection()
    {
        using TempDirectory temp = new();
        (string first, _) = await SeedLooseAsync(temp.Path);
        MediaLibrarySnapshot index = await MediaLibraryIndexTests.BuildAsync(temp.Path);
        MediaLibraryEntry a1 = index.Entries.Single(e => e.Directory == first && e.LocalSlot == "a.png");
        MediaLibraryEntry[] twins = index.Entries.Where(e => e.Sha256 == a1.Sha256 && e != a1).ToArray();
        Assert.Equal(2, twins.Length);
        PrivateTagStoreSnapshot tags;
        TagId cat, rpt;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStore store = new(temp.Path, lease);
            cat = store.Define("cat").Tag;
            rpt = store.Define("rpt").Tag;
            store.SetThread(rpt, Loose, true);
            tags = store.SetMediaSubjects(cat, [MediaTagSubjects.Of(a1)!], true).Snapshot;
        }
        MediaLibraryUserData data = new(tags, [], []);

        MediaLibraryEntryState state = MediaLibraryEntryState.Of(a1, data);
        Assert.Equal(new[] { "cat" }, state.MediaTags);
        Assert.Equal(new[] { "rpt" }, state.ThreadTags); // Thread tags are listed beside, never merged.
        Assert.All(twins, twin => Assert.Empty(MediaLibraryEntryState.Of(twin, data).MediaTags));
        Assert.All(index.Entries, entry => Assert.True(MediaLibraryEntryState.Of(entry, data).CanEditMediaTags));

        MediaLibraryFilter onlyCat = new(new ArchiveTagFilter([cat.Value], [], ArchiveTagMatch.All));
        Assert.Equal(new[] { a1 }, MediaLibraryFilterEngine.Apply(index.Entries, onlyCat, data));
        Assert.Equal(index.Entries.Count, MediaLibraryFilterEngine.Apply(index.Entries, new MediaLibraryFilter(new ArchiveTagFilter([rpt.Value], [], ArchiveTagMatch.All)), data).Count);
        Assert.Equal(1, tags.DistinctMediaCounts()[cat]);

        IReadOnlyList<MediaLibraryTile> tiles = MediaLibraryGrouping.Build(index.Entries, index.Entries, MediaLibrarySort.ByThread, includeMissing: true);
        IReadOnlyList<MediaTagCandidate> candidates = MediaLibraryTagging.Candidates(tiles);
        Assert.All(candidates, candidate => Assert.Equal(MediaTagBlock.None, candidate.Block));
        Assert.Equal(new MediaTagSubject[] { MediaTagSubjects.Of(a1)! }, MediaLibraryTagging.TargetsToRemove(tags, cat, candidates));
        Assert.Equal(3, MediaLibraryTagging.TargetsToAdd(tags, cat, candidates).Count);
    }

    // T1: a loose file counts as untagged until its own local registration carries a media tag; its equal-byte twins stay untagged,
    // and the thread tag that every one of them carries does not change that.
    [Fact]
    public async Task WithoutMediaTagsResolvesLooseFilesThroughTheirLocalRegistration()
    {
        using TempDirectory temp = new();
        (string first, _) = await SeedLooseAsync(temp.Path);
        MediaLibrarySnapshot index = await MediaLibraryIndexTests.BuildAsync(temp.Path);
        MediaLibraryEntry a1 = index.Entries.Single(e => e.Directory == first && e.LocalSlot == "a.png");
        PrivateTagStoreSnapshot tags;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStore store = new(temp.Path, lease);
            TagId cat = store.Define("cat").Tag;
            store.SetThread(store.Define("rpt").Tag, Loose, true);
            tags = store.SetMediaSubjects(cat, [MediaTagSubjects.Of(a1)!], true).Snapshot;
        }
        MediaLibraryUserData data = new(tags, [], []);
        Assert.All(index.Entries, entry => Assert.NotEmpty(data.TagsOf(entry))); // The tag filter finds all of them by the thread tag.

        IReadOnlyList<MediaLibraryEntry> untagged = MediaLibraryFilterEngine.Apply(index.Entries, new MediaLibraryFilter(withoutMediaTags: true), data);
        Assert.Equal(index.Entries.Where(entry => entry != a1).OrderBy(entry => entry.AssetId + entry.Directory),
            untagged.OrderBy(entry => entry.AssetId + entry.Directory));
        // The tagged copy's twins keep the shared bytes' tile in the count; the other file is a tile of its own.
        Assert.Equal(2, MediaLibraryUntagged.Of(index.Entries, data)!.CountTiles(index.Entries));
    }

    [Fact]
    public void ALegacyViewOfAFileThatIsShownBoundStaysInTheBoundNamespaceAndADamagedManifestOffersNothing()
    {
        ResourceId thread = new("src", "g", "400");
        string directory = Path.Combine(Path.GetTempPath(), "local-tag-namespace-fixture");
        byte[] bytes = { 4, 2 };
        MediaProviderMetadata provider = new("m1", "original", "https://offline.invalid/x.png", "x.png", 2, null, null);
        MediaActualIntegrity actual = new(2, Convert.ToBase64String(MD5.HashData(bytes)), Hex(bytes).ToLowerInvariant());
        string assetId = Hex(Encoding.UTF8.GetBytes("x")).ToLowerInvariant();
        MediaAssetReference asset = new(assetId, "m1", "original", "x.png", Path.Combine(directory, "x.png"), 2, actual.Sha256,
            provider.Url, "x.png", true) { Md5 = actual.Md5 };
        string key = MediaRevisionKeys.CreateAttachmentKey(thread, "100", 0, provider);
        MediaAttachmentBinding binding = new(new(thread, "100", 0, key), MediaRevisionKeys.CreateAcquisitionKey(thread, provider), assetId,
            provider, actual, new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch));
        MediaAssetSnapshot manifest = new(directory, MediaManifestStatus.Available, 3, "g", [asset],
            attachmentBindings: new Dictionary<string, MediaAttachmentBinding> { [key] = binding });
        CapturedAttachment boundAttachment = new(0, "m1", "original", provider.Url, "x.png", ExpectedSize: 2) { AttachmentRevisionKey = key };
        // A second, legacy post (no revision key) whose metadata matches the same file.
        CapturedAttachment legacyAttachment = new(0, "m1", "original", provider.Url, "x.png", ExpectedSize: 2);
        ArchiveThreadSummary summary = new(thread, "https://offline.invalid/400", "T", directory, ThreadSourceState.Archived, true,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 2, 1);
        ArchivedThreadView view = new(summary, [
            new CapturedPost("100", 0, PostFieldPresence.All, Html: "a", Attachments: [boundAttachment]),
            new CapturedPost("101", 1, PostFieldPresence.All, Html: "b", Attachments: [legacyAttachment])], [directory], decimalPostOrder: true);

        IReadOnlyList<GalleryEntry> entries = GallerySelection.Build(view, [manifest], allSavedVersions: true, includeOtherMedia: true).Entries;

        GalleryEntry bound = Assert.Single(entries, entry => entry.Post?.PostId == "100");
        GalleryEntry legacy = Assert.Single(entries, entry => entry.Post?.PostId == "101");
        Assert.IsType<MediaTagSubject.Bound>(MediaTagSubjects.Of(bound));
        Assert.True(legacy.AttachmentResolution is { IsLegacy: true });
        Assert.False(legacy.LocalTagEligible);
        Assert.Null(MediaTagSubjects.Of(legacy, out GalleryMediaTagUnavailable reason));
        Assert.Equal(GalleryMediaTagUnavailable.NoPostVersion, reason);

        // A damaged sibling manifest hides bindings, so no loose view is offered at all.
        MediaAssetSnapshot damaged = new(directory + "-damaged", MediaManifestStatus.Corrupt, null, null, []);
        IReadOnlyList<GalleryEntry> unreliable = GallerySelection.Build(view, [manifest, damaged], allSavedVersions: true, includeOtherMedia: true).Entries;
        Assert.All(unreliable, entry => Assert.False(entry.LocalTagEligible));
        // An entry made outside the selection (no eligibility mark) is never locally taggable.
        GalleryEntry synthetic = new(new GalleryAssetKey(thread, directory, assetId), manifest, asset, null, null, false, true);
        Assert.Null(MediaTagSubjects.Of(synthetic));
    }

    [Fact]
    public async Task RevalidationVerifiesMembershipAndBytesAndNamesWhatChanged()
    {
        using TempDirectory temp = new();
        (string first, string second) = await SeedLooseAsync(temp.Path);
        IReadOnlyList<GalleryEntry> gallery = await GalleryAsync(temp.Path, Loose);
        LocalMediaObservation a = Observation(MediaTagSubjects.Of(gallery.Single(e => e.Manifest.Directory == first && e.Asset.RequestedFileName == "a.png")));
        LocalMediaObservation b = Observation(MediaTagSubjects.Of(gallery.Single(e => e.Manifest.Directory == first && e.Asset.RequestedFileName == "b.png")));
        LocalMediaObservation c = Observation(MediaTagSubjects.Of(gallery.Single(e => e.Asset.RequestedFileName == "c.webm")));
        LocalMediaObservation old = Observation(MediaTagSubjects.Of(gallery.Single(e => e.Manifest.Directory == second)));
        Assert.True(LocalMediaObservation.TryCreate(new ResourceId("src", "g", "999"), first, "a.png", a.AssetId, a.Length, a.Sha256, out LocalMediaObservation? foreignThread));
        Assert.True(LocalMediaObservation.TryCreate(Loose, first, "a.png", new string('0', 64), a.Length, a.Sha256, out LocalMediaObservation? unknownAsset));
        Assert.True(LocalMediaObservation.TryCreate(Loose, first, "b.png", a.AssetId, a.Length, a.Sha256, out LocalMediaObservation? wrongSlot));
        Assert.True(LocalMediaObservation.TryCreate(Loose, Path.Combine(temp.Path, "moved"), "a.png", a.AssetId, a.Length, a.Sha256, out LocalMediaObservation? moved));

        Assert.Equal(Enumerable.Repeat(LocalMediaCheck.Verified, 4),
            await LocalMediaTagRevalidation.CheckAsync([a, b, c, old], Reader(temp.Path), MediaAssetReader.OpenAsync, true, default));
        Assert.Equal(new[] { LocalMediaCheck.Missing, LocalMediaCheck.Missing, LocalMediaCheck.NotLoose, LocalMediaCheck.Missing },
            await LocalMediaTagRevalidation.CheckAsync([foreignThread!, unknownAsset!, wrongSlot!, moved!], Reader(temp.Path), MediaAssetReader.OpenAsync, true, default));

        // Same length, other bytes: only a full verification sees it. A missing file is seen by both.
        await File.WriteAllBytesAsync(Path.Combine(first, "b.png"), new byte[] { 0, 0, 0 });
        File.Delete(Path.Combine(first, "c.webm"));
        Assert.Equal(new[] { LocalMediaCheck.Verified, LocalMediaCheck.Changed, LocalMediaCheck.Changed },
            await LocalMediaTagRevalidation.CheckAsync([a, b, c], Reader(temp.Path), MediaAssetReader.OpenAsync, true, default));
        Assert.Equal(new[] { LocalMediaCheck.Verified, LocalMediaCheck.Verified, LocalMediaCheck.Changed },
            await LocalMediaTagRevalidation.CheckAsync([a, b, c], Reader(temp.Path), MediaAssetReader.OpenAsync, false, default));

        // An unreadable manifest concludes nothing; cancellation is not a result.
        await File.WriteAllTextAsync(Path.Combine(second, ".meowchan-manifest.json"), "{damaged");
        Assert.Equal(new[] { LocalMediaCheck.Unreadable },
            await LocalMediaTagRevalidation.CheckAsync([a], Reader(temp.Path), MediaAssetReader.OpenAsync, true, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LocalMediaTagRevalidation.CheckAsync([a], Reader(temp.Path), MediaAssetReader.OpenAsync, true, new CancellationToken(true)));
    }

    [Fact]
    public async Task OrphanInspectionKeepsMissingMovedAndReplacedFilesVisibleWithoutRetargeting()
    {
        using TempDirectory temp = new();
        (string first, string second) = await SeedLooseAsync(temp.Path);
        IReadOnlyList<GalleryEntry> gallery = await GalleryAsync(temp.Path, Loose);
        MediaTagSubject[] subjects = gallery.Select(entry => MediaTagSubjects.Of(entry)!).ToArray();
        TagId cat;
        PrivateTagStoreSnapshot tagged;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStore store = new(temp.Path, lease);
            cat = store.Define("cat").Tag;
            tagged = store.SetMediaSubjects(cat, subjects, true).Snapshot;
        }
        LocalMediaTagInspection clean = await LocalMediaTagRevalidation.InspectAsync(tagged, Reader(temp.Path), MediaAssetReader.OpenAsync, default);
        Assert.Empty(clean.Orphans);
        Assert.Equal(0, clean.UncheckedFiles);

        // b goes missing; the second folder is moved away (its manifest record no longer exists where it was).
        File.Delete(Path.Combine(first, "b.png"));
        Directory.Move(second, second + "-moved");
        LocalMediaTagInspection inspection = await LocalMediaTagRevalidation.InspectAsync(tagged, Reader(temp.Path), MediaAssetReader.OpenAsync, default);

        Assert.Equal(tagged.Revision, inspection.Revision);
        Assert.Equal(2, inspection.OrphanedFiles);
        Assert.Equal(2, inspection.Orphans.Count);
        HashSet<LocalMediaTagTarget> orphaned = inspection.Orphans.Select(item => item.Target).ToHashSet();
        LocalMediaTagTarget b = tagged.ResolveLocal(Observation(subjects[Array.FindIndex(gallery.ToArray(), e => e.Manifest.Directory == first && e.Asset.RequestedFileName == "b.png")])).Target!.Value;
        Assert.Contains(b, orphaned);
        // Nothing was retargeted: the moved copy is not adopted under its new folder, the assignments are untouched.
        Assert.Equal(tagged.LocalAssignments, (await Task.FromResult(tagged)).LocalAssignments);
        Assert.Equal(LocalMediaResolutionState.NewOccurrence, tagged.ResolveLocal(MovedObservation(tagged, second + "-moved")).State);
    }

    private static LocalMediaObservation MovedObservation(PrivateTagStoreSnapshot snapshot, string directory)
    {
        LocalMediaOccurrence occurrence = snapshot.LocalRegistry.Occurrences.First(item => item.Locator.Directory.EndsWith("loose-old", StringComparison.Ordinal));
        LocalMediaRevision revision = occurrence.Revisions.Single();
        Assert.True(LocalMediaObservation.TryCreate(occurrence.Locator.Thread, directory, occurrence.Locator.RequestedFileName, revision.AssetId,
            revision.Length, revision.Sha256, out LocalMediaObservation? observation));
        return observation!;
    }

    [Fact]
    public async Task AFileThatBecomesBoundIsNoLongerLooseAndItsLocalTagsBecomeAVisibleOrphan()
    {
        using TempDirectory temp = new();
        string directory = Path.Combine(temp.Path, "media", "rebound");
        ResourceId thread = new("src", "g", "500");
        // First a loose file in a v3 manifest without bindings, then the same file bound to a newly captured post.
        await MediaLibraryIndexTests.WriteManifestAsync(directory, thread, new MediaLibraryIndexTests.File_("x.png", "m1", Same));
        await File.WriteAllBytesAsync(Path.Combine(directory, "x.png"), Same);
        await MediaLibraryIndexTests.CaptureAsync(temp.Path, thread, "Rebound", directory);
        GalleryEntry looseView = Assert.Single(await GalleryAsync(temp.Path, thread));
        MediaTagSubject subject = MediaTagSubjects.Of(looseView)!;
        PrivateTagStoreSnapshot tagged;
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            PrivateTagStore store = new(temp.Path, lease);
            tagged = store.SetMediaSubjects(store.Define("cat").Tag, [subject], true).Snapshot;
        }

        await MediaLibraryIndexTests.WriteManifestAsync(directory, thread, new MediaLibraryIndexTests.File_("x.png", "m1", Same, "100"));
        await MediaLibraryIndexTests.CaptureAsync(temp.Path, thread, "Rebound", directory,
            new CapturedPost("100", 0, PostFieldPresence.All, Html: "op", Attachments: [MediaLibraryIndexTests.Attachment(0, "m1", "x.png", Same)]));
        GalleryEntry boundView = Assert.Single(await GalleryAsync(temp.Path, thread));

        Assert.IsType<MediaTagSubject.Bound>(MediaTagSubjects.Of(boundView));
        Assert.Empty(tagged.TagsOf(MediaTagSubjects.Of(boundView)!)); // No automatic copy into the bound namespace.
        Assert.Equal(new[] { LocalMediaCheck.NotLoose },
            await LocalMediaTagRevalidation.CheckAsync([Observation(subject)], Reader(temp.Path), MediaAssetReader.OpenAsync, true, default));
        LocalMediaTagInspection inspection = await LocalMediaTagRevalidation.InspectAsync(tagged, Reader(temp.Path), MediaAssetReader.OpenAsync, default);
        Assert.Single(inspection.Orphans);
    }
}
