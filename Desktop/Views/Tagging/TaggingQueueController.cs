using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// The working part of the tagging page (PT2) without any control: it reads the queue, runs one host command at a time (states,
/// order, removal, clearing, cursor, stamp) and keeps the sentence the page shows afterwards. Every command writes
/// <c>tag-queue.json</c> before it completes; a refused or failed change keeps the shown queue and says why, an unreadable file
/// becomes <see cref="LoadError"/> (shown as the page's error state, recoverable by reloading or clearing). Nothing here assigns a tag, favorite
/// or rating.
/// </summary>
internal sealed class TaggingQueueController
{
    private readonly DesktopSessionHost _host;

    internal TaggingQueueController(DesktopSessionHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    internal TagQueueSnapshot Queue { get; private set; } = TagQueueSnapshot.Empty;

    /// <summary>
    /// The window's one stamp set (PT5, saved in <c>Config.TagStamp</c>), shared with the gallery panel and the popovers. The stamp of
    /// <c>tag-queue.json</c> stays in the file format but is not used (wave3: one source for the stamp). Null keeps the file's stamp.
    /// </summary>
    internal QuickTagStamp? SharedStamp { get; set; }

    /// <summary>The selected named group; null with <see cref="ActiveUngrouped"/> false means the whole queue.</summary>
    internal string? ActiveGroupId { get; private set; }
    internal bool ActiveUngrouped { get; private set; }
    internal void SelectGroup(string? groupId, bool ungrouped)
    {
        ActiveGroupId = groupId;
        ActiveUngrouped = groupId == null && ungrouped;
    }

    internal TagQueueGroup? ActiveGroup => ActiveGroupId == null ? null : Queue.Group(ActiveGroupId);

    /// <summary>The stamp names the page shows: the selected group's stamp, or the shared/general stamp.</summary>
    internal IReadOnlyList<string> StampNames => ActiveGroup?.Stamp ?? SharedStamp?.ToNames() ?? Queue.Stamp;
    internal bool UsesGroupStamp => ActiveGroup != null;

    /// <summary>Returns the selected scope's stamp for a tagging session; group edits are written back to the queue.</summary>
    internal QuickTagStamp? StampForActiveScope()
    {
        if (ActiveGroup is not { } group) return SharedStamp;
        QuickTagStamp stamp = new(group.Stamp, QuickTagOptions.MaximumStampSize);
        stamp.Changed += () => _ = _host.SetTagQueueGroupStampAsync(group.Id, stamp.ToNames());
        return stamp;
    }

    /// <summary>The reason the queue file cannot be read, or null.</summary>
    internal Exception? LoadError { get; private set; }
    internal bool Loaded { get; private set; }
    internal bool Loading { get; private set; }
    internal bool Busy { get; private set; }
    /// <summary>The sentence of the last command (or the reason it was refused); null when there is none.</summary>
    internal string? Status { get; private set; }
    internal bool Ended { get; private set; }

    /// <summary>Whether a change can be written now: the queue is read, writable (not of a newer version) and the session is ready.</summary>
    internal bool CanChange => !Ended && !Busy && !Loading && Loaded && Queue.CanWrite && LoadError == null
        && _host.State == DesktopSessionState.Ready;

    /// <summary>The error line of the page: the unreadable file, or the newer file that stays read-only; null when there is none.</summary>
    internal string? ErrorText => LoadError != null ? TagQueueActions.DescribeFailure(LoadError)
        : !Queue.CanWrite ? Texts.T("queue.error.newer") : null;

    /// <summary>The local source named next to an error, so a generic storage failure is not detached from its queue file.</summary>
    internal string? ErrorSource => ErrorText == null ? null : Texts.T("queue.title") + ": " + TagQueueStore.FileName;

    /// <summary>Whether the page can explicitly reload the queue after a failed read.</summary>
    internal bool CanReload => ErrorText != null && !Ended && !Busy && !Loading && Loaded && HostReady;

    internal bool HostReady => _host.State == DesktopSessionState.Ready;

    internal void End() => Ended = true;

    internal void ClearStatus() => Status = null;

    internal void SetStatus(string text) => Status = text;

    /// <summary>Reads the file and publishes it. An unreadable file is reported, not emptied.</summary>
    internal async Task LoadAsync()
    {
        if (Ended || Loading) return;
        Loading = true;
        try
        {
            TagQueueSnapshot loaded = await _host.LoadTagQueueAsync();
            if (Ended) return;
            Queue = loaded;
            LoadError = null;
        }
        catch (Exception error) when (IsQueueError(error))
        {
            if (Ended) return;
            // Keep the last good snapshot. The page must not turn a read failure into a plausible empty queue;
            // the error state owns the surface until an explicit reload succeeds.
            LoadError = error;
        }
        finally
        {
            Loaded = true;
            Loading = false;
        }
    }

    /// <summary>Takes the host's last committed queue when it differs from the shown one; true when the page has to render again.</summary>
    internal bool Refresh()
    {
        if (Ended || !Loaded || Busy || Loading) return false;
        TagQueueSnapshot? current;
        try { current = _host.GetTagQueue(); }
        catch (Exception error) when (IsQueueError(error))
        {
            if (LoadError != null) return false;
            Queue = TagQueueSnapshot.Empty;
            LoadError = error;
            return true;
        }
        if (current == null || ReferenceEquals(current, Queue)) return false;
        Queue = current;
        LoadError = null;
        return true;
    }

    internal Task<bool> SetStatesAsync(IReadOnlyList<TagQueueKey> keys, TagQueueState state)
    {
        KeyValuePair<TagQueueKey, TagQueueState>[] changes = keys.Where(key => Queue.IndexOf(key) is var index && index >= 0 && Queue.Items[index].State != state)
            .Select(key => new KeyValuePair<TagQueueKey, TagQueueState>(key, state)).ToArray();
        if (changes.Length == 0) return Task.FromResult(false);
        string sentence = state switch
        {
            TagQueueState.Done => "tagging.page.marked.done",
            TagQueueState.Skipped => "tagging.page.marked.skipped",
            _ => "tagging.page.marked.open"
        };
        return RunAsync(() => _host.SetTagQueueStatesAsync(changes), _ => Texts.T(sentence, changes.Length));
    }

    /// <summary>Moves one entry past its neighbouring shown entry (<paramref name="direction"/> −1 up, +1 down) under <paramref name="filter"/>.</summary>
    internal Task<bool> MoveAsync(TagQueueKey key, TaggingQueueFilter filter, int direction, Func<TagQueueItem, bool>? scope = null)
    {
        if (TaggingQueueView.MoveTarget(Queue, filter, key, direction, scope) is not { } target) return Task.FromResult(false);
        return RunAsync(() => _host.MoveInTagQueueAsync(key, target), queue => Texts.T("tagging.page.moved", queue.IndexOf(key) + 1));
    }

    /// <summary>Moves one entry to the start or to the end of the whole queue (absolute positions, whatever the filter hides).</summary>
    internal Task<bool> MoveToEdgeAsync(TagQueueKey key, bool toEnd)
    {
        if (TaggingQueueView.EdgeTarget(Queue, key, toEnd) is not { } target) return Task.FromResult(false);
        return RunAsync(() => _host.MoveInTagQueueAsync(key, target), queue => Texts.T("tagging.page.moved", queue.IndexOf(key) + 1));
    }

    internal Task<bool> RemoveAsync(IReadOnlyCollection<TagQueueKey> keys)
    {
        TagQueueKey[] frozen = keys.Where(Queue.Contains).ToArray();
        if (frozen.Length == 0) return Task.FromResult(false);
        return RunAsync(() => _host.RemoveFromTagQueueAsync(frozen), _ => Texts.T("tagging.page.removed", frozen.Length));
    }

    internal Task<bool> ClearFinishedAsync()
    {
        int done = Queue.Done;
        if (done == 0) return Task.FromResult(false);
        return RunAsync(() => _host.ClearTagQueueAsync(finishedOnly: true), _ => Texts.T("tagging.page.clearedDone", done));
    }

    /// <summary>Empties the queue; on an unreadable file the store keeps the damaged copy next to it and writes an empty queue.</summary>
    internal Task<bool> ClearAllAsync() =>
        RunAsync(() => _host.ClearTagQueueAsync(finishedOnly: false), _ => Texts.T("tagging.page.cleared"), allowDamaged: true);

    /// <summary>Puts the walk's cursor on an entry ("continue here"); no sentence, the gallery opens next.</summary>
    internal Task<bool> SetCursorAsync(int index) => index == Queue.Cursor ? Task.FromResult(true)
        : RunAsync(() => _host.SetTagQueueCursorAsync(index), _ => null);

    /// <summary>
    /// Appends a name to the stamp. Refused, with a sentence, when the name cannot be an own tag name, is already in the stamp, or the
    /// stamp holds <paramref name="capacity"/> names. The text may name several tags (<see cref="TagEntryParser"/>: spaces, commas,
    /// semicolons, quotes, line breaks), each resolved through the alias table: they are added in one write, as many as fit
    /// <paramref name="capacity"/> (the stamp size setting); the sentence says which were added, which were there already and which did
    /// not fit.
    /// </summary>
    internal Task<bool> AddStampAsync(string? text, int capacity)
    {
        string candidate = (text ?? string.Empty).Trim();
        if (!CanChange || candidate.Length == 0) return Task.FromResult(false);
        Status = null;
        TagEntry entry = TagEntryParser.Parse(candidate, _host.GetPublishedPrivateTagSnapshot() is { State: PrivateTagLoadState.Ready, Snapshot: { } tags } ? tags : null);
        if (entry.TooMany) return Refuse(Texts.T("tags.entry.tooMany"));
        if (entry.Invalid.Count == 0 && entry.Items.Count > 0 && !TagEntryParser.IsPlain(entry, candidate))
            return AddStampNamesAsync(entry.Items.Select(item => item.Name).ToArray(), capacity);
        if (!PrivateTagName.TryCreate(candidate, out PrivateTagName? name, out _) || name == null)
            return Refuse(Texts.T("tagging.page.stamp.invalid", candidate));
        if (ActiveGroup is { } group)
        {
            if (group.Stamp.Any(item => string.Equals(item, name.Display, StringComparison.Ordinal))) return Refuse(Texts.T("tagging.page.stamp.already", name.Display));
            if (group.Stamp.Count >= capacity) return Refuse(Texts.T("tags.quick.stamp.full", capacity));
            string[] groupNames = group.Stamp.Append(name.Display).ToArray();
            return RunAsync(() => _host.SetTagQueueGroupStampAsync(group.Id, groupNames), _ => Texts.T("tags.quick.stamp.added", name.Display));
        }
        if (SharedStamp is { } shared)
        {
            if (shared.Contains(name)) return Refuse(Texts.T("tagging.page.stamp.already", name.Display));
            if (shared.Count >= Math.Min(capacity, shared.Capacity)) return Refuse(Texts.T("tags.quick.stamp.full", Math.Min(capacity, shared.Capacity)));
            if (shared.Add(name) != QuickTagStampAdd.Added) return Refuse(Texts.T("tags.quick.stamp.full", shared.Capacity));
            Status = Texts.T("tags.quick.stamp.added", name.Display); // saved by the window on Changed
            return Task.FromResult(true);
        }
        QuickTagStamp stamp = new(Queue.Stamp, QuickTagOptions.MaximumStampSize);
        if (stamp.Contains(name)) return Refuse(Texts.T("tagging.page.stamp.already", name.Display));
        if (stamp.Count >= capacity) return Refuse(Texts.T("tags.quick.stamp.full", capacity));
        string[] names = stamp.ToNames().Append(name.Display).ToArray();
        return RunAsync(() => _host.SetTagQueueStampAsync(names), _ => Texts.T("tags.quick.stamp.added", name.Display));
    }

    // Several names of one entry: those not in the stamp yet, up to the room left, in one write of the stamp that is shown.
    private Task<bool> AddStampNamesAsync(IReadOnlyList<PrivateTagName> names, int capacity)
    {
        TagQueueGroup? group = ActiveGroup;
        QuickTagStamp? shared = group == null ? SharedStamp : null;
        IReadOnlyList<string> current = group?.Stamp ?? shared?.ToNames() ?? Queue.Stamp;
        int limit = shared != null ? Math.Min(capacity, shared.Capacity) : capacity;
        HashSet<string> present = current.Select(item => PrivateTagName.TryCreate(item, out PrivateTagName? known, out _) && known != null ? known.Key : item)
            .ToHashSet(StringComparer.Ordinal);
        List<PrivateTagName> give = new();
        List<string> already = new(), notAdded = new();
        foreach (PrivateTagName name in names)
        {
            if (present.Contains(name.Key)) already.Add(name.Display);
            else if (current.Count + give.Count >= limit) notAdded.Add(name.Display);
            else give.Add(name);
        }
        if (give.Count == 0)
            return Refuse(notAdded.Count > 0 ? Texts.T("tags.quick.stamp.full", limit) : Texts.T("tagging.page.stamp.already", string.Join(", ", already)));
        string Sentence()
        {
            List<string> parts = new() { Texts.T("tags.quick.stamp.added", string.Join(", ", give.Select(name => name.Display))) };
            if (already.Count > 0) parts.Add(Texts.T("tags.entry.already", string.Join(", ", already)));
            if (notAdded.Count > 0) parts.Add(Texts.T("tags.entry.notAdded", limit, string.Join(", ", notAdded)));
            return string.Join(" · ", parts);
        }
        string[] all = current.Concat(give.Select(name => name.Display)).ToArray();
        if (group != null) return RunAsync(() => _host.SetTagQueueGroupStampAsync(group.Id, all), _ => Sentence());
        if (shared != null)
        {
            // The window saves the shared stamp when it changes.
            foreach (PrivateTagName name in give.ToArray())
                if (shared.Add(name) != QuickTagStampAdd.Added) { give.Remove(name); notAdded.Add(name.Display); }
            if (give.Count == 0) return Refuse(Texts.T("tags.quick.stamp.full", shared.Capacity));
            Status = Sentence();
            return Task.FromResult(true);
        }
        return RunAsync(() => _host.SetTagQueueStampAsync(all), _ => Sentence());
    }

    internal Task<bool> RemoveStampAsync(string name)
    {
        if (ActiveGroup is { } group)
        {
            string[] groupNames = group.Stamp.Where(item => !string.Equals(item, name, StringComparison.Ordinal)).ToArray();
            if (groupNames.Length == group.Stamp.Count) return Task.FromResult(false);
            return RunAsync(() => _host.SetTagQueueGroupStampAsync(group.Id, groupNames), _ => Texts.T("tags.quick.stamp.removed", name));
        }
        if (SharedStamp is { } shared)
        {
            if (Ended || !PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _) || parsed == null || !shared.Remove(parsed))
                return Task.FromResult(false);
            Status = Texts.T("tags.quick.stamp.removed", name);
            return Task.FromResult(true);
        }
        string[] names = Queue.Stamp.Where(item => !string.Equals(item, name, StringComparison.Ordinal)).ToArray();
        if (names.Length == Queue.Stamp.Count) return Task.FromResult(false);
        return RunAsync(() => _host.SetTagQueueStampAsync(names), _ => Texts.T("tags.quick.stamp.removed", name));
    }

    internal Task<bool> CreateGroupAsync(string name, IReadOnlyCollection<TagQueueKey> keys) =>
        RunAsync(() => _host.CreateTagQueueGroupAsync(name, keys), queue => Texts.T("tagging.page.group.created", queue.Groups[^1].Name));

    internal Task<bool> RenameGroupAsync(string id, string name) =>
        RunAsync(() => _host.RenameTagQueueGroupAsync(id, name), queue => Texts.T("tagging.page.group.renamed", queue.Group(id)?.Name ?? name));

    internal Task<bool> DeleteGroupAsync(string id) =>
        RunAsync(() => _host.DeleteTagQueueGroupAsync(id), _ => Texts.T("tagging.page.group.deleted"));

    internal Task<bool> AssignGroupAsync(IReadOnlyCollection<TagQueueKey> keys, string? groupId) =>
        RunAsync(() => _host.AssignTagQueueGroupAsync(keys, groupId), _ => Texts.T("tagging.page.group.assigned"));

    private Task<bool> Refuse(string sentence)
    {
        Status = sentence;
        return Task.FromResult(false);
    }

    private async Task<bool> RunAsync(Func<Task<TagQueueSnapshot>> command, Func<TagQueueSnapshot, string?> sentence, bool allowDamaged = false)
    {
        if (Ended || Busy || !(CanChange || allowDamaged && Loaded && !Loading && HostReady)) return false;
        Busy = true;
        Status = null;
        try
        {
            TagQueueSnapshot result = await command();
            if (Ended) return false;
            Queue = result;
            LoadError = null;
            if (sentence(result) is { } text) Status = text;
            return true;
        }
        catch (Exception error) when (IsQueueError(error))
        {
            if (Ended) return false;
            Status = TagQueueActions.DescribeFailure(error);
            // Keep the last good snapshot while the error is visible. In particular, a failed write or reload
            // must never look like a successful empty queue.
            if (_host.TagQueueError is { } unreadable) LoadError = unreadable;
            return false;
        }
        finally { Busy = false; }
    }

    internal static bool IsQueueError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or ProfileBackupBlockedException;
}
