using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Tags;

namespace MeowChan.Desktop;

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
        if (snapshot == null) { Assigned = Available = Array.Empty<PrivateTagDefinition>(); return; }
        // A loose file freezes the first registration it is seen with; later writes must hit exactly that one.
        Subject = MediaTagSubjects.Freeze(Subject, snapshot);
        HashSet<TagId> on = snapshot.TagsOf(Subject).Select(tag => tag.Id).ToHashSet();
        Assigned = snapshot.Tags.Where(tag => on.Contains(tag.Id)).ToArray();
        Available = snapshot.Tags.Where(tag => !on.Contains(tag.Id)).ToArray();
    }

    /// <summary>Create-or-get by name and assign, in one write: a failed assignment never leaves a new unassigned tag.</summary>
    internal Task AddAsync(string? name, CancellationToken token = default) => RunAsync(async () =>
    {
        PrivateTagCommit commit = await _defineAndSet(name ?? string.Empty, Subject, token);
        Adopt(commit.Snapshot);
        return commit.Changed;
    });

    internal Task AddExistingAsync(TagId tag, CancellationToken token = default) => RunAsync(async () =>
    {
        PrivateTagCommit commit = await _set(tag, Subject, true, token);
        Adopt(commit.Snapshot);
        return commit.Changed;
    });

    internal Task RemoveAsync(TagId tag, CancellationToken token = default) => RunAsync(async () =>
    {
        PrivateTagCommit commit = await _set(tag, Subject, false, token);
        Adopt(commit.Snapshot);
        return commit.Changed;
    });

    private async Task RunAsync(Func<Task<bool>> operation)
    {
        if (Busy || Snapshot == null) return;
        Busy = true;
        MessageKey = null;
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
        PrivateTagStoreError.StaleRevision => "tags.error.stale",
        PrivateTagStoreError.UnknownTag => "tags.manage.error.gone",
        PrivateTagStoreError.StaleTarget => "tags.media.error.changed",
        PrivateTagStoreError.TargetUnavailable => "tags.media.error.unavailable",
        _ => "tags.error.other"
    };
}
