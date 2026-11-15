using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// "Add to tagging queue" for every surface (media page, archive, reader attachments, gallery): the frozen entries of the surface become
/// queue entries, the host writes them, and one localized sentence says what happened (added, already in it, not added and why, with the
/// setting named when the queue is full). The queue never assigns a tag, favorite or rating.
/// </summary>
internal static class TagQueueActions
{
    /// <summary>Queue entries of media index entries; the second value counts the ones that cannot take part (no saved file, provider medium).</summary>
    internal static (IReadOnlyList<TagQueueItem> Items, int Skipped) ItemsOf(IEnumerable<MediaLibraryEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<TagQueueItem> items = new();
        int skipped = 0;
        foreach (MediaLibraryEntry entry in entries)
        {
            TagQueueItem? item = entry.BooruPostId != null ? null : TagQueueItem.From(entry, now);
            if (item == null) skipped++; else items.Add(item);
        }
        return (items, skipped);
    }

    /// <summary>Queue entries of gallery entries (the archive, the reader and the gallery work on these).</summary>
    internal static (IReadOnlyList<TagQueueItem> Items, int Skipped) ItemsOf(IEnumerable<GalleryEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        List<TagQueueItem> items = new();
        int skipped = 0;
        foreach (GalleryEntry entry in entries)
        {
            TagQueueItem? item = BooruMediaIdentity.IsBooruThread(entry.Key.Thread) ? null : TagQueueItem.From(entry, now);
            if (item == null) skipped++; else items.Add(item);
        }
        return (items, skipped);
    }

    /// <summary>
    /// Delete on the media page: the keys of the selected media that are in the tagging queue (any state), once each, in selection order. Media that
    /// are not queued, have no saved asset or come while the queue is unreadable (<paramref name="queue"/> is null) give none, so the key does nothing then.
    /// It only ever names queue entries: no file, index entry, tag, favorite or rating is part of it.
    /// </summary>
    internal static IReadOnlyList<TagQueueKey> RemovableKeys(IEnumerable<MediaLibraryEntry> selected, IReadOnlySet<TagQueueKey>? queue)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (queue is not { Count: > 0 }) return Array.Empty<TagQueueKey>();
        HashSet<TagQueueKey> seen = new();
        List<TagQueueKey> keys = new();
        foreach (MediaLibraryEntry entry in selected)
            if (TagQueueKeys.Of(entry) is { } key && queue.Contains(key) && seen.Add(key)) keys.Add(key);
        return keys;
    }

    /// <summary>Removes queue entries ("Remove from queue", <c>tagging.remove</c>) and returns the sentence for the surface's status line; a failure is a sentence too.</summary>
    internal static async Task<string> RemoveAsync(DesktopSessionHost host, IReadOnlyList<TagQueueKey> keys, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return string.Empty;
        try
        {
            await host.RemoveFromTagQueueAsync(keys, token).ConfigureAwait(true);
            return Texts.T("tagging.page.removed", keys.Count);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (TaggingQueueController.IsQueueError(error)) { return DescribeFailure(error); }
    }

    /// <summary>The reason an entry cannot be queued (shown in the menu), or null when it can. A provider medium has no tag target.</summary>
    internal static string? BlockedReason(bool saved, bool providerMedia) =>
        !saved ? Texts.T("media.menu.reason.notSaved") : providerMedia ? Texts.T("booru.media.tags.unavailable") : null;

    internal static Task<string> AddAsync(DesktopSessionHost host, IEnumerable<MediaLibraryEntry> entries, CancellationToken token = default)
    {
        (IReadOnlyList<TagQueueItem> items, int skipped) = ItemsOf(entries, DateTimeOffset.UtcNow);
        return AddAsync(host, items, skipped, token);
    }

    internal static Task<string> AddAsync(DesktopSessionHost host, IEnumerable<GalleryEntry> entries, CancellationToken token = default)
    {
        (IReadOnlyList<TagQueueItem> items, int skipped) = ItemsOf(entries, DateTimeOffset.UtcNow);
        return AddAsync(host, items, skipped, token);
    }

    /// <summary>
    /// MP2: like <see cref="AddAsync(DesktopSessionHost, IEnumerable{MediaLibraryEntry}, CancellationToken)"/>, and how many entries were really
    /// added (0 when nothing was new, nothing could be queued or the write failed), so the surface can offer "Go through now".
    /// </summary>
    internal static Task<(string Message, int Added)> AddCountedAsync(DesktopSessionHost host, IEnumerable<MediaLibraryEntry> entries,
        CancellationToken token = default) => AddCountedAsync(host, entries, groupId: null, token);

    internal static Task<(string Message, int Added)> AddCountedAsync(DesktopSessionHost host, IEnumerable<MediaLibraryEntry> entries,
        string? groupId, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        (IReadOnlyList<TagQueueItem> items, int skipped) = ItemsOf(entries, DateTimeOffset.UtcNow);
        IReadOnlyList<TagQueueItem> scoped = groupId == null ? items : items.Select(item => item with { GroupId = groupId }).ToArray();
        return AddCountedItemsAsync(host, scoped, skipped, token);
    }

    private static async Task<(string Message, int Added)> AddCountedItemsAsync(DesktopSessionHost host, IReadOnlyList<TagQueueItem> items,
        int skipped, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (items.Count == 0) return (Describe(null, skipped), 0);
        try
        {
            TagQueueAddResult result = await host.EnqueueTagQueueAsync(items, token).ConfigureAwait(true);
            return (Describe(result, skipped), result.Added);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return (DescribeFailure(error), 0); }
    }

    /// <summary>Writes the entries and returns the sentence for the surface's status line. A failure is a sentence too, never an exception.</summary>
    internal static async Task<string> AddAsync(DesktopSessionHost host, IReadOnlyList<TagQueueItem> items, int skipped, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (items.Count == 0) return Describe(null, skipped);
        try { return Describe(await host.EnqueueTagQueueAsync(items, token).ConfigureAwait(true), skipped); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        { return DescribeFailure(error); }
    }

    /// <summary>The sentence for a result. <paramref name="result"/> null means nothing could be queued at all.</summary>
    internal static string Describe(TagQueueAddResult? result, int skipped)
    {
        string text = result == null ? Texts.T("queue.result.none")
            : result.OverLimit > 0 ? Texts.T("queue.result.limit", result.Added, result.AlreadyPresent, result.OverLimit, Texts.T("settings.limits.tagQueue"), Texts.T("settings.storageLimits"))
            : Texts.T("queue.result", result.Added, result.AlreadyPresent);
        return skipped > 0 ? text + " " + Texts.T("queue.result.skipped", skipped) : text;
    }

    /// <summary>A localized sentence for a refused or failed change: the full queue file, a newer file, a blocked backup, an unreadable file.</summary>
    internal static string DescribeFailure(Exception error) => StoreLimitText.Describe(error)
        // The host already localizes a refused limit into an outer error whose message is the sentence; the store's error stays inner.
        ?? (error is InvalidDataException && StoreLimitText.Describe(error.InnerException ?? error) != null ? error.Message
        : error is ProfileBackupBlockedException blocked ? StoreBackupBlockedText.For(blocked)
            : error is TagQueueNewerVersionException ? Texts.T("queue.error.newer")
            : error is DesktopSessionBusyException ? Texts.T("queue.error.busy")
            : Texts.T("queue.error.unreadable"));
}
