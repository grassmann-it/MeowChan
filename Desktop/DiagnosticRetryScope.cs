using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

internal sealed record DiagnosticRetryTarget(DiagnosticTarget Target, bool Check, bool Media);

internal static class DiagnosticRetryScope
{
    internal static bool Matches(DiagnosticEntry entry, string? source, string? query, bool problemsOnly)
    {
        if (source != null && entry.ResourceId?.Source != source || problemsOnly && !entry.IsProblem) return false;
        string text = query?.Trim() ?? string.Empty;
        return text.Length == 0 || new[] { entry.Resource, entry.Summary, entry.ResourceId?.Source,
            entry.ResourceId?.Board, entry.ResourceId?.Thread,
            entry.ResourceId == null ? null : entry.ResourceId.Source + " /" + entry.ResourceId.Board + "/ #" + entry.ResourceId.Thread }.Any(value => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    }

    // Capture once before awaiting. Several failure rows can refer to the same watch.
    // An explicit media action has thread scope, not one hidden retry per error row.
    internal static DiagnosticRetryTarget[] Capture(IEnumerable<DiagnosticEntry> entries) => entries
        .Where(entry => entry.IsProblem && entry.Target != null)
        .GroupBy(entry => entry.Target!)
        .Select(group => new DiagnosticRetryTarget(group.Key,
            group.Any(entry => entry.Kind != DiagnosticKind.Media || entry.CheckAlso),
            group.Key.Kind == DiagnosticTargetKind.Thread && group.Any(entry => entry.Kind == DiagnosticKind.Media)))
        .ToArray();
}
