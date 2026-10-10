using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace oChan.Archive;

internal static class ArchiveValidation
{
    internal static void ValidatePage(int offset, int limit)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit < 1 || limit > 500) throw new ArgumentOutOfRangeException(nameof(limit));
    }

    internal static void ValidateId(ResourceId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        ValidateIdentifier(id.Source, nameof(id.Source));
        ValidateIdentifier(id.Board, nameof(id.Board));
        ValidateIdentifier(id.Thread, nameof(id.Thread));
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("An archive identity must be non-empty and contain no control characters.", name);
    }

    internal static void ValidateRegistration(ArchiveThreadRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ValidateId(registration.Id);
        ValidateUrl(registration.Url);
        ArgumentNullException.ThrowIfNull(registration.Title);
        ValidatePath(registration.MediaDirectory);
        if (!Enum.IsDefined(registration.SourceState)) throw new ArgumentOutOfRangeException(nameof(registration.SourceState));
    }

    internal static CapturedThreadSnapshot CopySnapshot(CapturedThreadSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateRegistration(new ArchiveThreadRegistration(snapshot.Id, snapshot.Url, snapshot.Title,
            snapshot.MediaDirectory, true, snapshot.SourceState));
        ArgumentNullException.ThrowIfNull(snapshot.Posts);
        HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
        List<CapturedPost> posts = new List<CapturedPost>();
        foreach (CapturedPost post in snapshot.Posts)
        {
            ArgumentNullException.ThrowIfNull(post);
            ValidateIdentifier(post.PostId, nameof(post.PostId));
            if (!ids.Add(post.PostId)) throw new ArgumentException("A captured snapshot contains duplicate post identities.");
            if (post.Ordinal < 0 || (post.Fields & ~PostFieldPresence.All) != 0)
                throw new ArgumentException("A captured post contains invalid ordering or presence flags.");
            HashSet<int> positions = new HashSet<int>();
            foreach (CapturedAttachment attachment in post.Attachments ?? Array.Empty<CapturedAttachment>())
            {
                ArgumentNullException.ThrowIfNull(attachment);
                if (attachment.Position < 0 || !positions.Add(attachment.Position))
                    throw new ArgumentException("Attachment positions must be nonnegative and unique per post.");
                if (attachment.MediaId == null)
                {
                    if (!attachment.IsDeleted) throw new ArgumentException("Only a deletion marker may omit its media identity.");
                }
                else ValidateIdentifier(attachment.MediaId, nameof(attachment.MediaId));
                ValidateIdentifier(attachment.Variant, nameof(attachment.Variant));
                if (attachment.Url != null) ValidateUrl(attachment.Url);
                if (attachment.PlannedPath != null) ValidatePath(attachment.PlannedPath);
                if (attachment.ExpectedSize < 0) throw new ArgumentOutOfRangeException(nameof(attachment.ExpectedSize));
                if (attachment.ExpectedMd5 != null)
                {
                    try
                    {
                        if (Convert.FromBase64String(attachment.ExpectedMd5).Length != 16)
                            throw new ArgumentException("ExpectedMd5 must contain a base64 MD5 checksum.");
                    }
                    catch (FormatException exception) { throw new ArgumentException("ExpectedMd5 must contain a base64 MD5 checksum.", exception); }
                }
            }
            posts.Add(ArchiveCaptureMerge.CopyPost(post));
        }
        return snapshot with { Posts = Array.AsReadOnly(posts.ToArray()) };
    }

    private static void ValidatePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("Archive media paths must be absolute.");
        _ = Path.GetFullPath(value);
    }

    private static void ValidateUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("An archive source URL must be an absolute HTTP or HTTPS URL.");
    }
}
