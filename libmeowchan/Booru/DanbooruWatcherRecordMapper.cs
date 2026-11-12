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
            DanbooruWatcher.ProviderId,
            compact.CanonicalQuery,
            compact.State.ToString(),
            compact.CreatedAtUtc,
            compact.LastCheckedAtUtc,
            compact.CooldownUntilUtc,
            compact.ErrorCode,
            compact.HighWaterMark,
            compact.Progress.NextPage,
            compact.BaselineEstablished,
            compact.KnownPostIds,
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

    internal static DanbooruWatcher FromRecord(BooruWatcherRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SourceId != DanbooruWatcher.ProviderId)
            throw Invalid("provider");
        int pageSize = record.PageSize ?? DanbooruWatcher.DefaultPageSize;
        int pageBudget = record.PageBudget ?? DanbooruWatcher.DefaultPageBudget;
        if (!DanbooruWatcher.TryCreate(record.WatcherId, record.Query, record.CreatedAtUtc,
                out DanbooruWatcher? fresh, out _, pageSize, pageBudget) || fresh is null
            || fresh.CanonicalQuery != record.Query)
            throw Invalid("identity or query");
        if (!Enum.TryParse(record.State, ignoreCase: false, out DanbooruWatcherState state) || !Enum.IsDefined(state))
            throw Invalid("state");
        DanbooruWatcherState resume;
        if (record.ResumeState is null)
            // Records written before the restart fields existed: Paused is the only deliberate target.
            resume = state == DanbooruWatcherState.Paused ? DanbooruWatcherState.Paused : DanbooruWatcherState.Active;
        else if (!Enum.TryParse(record.ResumeState, ignoreCase: false, out resume)
                 || resume is not (DanbooruWatcherState.Active or DanbooruWatcherState.Paused))
            throw Invalid("resume state");

        // Fail closed on the restart-critical traversal fields. The store validates them too, but a
        // mapper that is used directly (or against a record from another source) must not silently
        // accept a partial or inconsistent traversal and then falsely complete it (B2 MUST-2).
        if (record.NextPage < 0 || record.PagesCheckedInRun < 0)
            throw Invalid("page or run counter");
        if (record.NextPage > BooruCatalogStore.MaximumWatcherPage)
            throw Invalid("next page");
        if (record.PageSize is < 1 or > DanbooruWatcher.MaximumPageSize
            || record.PageBudget is < 1 or > DanbooruWatcher.MaximumPageBudget)
            throw Invalid("page limits");
        if (record.NextPage > 0)
        {
            // A traversal that is meant to resume must carry its own limits, not the defaults.
            if (record.PageSize is null || record.PageBudget is null)
                throw Invalid("page limits");
            if (record.ResumeState is null)
                throw Invalid("resume state");
            if (!record.CheckStartHighWaterMarkCaptured)
                throw Invalid("check-start fence capture");
            // A continuation page (NextPage > 1) without a cursor cannot resume downward.
            if (record.NextPage > 1 && record.CursorBeforePostId is null)
                throw Invalid("cursor");
            if (record.CursorBeforePostId is not null && !DanbooruPostReference.IsValidPostId(record.CursorBeforePostId))
                throw Invalid("cursor");
            if (record.CheckStartHighWaterMark is not null && !DanbooruPostReference.IsValidPostId(record.CheckStartHighWaterMark))
                throw Invalid("check-start fence");
            if (record.PagesCheckedInRun > record.NextPage - 1 || record.PagesCheckedInRun >= pageBudget)
                throw Invalid("run counter");
        }
        else if (record.CursorBeforePostId is not null || record.CheckStartHighWaterMark is not null
                 || record.CheckStartHighWaterMarkCaptured || record.PagesCheckedInRun != 0 || record.BudgetLimited)
        {
            throw Invalid("traversal fields without a traversal");
        }

        return fresh with
        {
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
