using System;
using System.Collections.Generic;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Feedback;

internal enum FeedbackCategory { Information, Success, Warning, Error }

internal sealed record SessionFeedback(long Id, DateTimeOffset Timestamp, FeedbackCategory Category,
    string Operation, string? Context, string Summary, string? Details, int Count, string? FaultKey = null)
{
    public string CategoryLabel => Texts.T(Category == FeedbackCategory.Error ? "notification.error" : "feedback.warning");
    public string TimeLabel => Timestamp.ToLocalTime().ToString("g", Texts.Culture);
    public string Description => Summary + (Count > 1 ? " ×" + Count : string.Empty);
    public string DetailText => TimeLabel + " · " + CategoryLabel + " · " + Operation
        + (Context == null ? string.Empty : " · " + Context) + "\n" + Summary
        + (string.IsNullOrEmpty(Details) ? string.Empty : "\n" + Details)
        + (Count > 1 ? "\n" + Texts.T("feedback.repeated", Count) : string.Empty);
}

/// <summary>Owned by the window, used on its UI thread; no disk, timers or automatic eviction.</summary>
internal sealed class SessionFeedbackStore
{
    private readonly List<SessionFeedback> _entries = new();
    // Keep presentation tombstones separate from the diagnostic list. Clear is not recovery.
    // Unstructured callers keep their exact text identity. Only an owner-supplied stable fault
    // key can replace summary wording; category, operation, context and details still distinguish failures.
    private sealed record FeedbackIdentity(FeedbackCategory Category, string Operation, string? Context,
        string? FaultKey, string? Summary, string? Details)
    {
        public static FeedbackIdentity From(SessionFeedback entry) =>
            new(entry.Category, entry.Operation, entry.Context, entry.FaultKey,
                entry.FaultKey == null ? entry.Summary : null, entry.Details);
    }
    private readonly HashSet<FeedbackIdentity> _dismissed = new();
    private long _nextId;
    public IReadOnlyList<SessionFeedback> Entries => _entries.AsReadOnly();
    public event Action? Changed;

    public SessionFeedback? Record(FeedbackCategory category, string operation, string? context,
        string summary, string? details = null, DateTimeOffset? timestamp = null, string? faultKey = null)
    {
        if (category is not (FeedbackCategory.Warning or FeedbackCategory.Error)) return null;
        if (faultKey != null) ArgumentException.ThrowIfNullOrWhiteSpace(faultKey);
        DateTimeOffset now = timestamp ?? DateTimeOffset.Now;
        FeedbackIdentity identity = new(category, operation, context, faultKey,
            faultKey == null ? summary : null, details);
        int index = _entries.FindIndex(entry => FeedbackIdentity.From(entry) == identity);
        SessionFeedback entry = index < 0
            ? new(++_nextId, now, category, operation, context, summary, details, 1, faultKey)
            : _entries[index] with { Timestamp = now, Summary = summary, Count = _entries[index].Count + 1 };
        if (index < 0) _entries.Add(entry); else _entries[index] = entry;
        Changed?.Invoke();
        return entry;
    }

    /// <summary>
    /// Dismisses only presentation, never the underlying failure or diagnostic count. Immutable
    /// entries can outlive a diagnostic clear (e.g. an already captured banner close or queued toast).
    /// </summary>
    public void Dismiss(SessionFeedback entry) => _dismissed.Add(FeedbackIdentity.From(entry));
    public bool IsDismissed(SessionFeedback entry) => _dismissed.Contains(FeedbackIdentity.From(entry));
    public static bool SameOccurrence(SessionFeedback a, SessionFeedback b) =>
        FeedbackIdentity.From(a) == FeedbackIdentity.From(b);

    // Deliberately leave _dismissed intact: clearing diagnostics must not replay a dismissed event.
    public void Clear() { _entries.Clear(); Changed?.Invoke(); }
    public static bool AllowsPopup(Config config, FeedbackCategory category) => category switch
    {
        FeedbackCategory.Warning => config.ShowWarningNotifications,
        FeedbackCategory.Error => config.ShowErrorNotifications,
        _ => config.ShowInformationNotifications
    };
}
