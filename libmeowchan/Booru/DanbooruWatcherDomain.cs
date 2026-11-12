using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Booru;

internal enum DanbooruWatcherState
{
    Active,
    Paused,
    Checking,
    Cooldown,
    Error,
    Removed
}

internal enum DanbooruWatcherBaselineMode
{
    FromNow
}

internal enum DanbooruWatcherCreateError
{
    None,
    InvalidWatcherId,
    InvalidQuery,
    InvalidPageSize,
    InvalidPageBudget
}

/// <summary>A provider page supplied by the adapter. It is data, not a command.</summary>
internal sealed record DanbooruSearchPageObservation(
    string SourceId,
    string Query,
    int Page,
    IReadOnlyList<string> PostIds,
    bool IsEnd);

/// <summary>Restart-persisted, bounded traversal state.</summary>
internal sealed record DanbooruWatcherProgress(
    int NextPage,
    int PagesCheckedInRun,
    bool BudgetLimited)
{
    internal string? CursorBeforePostId { get; init; }
    internal static DanbooruWatcherProgress Initial => new(0, 0, false);
}

internal sealed record DanbooruWatcherTransition(
    DanbooruWatcher Watcher,
    bool Accepted,
    string? RejectionCode = null,
    int? RequestedPage = null,
    IReadOnlyList<string>? NewPostIds = null)
{
    internal IReadOnlyList<string> AddedPostIds => NewPostIds ?? Array.Empty<string>();
    internal string? RequestedCursorBeforePostId => Watcher.CursorPostId;
}

/// <summary>
/// Immutable watcher aggregate. It owns query canonicalization, baseline/high-water
/// semantics and bounded cursor progress; transport, persistence and scheduling stay
/// outside this domain type.
/// </summary>
internal sealed record DanbooruWatcher
{
    internal const string ProviderId = DanbooruPostReference.ProviderId;
    internal const int DefaultPageSize = 20;
    internal const int MaximumPageSize = 20;
    internal const int DefaultPageBudget = 5;
    internal const int MaximumPageBudget = 5;

    private DanbooruWatcher(string watcherId, DanbooruTagQuery query,
        DateTimeOffset createdAtUtc, int pageSize, int pageBudget)
    {
        WatcherId = watcherId;
        Query = query;
        State = DanbooruWatcherState.Active;
        BaselineMode = DanbooruWatcherBaselineMode.FromNow;
        CreatedAtUtc = createdAtUtc.ToUniversalTime();
        PageSize = pageSize;
        PageBudget = pageBudget;
        KnownPostIds = Array.AsReadOnly(Array.Empty<string>());
        Progress = DanbooruWatcherProgress.Initial;
        ResumeState = DanbooruWatcherState.Active;
    }

    internal string WatcherId { get; init; }
    internal DanbooruTagQuery Query { get; init; }
    internal string CanonicalQuery => Query.ProviderQuery;
    internal DanbooruWatcherState State { get; init; }
    internal DanbooruWatcherBaselineMode BaselineMode { get; init; }
    internal DateTimeOffset CreatedAtUtc { get; init; }
    internal DateTimeOffset? LastCheckedAtUtc { get; init; }
    internal DateTimeOffset? CooldownUntilUtc { get; init; }
    internal string? ErrorCode { get; init; }
    internal string? HighWaterMark { get; init; }
    internal IReadOnlyList<string> KnownPostIds { get; init; }
    internal DanbooruWatcherProgress Progress { get; init; }
    internal int PageSize { get; init; }
    internal int PageBudget { get; init; }
    internal DanbooruWatcherState ResumeState { get; init; }
    internal bool BaselineEstablished { get; init; }

    // The last ID observed on the previous page is the restart-safe cursor fence.
    // It is separate from HighWaterMark: the high-water moves up, while page traversal
    // must move down through a provider's newest-first result set.
    internal string? CursorPostId => Progress.CursorBeforePostId;

    // Boundary captured at the start of a bounded traversal. It must not move when
    // page 1 discovers newer posts, otherwise page 2 would look like a false boundary.
    internal string? CheckStartHighWaterMark { get; init; }

    internal static bool TryCreate(string? watcherId, string? explicitQuery,
        DateTimeOffset createdAtUtc, out DanbooruWatcher? watcher,
        out DanbooruWatcherCreateError error, int pageSize = DefaultPageSize,
        int pageBudget = DefaultPageBudget)
    {
        watcher = null;
        error = DanbooruWatcherCreateError.None;
        if (string.IsNullOrWhiteSpace(watcherId) || watcherId.Length > 128 || watcherId.Any(char.IsControl))
        {
            error = DanbooruWatcherCreateError.InvalidWatcherId;
            return false;
        }
        if (pageSize is < 1 or > MaximumPageSize)
        {
            error = DanbooruWatcherCreateError.InvalidPageSize;
            return false;
        }
        if (pageBudget is < 1 or > MaximumPageBudget)
        {
            error = DanbooruWatcherCreateError.InvalidPageBudget;
            return false;
        }
        if (!DanbooruTagQuery.TryParse(explicitQuery, out DanbooruTagQuery? query, out _))
        {
            error = DanbooruWatcherCreateError.InvalidQuery;
            return false;
        }

        watcher = new DanbooruWatcher(watcherId, query!, createdAtUtc, pageSize, pageBudget);
        return true;
    }

    internal static DanbooruWatcherTransition BeginCheck(DanbooruWatcher watcher,
        DateTimeOffset nowUtc, bool manual = false)
    {
        if (watcher.State == DanbooruWatcherState.Removed)
            return Reject(watcher, "watcher_removed");
        if (watcher.State == DanbooruWatcherState.Checking)
            return Reject(watcher, "check_already_running");
        if (watcher.State == DanbooruWatcherState.Paused && !manual)
            return Reject(watcher, "watcher_paused");
        if (watcher.State == DanbooruWatcherState.Cooldown
            && watcher.CooldownUntilUtc is { } retryAt
            && nowUtc.ToUniversalTime() < retryAt)
            return Reject(watcher, "cooldown_active");

        // The pause target is the last deliberately chosen resume state, not whatever state the
        // watcher happens to hold now. Deriving it from State loses the pause as soon as a check
        // passes through Cooldown/Error: BeginCheck on the retry would resume Active.
        DanbooruWatcherState returnState = watcher.State == DanbooruWatcherState.Paused
            ? DanbooruWatcherState.Paused
            : watcher.ResumeState == DanbooruWatcherState.Paused ? DanbooruWatcherState.Paused
            : DanbooruWatcherState.Active;
        int nextPage = watcher.Progress.NextPage == 0 ? 1 : watcher.Progress.NextPage;
        string? checkStartHighWaterMark = watcher.Progress.NextPage > 0 && watcher.CursorPostId is not null
            ? watcher.CheckStartHighWaterMark
            : watcher.HighWaterMark;
        DanbooruWatcher started = watcher with
        {
            State = DanbooruWatcherState.Checking,
            ResumeState = returnState,
            Progress = watcher.Progress with { NextPage = nextPage, PagesCheckedInRun = 0, BudgetLimited = false },
            CheckStartHighWaterMark = checkStartHighWaterMark,
            CooldownUntilUtc = null,
            ErrorCode = null
        };
        return new DanbooruWatcherTransition(started, true, RequestedPage: nextPage);
    }

    internal static DanbooruWatcherTransition ApplyPage(DanbooruWatcher watcher,
        DanbooruSearchPageObservation page, DateTimeOffset observedAtUtc)
    {
        if (watcher.State != DanbooruWatcherState.Checking)
            return Reject(watcher, "check_not_running");
        if (!page.SourceId.Equals(ProviderId, StringComparison.Ordinal)
            || !page.Query.Equals(watcher.CanonicalQuery, StringComparison.Ordinal)
            || page.Page != (watcher.Progress.NextPage == 0 ? 1 : watcher.Progress.NextPage))
            return Fail(watcher, "page_binding_mismatch");
        if (page.Page < 1 || page.PostIds is null || page.PostIds.Count > watcher.PageSize)
            return Fail(watcher, "page_limit_exceeded");

        string? previousId = null;
        HashSet<string> pageSet = new(StringComparer.Ordinal);
        foreach (string? postId in page.PostIds)
        {
            if (!DanbooruPostReference.IsValidPostId(postId))
                return Fail(watcher, "invalid_post_id");
            if (previousId is not null && DanbooruPostReference.ComparePostIds(previousId, postId) < 0)
                return Fail(watcher, "page_not_descending");
            if (pageSet.Add(postId) && watcher.CursorPostId is not null
                && DanbooruPostReference.ComparePostIds(postId, watcher.CursorPostId) >= 0)
                return Fail(watcher, "cursor_did_not_advance");
            previousId = postId;
        }
        if (watcher.BaselineEstablished && page.PostIds.Count == 0 && !page.IsEnd)
            return Fail(watcher, "empty_page_did_not_advance");

        HashSet<string> known = new(watcher.KnownPostIds, StringComparer.Ordinal);
        List<string> added = new();
        // Stable per-check fence: page 1 of a catch-up raises HighWaterMark, so later pages
        // of the same traversal must still be classified against the check-start value.
        // Only the baseline-establishing first check has no fence and keeps the live HWM
        // (which is null there and emits nothing anyway). An established watcher whose
        // check started with an empty HWM has a null fence: every unknown post is new.
        string? checkFence = watcher.BaselineEstablished
            ? watcher.CheckStartHighWaterMark
            : watcher.HighWaterMark;
        foreach (string id in page.PostIds)
        {
            bool newer = checkFence is null
                || DanbooruPostReference.ComparePostIds(id, checkFence) > 0;
            if (watcher.BaselineEstablished && newer && known.Add(id))
                added.Add(id);
            else
                known.Add(id);
        }

        string? highWater = watcher.HighWaterMark;
        foreach (string id in page.PostIds)
            if (highWater is null || DanbooruPostReference.ComparePostIds(id, highWater) > 0)
                highWater = id;

        bool establishingBaseline = !watcher.BaselineEstablished;
        bool reachedKnownBoundary = watcher.BaselineEstablished && checkFence is not null
            && page.PostIds.Any(id => DanbooruPostReference.ComparePostIds(id, checkFence) <= 0);
        int pagesChecked = watcher.Progress.PagesCheckedInRun + 1;
        bool complete = establishingBaseline || page.IsEnd || reachedKnownBoundary;
        bool budgetReached = pagesChecked >= watcher.PageBudget;
        DanbooruWatcherState state;
        DanbooruWatcherProgress progress;
        string? cursor;
        if (complete)
        {
            state = watcher.ResumeState;
            progress = DanbooruWatcherProgress.Initial;
            cursor = null;
        }
        else if (budgetReached)
        {
            state = watcher.ResumeState;
            cursor = page.PostIds.Count == 0 ? watcher.CursorPostId : page.PostIds[^1];
            progress = new DanbooruWatcherProgress(page.Page + 1, 0, true)
            {
                CursorBeforePostId = cursor
            };
        }
        else
        {
            state = DanbooruWatcherState.Checking;
            cursor = page.PostIds.Count == 0 ? watcher.CursorPostId : page.PostIds[^1];
            progress = new DanbooruWatcherProgress(page.Page + 1, pagesChecked, false)
            {
                CursorBeforePostId = cursor
            };
        }

        DanbooruWatcher updated = watcher with
        {
            State = state,
            BaselineEstablished = true,
            HighWaterMark = highWater,
            KnownPostIds = SortIds(known),
            Progress = progress,
            CheckStartHighWaterMark = complete ? null : watcher.CheckStartHighWaterMark,
            LastCheckedAtUtc = observedAtUtc.ToUniversalTime(),
            ErrorCode = null,
            CooldownUntilUtc = null
        };
        return new DanbooruWatcherTransition(updated, true, RequestedPage: progress.NextPage,
            NewPostIds: Array.AsReadOnly(added.ToArray()));
    }

    internal static DanbooruWatcherTransition ApplyDeferred(DanbooruWatcher watcher,
        DateTimeOffset retryAtUtc, string reasonCode, DateTimeOffset observedAtUtc)
    {
        if (watcher.State != DanbooruWatcherState.Checking)
            return Reject(watcher, "check_not_running");
        if (string.IsNullOrWhiteSpace(reasonCode)) return Fail(watcher, "missing_reason");
        return new DanbooruWatcherTransition(watcher with
        {
            State = DanbooruWatcherState.Cooldown,
            CooldownUntilUtc = retryAtUtc.ToUniversalTime(),
            ErrorCode = reasonCode,
            LastCheckedAtUtc = observedAtUtc.ToUniversalTime(),
            Progress = watcher.Progress with { PagesCheckedInRun = 0, BudgetLimited = false }
        }, true);
    }

    internal static DanbooruWatcherTransition ApplyError(DanbooruWatcher watcher,
        string errorCode, DateTimeOffset observedAtUtc)
    {
        if (watcher.State != DanbooruWatcherState.Checking)
            return Reject(watcher, "check_not_running");
        if (string.IsNullOrWhiteSpace(errorCode)) return Fail(watcher, "missing_error");
        return new DanbooruWatcherTransition(watcher with
        {
            State = DanbooruWatcherState.Error,
            ErrorCode = errorCode,
            CooldownUntilUtc = null,
            LastCheckedAtUtc = observedAtUtc.ToUniversalTime(),
            Progress = watcher.Progress with { PagesCheckedInRun = 0, BudgetLimited = false }
        }, true);
    }

    internal static DanbooruWatcherTransition CancelCheck(DanbooruWatcher watcher)
    {
        if (watcher.State != DanbooruWatcherState.Checking)
            return Reject(watcher, "check_not_running");
        return new DanbooruWatcherTransition(watcher with
        {
            State = watcher.ResumeState,
            Progress = watcher.Progress with { PagesCheckedInRun = 0, BudgetLimited = false }
        }, true);
    }

    internal static DanbooruWatcherTransition Pause(DanbooruWatcher watcher)
    {
        if (watcher.State == DanbooruWatcherState.Removed)
            return Reject(watcher, "watcher_removed");
        if (watcher.State == DanbooruWatcherState.Paused)
            return Reject(watcher, "watcher_already_paused");
        return new DanbooruWatcherTransition(watcher with
        {
            State = DanbooruWatcherState.Paused,
            ResumeState = DanbooruWatcherState.Paused,
            CooldownUntilUtc = null,
            Progress = watcher.Progress with { PagesCheckedInRun = 0, BudgetLimited = false }
        }, true);
    }

    internal static DanbooruWatcherTransition Resume(DanbooruWatcher watcher, DateTimeOffset nowUtc)
    {
        if (watcher.State == DanbooruWatcherState.Removed)
            return Reject(watcher, "watcher_removed");
        if (watcher.State == DanbooruWatcherState.Checking)
            return Reject(watcher, "check_already_running");
        if (watcher.State == DanbooruWatcherState.Cooldown
            && watcher.CooldownUntilUtc is { } retryAt && nowUtc.ToUniversalTime() < retryAt)
            return Reject(watcher, "cooldown_active");
        if (watcher.State == DanbooruWatcherState.Active)
            return Reject(watcher, "watcher_already_active");
        return new DanbooruWatcherTransition(watcher with
        {
            State = DanbooruWatcherState.Active,
            ResumeState = DanbooruWatcherState.Active,
            CooldownUntilUtc = null,
            ErrorCode = null
        }, true);
    }

    internal static DanbooruWatcherTransition Remove(DanbooruWatcher watcher) =>
        watcher.State == DanbooruWatcherState.Removed
            ? Reject(watcher, "watcher_removed")
            : new DanbooruWatcherTransition(watcher with
            {
                State = DanbooruWatcherState.Removed,
                CooldownUntilUtc = null,
                ErrorCode = null,
                Progress = DanbooruWatcherProgress.Initial,
                CheckStartHighWaterMark = null
            }, true);

    internal static DanbooruWatcher RecoverAfterRestart(DanbooruWatcher watcher)
    {
        if (watcher.State != DanbooruWatcherState.Checking) return watcher;
        return watcher with
        {
            State = watcher.ResumeState,
            Progress = watcher.Progress with { PagesCheckedInRun = 0, BudgetLimited = false }
        };
    }

    private static IReadOnlyList<string> SortIds(IEnumerable<string> ids) =>
        Array.AsReadOnly(ids.OrderBy(id => id, Comparer<string>.Create(DanbooruPostReference.ComparePostIds)).ToArray());

    private static DanbooruWatcherTransition Reject(DanbooruWatcher watcher, string code) =>
        new(watcher, false, code);

    private static DanbooruWatcherTransition Fail(DanbooruWatcher watcher, string code) =>
        new(watcher with
        {
            State = DanbooruWatcherState.Error,
            ErrorCode = code,
            CooldownUntilUtc = null,
            Progress = watcher.Progress with { PagesCheckedInRun = 0, BudgetLimited = false }
        }, false, code);
}
