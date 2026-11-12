using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources.Booru;

/// <summary>
/// Converts the public SDK booru contract into the core archive claim without making the core
/// library depend on the optional SDK assembly. This is a validation boundary, not a serializer:
/// unknown enums, presence mismatches and malformed provider values are rejected before storage.
/// </summary>
internal static class BooruClaimMapper
{
    internal static BooruPostClaim Map(BooruPost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        ValidatePost(post);

        BooruFieldPresence fields = post.Fields;
        return new BooruPostClaim(
            post.Id.SourceId,
            post.Id.PostId,
            MapPresence(fields),
            post.PostUrl?.AbsoluteUri,
            Has(fields, BooruFieldPresence.CreatedAt) ? post.CreatedAt : null,
            Has(fields, BooruFieldPresence.Rating) ? MapRating(post.Rating) : BooruPostClaimRating.Unknown,
            Has(fields, BooruFieldPresence.Rating) ? post.RawRating : null,
            Has(fields, BooruFieldPresence.Score) ? post.Score : null,
            Has(fields, BooruFieldPresence.FavoriteCount) ? post.FavoriteCount : null,
            Has(fields, BooruFieldPresence.Tags) ? MapTags(post.Tags) : Array.Empty<BooruPostClaimTag>(),
            Has(fields, BooruFieldPresence.Media) ? MapMedia(post.Media) : Array.Empty<BooruPostClaimMedia>(),
            Has(fields, BooruFieldPresence.SourceClaim) ? post.SourceClaim : null,
            Has(fields, BooruFieldPresence.IsDeleted) && post.IsDeleted,
            Has(fields, BooruFieldPresence.IsBanned) && post.IsBanned);
    }

    private static void ValidatePost(BooruPost post)
    {
        if (post.Id is null)
            throw Invalid("Post identity is required.");
        if (!string.Equals(post.Id.SourceId, DanbooruPostReference.ProviderId, StringComparison.Ordinal))
            throw Invalid("Only Danbooru posts can be mapped to the current core catalog.");
        if (!DanbooruPostReference.IsValidPostId(post.Id.PostId))
            throw Invalid("The Danbooru post identity is invalid.");

        BooruFieldPresence fields = post.Fields;
        if ((fields & ~BooruFieldPresence.All) != 0)
            throw Invalid("The booru post contains unknown field-presence flags.");

        if (!Has(fields, BooruFieldPresence.CreatedAt) && post.CreatedAt is not null
            || !Has(fields, BooruFieldPresence.Rating)
                && (post.Rating != BooruRating.Unknown || post.RawRating is not null)
            || !Has(fields, BooruFieldPresence.Score) && post.Score is not null
            || !Has(fields, BooruFieldPresence.FavoriteCount) && post.FavoriteCount is not null
            || !Has(fields, BooruFieldPresence.Tags) && post.Tags is not null
            || !Has(fields, BooruFieldPresence.Media) && post.Media is not null
            || !Has(fields, BooruFieldPresence.SourceClaim) && post.SourceClaim is not null
            || !Has(fields, BooruFieldPresence.IsDeleted) && post.IsDeleted
            || !Has(fields, BooruFieldPresence.IsBanned) && post.IsBanned)
            throw Invalid("The booru post contains a value without its presence flag.");

        if (post.PostUrl is not null)
        {
            if (!post.PostUrl.IsAbsoluteUri || post.PostUrl.Scheme != Uri.UriSchemeHttps
                || post.PostUrl.UserInfo.Length != 0 || !post.PostUrl.IsDefaultPort
                || post.PostUrl.Fragment.Length != 0
                || !DanbooruPostReference.TryParse(post.PostUrl.AbsoluteUri,
                    out DanbooruPostReference? reference, out _)
                || reference is null || reference.PostId != post.Id.PostId)
                throw Invalid("The booru post URL is not a matching canonical Danbooru post URL.");
        }

        _ = MapRating(post.Rating);
        ValidateText(post.RawRating, "RawRating", 16);
        ValidateText(post.SourceClaim, "SourceClaim", 4096);
        if (post.Score is < 0 || post.FavoriteCount is < 0)
            throw Invalid("Booru counters cannot be negative.");

        if (Has(fields, BooruFieldPresence.Tags)) ValidateTags(post.Tags);
        if (Has(fields, BooruFieldPresence.Media)) ValidateMedia(post.Media);
    }

    private static BooruPostClaimPresence MapPresence(BooruFieldPresence value)
    {
        if ((value & ~BooruFieldPresence.All) != 0)
            throw Invalid("The booru post contains unknown field-presence flags.");

        BooruPostClaimPresence mapped = BooruPostClaimPresence.None;
        if (Has(value, BooruFieldPresence.CreatedAt)) mapped |= BooruPostClaimPresence.CreatedAt;
        if (Has(value, BooruFieldPresence.Rating)) mapped |= BooruPostClaimPresence.Rating;
        if (Has(value, BooruFieldPresence.Score)) mapped |= BooruPostClaimPresence.Score;
        if (Has(value, BooruFieldPresence.FavoriteCount)) mapped |= BooruPostClaimPresence.FavoriteCount;
        if (Has(value, BooruFieldPresence.Tags)) mapped |= BooruPostClaimPresence.Tags;
        if (Has(value, BooruFieldPresence.Media)) mapped |= BooruPostClaimPresence.Media;
        if (Has(value, BooruFieldPresence.SourceClaim)) mapped |= BooruPostClaimPresence.SourceClaim;
        if (Has(value, BooruFieldPresence.IsDeleted)) mapped |= BooruPostClaimPresence.IsDeleted;
        if (Has(value, BooruFieldPresence.IsBanned)) mapped |= BooruPostClaimPresence.IsBanned;
        return mapped;
    }

    private static BooruPostClaimRating MapRating(BooruRating value) => value switch
    {
        BooruRating.Unknown => BooruPostClaimRating.Unknown,
        BooruRating.General => BooruPostClaimRating.General,
        BooruRating.Sensitive => BooruPostClaimRating.Sensitive,
        BooruRating.Questionable => BooruPostClaimRating.Questionable,
        BooruRating.Explicit => BooruPostClaimRating.Explicit,
        _ => throw Invalid("The booru rating enum value is unknown.")
    };

    private static IReadOnlyList<BooruPostClaimTag> MapTags(IReadOnlyList<BooruTag>? tags)
    {
        if (tags is null) return Array.Empty<BooruPostClaimTag>();
        HashSet<string> names = new(StringComparer.Ordinal);
        BooruPostClaimTag[] mapped = new BooruPostClaimTag[tags.Count];
        for (int index = 0; index < tags.Count; index++)
        {
            BooruTag? tag = tags[index];
            if (tag is null || !names.Add(tag.Name))
                throw Invalid("The booru post contains a missing or duplicate provider tag.");
            mapped[index] = new(tag.Name, MapTagCategory(tag.Category));
        }
        return Array.AsReadOnly(mapped);
    }

    private static BooruPostClaimTagCategory MapTagCategory(BooruTagCategory value) => value switch
    {
        BooruTagCategory.Unknown => BooruPostClaimTagCategory.Unknown,
        BooruTagCategory.General => BooruPostClaimTagCategory.General,
        BooruTagCategory.Artist => BooruPostClaimTagCategory.Artist,
        BooruTagCategory.Copyright => BooruPostClaimTagCategory.Copyright,
        BooruTagCategory.Character => BooruPostClaimTagCategory.Character,
        BooruTagCategory.Meta => BooruPostClaimTagCategory.Meta,
        _ => throw Invalid("The booru tag category enum value is unknown.")
    };

    private static IReadOnlyList<BooruPostClaimMedia> MapMedia(IReadOnlyList<BooruMediaVariant>? media)
    {
        if (media is null) return Array.Empty<BooruPostClaimMedia>();
        HashSet<BooruMediaRole> roles = new();
        BooruPostClaimMedia[] mapped = new BooruPostClaimMedia[media.Count];
        for (int index = 0; index < media.Count; index++)
        {
            BooruMediaVariant? variant = media[index];
            if (variant is null || !roles.Add(variant.Role))
                throw Invalid("The booru post contains a missing or duplicate media role.");
            mapped[index] = new(
                MapMediaRole(variant.Role),
                variant.Url?.AbsoluteUri,
                variant.Extension,
                variant.ExpectedSize,
                variant.Width,
                variant.Height,
                variant.ExpectedMd5);
        }
        return Array.AsReadOnly(mapped);
    }

    private static BooruPostClaimMediaRole MapMediaRole(BooruMediaRole value) => value switch
    {
        BooruMediaRole.Original => BooruPostClaimMediaRole.Original,
        BooruMediaRole.Sample => BooruPostClaimMediaRole.Sample,
        BooruMediaRole.Preview => BooruPostClaimMediaRole.Preview,
        _ => throw Invalid("The booru media role enum value is unknown.")
    };

    private static void ValidateTags(IReadOnlyList<BooruTag>? tags)
    {
        if (tags is null) return;
        if (tags.Count > 4096) throw Invalid("The booru post contains too many provider tags.");
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (BooruTag? tag in tags)
        {
            if (tag is null || string.IsNullOrWhiteSpace(tag.Name)
                || tag.Name.Any(char.IsControl) || tag.Name.Any(char.IsWhiteSpace)
                || System.Text.Encoding.UTF8.GetByteCount(tag.Name) > 256
                || !names.Add(tag.Name))
                throw Invalid("The booru post contains an invalid or duplicate provider tag.");
            _ = MapTagCategory(tag.Category);
        }
    }

    private static void ValidateMedia(IReadOnlyList<BooruMediaVariant>? media)
    {
        if (media is null) return;
        if (media.Count > 3) throw Invalid("The booru post contains too many media variants.");
        HashSet<BooruMediaRole> roles = new();
        foreach (BooruMediaVariant? variant in media)
        {
            if (variant is null || !roles.Add(variant.Role))
                throw Invalid("The booru post contains an invalid or duplicate media role.");
            _ = MapMediaRole(variant.Role);
            if (variant.Url is not null && (!variant.Url.IsAbsoluteUri || variant.Url.Scheme != Uri.UriSchemeHttps
                || variant.Url.UserInfo.Length != 0 || !variant.Url.IsDefaultPort || variant.Url.Fragment.Length != 0))
                throw Invalid("The booru media URL must be an absolute HTTPS URL without credentials.");
            ValidateText(variant.Extension, "Media extension", 33);
            if (variant.Extension is not null && (variant.Extension.Length < 2 || variant.Extension[0] != '.'
                || variant.Extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character))))
                throw Invalid("The booru media extension is invalid.");
            if (variant.ExpectedSize < 0 || variant.Width is <= 0 || variant.Height is <= 0)
                throw Invalid("The booru media dimensions or size are invalid.");
            ValidateMd5(variant.ExpectedMd5);
        }
    }

    private static void ValidateMd5(string? value)
    {
        if (value is null) return;
        Span<byte> bytes = stackalloc byte[16];
        if (!Convert.TryFromBase64String(value, bytes, out int written) || written != bytes.Length)
            throw Invalid("The booru media MD5 must be base64-encoded 16-byte data.");
    }

    private static void ValidateText(string? value, string name, int maximumLength)
    {
        if (value is null) return;
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw Invalid($"The booru {name} is outside its text bound.");
    }

    private static bool Has(BooruFieldPresence value, BooruFieldPresence flag) => (value & flag) != 0;

    private static InvalidDataException Invalid(string message) => new(message);
}
