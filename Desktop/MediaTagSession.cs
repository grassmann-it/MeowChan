using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// One accepted change of a tag dialog, kept for its one-step undo: the tag, whether it was given or taken away, and
/// exactly the files that changed (others that already were in the wanted state are not touched by the undo).
/// </summary>
internal sealed record TagDialogChange(TagId Tag, string Name, bool Assigned, IReadOnlyList<MediaTagSubject> Subjects);

/// <summary>
/// Own tags of one exact media file: a bound post attachment version or a loose local occurrence. The subject is
/// frozen when the session starts (a loose file additionally freezes the registration it resolves to, once known),
/// so navigating the gallery meanwhile can never redirect a change to another medium, occurrence or revision.
/// No UI and no storage.
/// </summary>
internal sealed class MediaTagSession
{
    private readonly Func<PrivateTagLoadSnapshot> _load;
    private readonly Func<string, MediaTagSubject, CancellationToken, Task<PrivateTagCommit>> _defineAndSet;
    private readonly Func<TagId, MediaTagSubject, bool, CancellationToken, Task<PrivateTagCommit>> _set;

    internal MediaTagSession(MediaTagSubject subject, Func<PrivateTagLoadSnapshot> load,
        Func<string, MediaTagSubject, CancellationToken, Task<PrivateTagCommit>> defineAndSet,
        Func<TagId, MediaTagSubject, bool, CancellationToken, Task<PrivateTagCommit>> set)
    {
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _defineAndSet = defineAndSet ?? throw new ArgumentNullException(nameof(defineAndSet));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        Reload();
    }

    internal MediaTagSubject Subject { get; private set; }
    /// <summary>The bound target, or null for a loose local file.</summary>
    internal PrivateMediaTagTarget? Target => (Subject as MediaTagSubject.Bound)?.Target;
    internal bool IsLocal => Subject is MediaTagSubject.Local;
    /// <summary>Null while the tag index is unavailable; then nothing can be changed.</summary>
    internal PrivateTagStoreSnapshot? Snapshot { get; private set; }
    /// <summary>Tags on exactly this media file, in vocabulary order.</summary>
    internal IReadOnlyList<PrivateTagDefinition> Assigned { get; private set; } = Array.Empty<PrivateTagDefinition>();
    /// <summary>Existing tags not yet on this media file, offered for one-click assignment.</summary>
    internal IReadOnlyList<PrivateTagDefinition> Available { get; private set; } = Array.Empty<PrivateTagDefinition>();
    /// <summary>Ids of <see cref="Assigned"/>.</summary>
    internal IReadOnlySet<TagId> AssignedIds { get; private set; } = new HashSet<TagId>();
    /// <summary>The last accepted change in this dialog, until it is undone or another change is made.</summary>
    internal TagDialogChange? LastChange { get; private set; }
    /// <summary>True right after <see cref="UndoAsync"/> reverted the last change.</summary>
    internal bool Undone { get; private set; }
    internal bool Busy { get; private set; }
    internal string? MessageKey { get; private set; }
    internal bool Changed { get; private set; }
    internal event Action? Updated;

    internal void Reload()
    {
        PrivateTagLoadSnapshot loaded = _load();
        Adopt(loaded.State == PrivateTagLoadState.Ready ? loaded.Snapshot : null);
    }

    private void Adopt(PrivateTagStoreSnapshot? snapshot)
    {
        // An older acknowledgement must not hide a newer committed state.
        if (snapshot != null && Snapshot != null && snapshot.Revision < Snapshot.Revision) return;
        Snapshot = snapshot;
        if (snapshot == null) { Assigned = Available = Array.Empty<PrivateTagDefinition>(); AssignedIds = new HashSet<TagId>(); return; }
        // A loose file freezes the first registration it is seen with; later writes must hit exactly that one.
        Subject = MediaTagSubjects.Freeze(Subject, snapshot);
        HashSet<TagId> on = snapshot.TagsOf(Subject).Select(tag => tag.Id).ToHashSet();
        Assigned = snapshot.Tags.Where(tag => on.Contains(tag.Id)).ToArray();
        Available = snapshot.Tags.Where(tag => !on.Contains(tag.Id)).ToArray();
        AssignedIds = on;
    }

    /// <summary>The tags of this file as the dialog's list shows them: one file, each tag on it or not.</summary>
    internal TagCoverage Coverage() => new(AssignedIds.ToDictionary(id => id, _ => 1), Snapshot == null ? 0 : 1);

    /// <summary>Create-or-get by name and assign, in one write: a failed assignment never leaves a new unassigned tag.</summary>
    internal Task AddAsync(string? name, CancellationToken token = default) => RunAsync(async () =>
    {
        MediaTagSubject subject = Subject;
        PrivateTagCommit commit = await _defineAndSet(name ?? string.Empty, subject, token);
        Adopt(commit.Snapshot);
        Remember(commit, subject, assigned: true);
        return commit.Changed;
    });

    internal Task AddExistingAsync(TagId tag, CancellationToken token = default) => SetAsync(tag, true, token);

    internal Task RemoveAsync(TagId tag, CancellationToken token = default) => SetAsync(tag, false, token);

    private Task SetAsync(TagId tag, bool assigned, CancellationToken token) => RunAsync(async () =>
    {
        MediaTagSubject subject = Subject;
        PrivateTagCommit commit = await _set(tag, subject, assigned, token);
        Adopt(commit.Snapshot);
        Remember(commit, subject, assigned);
        return commit.Changed;
    });

    private void Remember(PrivateTagCommit commit, MediaTagSubject subject, bool assigned)
    {
        if (!commit.Changed) return;
        string name = commit.Snapshot.Find(commit.Tag)?.Name.Display ?? string.Empty;
        LastChange = new TagDialogChange(commit.Tag, name, assigned, new[] { subject });
    }

    /// <summary>
    /// Reverts the last change of this dialog, once. A tag the change created stays in the vocabulary; it is only taken
    /// off this file again.
    /// </summary>
    internal Task UndoAsync(CancellationToken token = default) =>
        LastChange is { } change ? RevertAsync(change, token) : Task.CompletedTask;

    /// <summary>
    /// Writes the reverse of <paramref name="change"/> for exactly its subjects, which may be an earlier change of another
    /// medium (the quick tag surface keeps several steps and follows the gallery). Counts as an undo: it retires
    /// <see cref="LastChange"/> and sets <see cref="Undone"/>. A change of several files is reverted file by file.
    /// </summary>
    internal Task RevertAsync(TagDialogChange change, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        return RunAsync(async () =>
        {
            bool changed = false;
            foreach (MediaTagSubject subject in change.Subjects)
            {
                PrivateTagCommit commit = await _set(change.Tag, subject, !change.Assigned, token);
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
            MessageKey = MessageFor(error.Error);
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

    internal static string MessageFor(PrivateTagStoreError error) => error switch
    {
        PrivateTagStoreError.InvalidName => "tags.error.invalidName",
        PrivateTagStoreError.CapacityExceeded => "tags.error.capacity",
        PrivateTagStoreError.TagLimitReached => "tags.error.tagLimit",
        PrivateTagStoreError.DocumentLimitReached => "tags.error.fileLimit",
        PrivateTagStoreError.StaleRevision => "tags.error.stale",
        PrivateTagStoreError.UnknownTag => "tags.manage.error.gone",
        PrivateTagStoreError.StaleTarget => "tags.media.error.changed",
        PrivateTagStoreError.TargetUnavailable => "tags.media.error.unavailable",
        _ => "tags.error.other"
    };
}
