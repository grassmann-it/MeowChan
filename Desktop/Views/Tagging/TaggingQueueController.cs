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
/// becomes <see cref="LoadError"/> (shown as the page's error line, recoverable by clearing). Nothing here assigns a tag, favorite
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

    /// <summary>The stamp names the page shows: the shared stamp, else the file's.</summary>
    internal IReadOnlyList<string> StampNames => SharedStamp?.ToNames() ?? Queue.Stamp;
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
            Queue = TagQueueSnapshot.Empty;
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
    /// stamp holds <paramref name="capacity"/> names.
    /// </summary>
    internal Task<bool> AddStampAsync(string? text, int capacity)
    {
        string candidate = (text ?? string.Empty).Trim();
        if (!CanChange || candidate.Length == 0) return Task.FromResult(false);
        Status = null;
        if (!PrivateTagName.TryCreate(candidate, out PrivateTagName? name, out _) || name == null)
            return Refuse(Texts.T("tagging.page.stamp.invalid", candidate));
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

    internal Task<bool> RemoveStampAsync(string name)
    {
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
            if (_host.TagQueueError is { } unreadable) { LoadError = unreadable; Queue = TagQueueSnapshot.Empty; }
            return false;
        }
        finally { Busy = false; }
    }

    internal static bool IsQueueError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException or ProfileBackupBlockedException;
}
