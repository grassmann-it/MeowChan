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

    /// <summary>
    /// "Next step" text. A step that points at a button receives that button's label via <paramref name="button"/>
    /// so the sentence quotes the control exactly as the page labels it (see <see cref="RetryButton"/>).
    /// </summary>
    internal static string Next(string key, string? button = null) => Texts.T("diagnostics.observed.next") + ": "
        + (button == null ? Texts.T(key) : Texts.T(key, button));

    /// <summary>The label of the Diagnostics button that re-checks this kind of operation.</summary>
    internal static string RetryButton(DiagnosticOperation operation) => Texts.T(operation switch
    {
        DiagnosticOperation.Board => "check.boardAction",
        DiagnosticOperation.Storage => "targets.check",
        _ => "check.action"
    });

    /// <summary>
    /// One short failure label (for example "Not found (HTTP 404)") from the same facts as <see cref="Format"/>.
    /// Rows, the Downloads inspector, Diagnostics and the Sources detail all start from this text.
    /// </summary>
    internal static string Short(FailureInfo failure, DiagnosticOperation operation) =>
        Facts(failure, operation)[0].Short;

    /// <summary><see cref="Short"/> plus the reported retry time, when there is one.</summary>
    internal static string Headline(FailureInfo failure, DiagnosticOperation operation) =>
        failure.RetryAt is DateTimeOffset retryAt
            ? Short(failure, operation) + " · " + Texts.T("failure.short.retryAt", FormatTime(retryAt))
            : Short(failure, operation);

    /// <summary>Headline and message on one line: the form used where space is short (Sources detail, media list).</summary>
    internal static string Line(FailureInfo failure, DiagnosticOperation operation) =>
        Headline(failure, operation) + " · " + failure.Message;

    /// <summary>
    /// Downloads inspector form: the headline, the same observed-result sentences Diagnostics shows, then the
    /// producer's own message. No action text; the inspector has its own buttons.
    /// </summary>
    internal static string Inspector(FailureInfo failure, DiagnosticOperation operation)
    {
        List<string> lines = new() { Headline(failure, operation) };
        lines.AddRange(Facts(failure, operation).Select(fact => fact.Long));
        lines.Add(failure.Message);
        return string.Join(Environment.NewLine, lines);
    }

    internal static string Format(FailureInfo failure, DiagnosticOperation operation,
        RequestPolicyProgress? progress = null)
    {
        List<string> lines = new() { Operation(operation), Short(failure, operation),
            Texts.T("diagnostics.observed.evidence") + ":" };
        List<string> evidence = Facts(failure, operation).Select(fact => fact.Long).ToList();
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
        string retry = RetryButton(operation);
        lines.Add(operation == DiagnosticOperation.Selection ? Next("diagnostics.observed.checkSource")
            : failure.Kind == FailureKind.Storage ? Next("diagnostics.observed.checkStorage", retry)
            : IsWaiting(progress) ? Next("diagnostics.observed.wait")
            : failure.Kind == FailureKind.InvalidData
                || failure.HttpStatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound
                ? Next("diagnostics.observed.checkSourceRetry", retry)
            : Next("diagnostics.observed.retry", retry));
        return string.Join(Environment.NewLine, lines);
    }

    internal static string FormatRequest(DiagnosticOperation operation, RequestPolicyProgress progress, string status) =>
        string.Join(Environment.NewLine, Operation(operation), Texts.T("diagnostics.observed.evidence") + ":",
            status, Texts.T("diagnostics.attempt", progress.Attempt),
            Next(IsWaiting(progress) ? "diagnostics.observed.wait" : "diagnostics.observed.checkSource"));

    internal static string FormatStorage(string path, string reason, DateTimeOffset nextCheckAt) =>
        string.Join(Environment.NewLine, Operation(DiagnosticOperation.Storage), path,
            Texts.T("diagnostics.observed.evidence") + ":", reason,
            Texts.T("targets.nextCheck", nextCheckAt.ToLocalTime()),
            Next("diagnostics.observed.checkStorage", RetryButton(DiagnosticOperation.Storage)));

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
                lines.Add(HttpEvidence(http, DiagnosticOperation.Media).Long);
            else if (media.Failure != null || media.Status is MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired)
                lines.Add(Texts.T("diagnostics.observed.unknown"));
            if (media.DueAt is DateTimeOffset due) lines.Add(RetryAt(due));
            lines.Add(media.Status == MediaWorkStatus.Waiting ? Next("diagnostics.observed.wait")
                : Next("diagnostics.observed.retry", RetryButton(DiagnosticOperation.Media)));
        }
        if (work.CapturePending)
        {
            lines.Add(Operation(DiagnosticOperation.Capture));
            lines.Add(Next("diagnostics.observed.retry", RetryButton(DiagnosticOperation.Capture)));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatTime(DateTimeOffset value) => value.ToLocalTime().ToString("g", Texts.Culture);

    private static string RetryAt(DateTimeOffset value) =>
        Texts.T("diagnostics.observed.retryAt", FormatTime(value));

    private static bool IsWaiting(RequestPolicyProgress? progress) =>
        progress?.Phase is RequestPolicyPhase.WaitingForAdmission or RequestPolicyPhase.Cooldown;

    private readonly record struct Fact(string Long, string Short);

    private static Fact Of(string longKey, string shortKey) => new(Texts.T(longKey), Texts.T(shortKey));

    /// <summary>
    /// The observed facts of one failure, each as its long sentence (Diagnostics, inspector) and its short label
    /// (rows, Sources). Orthogonal facts stay orthogonal; the first entry is the headline. Never empty.
    /// </summary>
    private static List<Fact> Facts(FailureInfo failure, DiagnosticOperation operation)
    {
        List<Fact> facts = new();
        // Selection uses Storage as a legacy catch-all, including an unavailable filter host.
        // Without typed origin evidence this category does not establish a local storage failure.
        if (failure.Kind == FailureKind.Storage)
            facts.Add(operation == DiagnosticOperation.Selection
                ? Of("diagnostics.observed.unknown", "failure.short.unknown")
                : Of("diagnostics.observed.storage", "failure.short.storage"));
        if (failure.HttpStatusCode is System.Net.HttpStatusCode http)
            facts.Add(HttpEvidence((int)http, operation));
        if (failure.NetworkStage is FailureNetworkStage stage)
        {
            Fact? fact = stage switch
            {
                FailureNetworkStage.NameResolution => Of("diagnostics.observed.dns", "failure.short.dns"),
                FailureNetworkStage.Connection => Of("diagnostics.observed.connection", "failure.short.connection"),
                FailureNetworkStage.SecureConnection => Of("diagnostics.observed.tls", "failure.short.tls"),
                _ => null
            };
            if (fact is Fact known) facts.Add(known);
        }
        if (failure.DiagnosticCode == "operation-cancelled")
            facts.Add(Of("diagnostics.observed.cancelled", "failure.short.cancelled"));
        else if (failure.Kind == FailureKind.Timeout)
            facts.Add(Of("diagnostics.observed.timeout", "failure.short.timeout"));
        if (failure.Kind == FailureKind.InvalidData)
            facts.Add(Of("diagnostics.observed.invalidData", "failure.short.invalidData"));
        if (facts.Count == 0)
            facts.Add(Of("diagnostics.observed.unknown", "failure.short.unknown"));
        return facts;
    }

    private static Fact HttpEvidence(int status, DiagnosticOperation operation) => status switch
    {
        403 => Of("diagnostics.observed.http403", "failure.short.refused"),
        404 => Of(operation == DiagnosticOperation.Media
            ? "diagnostics.observed.media404" : "diagnostics.observed.http404", "failure.short.notFound"),
        429 => Of("diagnostics.observed.http429", "failure.short.throttled"),
        _ => new("HTTP " + status.ToString(CultureInfo.InvariantCulture) + " · " + Texts.T("diagnostics.observed.unknown"),
            "HTTP " + status.ToString(CultureInfo.InvariantCulture))
    };
}
