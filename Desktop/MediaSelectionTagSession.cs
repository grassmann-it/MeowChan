using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// Own tags for the occurrences of a selection of media page tiles. The occurrences are frozen when the session starts;
/// every occurrence is chosen by default and can be deselected. An add or remove is one admitted write for all chosen
/// occurrences, bound to the tag revision the dialog shows: it happens completely or not at all. Media tags only.
/// </summary>
internal sealed class MediaSelectionTagSession
{
    private readonly Func<PrivateTagLoadSnapshot> _load;
    private readonly Func<string, CancellationToken, Task<PrivateTagCommit>> _define;
    private readonly Func<TagId, IReadOnlyList<PrivateMediaTagTarget>, bool, long?, CancellationToken, Task<PrivateTagCommit>> _set;
    private readonly bool[] _chosen;

    internal MediaSelectionTagSession(IReadOnlyList<MediaTagCandidate> candidates, Func<PrivateTagLoadSnapshot> load,
        Func<string, CancellationToken, Task<PrivateTagCommit>> define,
        Func<TagId, IReadOnlyList<PrivateMediaTagTarget>, bool, long?, CancellationToken, Task<PrivateTagCommit>> set)
    {
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _define = define ?? throw new ArgumentNullException(nameof(define));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _chosen = candidates.Select(candidate => candidate.Target != null).ToArray();
        Reload();
    }

    internal IReadOnlyList<MediaTagCandidate> Candidates { get; }
    /// <summary>Null while the tag index is unavailable; then nothing can be changed.</summary>
    internal PrivateTagStoreSnapshot? Snapshot { get; private set; }
    internal bool Busy { get; private set; }
    internal string? MessageKey { get; private set; }
    internal bool Changed { get; private set; }
    internal event Action? Updated;

    internal bool IsChosen(int index) => _chosen[index];
    internal IReadOnlyList<MediaTagCandidate> Chosen => Candidates.Where((_, index) => _chosen[index]).ToArray();

    /// <summary>Includes or excludes one occurrence. An occurrence without a tag target cannot be chosen.</summary>
    internal void Choose(int index, bool chosen)
    {
        if (Busy || index < 0 || index >= _chosen.Length || (chosen && Candidates[index].Target == null)) return;
        _chosen[index] = chosen;
        Updated?.Invoke();
    }

    internal void ChooseAll(bool chosen)
    {
        if (Busy) return;
        for (int index = 0; index < _chosen.Length; index++) _chosen[index] = chosen && Candidates[index].Target != null;
        Updated?.Invoke();
    }

    /// <summary>Tags that at least one chosen occurrence carries, with how many of the chosen ones do.</summary>
    internal IReadOnlyList<(PrivateTagDefinition Tag, int Count)> Removable()
    {
        if (Snapshot is not { } snapshot) return Array.Empty<(PrivateTagDefinition, int)>();
        IReadOnlyList<MediaTagCandidate> chosen = Chosen;
        return snapshot.Tags.Select(tag => (Tag: tag, Count: MediaLibraryTagging.TargetsToRemove(snapshot, tag.Id, chosen).Count))
            .Where(item => item.Count > 0).ToArray();
    }

    /// <summary>Existing tags that at least one chosen occurrence still lacks, with how many that are.</summary>
    internal IReadOnlyList<(PrivateTagDefinition Tag, int Count)> Addable()
    {
        if (Snapshot is not { } snapshot) return Array.Empty<(PrivateTagDefinition, int)>();
        IReadOnlyList<MediaTagCandidate> chosen = Chosen;
        return snapshot.Tags.Select(tag => (Tag: tag, Count: MediaLibraryTagging.TargetsToAdd(snapshot, tag.Id, chosen).Count))
            .Where(item => item.Count > 0).ToArray();
    }

    internal void Reload()
    {
        PrivateTagLoadSnapshot loaded = _load();
        Adopt(loaded.State == PrivateTagLoadState.Ready ? loaded.Snapshot : null);
        Updated?.Invoke();
    }

    private void Adopt(PrivateTagStoreSnapshot? snapshot)
    {
        // An older acknowledgement must not hide a newer committed state.
        if (snapshot != null && Snapshot != null && snapshot.Revision < Snapshot.Revision) return;
        Snapshot = snapshot;
    }

    /// <summary>Create-or-get by name, then give it to every chosen occurrence in one write.</summary>
    internal Task AddAsync(string? name, CancellationToken token = default) => RunAsync(async () =>
    {
        PrivateTagCommit defined = await _define(name ?? string.Empty, token);
        Adopt(defined.Snapshot);
        return await WriteAsync(defined.Tag, assigned: true, token) || defined.Changed;
    });

    internal Task AddExistingAsync(TagId tag, CancellationToken token = default) => RunAsync(() => WriteAsync(tag, assigned: true, token));

    /// <summary>Takes the tag away from the chosen occurrences that carry it; the others are not touched.</summary>
    internal Task RemoveAsync(TagId tag, CancellationToken token = default) => RunAsync(() => WriteAsync(tag, assigned: false, token));

    private async Task<bool> WriteAsync(TagId tag, bool assigned, CancellationToken token)
    {
        PrivateTagStoreSnapshot snapshot = Snapshot!;
        IReadOnlyList<PrivateMediaTagTarget> targets = assigned
            ? MediaLibraryTagging.TargetsToAdd(snapshot, tag, Chosen) : MediaLibraryTagging.TargetsToRemove(snapshot, tag, Chosen);
        if (targets.Count == 0) return false;
        PrivateTagCommit commit = await _set(tag, targets, assigned, snapshot.Revision, token);
        Adopt(commit.Snapshot);
        return commit.Changed;
    }

    private async Task RunAsync(Func<Task<bool>> operation)
    {
        if (Busy || Snapshot == null) return;
        if (Chosen.Count == 0) { MessageKey = "media.selection.tag.noneChosen"; Updated?.Invoke(); return; }
        Busy = true;
        MessageKey = null;
        Updated?.Invoke();
        try { if (await operation()) Changed = true; }
        catch (OperationCanceledException) { }
        catch (PrivateTagStoreException error)
        {
            MessageKey = error.Error switch
            {
                PrivateTagStoreError.InvalidName => "tags.error.invalidName",
                PrivateTagStoreError.CapacityExceeded => "tags.error.capacity",
                PrivateTagStoreError.StaleRevision => "tags.error.stale",
                PrivateTagStoreError.UnknownTag => "tags.manage.error.gone",
                _ => "tags.error.other"
            };
            if (error.Error is PrivateTagStoreError.UnknownTag or PrivateTagStoreError.StaleRevision) Reload();
        }
        catch (Exception)
        {
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
