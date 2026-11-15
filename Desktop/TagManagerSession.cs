using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>One tag with the number of threads and media versions that carry it.</summary>
internal sealed record TagManagerRow(TagId Id, string Name, int Threads, int Media)
{
    /// <summary>Threads plus distinct media files: the same count as the tag field's suggestions and the tag dialogs.</summary>
    internal int Uses => Threads + Media;

    /// <summary>The rows in the chosen order: most used first, or alphabetical.</summary>
    internal static IReadOnlyList<TagManagerRow> Sorted(IEnumerable<TagManagerRow> rows, TagListSort sort) => sort == TagListSort.Uses
        ? TagToggleRow.OrderByUses(rows, row => row.Uses, row => row.Name).ToArray()
        : rows.OrderBy(row => row.Name, StringComparer.Create(MeowChan.Desktop.Localization.Texts.Culture, ignoreCase: true))
            .ThenBy(row => row.Name, StringComparer.Ordinal).ToArray();
}

/// <summary>The host commands behind merging and aliases; a manager without them offers neither.</summary>
internal sealed record TagMergeCommands(
    Func<TagId, IReadOnlyList<TagId>, long, CancellationToken, Task<PrivateTagMergeCommit>> Merge,
    Func<PrivateTagMergeUndo, CancellationToken, Task<PrivateTagCommit>> Undo,
    Func<TagId, string, long, CancellationToken, Task<PrivateTagCommit>> AddAlias,
    Func<string, long, CancellationToken, Task<PrivateTagCommit>> RemoveAlias);

/// <summary>
/// What a merge would do, counted on the shown snapshot: the media files and threads that newly get the target (an item that
/// already carries it is not counted), and the tags that go away (their names stay as aliases of the target).
/// </summary>
internal sealed record TagMergePreview(TagManagerRow Target, IReadOnlyList<TagManagerRow> Merged, int Media, int Threads)
{
    internal string MergedNames => string.Join(", ", Merged.Select(row => MeowChan.Desktop.Localization.Texts.T("tags.merge.quoted", row.Name)));
}

/// <summary>
/// State behind the tag manager: the current tag index plus rename and delete, each bound to the
/// revision the user is looking at; with <see cref="TagMergeCommands"/> also merging (with a one-step undo), aliases and the
/// probable-duplicate suggestions. No UI and no storage; the host commands do the writing.
/// </summary>
internal sealed class TagManagerSession
{
    private readonly Func<PrivateTagLoadSnapshot> _load;
    private readonly Func<TagId, string, long, CancellationToken, Task<PrivateTagCommit>> _rename;
    private readonly Func<TagId, long, CancellationToken, Task<PrivateTagCommit>> _delete;
    private readonly Func<CancellationToken, Task<LocalMediaTagInspection?>>? _inspectOrphans;
    private readonly Func<IReadOnlyCollection<PrivateLocalMediaTagAssignment>, long, CancellationToken, Task<PrivateTagCommit>>? _removeOrphans;

    internal TagManagerSession(Func<PrivateTagLoadSnapshot> load,
        Func<TagId, string, long, CancellationToken, Task<PrivateTagCommit>> rename,
        Func<TagId, long, CancellationToken, Task<PrivateTagCommit>> delete,
        Func<CancellationToken, Task<LocalMediaTagInspection?>>? inspectOrphans = null,
        Func<IReadOnlyCollection<PrivateLocalMediaTagAssignment>, long, CancellationToken, Task<PrivateTagCommit>>? removeOrphans = null,
        TagMergeCommands? merge = null)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _rename = rename ?? throw new ArgumentNullException(nameof(rename));
        _delete = delete ?? throw new ArgumentNullException(nameof(delete));
        _inspectOrphans = inspectOrphans;
        _removeOrphans = removeOrphans;
        _merge = merge;
        Reload();
    }

    private readonly TagMergeCommands? _merge;

    /// <summary>True when the host can merge tags and edit aliases.</summary>
    internal bool CanMerge => _merge != null;
    /// <summary>The way back from the last merge of this session; null before one or after its undo.</summary>
    internal PrivateTagMergeUndo? LastMerge { get; private set; }
    /// <summary>True while the tags are still exactly as the last merge left them, so it can be taken back as one step.</summary>
    internal bool CanUndoMerge => LastMerge != null && Snapshot?.Revision == LastMerge.Revision;
    /// <summary>The name the last merge gave its tags to (for the status text).</summary>
    internal string? LastMergeTarget { get; private set; }

    /// <summary>Probable duplicates among the tags, most used first. Suggestions only: nothing is merged by itself.</summary>
    internal IReadOnlyList<TagDuplicateGroup> Duplicates() => Snapshot is { } snapshot
        ? TagNameFold.Duplicates(snapshot.Tags, Rows.ToDictionary(row => row.Id, row => row.Uses)) : Array.Empty<TagDuplicateGroup>();

    /// <summary>The other names of a tag, in the order they were added.</summary>
    internal IReadOnlyList<PrivateTagAlias> AliasesOf(TagId tag) => Snapshot?.AliasesOf(tag) ?? Array.Empty<PrivateTagAlias>();

    /// <summary>The target a merge of <paramref name="tags"/> proposes: the most used of them (ties: the first given).</summary>
    internal TagId? DefaultMergeTarget(IReadOnlyList<TagId> tags)
    {
        Dictionary<TagId, TagManagerRow> rows = Rows.ToDictionary(row => row.Id);
        TagManagerRow? best = null;
        foreach (TagId id in tags)
            if (rows.TryGetValue(id, out TagManagerRow? row) && (best == null || row.Uses > best.Uses)) best = row;
        return best?.Id;
    }

    /// <summary>What merging <paramref name="tags"/> into <paramref name="target"/> would do; null when that is no valid merge now.</summary>
    internal TagMergePreview? Preview(IReadOnlyList<TagId> tags, TagId target)
    {
        if (Snapshot is not { } snapshot) return null;
        Dictionary<TagId, TagManagerRow> rows = Rows.ToDictionary(row => row.Id);
        HashSet<TagId> merged = tags.Where(id => id != target && rows.ContainsKey(id)).ToHashSet();
        if (merged.Count == 0 || !rows.TryGetValue(target, out TagManagerRow? targetRow)) return null;
        // Files and threads that newly get the target: carried by a merged tag, not by the target already.
        HashSet<MediaIdentity> boundHas = new(), boundGets = new();
        foreach (PrivateMediaTagAssignment item in snapshot.MediaAssignments)
            if ((TagId)item.TagId == target) boundHas.Add(item.Target.Identity);
        foreach (PrivateMediaTagAssignment item in snapshot.MediaAssignments)
            if (merged.Contains((TagId)item.TagId) && !boundHas.Contains(item.Target.Identity)) boundGets.Add(item.Target.Identity);
        HashSet<LocalMediaTagTarget> localHas = snapshot.LocalAssignments.Where(item => item.TagId == target).Select(item => item.Target).ToHashSet();
        int local = snapshot.LocalAssignments.Where(item => merged.Contains(item.TagId) && !localHas.Contains(item.Target))
            .Select(item => item.Target).Distinct().Count();
        HashSet<ResourceId> threadHas = snapshot.ThreadAssignments.Where(item => (TagId)item.TagId == target).Select(item => item.Thread).ToHashSet();
        int threads = snapshot.ThreadAssignments.Where(item => merged.Contains((TagId)item.TagId) && !threadHas.Contains(item.Thread))
            .Select(item => item.Thread).Distinct().Count();
        return new TagMergePreview(targetRow, tags.Where(merged.Contains).Distinct().Select(id => rows[id]).ToArray(), boundGets.Count + local, threads);
    }

    /// <summary>Merges <paramref name="tags"/> into <paramref name="target"/> in one write bound to the shown revision.</summary>
    internal Task MergeAsync(IReadOnlyList<TagId> tags, TagId target, CancellationToken token = default)
    {
        if (_merge == null) return Task.CompletedTask;
        TagId[] merged = tags.Where(id => id != target).Distinct().ToArray();
        string? targetName = Rows.FirstOrDefault(row => row.Id == target)?.Name;
        return RunAsync(async revision =>
        {
            PrivateTagMergeCommit result = await _merge.Merge(target, merged, revision, token);
            LastMerge = result.Undo;
            LastMergeTarget = targetName;
            return result.Commit;
        }, "tags.merge.done");
    }

    /// <summary>Takes the last merge back as one step, while nothing else changed the tags since.</summary>
    internal Task UndoMergeAsync(CancellationToken token = default)
    {
        if (_merge == null || LastMerge is not { } undo) return Task.CompletedTask;
        return RunAsync(async _ =>
        {
            PrivateTagCommit commit = await _merge.Undo(undo, token);
            LastMerge = null;
            return commit;
        }, "tags.merge.undone", staleKey: "tags.merge.error.undoStale");
    }

    /// <summary>Gives the tag another name.</summary>
    internal Task AddAliasAsync(TagId tag, string? name, CancellationToken token = default) => _merge == null ? Task.CompletedTask
        : RunAsync(revision => _merge.AddAlias(tag, name ?? string.Empty, revision, token), "tags.alias.added", collisionKey: "tags.alias.error.collision");

    /// <summary>Removes an alias.</summary>
    internal Task RemoveAliasAsync(string name, CancellationToken token = default) => _merge == null ? Task.CompletedTask
        : RunAsync(revision => _merge.RemoveAlias(name, revision, token), "tags.alias.removed");

    /// <summary>True when the host can inspect and remove orphaned local media tag assignments.</summary>
    internal bool CanInspectOrphans => _inspectOrphans != null && _removeOrphans != null;
    /// <summary>The last orphan inspection, valid only for the tag revision it names; null before a check.</summary>
    internal LocalMediaTagInspection? Inspection { get; private set; }
    /// <summary>True while an inspection reads the catalog and manifests.</summary>
    internal bool Inspecting { get; private set; }

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
        // An inspection describes one revision; after any change it would name the wrong assignments.
        if (Inspection != null && Inspection.Revision != snapshot?.Revision) Inspection = null;
    }

    /// <summary>
    /// Checks which local media tag assignments no longer reach a loose file (gone, moved, replaced, now bound). Read-only:
    /// the result is shown and can be removed explicitly; nothing is retargeted.
    /// </summary>
    internal async Task CheckOrphansAsync(CancellationToken token = default)
    {
        if (_inspectOrphans == null || Busy || Inspecting || Snapshot == null) return;
        Inspecting = true;
        MessageKey = null;
        Updated?.Invoke();
        try
        {
            LocalMediaTagInspection? inspection = await _inspectOrphans(token);
            Reload();
            Inspection = inspection != null && inspection.Revision == Snapshot?.Revision ? inspection : null;
            // The dialog shows the result itself; only a check that produced no usable result is an outcome message.
            if (inspection == null || Inspection == null) { MessageIsError = true; MessageKey = inspection == null ? "tags.unavailable" : "tags.error.stale"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            MessageIsError = true;
            MessageKey = "tags.error.other";
        }
        finally
        {
            Inspecting = false;
            Updated?.Invoke();
        }
    }

    /// <summary>Removes exactly the inspected orphaned assignments, bound to the inspected revision. Tags stay.</summary>
    internal Task RemoveOrphansAsync(CancellationToken token = default)
    {
        if (_removeOrphans == null || Inspection is not { Orphans.Count: > 0 } inspection) return Task.CompletedTask;
        return RunAsync(_ => _removeOrphans(inspection.Orphans, inspection.Revision, token), "tags.manage.orphansRemoved");
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

    private async Task RunAsync(Func<long, Task<PrivateTagCommit>> operation, string doneKey, string? staleKey = null,
        string? collisionKey = null)
    {
        if (Busy || Inspecting || Snapshot == null) return;
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
                PrivateTagStoreError.NameCollision => collisionKey ?? "tags.manage.error.collision",
                PrivateTagStoreError.StaleRevision => staleKey ?? "tags.error.stale",
                PrivateTagStoreError.InvalidMerge => "tags.merge.error.invalid",
                PrivateTagStoreError.UnknownTag => "tags.manage.error.gone",
                PrivateTagStoreError.CapacityExceeded => "tags.error.capacity",
                PrivateTagStoreError.TagLimitReached => "tags.error.tagLimit",
                PrivateTagStoreError.DocumentLimitReached => "tags.error.fileLimit",
                _ => "tags.error.other"
            };
            // A stale or vanished target means this view is old: show the current state before a retry.
            if (error.Error is PrivateTagStoreError.StaleRevision or PrivateTagStoreError.UnknownTag) Reload();
            // An undo that came too late is gone for good.
            if (error.Error == PrivateTagStoreError.StaleRevision && staleKey != null) LastMerge = null;
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
