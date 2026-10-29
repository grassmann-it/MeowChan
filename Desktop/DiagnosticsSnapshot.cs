using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal enum DiagnosticTargetKind { Thread, Board }
internal sealed record DiagnosticTarget(DiagnosticTargetKind Kind, long WatchId);
internal enum DiagnosticKind { Information, Check, Media, Capture, Selection, PendingWork, Request, Storage }
internal sealed record DiagnosticEntry(string Key, string Category, string Resource, string Summary,
    string Details, DiagnosticTarget? Target, string? DownloadTargetPath = null)
{
    public ResourceId? ResourceId { get; init; }
    public DiagnosticKind Kind { get; init; }
    public bool CheckAlso { get; init; }
    public bool IsProblem => Kind is DiagnosticKind.Check or DiagnosticKind.Media or DiagnosticKind.Capture
        or DiagnosticKind.Selection or DiagnosticKind.Storage;
}

/// <summary>Current state only: no invented timestamps or history, and no URL-based action targets.</summary>
internal sealed record DiagnosticsSnapshot(long Queued, long Active, int Threads, int Boards,
    IReadOnlyList<DiagnosticEntry> Entries)
{
    internal static DiagnosticsSnapshot From(DesktopSessionView view, IReadOnlyList<MeowChan.Downloader.DownloadTargetWaitState>? targetWaits = null)
    {
        List<DiagnosticEntry> entries = new List<DiagnosticEntry>();
        foreach (MeowChan.Downloader.DownloadTargetWaitState waiting in targetWaits ?? Array.Empty<MeowChan.Downloader.DownloadTargetWaitState>())
        {
            string reason = Texts.T(waiting.Reason switch { "free-space-reserve" => "targets.freeSpace", "disk-full" => "targets.diskFull", _ => "targets.unavailable" });
            entries.Add(new DiagnosticEntry("target:" + waiting.Path, Texts.T("targets.category"), waiting.Path, reason,
                waiting.Path + Environment.NewLine + reason + Environment.NewLine + Texts.T("targets.nextCheck", waiting.NextCheckAt.ToLocalTime()), null, waiting.Path) { Kind = DiagnosticKind.Storage });
        }
        if (view.StartupError is string startupError)
            entries.Add(new DiagnosticEntry("startup", Texts.T("diagnostics.session"), "MeowChan", startupError, startupError, null));
        foreach (string warning in view.Warnings.Distinct(StringComparer.Ordinal))
            entries.Add(new DiagnosticEntry("warning:" + warning, Texts.T("diagnostics.warning"), "MeowChan", warning, warning, null));
        foreach (DesktopThreadEntry thread in view.Threads)
        {
            ThreadSnapshot snapshot = thread.Snapshot;
            DiagnosticTarget target = new DiagnosticTarget(DiagnosticTargetKind.Thread, thread.WatchId);
            AddResource(entries, target, snapshot.Url, snapshot.Title, snapshot.RequestProgress, snapshot.LastCheckFailure, snapshot.Id);
            if (snapshot.Work is ThreadWorkSnapshot work && (work.MissingMedia > 0 || work.CapturePending))
            {
                WorkStatusPresentation status = WorkStatusFormatter.Format(work);
                bool mediaProblem = work.Media.Any(media => media.Status is MeowChan.Persistence.MediaWorkStatus.NeedsIntervention
                    or MeowChan.Persistence.MediaWorkStatus.Expired || media.Failure != null);
                DiagnosticKind kind = mediaProblem ? DiagnosticKind.Media
                    : work.CapturePending ? DiagnosticKind.Capture : DiagnosticKind.PendingWork;
                entries.Add(new DiagnosticEntry($"thread:{thread.WatchId}:work", Texts.T("diagnostics.media"), snapshot.Title,
                    status.Status + " · " + status.Progress, snapshot.Url + Environment.NewLine + status.Details, target) { ResourceId = snapshot.Id, Kind = kind, CheckAlso = work.CapturePending });
            }
            if (snapshot.Content?.CaptureFailure is FailureInfo capture)
                entries.Add(new DiagnosticEntry($"thread:{thread.WatchId}:capture", Texts.T("nav.archive"), snapshot.Title,
                    capture.Message, snapshot.Url + Environment.NewLine + FormatFailure(capture), target) { ResourceId = snapshot.Id, Kind = DiagnosticKind.Capture });
            if (snapshot.Content?.SelectionFailure is FailureInfo selection)
                entries.Add(new DiagnosticEntry($"thread:{thread.WatchId}:selection", Texts.T("nav.rules"), snapshot.Title,
                    selection.Message, snapshot.Url + Environment.NewLine + FormatFailure(selection), target) { ResourceId = snapshot.Id, Kind = DiagnosticKind.Selection });
            foreach (KeyValuePair<string, FailureInfo> failure in snapshot.MediaFailures.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string details = snapshot.Url + Environment.NewLine + failure.Key + Environment.NewLine + FormatFailure(failure.Value);
                entries.Add(new DiagnosticEntry($"thread:{thread.WatchId}:media:{failure.Key}", Texts.T("diagnostics.media"),
                    snapshot.Title, failure.Value.Message, details, target) { ResourceId = snapshot.Id, Kind = DiagnosticKind.Media });
            }
        }
        foreach (DesktopBoardEntry board in view.Boards)
            AddResource(entries, new DiagnosticTarget(DiagnosticTargetKind.Board, board.WatchId), board.Snapshot.Url,
                board.HostName + " /" + board.Snapshot.Id.Board + "/", board.Snapshot.RequestProgress, board.Snapshot.LastCheckFailure, board.Snapshot.Id);
        return new DiagnosticsSnapshot(view.Threads.Sum(thread => (long)thread.Snapshot.Downloads.Queued),
            view.Threads.Sum(thread => (long)thread.Snapshot.Downloads.Active), view.Threads.Count, view.Boards.Count,
            Array.AsReadOnly(entries.ToArray()));
    }

    internal static bool ContainsTarget(DesktopSessionView view, DiagnosticTarget target) => target.Kind switch
    {
        DiagnosticTargetKind.Thread => view.Threads.Any(thread => thread.WatchId == target.WatchId),
        DiagnosticTargetKind.Board => view.Boards.Any(board => board.WatchId == target.WatchId),
        _ => false
    };

    private static void AddResource(List<DiagnosticEntry> entries, DiagnosticTarget target, string url,
        string title, Http.RequestPolicyProgress? progress, FailureInfo? failure, ResourceId id)
    {
        string key = target.Kind + ":" + target.WatchId;
        if (failure != null)
            entries.Add(new DiagnosticEntry(key + ":check", Texts.T("diagnostics.check"), title, failure.Message,
                url + Environment.NewLine + FormatFailure(failure), target) { ResourceId = id, Kind = DiagnosticKind.Check });
        if (RequestStatus.Format(progress) is string status)
            entries.Add(new DiagnosticEntry(key + ":request", Texts.T("diagnostics.request"), title, status,
                url + Environment.NewLine + status + Environment.NewLine + Texts.T("diagnostics.attempt", progress!.Attempt), target) { ResourceId = id, Kind = DiagnosticKind.Request });
    }

    private static string FormatFailure(FailureInfo failure) => failure.Kind
        + (failure.HttpStatusCode is System.Net.HttpStatusCode code ? " · HTTP " + (int)code : string.Empty)
        + Environment.NewLine + failure.Message;
}
