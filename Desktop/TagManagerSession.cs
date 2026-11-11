using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>One tag with the number of threads and media versions that carry it.</summary>
internal sealed record TagManagerRow(TagId Id, string Name, int Threads, int Media);

/// <summary>
/// State behind the tag manager: the current tag index plus rename and delete, each bound to the
/// revision the user is looking at. No UI and no storage; the host commands do the writing.
/// </summary>
internal sealed class TagManagerSession
{
    private readonly Func<PrivateTagLoadSnapshot> _load;
    private readonly Func<TagId, string, long, CancellationToken, Task<PrivateTagCommit>> _rename;
    private readonly Func<TagId, long, CancellationToken, Task<PrivateTagCommit>> _delete;

    internal TagManagerSession(Func<PrivateTagLoadSnapshot> load,
        Func<TagId, string, long, CancellationToken, Task<PrivateTagCommit>> rename,
        Func<TagId, long, CancellationToken, Task<PrivateTagCommit>> delete)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _rename = rename ?? throw new ArgumentNullException(nameof(rename));
        _delete = delete ?? throw new ArgumentNullException(nameof(delete));
        Reload();
    }

    /// <summary>Null while the tag index is unavailable; then nothing can be changed.</summary>
    internal PrivateTagStoreSnapshot? Snapshot { get; private set; }
    internal IReadOnlyList<TagManagerRow> Rows { get; private set; } = Array.Empty<TagManagerRow>();
    internal bool Busy { get; private set; }
    /// <summary>Localization key of the last outcome, or null.</summary>
    internal string? MessageKey { get; private set; }
    internal bool MessageIsError { get; private set; }
    /// <summary>True once any rename or delete was committed in this session.</summary>
    internal bool Changed { get; private set; }
    internal event Action? Updated;

    /// <summary>Drops the last outcome text; the user moved on to something else.</summary>
    internal void ClearMessage() => MessageKey = null;

    internal void Reload()
    {
        PrivateTagLoadSnapshot loaded = _load();
        Adopt(loaded.State == PrivateTagLoadState.Ready ? loaded.Snapshot : null);
    }

    private void Adopt(PrivateTagStoreSnapshot? snapshot)
    {
        Snapshot = snapshot;
        Rows = BuildRows(snapshot);
    }

    internal static IReadOnlyList<TagManagerRow> BuildRows(PrivateTagStoreSnapshot? snapshot)
    {
        if (snapshot == null) return Array.Empty<TagManagerRow>();
        Dictionary<TagId, int> threads = snapshot.ThreadAssignments.GroupBy(item => (TagId)item.TagId).ToDictionary(group => group.Key, group => group.Count());
        // Distinct files, not assignments: a file whose tag was handed over to a new revision key counts once.
        IReadOnlyDictionary<TagId, int> media = snapshot.DistinctMediaCounts();
        return snapshot.Tags.Select(tag => new TagManagerRow(tag.Id, tag.Name.Display,
            threads.GetValueOrDefault(tag.Id), media.GetValueOrDefault(tag.Id))).ToArray();
    }

    internal Task RenameAsync(TagId id, string? name, CancellationToken token = default) =>
        RunAsync(revision => _rename(id, name ?? string.Empty, revision, token), "tags.manage.renamed");

    internal Task DeleteAsync(TagId id, CancellationToken token = default) =>
        RunAsync(revision => _delete(id, revision, token), "tags.manage.deleted");

    private async Task RunAsync(Func<long, Task<PrivateTagCommit>> operation, string doneKey)
    {
        if (Busy || Snapshot == null) return;
        Busy = true;
        MessageKey = null;
        Updated?.Invoke();
        try
        {
            PrivateTagCommit commit = await operation(Snapshot.Revision);
            Adopt(commit.Snapshot);
            if (commit.Changed)
            {
                Changed = true;
                MessageKey = doneKey;
                MessageIsError = false;
            }
        }
        catch (OperationCanceledException) { }
        catch (PrivateTagStoreException error)
        {
            MessageIsError = true;
            MessageKey = error.Error switch
            {
                PrivateTagStoreError.InvalidName => "tags.error.invalidName",
                PrivateTagStoreError.NameCollision => "tags.manage.error.collision",
                PrivateTagStoreError.StaleRevision => "tags.error.stale",
                PrivateTagStoreError.UnknownTag => "tags.manage.error.gone",
                PrivateTagStoreError.CapacityExceeded => "tags.error.capacity",
                _ => "tags.error.other"
            };
            // A stale or vanished target means this view is old: show the current state before a retry.
            if (error.Error is PrivateTagStoreError.StaleRevision or PrivateTagStoreError.UnknownTag) Reload();
        }
        catch (Exception)
        {
            MessageIsError = true;
            MessageKey = "tags.error.other";
            Reload();
        }
        finally
        {
            Busy = false;
            Updated?.Invoke();
        }
    }
}
