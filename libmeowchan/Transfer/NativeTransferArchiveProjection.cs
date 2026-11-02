using System;
using System.Collections.Immutable;
using System.Linq;
using System.IO;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Transfer;

/// <summary>
/// Metadata projection of one immutable archive read, not export admission. Does not read
/// media, resolve availability, inspect favorites or copy operational capture/watch decisions.
/// </summary>
internal static class NativeTransferArchiveProjection
{
    internal static TransferThread From(ArchivedThreadView view, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        cancellationToken.ThrowIfCancellationRequested();
        if (view.PostCount > 100_000) throw new InvalidDataException("The selected archive exceeds native transfer limits.");
        int attachmentCount = 0;
        ArchiveThreadSummary summary = view.Summary;
        ImmutableArray<TransferPost>.Builder posts = ImmutableArray.CreateBuilder<TransferPost>(view.PostCount);
        foreach (CapturedPost post in view.Posts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (CapturedAttachment attachment in post.Attachments ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++attachmentCount > 100_000) throw new InvalidDataException("The selected archive exceeds native transfer limits.");
                // Variant names are opaque without a role contract. No preview-role guessing.
                if (attachment.Variant != "original") throw new InvalidDataException("This attachment variant has no supported native export role.");
            }
            // PlannedPath is a local acquisition path, not revision metadata and never portable.
            ImmutableArray<TransferAttachment> attachments = (post.Attachments ?? [])
                .Select(a => new TransferAttachment(a with { PlannedPath = null }, TransferMediaRole.Original,
                    TransferContentStatus.Unknown, null, null)).ToImmutableArray();
            CapturedPost clean = post with { Attachments = post.Attachments == null ? null : attachments.Select(a => a.Metadata).ToImmutableArray() };
            posts.Add(new(clean, attachments) { AttachmentsWereNull = post.Attachments == null });
        }
        return new(summary.Id, summary.Url, summary.Title, summary.SourceState, summary.FirstSeenAt,
            summary.LastCapturedAt, summary.Revision, view.DecimalPostOrder, posts.ToImmutable());
    }
}
