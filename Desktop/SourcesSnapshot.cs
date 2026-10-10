using System;
using System.Collections.Generic;
using System.Linq;
using oChan.Desktop.Localization;

namespace oChan.Desktop;

internal sealed record SourceWatchRow(DiagnosticTarget Target, string Resource, string Work, string Observation);
internal sealed record SourceRow(ProviderDescriptor Descriptor, string Summary, IReadOnlyList<SourceWatchRow> Watches)
{
    public string Id => Descriptor.SourceId;
    public string Name => Descriptor.DisplayName;
    public string Address => Descriptor.BaseUri.AbsoluteUri;
}

/// <summary>Observations retain watch lifetimes and never turn a single 404 into provider-wide offline status.</summary>
internal static class SourcesSnapshot
{
    internal static IReadOnlyList<SourceRow> From(IReadOnlyList<ProviderDescriptor> providers, DesktopSessionView view)
    {
        return Array.AsReadOnly(providers.Select(provider =>
        {
            DesktopThreadEntry[] threads = view.Threads.Where(thread => thread.Snapshot.Id.Source == provider.SourceId).ToArray();
            DesktopBoardEntry[] boards = view.Boards.Where(board => board.Snapshot.Id.Source == provider.SourceId).ToArray();
            List<SourceWatchRow> watches = new List<SourceWatchRow>();
            foreach (DesktopThreadEntry thread in threads)
            {
                ThreadSnapshot state = thread.Snapshot;
                List<string> observations = new List<string>();
                if (RequestStatus.Format(state.RequestProgress) is string request) observations.Add(request);
                if (state.LastCheckFailure is FailureInfo failure) observations.Add(FormatFailure(failure));
                if (state.Content?.CaptureFailure is FailureInfo capture) observations.Add(FormatFailure(capture));
                if (state.Content?.SelectionFailure is FailureInfo selection) observations.Add(FormatFailure(selection));
                observations.AddRange(state.MediaFailures.Values.Select(FormatFailure));
                if (state.SourceState != Interfaces.ThreadSourceState.Active) observations.Add(Texts.T(state.SourceState == Interfaces.ThreadSourceState.Archived ? "sources.archived" : "status.notFound"));
                watches.Add(new SourceWatchRow(new DiagnosticTarget(DiagnosticTargetKind.Thread, thread.WatchId),
                    state.Url, Texts.T("sources.mediaCounts", state.Downloads.Queued, state.Downloads.Active),
                    observations.Count > 0 ? string.Join(" · ", observations.Distinct(StringComparer.Ordinal)) : Texts.T("sources.noProblem")));
            }
            foreach (DesktopBoardEntry board in boards)
            {
                BoardSnapshot state = board.Snapshot;
                string? observation = RequestStatus.Format(state.RequestProgress);
                if (state.LastCheckFailure != null) observation = observation == null ? FormatFailure(state.LastCheckFailure)
                    : observation + " · " + FormatFailure(state.LastCheckFailure);
                watches.Add(new SourceWatchRow(new DiagnosticTarget(DiagnosticTargetKind.Board, board.WatchId),
                    state.Url, Texts.T("sources.catalog"), observation ?? Texts.T("sources.noProblem")));
            }
            string summary = watches.Count == 0 ? Texts.T("sources.noObservation")
                : Texts.T("sources.watchCounts", threads.Length, boards.Length,
                    threads.Sum(thread => (long)thread.Snapshot.Downloads.Queued), threads.Sum(thread => (long)thread.Snapshot.Downloads.Active));
            if (view.State is DesktopSessionState.Stopping or DesktopSessionState.Stopped) summary = Texts.T("sources.stopped");
            return new SourceRow(provider, summary, Array.AsReadOnly(watches.ToArray()));
        }).ToArray());
    }
    private static string FormatFailure(FailureInfo failure) => failure.HttpStatusCode is System.Net.HttpStatusCode code
        ? "HTTP " + (int)code + " · " + failure.Message : failure.Message;

}
