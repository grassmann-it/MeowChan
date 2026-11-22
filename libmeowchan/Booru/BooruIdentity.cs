using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;

namespace MeowChan.Booru;

/// <summary>A provider-defined post in one host-configured source instance.</summary>
internal readonly record struct BooruPostKey(string SourceId, string InstanceId, string PostId)
{
    internal const string DefaultInstance = "default";

    internal static BooruPostKey FromLegacy(string sourceId, string postId) => new(sourceId, DefaultInstance, postId);

    internal bool IsLegacyDanbooru => IsLegacyScope(SourceId, InstanceId);

    internal bool IsValid => IsValidScope(SourceId, InstanceId) && IsToken(PostId)
        && (!IsLegacyDanbooru || DanbooruPostReference.IsValidPostId(PostId));

    internal static bool IsLegacyScope(string sourceId, string instanceId) =>
        sourceId == DanbooruPostReference.ProviderId && instanceId == DefaultInstance;

    internal static bool IsValidScope(string sourceId, string instanceId) => IsToken(sourceId) && IsToken(instanceId);

    private const int MaximumTokenLength = 128;

    internal static bool IsToken(string? value) => value is { Length: > 0 and <= MaximumTokenLength }
        && !value.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
        && !value.EnumerateRunes().Any(rune => rune == Rune.ReplacementChar);

    internal Uri? CanonicalDanbooruUrl => IsLegacyDanbooru && IsValid
        ? new Uri($"https://{DanbooruPostReference.MetadataHost}/posts/{PostId}") : null;
}

/// <summary>A logical medium and an exact variant of that medium within a provider post.</summary>
internal readonly record struct BooruMediaKey(BooruPostKey Post, string MediaId, BooruPostClaimMediaRole Variant)
{
    internal const string PrimaryMedia = "primary";

    internal static BooruMediaKey Original(BooruPostKey post) => new(post, PrimaryMedia, BooruPostClaimMediaRole.Original);

    // Preserve the existing MediaStore lookup for Danbooru receipts, including crash recovery.
    internal bool UsesLegacyLayout => Post.IsLegacyDanbooru && MediaId == PrimaryMedia
        && Variant == BooruPostClaimMediaRole.Original;

    internal string StorageId
    {
        get
        {
            if (!Post.IsValid || !BooruPostKey.IsToken(MediaId))
                throw new InvalidOperationException("The Booru media identity is invalid.");
            if (UsesLegacyLayout) return $"booru-danbooru-{Post.PostId}";
            string variant = Variant switch
            {
                BooruPostClaimMediaRole.Original => "original",
                BooruPostClaimMediaRole.Sample => "sample",
                BooruPostClaimMediaRole.Preview => "preview",
                _ => throw new InvalidOperationException("The Booru media variant is invalid.")
            };
            string encoded = string.Join('\0', Post.SourceId, Post.InstanceId, Post.PostId, MediaId, variant);
            return "booru-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)));
        }
    }
}
