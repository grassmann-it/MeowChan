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
/// Occurrences are bound post versions or loose local files; both go through the same subject resolver and write.
/// </summary>
internal sealed class MediaSelectionTagSession
{
    private readonly Func<PrivateTagLoadSnapshot> _load;
    private readonly Func<string, IReadOnlyList<MediaTagSubject>, long?, CancellationToken, Task<PrivateTagCommit>> _defineAndSet;
    private readonly Func<TagId, IReadOnlyList<MediaTagSubject>, bool, long?, CancellationToken, Task<PrivateTagCommit>> _set;
    private readonly bool[] _chosen;
    private int _choice;
    private (PrivateTagStoreSnapshot Snapshot, int Choice, IReadOnlyList<MediaTagSubject> Subjects, HashSet<TagId>[] Tags, TagCoverage Coverage)? _carried;

    internal MediaSelectionTagSession(IReadOnlyList<MediaTagCandidate> candidates, Func<PrivateTagLoadSnapshot> load,
        Func<string, IReadOnlyList<MediaTagSubject>, long?, CancellationToken, Task<PrivateTagCommit>> defineAndSet,
        Func<TagId, IReadOnlyList<MediaTagSubject>, bool, long?, CancellationToken, Task<PrivateTagCommit>> set)
    {
        Candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _defineAndSet = defineAndSet ?? throw new ArgumentNullException(nameof(defineAndSet));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _chosen = candidates.Select(candidate => candidate.Subject != null).ToArray();
        Reload();
    }

    internal IReadOnlyList<MediaTagCandidate> Candidates { get; }
    /// <summary>Null while the tag index is unavailable; then nothing can be changed.</summary>
    internal PrivateTagStoreSnapshot? Snapshot { get; private set; }
    /// <summary>The last accepted change in this dialog, until it is undone or another change is made.</summary>
    internal TagDialogChange? LastChange { get; private set; }
    /// <summary>True right after <see cref="UndoAsync"/> reverted the last change.</summary>
    internal bool Undone { get; private set; }
    internal bool Busy { get; private set; }
    internal string? MessageKey { get; private set; }
    internal bool Changed { get; private set; }
    internal event Action? Updated;

    internal bool IsChosen(int index) => _chosen[index];
    internal IReadOnlyList<MediaTagCandidate> Chosen => Candidates.Where((_, index) => _chosen[index]).ToArray();

    /// <summary>Includes or excludes one occurrence. An occurrence without a tag target cannot be chosen.</summary>
    internal void Choose(int index, bool chosen)
    {
        if (Busy || index < 0 || index >= _chosen.Length || (chosen && Candidates[index].Subject == null)) return;
        _chosen[index] = chosen;
        _choice++;
        Updated?.Invoke();
    }

    internal void ChooseAll(bool chosen)
    {
        if (Busy) return;
        for (int index = 0; index < _chosen.Length; index++) _chosen[index] = chosen && Candidates[index].Subject != null;
        _choice++;
        Updated?.Invoke();
    }

    /// <summary>
    /// How the distinct chosen subjects carry each tag (the tri-state rows of the dialog). Each subject's tags are read
    /// once per snapshot and choice, so a vocabulary of thousands of tags costs one pass, not one per tag.
    /// </summary>
    internal TagCoverage Coverage() => Carried()?.Coverage ?? TagCoverage.Empty;

    private (PrivateTagStoreSnapshot Snapshot, int Choice, IReadOnlyList<MediaTagSubject> Subjects, HashSet<TagId>[] Tags, TagCoverage Coverage)? Carried()
    {
        if (Snapshot is not { } snapshot) return null;
        if (_carried is { } known && ReferenceEquals(known.Snapshot, snapshot) && known.Choice == _choice) return known;
        // Two views of the same loose file (for example two legacy posts) are one occurrence: one subject.
        IReadOnlyList<MediaTagSubject> subjects = Chosen.Select(candidate => candidate.Subject).OfType<MediaTagSubject>().Distinct().ToArray();
        HashSet<TagId>[] tags = subjects.Select(subject => snapshot.TagsOf(subject).Select(tag => tag.Id).ToHashSet()).ToArray();
        Dictionary<TagId, int> carriers = new();
        foreach (HashSet<TagId> set in tags)
            foreach (TagId tag in set) carriers[tag] = carriers.GetValueOrDefault(tag) + 1;
        _carried = (snapshot, _choice, subjects, tags, new TagCoverage(carriers, subjects.Count));
        return _carried;
    }

    /// <summary>The distinct chosen subjects that do (<paramref name="carrying"/>) or do not carry the tag.</summary>
    private IReadOnlyList<MediaTagSubject> Subjects(TagId tag, bool carrying)
    {
        if (Carried() is not { } carried) return Array.Empty<MediaTagSubject>();
        return carried.Subjects.Where((_, at) => carried.Tags[at].Contains(tag) == carrying).ToArray();
    }

    /// <summary>Tags that at least one chosen occurrence carries, with how many of the chosen ones do.</summary>
    internal IReadOnlyList<(PrivateTagDefinition Tag, int Count)> Removable()
    {
        if (Snapshot is not { } snapshot) return Array.Empty<(PrivateTagDefinition, int)>();
        TagCoverage coverage = Coverage();
        return snapshot.Tags.Select(tag => (Tag: tag, Count: coverage.Carriers.GetValueOrDefault(tag.Id)))
            .Where(item => item.Count > 0).ToArray();
    }

    /// <summary>Existing tags that at least one chosen occurrence still lacks, with how many that are.</summary>
    internal IReadOnlyList<(PrivateTagDefinition Tag, int Count)> Addable()
    {
        if (Snapshot is not { } snapshot) return Array.Empty<(PrivateTagDefinition, int)>();
        TagCoverage coverage = Coverage();
        return snapshot.Tags.Select(tag => (Tag: tag, Count: coverage.Total - coverage.Carriers.GetValueOrDefault(tag.Id)))
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

    /// <summary>
    /// Create-or-get by name and give it to every chosen occurrence, in ONE write: a failure leaves neither a new unassigned
    /// tag nor a partial assignment. Like the former define-then-assign, it is not bound to the shown tag revision (adding a
    /// name to fixed occurrences is idempotent); loose files are still frozen to the registration this dialog shows.
    /// </summary>
    internal Task AddAsync(string? name, CancellationToken token = default) => RunAsync(async () =>
    {
        PrivateTagStoreSnapshot snapshot = Snapshot!;
        IReadOnlyList<MediaTagSubject> subjects = Chosen.Select(candidate => candidate.Subject).OfType<MediaTagSubject>()
            .Select(subject => MediaTagSubjects.Freeze(subject, snapshot)).Distinct().ToArray();
        // The undo takes the tag off exactly the files that lacked it; files that already had it keep it.
        PrivateTagDefinition? known = PrivateTagName.TryCreate(name, out PrivateTagName? parsed, out _) && parsed != null
            ? snapshot.Tags.FirstOrDefault(tag => tag.Name.Key == parsed.Key) : null;
        IReadOnlyList<MediaTagSubject> lacking = known == null ? subjects
            : subjects.Where(subject => !snapshot.Carries(known.Id, subject)).ToArray();
        PrivateTagCommit commit = await _defineAndSet(name ?? string.Empty, subjects, null, token);
        Adopt(commit.Snapshot);
        Remember(commit, lacking, assigned: true);
        return commit.Changed;
    });

    internal Task AddExistingAsync(TagId tag, CancellationToken token = default) => RunAsync(() => WriteAsync(tag, assigned: true, token));

    /// <summary>Takes the tag away from the chosen occurrences that carry it; the others are not touched.</summary>
    internal Task RemoveAsync(TagId tag, CancellationToken token = default) => RunAsync(() => WriteAsync(tag, assigned: false, token));

    private async Task<bool> WriteAsync(TagId tag, bool assigned, CancellationToken token)
    {
        PrivateTagStoreSnapshot snapshot = Snapshot!;
        // Local subjects are frozen to the registration this dialog shows, so a write is refused rather than redirected.
        IReadOnlyList<MediaTagSubject> targets = Subjects(tag, carrying: !assigned)
            .Select(subject => MediaTagSubjects.Freeze(subject, snapshot)).ToArray();
        if (targets.Count == 0) return false;
        PrivateTagCommit commit = await _set(tag, targets, assigned, snapshot.Revision, token);
        Adopt(commit.Snapshot);
        Remember(commit, targets, assigned);
        return commit.Changed;
    }

    private void Remember(PrivateTagCommit commit, IReadOnlyList<MediaTagSubject> subjects, bool assigned)
    {
        if (!commit.Changed || subjects.Count == 0) return;
        string name = commit.Snapshot.Find(commit.Tag)?.Name.Display ?? string.Empty;
        LastChange = new TagDialogChange(commit.Tag, name, assigned, subjects);
    }

    /// <summary>
    /// Reverts the last change of this dialog on exactly the files it changed, once, in one write bound to the shown
    /// revision. It does not depend on which occurrences are ticked now. A tag the change created stays in the vocabulary.
    /// </summary>
    internal Task UndoAsync(CancellationToken token = default) =>
        LastChange is { } change ? RevertAsync(change, token) : Task.CompletedTask;

    /// <summary>
    /// Writes the reverse of <paramref name="change"/> on exactly its subjects in one write bound to the shown revision; the
    /// change may be older than <see cref="LastChange"/> (the quick tag surface keeps several steps). Counts as an undo.
    /// </summary>
    internal Task RevertAsync(TagDialogChange change, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return RunAsync(async () =>
        {
            PrivateTagStoreSnapshot snapshot = Snapshot!;
            PrivateTagCommit commit = await _set(change.Tag, change.Subjects, !change.Assigned, snapshot.Revision, token);
            Adopt(commit.Snapshot);
            LastChange = null;
            Undone = true;
            return commit.Changed;
        }, undo: true);
    }

    private async Task RunAsync(Func<Task<bool>> operation, bool undo = false)
    {
        if (Busy || Snapshot == null) return;
        if (!undo && Chosen.Count == 0) { MessageKey = "media.selection.tag.noneChosen"; Updated?.Invoke(); return; }
        Busy = true;
        MessageKey = null;
        Undone = false;
        // Only the newest change can be undone; a new attempt retires the older one even when it fails.
        if (!undo) LastChange = null;
        Updated?.Invoke();
        try { if (await operation()) Changed = true; }
        catch (OperationCanceledException) { }
        catch (PrivateTagStoreException error)
        {
            MessageKey = MediaTagSession.MessageFor(error.Error);
            if (error.Error is PrivateTagStoreError.UnknownTag or PrivateTagStoreError.StaleRevision
                or PrivateTagStoreError.StaleTarget or PrivateTagStoreError.TargetUnavailable) Reload();
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
