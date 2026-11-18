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
/// The session copies what it is given: later changes of the caller's list (the gallery's marks) never reach it, and a
/// change it makes keeps the exact subjects it wrote, so an undo reverts those and nothing else.
/// </summary>
internal sealed class MediaSelectionTagSession
{
    private readonly Func<PrivateTagLoadSnapshot> _load;
    private readonly Func<string, IReadOnlyList<MediaTagSubject>, long?, CancellationToken, Task<PrivateTagCommit>> _defineAndSet;
    private readonly Func<TagId, IReadOnlyList<MediaTagSubject>, bool, long?, CancellationToken, Task<PrivateTagCommit>> _set;
    private readonly IReadOnlyList<MediaTagCandidate> _candidates;
    // The subject of each occurrence, fixed when the session starts (index = candidate index); null: cannot be tagged.
    private readonly MediaTagSubject?[] _subjects;
    private readonly bool[] _chosen;
    private int _choice;
    private (PrivateTagStoreSnapshot Snapshot, int Choice, IReadOnlyList<MediaTagSubject> Subjects, HashSet<TagId>[] Tags, TagCoverage Coverage)? _carried;

    internal MediaSelectionTagSession(IReadOnlyList<MediaTagCandidate> candidates, Func<PrivateTagLoadSnapshot> load,
        Func<string, IReadOnlyList<MediaTagSubject>, long?, CancellationToken, Task<PrivateTagCommit>> defineAndSet,
        Func<TagId, IReadOnlyList<MediaTagSubject>, bool, long?, CancellationToken, Task<PrivateTagCommit>> set)
        : this(Copy(candidates), load, defineAndSet, set, null)
    {
    }

    private MediaSelectionTagSession(IReadOnlyList<MediaTagCandidate> candidates, Func<PrivateTagLoadSnapshot> load,
        Func<string, IReadOnlyList<MediaTagSubject>, long?, CancellationToken, Task<PrivateTagCommit>> defineAndSet,
        Func<TagId, IReadOnlyList<MediaTagSubject>, bool, long?, CancellationToken, Task<PrivateTagCommit>> set,
        IReadOnlyList<MediaTagSubject>? subjects)
    {
        _candidates = candidates;
        _subjects = subjects != null ? subjects.Cast<MediaTagSubject?>().ToArray() : candidates.Select(candidate => candidate.Subject).ToArray();
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _defineAndSet = defineAndSet ?? throw new ArgumentNullException(nameof(defineAndSet));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        _chosen = _subjects.Select(subject => subject != null).ToArray();
        Reload();
    }

    private static IReadOnlyList<MediaTagCandidate> Copy(IReadOnlyList<MediaTagCandidate> candidates) =>
        (candidates ?? throw new ArgumentNullException(nameof(candidates))).ToArray();

    /// <summary>
    /// A session over exactly these subjects (the gallery's marked media), each once, all chosen. The list is copied: marking or
    /// unmarking afterwards needs a new session and never changes this one. It has no <see cref="Candidates"/> (those belong
    /// to the media page dialog); <see cref="Count"/> is the number of subjects.
    /// </summary>
    internal static MediaSelectionTagSession ForSubjects(IEnumerable<MediaTagSubject> subjects, Func<PrivateTagLoadSnapshot> load,
        Func<string, IReadOnlyList<MediaTagSubject>, long?, CancellationToken, Task<PrivateTagCommit>> defineAndSet,
        Func<TagId, IReadOnlyList<MediaTagSubject>, bool, long?, CancellationToken, Task<PrivateTagCommit>> set)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        MediaTagSubject[] fixedSubjects = subjects.Distinct().ToArray();
        return new MediaSelectionTagSession(Array.Empty<MediaTagCandidate>(), load, defineAndSet, set, fixedSubjects);
    }

    /// <summary>The occurrences the media page dialog lists; empty for a session made by <see cref="ForSubjects"/>.</summary>
    internal IReadOnlyList<MediaTagCandidate> Candidates => _candidates;
    /// <summary>How many occurrences (or subjects) the session spans, taggable or not.</summary>
    internal int Count => _subjects.Length;
    /// <summary>The distinct subjects the session was made over, in order (frozen at the start).</summary>
    internal IReadOnlyList<MediaTagSubject> Subjects() => _subjects.OfType<MediaTagSubject>().Distinct().ToArray();
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
    internal IReadOnlyList<MediaTagCandidate> Chosen => _candidates.Where((_, index) => _chosen[index]).ToArray();
    private IEnumerable<MediaTagSubject> ChosenSubjects() => _subjects.Where((_, index) => _chosen[index]).OfType<MediaTagSubject>();

    /// <summary>Includes or excludes one occurrence. An occurrence without a tag target cannot be chosen.</summary>
    internal void Choose(int index, bool chosen)
    {
        if (Busy || index < 0 || index >= _chosen.Length || (chosen && _subjects[index] == null)) return;
        _chosen[index] = chosen;
        _choice++;
        Updated?.Invoke();
    }

    internal void ChooseAll(bool chosen)
    {
        if (Busy) return;
        for (int index = 0; index < _chosen.Length; index++) _chosen[index] = chosen && _subjects[index] != null;
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
        IReadOnlyList<MediaTagSubject> subjects = ChosenSubjects().Distinct().ToArray();
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
        IReadOnlyList<MediaTagSubject> subjects = ChosenSubjects()
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
    /// Makes the writes of one entry that gave several tags one undo step: <see cref="LastChange"/> becomes
    /// <see cref="TagDialogChange.Combine"/> of <paramref name="parts"/> (a single part stays as it is).
    /// </summary>
    internal void ReplaceLastChange(IReadOnlyList<TagDialogChange> parts)
    {
        if (parts.Count < 2) return;
        LastChange = TagDialogChange.Combine(parts);
        Updated?.Invoke();
    }

    /// <summary>Shows a refusal of the entry before any write (the message key of the dialog's error line); nothing was changed.</summary>
    internal void Refuse(string messageKey)
    {
        if (Busy) return;
        MessageKey = messageKey;
        Undone = false;
        Updated?.Invoke();
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
            // An entry that gave several tags is one step: its tags go back newest first, each in one write bound to the revision
            // the previous write left.
            bool changed = false;
            foreach (TagDialogChange part in change.Parts.Reverse())
            {
                PrivateTagCommit commit = await _set(part.Tag, part.Subjects, !part.Assigned, Snapshot!.Revision, token);
                Adopt(commit.Snapshot);
                changed |= commit.Changed;
            }
            LastChange = null;
            Undone = true;
            return changed;
        }, undo: true);
    }

    private async Task RunAsync(Func<Task<bool>> operation, bool undo = false)
    {
        if (Busy || Snapshot == null) return;
        if (!undo && !_chosen.Any(chosen => chosen)) { MessageKey = "media.selection.tag.noneChosen"; Updated?.Invoke(); return; }
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
