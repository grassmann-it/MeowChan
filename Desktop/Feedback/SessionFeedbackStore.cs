using System;
using System.Collections.Generic;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Feedback;

internal enum FeedbackCategory { Information, Success, Warning, Error }

internal sealed record SessionFeedback(long Id, DateTimeOffset Timestamp, FeedbackCategory Category,
    string Operation, string? Context, string Summary, string? Details, int Count)
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
    private long _nextId;
    public IReadOnlyList<SessionFeedback> Entries => _entries.AsReadOnly();
    public event Action? Changed;

    public SessionFeedback? Record(FeedbackCategory category, string operation, string? context,
        string summary, string? details = null, DateTimeOffset? timestamp = null)
    {
        if (category is not (FeedbackCategory.Warning or FeedbackCategory.Error)) return null;
        DateTimeOffset now = timestamp ?? DateTimeOffset.Now;
        int index = _entries.FindIndex(entry => entry.Category == category && entry.Operation == operation
            && entry.Context == context && entry.Summary == summary && entry.Details == details);
        SessionFeedback entry = index < 0
            ? new(++_nextId, now, category, operation, context, summary, details, 1)
            : _entries[index] with { Timestamp = now, Count = _entries[index].Count + 1 };
        if (index < 0) _entries.Add(entry); else _entries[index] = entry;
        Changed?.Invoke();
        return entry;
    }

    public void Clear() { _entries.Clear(); Changed?.Invoke(); }
    public static bool AllowsPopup(Config config, FeedbackCategory category) => category switch
    {
        FeedbackCategory.Warning => config.ShowWarningNotifications,
        FeedbackCategory.Error => config.ShowErrorNotifications,
        _ => config.ShowInformationNotifications
    };
}
