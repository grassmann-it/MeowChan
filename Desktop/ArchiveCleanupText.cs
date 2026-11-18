using System;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal static class ArchiveCleanupText
{
    internal static string Title => Texts.T("archive.cleanup.title");
    internal static string Identity(ResourceId id) => $"{id.Source} /{id.Board}/ {id.Thread}";
    internal static string State(ArchiveCleanupEligibility value) => value switch
    {
        ArchiveCleanupEligibility.Candidate => Texts.T("archive.cleanup.state.candidate"),
        ArchiveCleanupEligibility.Protected => Texts.T("archive.cleanup.state.protected"),
        ArchiveCleanupEligibility.Active => Texts.T("archive.cleanup.state.active"),
        ArchiveCleanupEligibility.Kept => Texts.T("archive.cleanup.state.kept"),
        _ => Texts.T("archive.cleanup.state.unknown")
    };
    internal static string Reason(ArchiveCleanupReason value) => value switch
    {
        ArchiveCleanupReason.ThreadFavorite => Texts.T("archive.cleanup.reason.threadFavorite"),
        ArchiveCleanupReason.MediaFavorite => Texts.T("archive.cleanup.reason.mediaFavorite"),
        ArchiveCleanupReason.MediaRating => Texts.T("archive.cleanup.reason.mediaRating"),
        ArchiveCleanupReason.PostBookmark => Texts.T("archive.cleanup.reason.postBookmark"),
        ArchiveCleanupReason.ReaderGroup => Texts.T("archive.cleanup.reason.readerGroup"),
        ArchiveCleanupReason.PrivateTag => Texts.T("archive.cleanup.reason.privateTag"),
        ArchiveCleanupReason.ReadingPosition => Texts.T("archive.cleanup.reason.readingPosition"),
        ArchiveCleanupReason.OpenView => Texts.T("archive.cleanup.reason.openView"),
        ArchiveCleanupReason.ActiveWatch => Texts.T("archive.cleanup.reason.activeWatch"),
        ArchiveCleanupReason.PendingWork => Texts.T("archive.cleanup.reason.pendingWork"),
        ArchiveCleanupReason.SharedDirectory => Texts.T("archive.cleanup.reason.sharedDirectory"),
        ArchiveCleanupReason.MediaUnknown => Texts.T("archive.cleanup.reason.mediaUnknown"),
        ArchiveCleanupReason.ProtectionUnknown => Texts.T("archive.cleanup.reason.protectionUnknown"),
        ArchiveCleanupReason.WorkUnknown => Texts.T("archive.cleanup.reason.workUnknown"),
        ArchiveCleanupReason.MoreThanTwoAssets => Texts.T("archive.cleanup.reason.moreThanTwoAssets"),
        _ => Texts.T("archive.cleanup.reason.changed")
    };
    internal static string Media(ArchiveCleanupPreviewRow row) => row.Media.SavedAssetCount is int count
        ? Texts.T("archive.cleanup.media.versions", count, row.Media.VerifiedBytes, row.PostCount)
        : Texts.T("archive.cleanup.media.unknown", row.PostCount);
    internal static string Reasons(ArchiveCleanupPreviewRow row) => string.Join(" · ", row.Reasons.Select(Reason));
    internal static string Stale => Texts.T("archive.cleanup.stale");
}
