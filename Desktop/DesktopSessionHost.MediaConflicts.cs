using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

internal enum MediaConflictUserAction { Keep, Discard }
internal enum MediaConflictUserStatus { Open, Kept, Discarded, Promoted }
internal enum MediaConflictActionOutcome { Applied, AlreadyApplied, Missing, NotAllowed }

/// <summary>Content-free row: identities, digests and sizes only; never a path or file name.</summary>
internal sealed record MediaConflictRow(string Id, MeowChan.ResourceId Resource, string ActiveSha256,
    string CandidateSha256, long ActiveBytes, long CandidateBytes, MediaConflictUserStatus Status);

internal sealed record MediaConflictPage(int Total, IReadOnlyList<MediaConflictRow> Rows);

internal sealed partial class DesktopSessionHost
{
    internal async Task<MediaConflictPage> ListMediaConflictsAsync(int offset, int limit, CancellationToken token)
    {
        EnsureReady();
        await using ArchiveReader reader = await _content.OpenReaderAsync(token).ConfigureAwait(false);
        (int total, List<PersistedMediaConflict> items) = await reader.ReadMediaConflictsAsync(offset, limit, token).ConfigureAwait(false);
        return new(total, items.Select(item => new MediaConflictRow(item.Entry.Identity.IdentitySha256,
            item.Entry.Identity.Resource, item.Entry.Identity.ActiveVariantSha256, item.Entry.Identity.CandidateVariantSha256,
            item.Entry.Target.ByteLength, item.Entry.Candidate.ByteLength, MapStatus(item.Entry.Resolution))).ToArray());
    }

    // The user decision is recorded in the archive ledger only. It never changes the active value,
    // favorites, ratings or notes; Discard reclaims nothing until the cleanup path removes unreferenced bytes.
    internal async Task<MediaConflictActionOutcome> DecideMediaConflictAsync(string id, MediaConflictUserAction action,
        CancellationToken token)
    {
        EnsureReady();
        NativeMediaConflictResolutionStatus status = action switch
        {
            MediaConflictUserAction.Keep => NativeMediaConflictResolutionStatus.Kept,
            MediaConflictUserAction.Discard => NativeMediaConflictResolutionStatus.Discarded,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        ArchiveSql.MediaConflictDecisionOutcome outcome = await _content.Catalog
            .DecideMediaConflictAsync(id, status, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        return outcome switch
        {
            ArchiveSql.MediaConflictDecisionOutcome.Applied => MediaConflictActionOutcome.Applied,
            ArchiveSql.MediaConflictDecisionOutcome.AlreadyApplied => MediaConflictActionOutcome.AlreadyApplied,
            ArchiveSql.MediaConflictDecisionOutcome.Missing => MediaConflictActionOutcome.Missing,
            _ => MediaConflictActionOutcome.NotAllowed
        };
    }

    private static MediaConflictUserStatus MapStatus(NativeMediaConflictResolutionStatus status) => status switch
    {
        NativeMediaConflictResolutionStatus.Kept => MediaConflictUserStatus.Kept,
        NativeMediaConflictResolutionStatus.Discarded => MediaConflictUserStatus.Discarded,
        NativeMediaConflictResolutionStatus.Promoted => MediaConflictUserStatus.Promoted,
        _ => MediaConflictUserStatus.Open
    };
}
