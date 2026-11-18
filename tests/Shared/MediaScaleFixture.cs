using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.Tests.Shared;

// Shared by MeowChan.Tests (GUI-free micro-benchmarks) and the DesktopSmoke media-scale benchmark: a deterministic,
// synthetic "large collection" profile. Threads and bound manifests follow MediaPageChecks.WriteAsync, Danbooru posts
// BooruMediaChecks.SeedAsync. Every file is a tiny real PNG with a name suffix (verifiable hashes, little disk); the
// private tag file and the Booru catalog are written in one go in the stores' own document formats, because 20,000
// single store writes would themselves take hours (the cost the benchmark is meant to show). Nothing here touches a
// network or any profile other than the one passed in.

/// <summary>One benchmark size. Assignment counts follow the audit's "moderate" collection. The audit's tag counts
/// (2k/10k/20k) exceed the store's definition limit (PrivateTagSet.MaximumCount, 1,024 today), so the generated
/// vocabulary is capped there; <see cref="RequestedTags"/> keeps the audit's figure for the report.</summary>
internal sealed record MediaScaleTier(string Name, int Threads, int MediaPerThread, int RequestedTags, int ThreadTagAssignments,
    int MediaTagAssignments, int BooruPosts, int BooruTagsPerPost)
{
    internal static readonly MediaScaleTier S = new("S", 200, 100, 2_000, 500, 1_000, 500, 52);
    internal static readonly MediaScaleTier M = new("M", 1_000, 100, 10_000, 2_500, 5_000, 2_500, 52);
    // 46 claim tags keep L at about 30 MiB, below the default 32 MiB catalog write limit (52 gave 33.3 MiB).
    internal static readonly MediaScaleTier L = new("L", 2_000, 100, 20_000, 5_000, 10_000, 5_000, 46);

    internal const string Variable = "MEOWCHAN_MEDIA_SCALE";

    internal int Media => Threads * MediaPerThread;
    internal int Tags => Math.Min(RequestedTags, PrivateTagStore.MaximumTags);

    internal static MediaScaleTier Parse(string? name) => (name ?? "S").Trim().ToUpperInvariant() switch
    {
        "S" => S, "M" => M, "L" => L,
        _ => throw new ArgumentException("MEOWCHAN_MEDIA_SCALE must be S, M or L.")
    };

    internal static MediaScaleTier FromEnvironment() => Parse(Environment.GetEnvironmentVariable(Variable));
}

/// <summary>A file of one archived thread as the layout defines it; a missing one is captured but was never saved.</summary>
internal sealed record MediaScaleFile(ResourceId Thread, int Index, string PostId, string Name, string MediaId, bool Missing, bool Duplicate)
{
    internal byte[] Bytes => MediaScaleFixture.BytesOf(this);
}

/// <summary>What one generation wrote, with its timings (milliseconds).</summary>
internal sealed record MediaScaleFixtureSummary(string Tier, int Threads, int MediaFiles, int MissingAttachments, int DuplicateFiles,
    int RequestedTags, int Tags, int ThreadTagAssignments, int MediaTagAssignments, int BooruPosts, long PrivateTagFileBytes, long BooruCatalogBytes,
    long ManifestBytes, long MediaBytes, double FilesMilliseconds, double CatalogMilliseconds, double TagsMilliseconds,
    double BooruMilliseconds, double TotalMilliseconds);

internal static class MediaScaleFixture
{
    internal const string Source = "media-scale";
    internal const string NamePrefix = "img-";
    private static readonly string[] Boards = ["a", "b", "c", "d"];
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    // The same 17x11 PNG as MediaPageChecks; a trailing note gives each file its own bytes.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAABEAAAALCAIAAAAWQvFQAAAAF0lEQVR4nGO8oxHAQCJgIlXDqJ5hqQcAEJEBagzqPS4AAAAASUVORK5CYII=");

    internal static ResourceId ThreadId(int thread) =>
        new(Source, Boards[thread % Boards.Length], (100_000 + thread).ToString(CultureInfo.InvariantCulture));

    internal static string ThreadDirectory(string profile, int thread) =>
        Path.Combine(profile, "media", "t" + thread.ToString("D5", CultureInfo.InvariantCulture));

    internal static DateTimeOffset CapturedAt(int thread) => Epoch.AddHours(thread);

    /// <summary>Every 50th attachment was never saved (2 %); every 20th file has the same bytes as the same slot of the
    /// two neighbouring threads of its group of three (5 %), so equal-byte tiles exist.</summary>
    internal static IEnumerable<MediaScaleFile> FilesOf(MediaScaleTier tier, int thread)
    {
        ResourceId id = ThreadId(thread);
        long number = 100_000L + thread;
        for (int index = 0; index < tier.MediaPerThread; index++)
        {
            string post = (number * 1000 + index + 1).ToString(CultureInfo.InvariantCulture);
            string name = NamePrefix + thread.ToString("D4", CultureInfo.InvariantCulture) + "-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".png";
            yield return new MediaScaleFile(id, index, post, name, "m" + post, Missing: index % 50 == 49, Duplicate: index % 20 == 0);
        }
    }

    internal static byte[] BytesOf(MediaScaleFile file) => file.Duplicate
        ? [.. Png, .. Encoding.UTF8.GetBytes("dup-" + (int.Parse(file.Thread.Thread!, CultureInfo.InvariantCulture) / 3) + "-" + file.Index)]
        : [.. Png, .. Encoding.UTF8.GetBytes(file.Name)];

    internal static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    internal static string Md5Of(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
    internal static string UrlOf(string name) => "https://offline.invalid/" + name;

    /// <summary>MediaStore.GetAssetId for an original: the hash of requested name, media id, file name and SHA-256.</summary>
    internal static string AssetIdOf(string requestedName, string mediaId, string fileName, string sha256) =>
        Sha256Of(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { requestedName, mediaId, fileName, sha256.ToLowerInvariant() })));

    internal static MediaProviderMetadata ProviderOf(MediaScaleFile file, byte[] bytes) =>
        new(file.MediaId, "original", UrlOf(file.Name), file.Name, bytes.Length, Md5Of(bytes), Sha256Of(bytes));

    internal static string RevisionKeyOf(MediaScaleFile file, byte[] bytes) =>
        MediaRevisionKeys.CreateAttachmentKey(file.Thread, file.PostId, 0, ProviderOf(file, bytes));

    internal static Guid TagGuid(int tag)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("media-scale-tag-" + tag.ToString(CultureInfo.InvariantCulture)));
        return new Guid(hash.AsSpan(0, 16));
    }

    internal static string TagName(int tag) => "scale-" + tag.ToString("D5", CultureInfo.InvariantCulture);

    /// <summary>Writes the whole profile content (not settings.json). The profile must not be in use.</summary>
    internal static async Task<MediaScaleFixtureSummary> WriteAsync(string profile, MediaScaleTier tier, Action<string>? log = null,
        CancellationToken token = default)
    {
        Stopwatch total = Stopwatch.StartNew(), step = Stopwatch.StartNew();
        long manifestBytes = 0, mediaBytes = 0;
        // 1. Files and bound v3 manifests, written in parallel per thread (content does not depend on the order).
        CapturedThreadSnapshot[] snapshots = new CapturedThreadSnapshot[tier.Threads];
        await Parallel.ForAsync(0, tier.Threads, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (thread, cancel) =>
        {
            (CapturedThreadSnapshot snapshot, long manifest, long media) = await WriteThreadAsync(profile, tier, thread, cancel);
            snapshots[thread] = snapshot;
            Interlocked.Add(ref manifestBytes, manifest);
            Interlocked.Add(ref mediaBytes, media);
        });
        // The replicated asset identity and bindings must be exactly what the product reads back.
        for (int thread = 0; thread < tier.Threads; thread += Math.Max(1, tier.Threads / 10))
        {
            MediaAssetSnapshot read = await MediaAssetReader.OpenAsync(ThreadDirectory(profile, thread), token);
            int expected = FilesOf(tier, thread).Count(file => !file.Missing);
            if (read.Status != MediaManifestStatus.Available || read.Count != expected)
                throw new InvalidDataException("The generated manifest of thread " + thread + " is not read back as written: " + read.Status);
        }
        double files = step.Elapsed.TotalMilliseconds;
        log?.Invoke($"media-scale {tier.Name}: {tier.Media} files and {tier.Threads} manifests in {files / 1000:0.0} s");

        // 2. The archive catalog, one capture per thread as a real capture would leave it.
        step.Restart();
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease, token);
            foreach (CapturedThreadSnapshot snapshot in snapshots) await catalog.CaptureAsync(snapshot, token);
        }
        double catalogTime = step.Elapsed.TotalMilliseconds;
        log?.Invoke($"media-scale {tier.Name}: catalog captured in {catalogTime / 1000:0.0} s");

        // 3. The private tag file (format v1: tags, thread and bound media assignments).
        step.Restart();
        string tagPath = Path.Combine(profile, "private-tags.json");
        (int threadAssignments, int mediaAssignments) = WritePrivateTags(tagPath, tier);
        double tags = step.Elapsed.TotalMilliseconds;
        log?.Invoke($"media-scale {tier.Name}: {tier.Tags} private tags, {threadAssignments}+{mediaAssignments} assignments in {tags / 1000:0.0} s");

        // 4. Danbooru originals in their own post folders plus the catalog that links them.
        step.Restart();
        string catalogPath = await WriteBooruAsync(profile, tier, token);
        double booru = step.Elapsed.TotalMilliseconds;
        log?.Invoke($"media-scale {tier.Name}: {tier.BooruPosts} Danbooru posts, catalog {new FileInfo(catalogPath).Length / 1048576.0:0.0} MiB in {booru / 1000:0.0} s");

        int missing = Enumerable.Range(0, tier.Threads).Sum(thread => FilesOf(tier, thread).Count(file => file.Missing));
        int duplicates = Enumerable.Range(0, tier.Threads).Sum(thread => FilesOf(tier, thread).Count(file => file.Duplicate && !file.Missing));
        return new(tier.Name, tier.Threads, tier.Media - missing, missing, duplicates, tier.RequestedTags, tier.Tags, threadAssignments, mediaAssignments,
            tier.BooruPosts, new FileInfo(tagPath).Length, new FileInfo(catalogPath).Length, manifestBytes, mediaBytes,
            files, catalogTime, tags, booru, total.Elapsed.TotalMilliseconds);
    }

    private static async Task<(CapturedThreadSnapshot Snapshot, long ManifestBytes, long MediaBytes)> WriteThreadAsync(string profile,
        MediaScaleTier tier, int thread, CancellationToken token)
    {
        ResourceId id = ThreadId(thread);
        string directory = ThreadDirectory(profile, thread);
        Directory.CreateDirectory(directory);
        Dictionary<string, object> entries = new(StringComparer.Ordinal), assets = new(StringComparer.Ordinal);
        Dictionary<string, MediaAttachmentBinding> bindings = new(StringComparer.Ordinal);
        List<CapturedPost> posts = [new CapturedPost((100_000L + thread).ToString(CultureInfo.InvariantCulture), 0, PostFieldPresence.All,
            PostedAt: CapturedAt(thread), Html: "Synthetic large-collection thread " + thread)];
        long media = 0;
        foreach (MediaScaleFile file in FilesOf(tier, thread))
        {
            byte[] bytes = file.Bytes;
            string sha = Sha256Of(bytes), md5 = Md5Of(bytes);
            DateTimeOffset posted = CapturedAt(thread).AddMinutes(file.Index + 1);
            if (file.Missing)
            {
                // Captured, never saved: the page shows it only when "show missing" is on.
                posts.Add(new CapturedPost(file.PostId, file.Index + 1, PostFieldPresence.All, PostedAt: posted, Html: "missing file",
                    Attachments: [new CapturedAttachment(0, file.MediaId, "original", UrlOf(file.Name), file.Name)]));
                continue;
            }
            await File.WriteAllBytesAsync(Path.Combine(directory, file.Name), bytes, token);
            media += bytes.Length;
            var entry = new { MediaIdentifier = file.MediaId, Variant = "original", SourceUrl = UrlOf(file.Name), FileName = file.Name,
                OriginalFileName = file.Name, Length = bytes.Length, Md5 = md5, Sha256 = sha };
            entries[file.Name] = entry;
            string assetId = AssetIdOf(file.Name, file.MediaId, file.Name, sha);
            assets[assetId] = new { RequestedFileName = file.Name, Entry = entry };
            MediaProviderMetadata provider = ProviderOf(file, bytes);
            string key = MediaRevisionKeys.CreateAttachmentKey(id, file.PostId, 0, provider);
            bindings[key] = new(new(id, file.PostId, 0, key), MediaRevisionKeys.CreateAcquisitionKey(id, provider), assetId, provider,
                new(bytes.Length, md5, sha), new(MediaAcceptanceKind.Strict, false, false, DateTimeOffset.UnixEpoch));
            posts.Add(new CapturedPost(file.PostId, file.Index + 1, PostFieldPresence.All, PostedAt: posted, Html: "file " + file.Index,
                Attachments: [new CapturedAttachment(0, file.MediaId, "original", UrlOf(file.Name), file.Name,
                    ExpectedSize: bytes.Length, ExpectedMd5: md5) { ExpectedSha256 = sha }]));
        }
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { Version = 3, Entries = entries, Assets = assets, AttachmentBindings = bindings });
        await File.WriteAllBytesAsync(Path.Combine(directory, ".meowchan-manifest.json"), manifest, token);
        CapturedThreadSnapshot snapshot = new(id, "https://offline.invalid/" + id.Board + "/" + id.Thread,
            "Scale thread " + thread.ToString("D4", CultureInfo.InvariantCulture), ThreadSourceState.Archived, CapturedAt(thread), directory, posts);
        return (snapshot, manifest.Length, media);
    }

    /// <summary>The private tag file in the store's v1 document format (PrivateTagStore.Serialize); returns the assignment counts.</summary>
    internal static (int Threads, int Media) WritePrivateTags(string path, MediaScaleTier tier)
    {
        HashSet<(int, int)> threadPairs = new();
        for (int index = 0; threadPairs.Count < tier.ThreadTagAssignments; index++)
            threadPairs.Add((index % tier.Tags, (int)((index * 7919L + index / tier.Threads) % tier.Threads)));
        // Bound media: every present file in post order; picked with a stride so all threads carry some.
        List<MediaScaleFile> bound = Enumerable.Range(0, tier.Threads).SelectMany(thread => FilesOf(tier, thread)).Where(file => !file.Missing).ToList();
        HashSet<(int, int)> mediaPairs = new();
        for (int index = 0; mediaPairs.Count < tier.MediaTagAssignments; index++)
            mediaPairs.Add(((index * 13) % tier.Tags, (int)((index * 104_729L + index / bound.Count) % bound.Count)));

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", 1);
            writer.WriteNumber("Revision", 1);
            writer.WriteStartArray("Tags");
            for (int tag = 0; tag < tier.Tags; tag++)
            {
                writer.WriteStartObject();
                writer.WriteString("Id", TagGuid(tag).ToString("D"));
                writer.WriteString("Name", TagName(tag));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Threads");
            foreach ((int tag, int thread) in threadPairs)
            {
                ResourceId id = ThreadId(thread);
                writer.WriteStartObject();
                writer.WriteString("Tag", TagGuid(tag).ToString("D"));
                writer.WriteString("Source", id.Source); writer.WriteString("Board", id.Board); writer.WriteString("Thread", id.Thread);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("Media");
            foreach ((int tag, int media) in mediaPairs)
            {
                MediaScaleFile file = bound[media];
                byte[] bytes = file.Bytes;
                writer.WriteStartObject();
                writer.WriteString("Tag", TagGuid(tag).ToString("D"));
                writer.WriteString("Source", file.Thread.Source); writer.WriteString("Board", file.Thread.Board); writer.WriteString("Thread", file.Thread.Thread);
                writer.WriteString("Post", file.PostId);
                writer.WriteNumber("Position", 0);
                writer.WriteString("RevisionKey", RevisionKeyOf(file, bytes));
                writer.WriteString("MediaId", file.MediaId);
                writer.WriteString("Variant", "original");
                writer.WriteString("Role", "original");
                writer.WriteString("Sha256", Sha256Of(bytes));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
        return (threadPairs.Count, mediaPairs.Count);
    }

    internal static string BooruPostId(int post) => (7_000_000 + post).ToString(CultureInfo.InvariantCulture);

    private static async Task<string> WriteBooruAsync(string profile, MediaScaleTier tier, CancellationToken token)
    {
        DateTimeOffset observed = Epoch.AddDays(300);
        BooruPostClaimTagCategory[] categories = Enum.GetValues<BooruPostClaimTagCategory>();
        BooruPostRecord[] posts = new BooruPostRecord[tier.BooruPosts];
        await Parallel.ForAsync(0, tier.BooruPosts, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (post, cancel) =>
        {
            string id = BooruPostId(post), name = id + ".png", mediaId = BooruMediaIdentity.MediaIdentifier(id);
            string directory = Path.Combine(profile, "danbooru", id);
            Directory.CreateDirectory(directory);
            byte[] bytes = [.. Png, .. Encoding.UTF8.GetBytes("danbooru-" + id)];
            string sha = Sha256Of(bytes), md5 = Md5Of(bytes);
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, cancel);
            await File.WriteAllBytesAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = 1,
                Entries = new Dictionary<string, object>
                {
                    [name] = new { MediaIdentifier = mediaId, Variant = "original", SourceUrl = "https://offline.invalid/" + name, FileName = name,
                        OriginalFileName = name, Length = bytes.Length, Md5 = md5, Sha256 = sha }
                }
            }), cancel);
            BooruPostClaimTag[] tags = Enumerable.Range(0, tier.BooruTagsPerPost).Select(tag => new BooruPostClaimTag(
                "scale_tag_" + ((post * 31 + tag * 997) % 20_000).ToString("D5", CultureInfo.InvariantCulture) + "_" + tag.ToString(CultureInfo.InvariantCulture),
                categories[tag % categories.Length])).ToArray();
            BooruPostClaim claim = new("danbooru", id, BooruPostClaimPresence.All, $"https://danbooru.donmai.us/posts/{id}",
                CreatedAtUtc: observed.AddMinutes(-post), Rating: BooruPostClaimRating.General, Score: post % 500, FavoriteCount: post % 97,
                Tags: tags, Media:
                [
                    new BooruPostClaimMedia(BooruPostClaimMediaRole.Original, $"https://cdn.donmai.us/original/{id}.png", ".png", bytes.Length, 17, 11),
                    new BooruPostClaimMedia(BooruPostClaimMediaRole.Sample, $"https://cdn.donmai.us/sample/{id}.jpg", ".jpg", null, 17, 11),
                    new BooruPostClaimMedia(BooruPostClaimMediaRole.Preview, $"https://cdn.donmai.us/preview/{id}.jpg", ".jpg", null, 17, 11)
                ], SourceClaim: "https://source.example/art/" + id);
            posts[post] = new BooruPostRecord("danbooru", id, claim, observed.AddMinutes(post), observed.AddMinutes(post),
                AssetIdOf(name, mediaId, name, sha), Path.Combine(directory, name), OriginalIntent: BooruOriginalIntent.Linked);
        });
        if (tier.BooruPosts > 0)
        {
            MediaAssetSnapshot read = await MediaAssetReader.OpenAsync(Path.Combine(profile, "danbooru", BooruPostId(0)), token);
            if (read.FindById(posts[0].LocalAssetId!) == null)
                throw new InvalidDataException("The generated Danbooru asset identity is not the one the product reads.");
        }
        // The store's own document options (BooruCatalogStore.CreateJsonOptions); a single write instead of 2 per post.
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        string path = Path.Combine(profile, BooruCatalogStore.FileName);
        await File.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(
            new BooruCatalogSnapshot(BooruCatalogSnapshot.CurrentSchemaVersion, posts, Array.Empty<BooruWatcherRecord>()), options), token);
        // The store validates the whole document fail-closed: a generator mistake surfaces here, not as an empty catalog later.
        using (ProfileLease lease = ProfileLease.Acquire(profile))
            if (new BooruCatalogStore(profile, lease).Read().Posts.Count != tier.BooruPosts)
                throw new InvalidDataException("The generated Booru catalog is not read back completely.");
        return path;
    }
}
