using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Ratings;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

/// <summary>Realizes the fixed W14 20-media model as verified local PNG versions; no provider/network.</summary>
internal static class MediaRatingFixture
{
    internal static readonly ResourceId Thread = new("source", "b", "42");
    internal static readonly ResourceId OtherThread = new("other-source", "c", "99");
    internal sealed record Spec(int Index, string Media, string Variant, string ModelSha256, int? Rating,
        bool Favorite, bool Available, string[] Posts);
    internal static IReadOnlyList<Spec> Specs
    {
        get
        {
            using JsonDocument json = JsonDocument.Parse(ModelJson);
            return json.RootElement.GetProperty("assets").EnumerateArray().Select((item, index) => new Spec(index,
                item.GetProperty("media").GetString()!, item.GetProperty("variant").GetString()!,
                item.GetProperty("sha256").GetString()!, item.GetProperty("rating").ValueKind == JsonValueKind.Null
                    ? null : item.GetProperty("rating").GetInt32(),
                item.GetProperty("favorite").GetBoolean(), item.GetProperty("available").GetBoolean(),
                item.GetProperty("posts").EnumerateArray().Select(post => post.GetString()!).ToArray())).ToArray();
        }
    }
    internal static string ModelDigest => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ModelJson + "\n"))).ToLowerInvariant();
    internal static string MediaDirectory(string profile) => Path.Combine(profile, "rating-fixture-media");
    internal static async Task SeedAsync(string profile)
    {
        string directory = MediaDirectory(profile); Directory.CreateDirectory(directory);
        Dictionary<string, object> current = new(), assets = new();
        Dictionary<int, string> md5s = new();
        foreach (Spec spec in Specs)
        {
            using SKBitmap bitmap = new(120, 80);
            bitmap.Erase(new SKColor((byte)(30 + spec.Index * 9), (byte)(220 - spec.Index * 6), (byte)(40 + spec.Index * 7)));
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            byte[] bytes = png.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string filename = $"rating-{spec.Index:D2}.png", requested = spec.Media + ".png";
            md5s[spec.Index] = Convert.ToBase64String(MD5.HashData(bytes));
            var entry = new { MediaIdentifier = spec.Media, spec.Variant, SourceUrl = "https://offline.invalid/" + filename,
                FileName = filename, OriginalFileName = filename, Length = bytes.Length, Sha256 = hash, Md5 = md5s[spec.Index] };
            string identity = JsonSerializer.Serialize(new[] { requested, spec.Media, filename, hash });
            string assetId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            assets[assetId] = new { RequestedFileName = requested, Entry = entry }; current[requested] = entry;
            if (spec.Available) await File.WriteAllBytesAsync(Path.Combine(directory, filename), bytes);
        }
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"),
            JsonSerializer.Serialize(new { Version = 2, Entries = current, Assets = assets }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        if (manifest.Status != MediaManifestStatus.Available || manifest.Count != 20)
            throw new InvalidOperationException("Rating fixture must expose exactly 20 valid manifest versions.");
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        var posts = Specs.SelectMany(spec => spec.Posts.Select(post => (post, spec))).GroupBy(item => item.post)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select((group, ordinal) => new CapturedPost(group.Key, ordinal,
                PostFieldPresence.All, Html: "<p>Rating comparison post " + group.Key + "</p>",
                Attachments: group.GroupBy(item => item.spec.Media).Select((media, position) =>
                {
                    Spec spec = media.OrderByDescending(item => item.spec.Index).First().spec;
                    return new CapturedAttachment(position, spec.Media, spec.Variant, OriginalFileName: $"rating-{spec.Index:D2}.png",
                        ExpectedMd5: md5s[spec.Index]);
                }).ToArray())).ToArray();
        await catalog.CaptureAsync(new(Thread, "https://offline.invalid/b/thread/42", "URSA16 rating comparison",
            ThreadSourceState.Archived, new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero), directory, posts));
        if (Environment.GetEnvironmentVariable("MEOWCHAN_RATINGS_LATE_ERROR_ONLY") == "1")
            await catalog.CaptureAsync(new(OtherThread, "https://offline.invalid/c/thread/99", "Other source for late rating error",
                ThreadSourceState.Archived, new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), directory, posts));
        MediaRatingStore ratings = new(profile, lease); FavoriteStore favorites = new(profile, lease);
        foreach (Spec spec in Specs)
        {
            MediaAssetReference asset = manifest.GetPage(0, 100).Single(item => item.OriginalFileName == $"rating-{spec.Index:D2}.png");
            FavoriteAssetKey key = FavoriteAssetKey.From(Thread, asset);
            if (spec.Rating is int value) ratings.Set(key, value);
            if (spec.Favorite) favorites.SetFavorite(FavoriteStore.From(Thread, asset), true);
        }
    }
    // Byte-for-byte embedded model from reports/w14-alternatives.json; SHA reported in every run.
    private const string ModelJson = """
{
  "kind": "technical-model-counterexamples-not-product-or-user-validation",
  "assets": [
    {
      "thread": "source/b/42",
      "media": "m0",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
      "rating": 1,
      "favorite": true,
      "available": true,
      "posts": [
        "100"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m0",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000001",
      "rating": 5,
      "favorite": false,
      "available": true,
      "posts": [
        "100"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m2",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000002",
      "rating": 3,
      "favorite": false,
      "available": true,
      "posts": [
        "102",
        "199"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m3",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000003",
      "rating": 4,
      "favorite": true,
      "available": false,
      "posts": [
        "103"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m4",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000004",
      "rating": 5,
      "favorite": false,
      "available": true,
      "posts": [
        "104"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m5",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000005",
      "rating": 1,
      "favorite": false,
      "available": true,
      "posts": [
        "105"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m6",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000006",
      "rating": 2,
      "favorite": true,
      "available": true,
      "posts": [
        "106"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m7",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000007",
      "rating": 3,
      "favorite": false,
      "available": true,
      "posts": [
        "107"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m8",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000008",
      "rating": 4,
      "favorite": false,
      "available": true,
      "posts": [
        "108"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m9",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000009",
      "rating": 5,
      "favorite": true,
      "available": false,
      "posts": [
        "109"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m10",
      "variant": "original",
      "sha256": "000000000000000000000000000000000000000000000000000000000000000a",
      "rating": 1,
      "favorite": false,
      "available": true,
      "posts": [
        "110"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m11",
      "variant": "original",
      "sha256": "000000000000000000000000000000000000000000000000000000000000000b",
      "rating": 2,
      "favorite": false,
      "available": true,
      "posts": [
        "111"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m12",
      "variant": "original",
      "sha256": "000000000000000000000000000000000000000000000000000000000000000c",
      "rating": 3,
      "favorite": true,
      "available": true,
      "posts": [
        "112"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m13",
      "variant": "original",
      "sha256": "000000000000000000000000000000000000000000000000000000000000000d",
      "rating": 4,
      "favorite": false,
      "available": true,
      "posts": [
        "113"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m14",
      "variant": "original",
      "sha256": "000000000000000000000000000000000000000000000000000000000000000e",
      "rating": 5,
      "favorite": false,
      "available": true,
      "posts": [
        "114"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m15",
      "variant": "original",
      "sha256": "000000000000000000000000000000000000000000000000000000000000000f",
      "rating": null,
      "favorite": true,
      "available": true,
      "posts": [
        "115"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m16",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000010",
      "rating": null,
      "favorite": false,
      "available": true,
      "posts": [
        "116"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m17",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000011",
      "rating": null,
      "favorite": false,
      "available": true,
      "posts": [
        "117"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m18",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000012",
      "rating": null,
      "favorite": true,
      "available": true,
      "posts": [
        "118"
      ]
    },
    {
      "thread": "source/b/42",
      "media": "m19",
      "variant": "original",
      "sha256": "0000000000000000000000000000000000000000000000000000000000000013",
      "rating": null,
      "favorite": false,
      "available": true,
      "posts": [
        "119"
      ]
    }
  ],
  "exact_keys": 20,
  "missing_bytes_preserve_ratings": 2,
  "thread_rating_conflicts": 1,
  "post_rating_conflicts": 1,
  "favorite_equals_five_would_change_flags": 9,
  "numeric_and_star_presentation_have_same_data_domain": [
    1,
    2,
    3,
    4,
    5,
    null
  ],
  "native_accessibility_comparison": "not run",
  "user_walkthrough": "not run"
}
""";
}
