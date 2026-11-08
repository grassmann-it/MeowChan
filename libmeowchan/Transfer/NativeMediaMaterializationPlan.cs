using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Transfer;

/// <summary>
/// Frozen, passive v4 member view used by the materialization workstream. This class only
/// interprets the already validated stage; it does not inspect files, acquire a profile lease,
/// create a candidate, publish SQL, or grant download/source authority.
/// </summary>
internal sealed class NativeMediaMaterializationPlan
{
    private NativeMediaMaterializationPlan(NativeTransferImportStage stage, ResourceId resource,
        NativeMediaCandidateSelection fullSelection)
    {
        Stage = stage;
        Package = stage.Package;
        Resource = resource;
        FullSelection = fullSelection;
    }

    internal NativeTransferImportStage Stage { get; }
    internal NativeTransferPackage Package { get; }
    internal ResourceId Resource { get; }
    internal NativeMediaCandidateSelection FullSelection { get; }

    /// <summary>
    /// Builds a one-thread public v4 member selection from the immutable validated stage.
    /// Unsupported private/multi-thread/legacy packages fail before any target or filesystem IO.
    /// </summary>
    internal static NativeMediaMaterializationPlan Create(NativeTransferImportStage stage,
        ResourceId resource, System.Threading.CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stage);
        ArgumentNullException.ThrowIfNull(resource);
        cancellationToken.ThrowIfCancellationRequested();
        NativeTransferPackage package = stage.Package;
        if (!package.IsChanV4 || package.PrivateMetadata != null || package.Catalog.Threads.Length != 1)
            throw new InvalidDataException("Only one public chan-v4 thread can be materialized by this plan.");
        TransferThread thread = package.Catalog.Threads[0];
        if (thread.Id != resource || thread.RetainedBindings.IsDefault
            || thread.Posts.IsDefault || thread.Posts.Any(post => post == null || post.Attachments.IsDefault))
            throw new InvalidDataException("The stage does not contain the requested public thread.");

        var members = ImmutableArray.CreateBuilder<NativeMediaSelectedMember>();
        foreach (TransferPost post in thread.Posts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (TransferAttachment attachment in post.Attachments)
                AddAttachment(attachment, post.Metadata.PostId, retained: false);
        }
        foreach (TransferRetainedBinding retained in thread.RetainedBindings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retained == null || retained.Attachment == null || retained.PostId == null)
                throw new InvalidDataException("Invalid retained transfer binding.");
            AddAttachment(retained.Attachment, retained.PostId, retained: true);
        }
        var full = NativeMediaCandidateSelection.Create(members.ToImmutable());
        HashSet<string> usedPayloads = full.Members.Where(m => m.Id.Role != NativeMediaMemberRole.OriginalEvidence
            && m.Descriptor.PayloadSha256 != null).Select(m => m.Descriptor.PayloadSha256!).ToHashSet(StringComparer.Ordinal);
        if (usedPayloads.Count != package.Envelope.Payloads.Length
            || package.Envelope.Payloads.Any(payload => !usedPayloads.Contains(payload.Sha256)))
            throw new InvalidDataException("The member plan does not cover the whole staged package.");
        cancellationToken.ThrowIfCancellationRequested();
        return new NativeMediaMaterializationPlan(stage, resource, full);

        void AddAttachment(TransferAttachment attachment, string postId, bool retained)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attachment == null || attachment.Metadata == null || postId == null)
                throw new InvalidDataException("Invalid transfer attachment.");
            if (attachment.Role != TransferMediaRole.Original || attachment.Metadata.Variant != "original"
                || attachment.Metadata.PlannedPath != null)
                throw new InvalidDataException("Only exact original occurrences can own passive member representations.");
            CapturedAttachment metadata = attachment.Metadata;
            MediaOccurrence occurrence = new(resource, postId, metadata.Position, metadata.AttachmentRevisionKey);
            TransferOriginalByteVersion? source = attachment.OriginalByteVersion;
            NativeMediaMemberId evidenceId = new(occurrence, source, NativeMediaMemberRole.OriginalEvidence);
            // Evidence is always selected. It carries no physical payload and therefore can
            // never manufacture a local Original asset or binding.
            TransferMeasuredBlob? originalMeasured = null;
            if (attachment.Status == TransferContentStatus.Contained)
            {
                if (metadata.AttachmentRevisionKey == null || attachment.PayloadSha256 == null
                    || !package.Envelope.MeasuredPayloads.TryGetValue(attachment.PayloadSha256, out originalMeasured))
                    throw new InvalidDataException("A contained original requires an exact key and measured staged payload.");
            }
            members.Add(new(new(evidenceId, metadata, retained, attachment.Status,
                attachment.PayloadSha256, attachment.History, originalMeasured), false));

            if (attachment.Status == TransferContentStatus.Contained)
            {
                if (attachment.Role != TransferMediaRole.Original || attachment.PayloadSha256 == null
                    || !package.Envelope.MeasuredPayloads.TryGetValue(attachment.PayloadSha256, out TransferMeasuredBlob? measured))
                    throw new InvalidDataException("A contained original has no measured staged payload.");
                if (source != null && (source.Sha256 != measured.Sha256 || source.Length != measured.Length
                    || source.Md5 != measured.Md5 || source.Sha1 != null && source.Sha1 != measured.Sha1))
                    throw new InvalidDataException("The original byte evidence differs from its staged payload.");
                NativeMediaMemberId payloadId = new(occurrence, source, NativeMediaMemberRole.OriginalPayload);
                members.Add(new(new(payloadId, metadata, retained, attachment.Status,
                    attachment.PayloadSha256, attachment.History, measured), false));
            }

            if (attachment.DerivedRepresentations.IsDefault)
                throw new InvalidDataException("Derived representation collection is not canonical.");
            foreach (TransferDerivedRepresentation derived in attachment.DerivedRepresentations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NativeMediaMemberRole role = derived.Kind switch
                {
                    TransferDerivedRepresentationKind.Preview => NativeMediaMemberRole.Preview,
                    TransferDerivedRepresentationKind.VideoPosterPng => NativeMediaMemberRole.VideoPosterPng,
                    _ => throw new InvalidDataException("Unknown derived representation kind.")
                };
                TransferMeasuredBlob? measured = null;
                if (derived.Status == TransferContentStatus.Contained)
                {
                    if (derived.PayloadSha256 == null
                        || !package.Envelope.MeasuredPayloads.TryGetValue(derived.PayloadSha256, out measured))
                        throw new InvalidDataException("A contained derived representation has no measured staged payload.");
                }
                NativeMediaMemberId derivedId = new(occurrence, derived.SourceByteVersion, role);
                members.Add(new(new(derivedId, metadata, retained, derived.Status,
                    derived.PayloadSha256, null, measured), false));
            }
        }
    }

    /// <summary>Validates a proposed safe subset without changing this frozen plan.</summary>
    internal void ValidateSelection(NativeMediaCandidateSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        FullSelection.ValidateSubset(selection);
    }

    internal NativeMediaCandidateSelection Select(IEnumerable<NativeMediaMemberId> members)
        => FullSelection.Select(members);
}
