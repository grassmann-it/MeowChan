using MeowChan.Interfaces;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

internal static class CheckActionText
{
    internal static string ForResult(CheckRequestStatus result, DiagnosticTargetKind kind = DiagnosticTargetKind.Thread) => Texts.T(result switch
    {
        CheckRequestStatus.Scheduled => kind == DiagnosticTargetKind.Board ? "check.boardScheduled" : "check.scheduled",
        CheckRequestStatus.AlreadyScheduled => "check.alreadyScheduled",
        CheckRequestStatus.Paused => "check.paused",
        CheckRequestStatus.Stale => "diagnostics.removed",
        CheckRequestStatus.Unsupported => "check.unsupported",
        _ => "check.stopping"
    });
}
