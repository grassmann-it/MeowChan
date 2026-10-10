using System;
using System.Collections.Generic;
using System.Linq;
using oChan.Desktop.Localization;

namespace oChan.Desktop;

internal enum DiagnosticTargetKind { Thread, Board }
internal sealed record DiagnosticTarget(DiagnosticTargetKind Kind, long WatchId);
internal sealed record DiagnosticEntry(string Key, string Category, string Resource, string Summary,
    string Details, DiagnosticTarget? Target);

/// <summary>Current state only: no invented timestamps or history, and no URL-based action targets.</summary>
internal sealed record DiagnosticsSnapshot(long Queued, long Active, int Threads, int Boards,
    IReadOnlyList<DiagnosticEntry> Entries)
{
    internal static DiagnosticsSnapshot From(DesktopSessionView view)
    {
        List<DiagnosticEntry> entries = new List<DiagnosticEntry>();
        if (view.StartupError is string startupError)
            entries.Add(new DiagnosticEntry("startup", Texts.T("diagnostics.session"), "oChan", startupError, startupError, null));
        foreach (string warning in view.Warnings.Distinct(StringComparer.Ordinal))
            entries.Add(new DiagnosticEntry("warning:" + warning, Texts.T("diagnostics.warning"), "oChan", warning, warning, null));
        foreach (DesktopThreadEntry thread in view.Threads)
        {
            ThreadSnapshot snapshot = thread.Snapshot;
            DiagnosticTarget target = new DiagnosticTarget(DiagnosticTargetKind.Thread, thread.WatchId);
            AddResource(entries, target, snapshot.Url, snapshot.Title, snapshot.RequestProgress, snapshot.LastCheckFailure);
            if (snapshot.Content?.CaptureFailure is FailureInfo capture)
                entries.Add(new DiagnosticEntry($"thread:{thread.WatchId}:capture", Texts.T("nav.archive"), snapshot.Title,
                    capture.Message, snapshot.Url + Environment.NewLine + FormatFailure(capture), target));
            if (snapshot.Content?.SelectionFailure is FailureInfo selection)
                entries.Add(new DiagnosticEntry($"thread:{thread.WatchId}:selection", Texts.T("nav.rules"), snapshot.Title,
                    selection.Message, snapshot.Url + Environment.NewLine + FormatFailure(selection), target));
            foreach (KeyValuePair<string, FailureInfo> failure in snapshot.MediaFailures.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string details = snapshot.Url + Environment.NewLine + failure.Key + Environment.NewLine + FormatFailure(failure.Value);
                entries.Add(new DiagnosticEntry($"thread:{thread.WatchId}:media:{failure.Key}", Texts.T("diagnostics.media"),
                    snapshot.Title, failure.Value.Message, details, target));
            }
        }
        foreach (DesktopBoardEntry board in view.Boards)
            AddResource(entries, new DiagnosticTarget(DiagnosticTargetKind.Board, board.WatchId), board.Snapshot.Url,
                board.HostName + " /" + board.Snapshot.Id.Board + "/", board.Snapshot.RequestProgress, board.Snapshot.LastCheckFailure);
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
        string title, Http.RequestPolicyProgress? progress, FailureInfo? failure)
    {
        string key = target.Kind + ":" + target.WatchId;
        if (failure != null)
            entries.Add(new DiagnosticEntry(key + ":check", Texts.T("diagnostics.check"), title, failure.Message,
                url + Environment.NewLine + FormatFailure(failure), target));
        if (RequestStatus.Format(progress) is string status)
            entries.Add(new DiagnosticEntry(key + ":request", Texts.T("diagnostics.request"), title, status,
                url + Environment.NewLine + status + Environment.NewLine + Texts.T("diagnostics.attempt", progress!.Attempt), target));
    }

    private static string FormatFailure(FailureInfo failure) => failure.Kind
        + (failure.HttpStatusCode is System.Net.HttpStatusCode code ? " · HTTP " + (int)code : string.Empty)
        + Environment.NewLine + failure.Message;
}
