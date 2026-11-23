using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeowChan.Archive;

namespace MeowChan.Booru;

/// <summary>
/// Production projection between the <see cref="DanbooruWatcher"/> aggregate and the B3 storage record
/// <see cref="BooruWatcherRecord"/> (restart contract B2 MUST-2). It copies every traversal field 1:1; it
/// never interprets or repairs domain state. A record that does not describe a valid watcher fails closed
/// with <see cref="InvalidDataException"/>; the scheduler then refuses to run instead of guessing.
/// </summary>
internal static class DanbooruWatcherRecordMapper
{
    internal static BooruWatcherRecord ToRecord(DanbooruWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        DanbooruWatcher compact = CompactKnownPostIds(watcher);
        return new BooruWatcherRecord(
            compact.WatcherId,
            compact.SourceId,
            compact.CanonicalQuery,
            compact.State.ToString(),
            compact.CreatedAtUtc,
            compact.LastCheckedAtUtc,
            compact.CooldownUntilUtc,
            compact.ErrorCode,
            compact.HighWaterMark,
            compact.Progress.NextPage,
            compact.BaselineEstablished,
            Array.AsReadOnly(compact.KnownPostIds.ToArray()),
            CursorBeforePostId: compact.Progress.CursorBeforePostId,
            CheckStartHighWaterMark: compact.CheckStartHighWaterMark,
            // A fence is captured exactly while a traversal is in flight, even when its value is null
            // (an established watcher whose empty baseline had no high-water mark yet).
            CheckStartHighWaterMarkCaptured: compact.Progress.NextPage > 0,
            ResumeState: compact.ResumeState.ToString(),
            PageSize: compact.PageSize,
            PageBudget: compact.PageBudget,
            PagesCheckedInRun: compact.Progress.PagesCheckedInRun,
            BudgetLimited: compact.Progress.BudgetLimited);
    }

    internal static DanbooruWatcher FromRecord(BooruWatcherRecord record, string sourceId = DanbooruWatcher.ProviderId)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SourceId != sourceId || !BooruPostKey.IsToken(sourceId))
            throw Invalid("provider");
        int pageSize = record.PageSize ?? DanbooruWatcher.DefaultPageSize;
        int pageBudget = record.PageBudget ?? DanbooruWatcher.DefaultPageBudget;
        if (!DanbooruWatcher.TryCreate(record.WatcherId, record.Query, record.CreatedAtUtc,
                out DanbooruWatcher? fresh, out _, pageSize, pageBudget) || fresh is null
            || fresh.CanonicalQuery != record.Query)
            throw Invalid("identity or query");
        if (!Enum.TryParse(record.State, ignoreCase: false, out DanbooruWatcherState state) || !Enum.IsDefined(state))
            throw Invalid("state");

        ValidateObservation(record);
        DanbooruWatcherState resume = ValidateStateAndResume(record, state);
        ValidateTraversal(record, state, resume);

        return fresh with
        {
            SourceId = record.SourceId,
            State = state,
            ResumeState = resume,
            LastCheckedAtUtc = record.LastCheckedAtUtc,
            CooldownUntilUtc = record.CooldownUntilUtc,
            ErrorCode = record.ErrorCode,
            HighWaterMark = record.HighWaterMark,
            BaselineEstablished = record.BaselineEstablished,
            KnownPostIds = Array.AsReadOnly(record.KnownPostIds.ToArray()),
            Progress = new DanbooruWatcherProgress(record.NextPage, record.PagesCheckedInRun, record.BudgetLimited)
            {
                CursorBeforePostId = record.CursorBeforePostId
            },
            CheckStartHighWaterMark = record.CheckStartHighWaterMark
        };
    }

    /// <summary>
    /// The catalog normally validates records before the mapper sees them, but the mapper is also used by
    /// startup/recovery tests and is the only boundary that constructs a domain aggregate. Validate the
    /// observation and state geometry here as well, so a caller cannot bypass the storage writer and turn a
    /// malformed record into a runnable watcher.
    /// </summary>
    private static void ValidateObservation(BooruWatcherRecord record)
    {
        if (record.HighWaterMark is not null && !DanbooruPostReference.IsValidPostId(record.HighWaterMark))
            throw Invalid("high-water mark");
        if (record.HighWaterMark is not null && !record.BaselineEstablished)
            throw Invalid("high-water mark without baseline");
        if (record.ErrorCode is not null
            && (record.ErrorCode.Length > 128 || record.ErrorCode.Any(char.IsControl)))
            throw Invalid("error code");
        if (record.KnownPostIds is null || record.KnownPostIds.Count > BooruCatalogStore.MaximumKnownPostIds)
            throw Invalid("known posts");

        string? previous = null;
        foreach (string? postId in record.KnownPostIds)
        {
            if (!DanbooruPostReference.IsValidPostId(postId))
                throw Invalid("known post id");
            if (previous is not null && DanbooruPostReference.ComparePostIds(previous, postId) >= 0)
                throw Invalid("known post order");
            previous = postId;
        }
        if (previous is not null && (record.HighWaterMark is null
                || DanbooruPostReference.ComparePostIds(previous, record.HighWaterMark) > 0))
            throw Invalid("known posts above high-water mark");
    }

    private static DanbooruWatcherState ValidateStateAndResume(BooruWatcherRecord record,
        DanbooruWatcherState state)
    {
        DanbooruWatcherState resume;
        if (record.ResumeState is null)
        {
            // Schema-1 records written before the additive restart fields existed are safe to
            // restore only when they have no in-flight traversal. Paused is the only deliberate
            // resume target that can be inferred from the old state field.
            resume = state == DanbooruWatcherState.Paused
                ? DanbooruWatcherState.Paused : DanbooruWatcherState.Active;
        }
        else if (!Enum.TryParse(record.ResumeState, ignoreCase: false, out resume)
                 || resume is not (DanbooruWatcherState.Active or DanbooruWatcherState.Paused))
            throw Invalid("resume state");

        if (state == DanbooruWatcherState.Active && resume != DanbooruWatcherState.Active
            || state == DanbooruWatcherState.Paused && resume != DanbooruWatcherState.Paused)
            throw Invalid("state and resume state");
        if (state == DanbooruWatcherState.Cooldown && record.CooldownUntilUtc is null
            || state != DanbooruWatcherState.Cooldown && record.CooldownUntilUtc is not null)
            throw Invalid("cooldown state");
        if (state is DanbooruWatcherState.Error or DanbooruWatcherState.Cooldown
            && string.IsNullOrWhiteSpace(record.ErrorCode)
            || state is not (DanbooruWatcherState.Error or DanbooruWatcherState.Cooldown)
            && record.ErrorCode is not null)
            throw Invalid("error state");
        return resume;
    }

    private static void ValidateTraversal(BooruWatcherRecord record, DanbooruWatcherState state,
        DanbooruWatcherState resume)
    {
        if (record.NextPage < 0 || record.NextPage > BooruCatalogStore.MaximumWatcherPage
            || record.PagesCheckedInRun < 0)
            throw Invalid("page or run counter");
        if ((record.PageSize is null) != (record.PageBudget is null))
            throw Invalid("partial page limits");
        if (record.PageSize is < 1 or > DanbooruWatcher.MaximumPageSize
            || record.PageBudget is < 1 or > DanbooruWatcher.MaximumPageBudget)
            throw Invalid("page limits");
        if (record.CursorBeforePostId is not null && !DanbooruPostReference.IsValidPostId(record.CursorBeforePostId))
            throw Invalid("cursor");
        if (record.CheckStartHighWaterMark is not null
            && !DanbooruPostReference.IsValidPostId(record.CheckStartHighWaterMark))
            throw Invalid("check-start fence");
        if (record.CheckStartHighWaterMark is not null && !record.CheckStartHighWaterMarkCaptured)
            throw Invalid("check-start fence capture");

        if (state == DanbooruWatcherState.Removed || record.NextPage == 0)
        {
            if (state == DanbooruWatcherState.Checking)
                throw Invalid("checking watcher without a page");
            if (record.NextPage != 0 || record.CursorBeforePostId is not null
                || record.CheckStartHighWaterMark is not null || record.CheckStartHighWaterMarkCaptured
                || record.PagesCheckedInRun != 0 || record.BudgetLimited)
                throw Invalid("traversal fields without a traversal");
            return;
        }

        // An in-flight traversal must carry the additive fields. Legacy records with NextPage > 0
        // are deliberately refused instead of being replayed from the newest page.
        if (record.PageSize is null || record.PageBudget is null || record.ResumeState is null
            || !record.CheckStartHighWaterMarkCaptured)
            throw Invalid("in-flight traversal contract");
        int pageBudget = record.PageBudget.Value;
        if (record.PagesCheckedInRun >= pageBudget || record.PagesCheckedInRun > record.NextPage - 1)
            throw Invalid("run counter");
        if (record.PagesCheckedInRun > 0 && state != DanbooruWatcherState.Checking)
            throw Invalid("run counter outside checking state");

        if (record.NextPage == 1)
        {
            if (record.CursorBeforePostId is not null || record.BudgetLimited
                || record.CheckStartHighWaterMark != record.HighWaterMark)
                throw Invalid("first-page traversal");
        }
        else
        {
            if (!record.BaselineEstablished || record.CursorBeforePostId is null
                || record.HighWaterMark is null
                || DanbooruPostReference.ComparePostIds(record.CursorBeforePostId, record.HighWaterMark) > 0)
                throw Invalid("continuation geometry");
        }

        if (record.CheckStartHighWaterMark is not null)
        {
            if (record.HighWaterMark is null
                || DanbooruPostReference.ComparePostIds(record.CheckStartHighWaterMark, record.HighWaterMark) > 0)
                throw Invalid("check-start fence above high-water mark");
            if (record.CursorBeforePostId is not null
                && DanbooruPostReference.ComparePostIds(record.CursorBeforePostId, record.CheckStartHighWaterMark) <= 0)
                throw Invalid("cursor at or below check-start fence");
        }
        if (record.BudgetLimited
            && (record.NextPage < pageBudget + 1 || record.PagesCheckedInRun != 0
                || state is not (DanbooruWatcherState.Active or DanbooruWatcherState.Paused)
                || resume != state))
            throw Invalid("budget limit");
    }

    /// <summary>
    /// Drops known post ids that can no longer influence any transition. <see cref="DanbooruWatcher.ApplyPage"/>
    /// only ever treats an id as new when it is above the traversal fence, and every future fence is at
    /// least the current high-water mark. Ids at or below the fence (the check-start fence while a
    /// traversal is in flight, otherwise the high-water mark) are therefore dead weight; keeping them
    /// would eventually exceed <see cref="BooruCatalogStore.MaximumKnownPostIds"/> and make a long-lived
    /// watcher unpersistable (preflight G10). With no fence (null high-water mark) nothing is dropped.
    /// </summary>
    internal static DanbooruWatcher CompactKnownPostIds(DanbooruWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        string? fence = watcher.Progress.NextPage > 0 ? watcher.CheckStartHighWaterMark : watcher.HighWaterMark;
        if (fence is null || watcher.KnownPostIds.Count == 0) return watcher;
        string[] kept = watcher.KnownPostIds
            .Where(id => DanbooruPostReference.ComparePostIds(id, fence) > 0)
            .ToArray();
        return kept.Length == watcher.KnownPostIds.Count
            ? watcher
            : watcher with { KnownPostIds = Array.AsReadOnly(kept) };
    }

    private static InvalidDataException Invalid(string field) =>
        new($"The stored Booru watcher cannot be restored: {field}.");
}
