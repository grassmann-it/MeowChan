using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Http;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal enum DiagnosticOperation { Thread, Board, Media, Capture, Selection, Storage }

/// <summary>
/// Passive explanation of supplied facts, never a classifier of messages/URLs or action authority.
/// Render at snapshot creation so a refresh uses the current language. No history is reconstructed.
/// </summary>
internal static class DiagnosticFailurePresentation
{
    internal static string WatchUrl(string url) => Texts.T("diagnostics.observed.watchUrl") + ": " + url;

    internal static string Operation(DiagnosticOperation operation) =>
        Texts.T("diagnostics.observed.operation") + ": " + Texts.T(operation switch
        {
            DiagnosticOperation.Thread => "diagnostics.observed.thread",
            DiagnosticOperation.Board => "diagnostics.observed.board",
            DiagnosticOperation.Media => "diagnostics.observed.media",
            DiagnosticOperation.Capture => "diagnostics.observed.capture",
            DiagnosticOperation.Selection => "diagnostics.observed.selection",
            _ => "targets.category"
        });

    internal static string Next(string key) => Texts.T("diagnostics.observed.next") + ": " + Texts.T(key);

    internal static string Format(FailureInfo failure, DiagnosticOperation operation,
        RequestPolicyProgress? progress = null)
    {
        List<string> lines = new() { Operation(operation), Texts.T("diagnostics.observed.evidence") + ":" };
        List<string> evidence = new();
        // Orthogonal facts stay orthogonal: storage/capture/selection are not transport phases.
        if (failure.Kind == FailureKind.Storage)
        {
            // Selection uses Storage as a legacy catch-all, including an unavailable filter host.
            // Without typed origin evidence this category does not establish a local storage failure.
            evidence.Add(Texts.T(operation == DiagnosticOperation.Selection
                ? "diagnostics.observed.unknown" : "diagnostics.observed.storage"));
        }
        if (failure.HttpStatusCode is System.Net.HttpStatusCode http)
            evidence.Add(HttpEvidence((int)http, operation));
        if (failure.NetworkStage is FailureNetworkStage stage)
        {
            string? key = stage switch
            {
                FailureNetworkStage.NameResolution => "diagnostics.observed.dns",
                FailureNetworkStage.Connection => "diagnostics.observed.connection",
                FailureNetworkStage.SecureConnection => "diagnostics.observed.tls",
                _ => null
            };
            if (key != null) evidence.Add(Texts.T(key));
        }
        if (failure.DiagnosticCode == "operation-cancelled")
            evidence.Add(Texts.T("diagnostics.observed.cancelled"));
        else if (failure.Kind == FailureKind.Timeout)
            evidence.Add(Texts.T("diagnostics.observed.timeout"));
        if (failure.Kind == FailureKind.InvalidData)
            evidence.Add(Texts.T("diagnostics.observed.invalidData"));
        if (evidence.Count == 0)
            evidence.Add(Texts.T("diagnostics.observed.unknown"));
        lines.AddRange(evidence);
        // Keep the established technical category, HTTP spelling and original message intact.
        lines.Add(failure.Kind + (failure.HttpStatusCode is System.Net.HttpStatusCode code
            ? " · HTTP " + ((int)code).ToString(CultureInfo.InvariantCulture) : string.Empty));
        lines.Add(failure.Message);
        if (!string.IsNullOrWhiteSpace(failure.DiagnosticCode))
            lines.Add(Texts.T("diagnostics.observed.code", failure.DiagnosticCode));
        if (failure.RetryAt is DateTimeOffset retryAt)
            lines.Add(RetryAt(retryAt));
        if (RequestStatus.Format(progress) is string status)
        {
            lines.Add(Texts.T("diagnostics.request") + ": " + status);
            lines.Add(Texts.T("diagnostics.attempt", progress!.Attempt));
        }
        // Status and reported RetryAt are observations, not evidence of a current retry plan.
        lines.Add(Next(operation == DiagnosticOperation.Selection ? "diagnostics.observed.checkSource"
            : failure.Kind == FailureKind.Storage ? "diagnostics.observed.checkStorage"
            : IsWaiting(progress) ? "diagnostics.observed.wait"
            : failure.Kind == FailureKind.InvalidData
                || failure.HttpStatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound
                ? "diagnostics.observed.checkSource"
            : "diagnostics.observed.retry"));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatRequest(DiagnosticOperation operation, RequestPolicyProgress progress, string status) =>
        string.Join(Environment.NewLine, Operation(operation), Texts.T("diagnostics.observed.evidence") + ":",
            status, Texts.T("diagnostics.attempt", progress.Attempt),
            Next(IsWaiting(progress) ? "diagnostics.observed.wait" : "diagnostics.observed.checkSource"));

    internal static string FormatStorage(string path, string reason, DateTimeOffset nextCheckAt) =>
        string.Join(Environment.NewLine, Operation(DiagnosticOperation.Storage), path,
            Texts.T("diagnostics.observed.evidence") + ":", reason,
            Texts.T("targets.nextCheck", nextCheckAt.ToLocalTime()), Next("diagnostics.observed.checkStorage"));

    internal static string FormatWork(ThreadWorkSnapshot work, string existingDetails)
    {
        List<string> lines = new();
        if (work.Media.Any(item => item.Status is not (MediaWorkStatus.Succeeded or MediaWorkStatus.Excluded)))
            lines.Add(Operation(DiagnosticOperation.Media));
        if (work.Media.Any(item => item.Status == MediaWorkStatus.Excluded))
            lines.Add(Operation(DiagnosticOperation.Selection));
        if (work.CapturePending) lines.Add(Operation(DiagnosticOperation.Capture));
        lines.Add(Texts.T("diagnostics.observed.evidence") + ":");
        lines.Add(existingDetails);
        // Match the existing work-detail window, without adding endpoints or interpreting durable Failure text.
        // These records retain status/attempt/due facts, but no diagnostic code or network-stage evidence.
        foreach (MediaWork media in work.Media.Where(item => item.Status != MediaWorkStatus.Succeeded).Take(5))
        {
            string name = string.IsNullOrWhiteSpace(media.OriginalFileName)
                ? Texts.T("work.unnamedMedia") : media.OriginalFileName;
            lines.Add(Operation(media.Status == MediaWorkStatus.Excluded
                ? DiagnosticOperation.Selection : DiagnosticOperation.Media));
            lines.Add(Texts.T("diagnostics.observed.mediaItem") + ": " + name);
            lines.Add(Texts.T("detail.status") + ": " + media.Status);
            lines.Add(Texts.T("diagnostics.attempt", media.Attempts));
            // A deliberate filter exclusion is not a failed transfer, even if an older HTTP fact remains.
            if (media.Status == MediaWorkStatus.Excluded)
            {
                lines.Add(Next("diagnostics.observed.checkSource"));
                continue;
            }
            if (media.HttpStatus is int http)
                lines.Add(HttpEvidence(http, DiagnosticOperation.Media));
            else if (media.Failure != null || media.Status is MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired)
                lines.Add(Texts.T("diagnostics.observed.unknown"));
            if (media.DueAt is DateTimeOffset due) lines.Add(RetryAt(due));
            lines.Add(Next(media.Status == MediaWorkStatus.Waiting
                ? "diagnostics.observed.wait" : "diagnostics.observed.retry"));
        }
        if (work.CapturePending)
        {
            lines.Add(Operation(DiagnosticOperation.Capture));
            lines.Add(Next("diagnostics.observed.retry"));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string RetryAt(DateTimeOffset value) =>
        Texts.T("diagnostics.observed.retryAt", value.ToLocalTime().ToString("g", Texts.Culture));

    private static bool IsWaiting(RequestPolicyProgress? progress) =>
        progress?.Phase is RequestPolicyPhase.WaitingForAdmission or RequestPolicyPhase.Cooldown;

    private static string HttpEvidence(int status, DiagnosticOperation operation) => status switch
    {
        403 => Texts.T("diagnostics.observed.http403"),
        404 => Texts.T(operation == DiagnosticOperation.Media
            ? "diagnostics.observed.media404" : "diagnostics.observed.http404"),
        429 => Texts.T("diagnostics.observed.http429"),
        _ => "HTTP " + status.ToString(CultureInfo.InvariantCulture) + " · " + Texts.T("diagnostics.observed.unknown")
    };
}
