using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeowChan.Storage;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    internal static CapturedThreadSnapshot StampObservedAttachments(CapturedThreadSnapshot snapshot)
    {
        snapshot = ArchiveValidation.CopySnapshot(snapshot);
        return snapshot with { Posts = Array.AsReadOnly(snapshot.Posts.Select(post => post with
        {
            Attachments = Array.AsReadOnly((post.Attachments ?? Array.Empty<CapturedAttachment>()).Select(attachment =>
            {
                // An identity-free deletion marker retains the previous full attachment during merge.
                if (attachment.MediaId == null) return attachment;
                string key = MediaRevisionKeys.CreateAttachmentKey(snapshot.Id, post.PostId, attachment.Position,
                    new(attachment.MediaId, attachment.Variant, attachment.Url!, attachment.OriginalFileName,
                        attachment.ExpectedSize, attachment.ExpectedMd5, attachment.ExpectedSha256) { ExpectedSha1 = attachment.ExpectedSha1, ProviderSizeHint = attachment.ProviderSizeHint });
                if (attachment.AttachmentRevisionKey != null && attachment.AttachmentRevisionKey != key)
                    throw new ArgumentException("The captured attachment revision differs from its provider metadata.");
                return attachment with { AttachmentRevisionKey = key };
            }).ToArray())
        }).ToArray()) };
    }

    private static CandidateMetadata CopyCandidateIntegrity(CandidateMetadata metadata)
    {
        if (metadata.ExpectedSha256 != null && !MediaIntegrityValidation.IsSha256(metadata.ExpectedSha256))
            throw new InvalidDataException("Invalid candidate provider SHA256.");
        MediaProviderMetadata provider = new(metadata.Key.MediaId, metadata.Key.Variant, metadata.DownloadUri.AbsoluteUri,
            metadata.OriginalFileName, metadata.ExpectedSize, metadata.ExpectedMd5, metadata.ExpectedSha256) { ExpectedSha1 = metadata.ExpectedSha1, ProviderSizeHint = metadata.ProviderSizeHint };
        MediaIntegrityValidation.ValidateProvider(provider);
        string acquisition = MediaRevisionKeys.CreateAcquisitionKey(metadata.Key.Resource, provider);
        if (metadata.AcquisitionRevisionKey != null && metadata.AcquisitionRevisionKey != acquisition)
            throw new InvalidDataException("The saved acquisition revision differs from its provider metadata.");
        ArgumentNullException.ThrowIfNull(metadata.AttachmentReferences);
        AttachmentRevisionReference[] references = metadata.AttachmentReferences.ToArray();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (AttachmentRevisionReference reference in references)
            if (reference == null || reference.Resource != metadata.Key.Resource
                || reference.RevisionKey != MediaRevisionKeys.CreateAttachmentKey(reference.Resource, reference.PostId, reference.Position, provider)
                || !seen.Add(reference.RevisionKey))
                throw new InvalidDataException("Invalid saved attachment acquisition references.");
        if (references.Length != 0 && metadata.AcquisitionRevisionKey == null)
            throw new InvalidDataException("Attachment references require an exact acquisition revision.");
        return metadata with { AttachmentReferences = references.Length == 0
            ? Array.Empty<AttachmentRevisionReference>() : Array.AsReadOnly(references) };
    }

    private static IReadOnlyList<AttachmentRevisionReference> ReadAttachmentReferences(string? json)
    {
        if (json == null) return Array.Empty<AttachmentRevisionReference>();
        try
        {
            AttachmentRevisionReference[] references = JsonSerializer.Deserialize<AttachmentRevisionReference[]>(json)
                ?? throw new InvalidDataException("Saved attachment references are empty.");
            return Array.AsReadOnly(references);
        }
        catch (JsonException error) { throw new InvalidDataException("Invalid saved attachment references.", error); }
    }
}
