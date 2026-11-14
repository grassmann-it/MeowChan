using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>
/// UX flows AP16: what the Add results do with the URLs that were not added (invalid, unsupported, failed or skipped).
/// Pure text rules so the "put rejected back into the field" action is testable without a window.
/// </summary>
internal static class AdmissionProblemPlan
{
    /// <summary>The rejected URLs in first-seen order, each once.</summary>
    internal static IReadOnlyList<string> RejectedUrls(IEnumerable<(string Url, bool Rejected)> results) =>
        results.Where(item => item.Rejected && !string.IsNullOrWhiteSpace(item.Url))
            .Select(item => item.Url.Trim()).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The new Add field text. A field that still holds the submitted text (or nothing) is replaced by the rejected
    /// URLs alone: the accepted ones are already in the list. Text the user typed since is kept, and only the rejected
    /// URLs it does not contain yet are appended on new lines.
    /// </summary>
    internal static string FieldText(string? field, string? submitted, IReadOnlyList<string> rejected)
    {
        string current = field ?? string.Empty;
        if (rejected.Count == 0) return current;
        string all = string.Join(Environment.NewLine, rejected);
        if (string.IsNullOrWhiteSpace(current) || (submitted != null && string.Equals(current.Trim(), submitted.Trim(), StringComparison.Ordinal)))
            return all;
        HashSet<string> present = AddInput.Split(current).ToHashSet(StringComparer.Ordinal);
        string[] missing = rejected.Where(url => !present.Contains(url)).ToArray();
        return missing.Length == 0 ? current : current.TrimEnd() + Environment.NewLine + string.Join(Environment.NewLine, missing);
    }
}
