using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// Own tags of one exact media version. The target is frozen when the session starts, so navigating
/// the gallery meanwhile can never redirect a change to another medium. No UI and no storage.
/// </summary>
internal sealed class MediaTagSession
{
    private readonly Func<PrivateTagLoadSnapshot> _load;
    private readonly Func<string, CancellationToken, Task<PrivateTagCommit>> _define;
    private readonly Func<TagId, PrivateMediaTagTarget, bool, CancellationToken, Task<PrivateTagCommit>> _set;

    internal MediaTagSession(PrivateMediaTagTarget target, Func<PrivateTagLoadSnapshot> load,
        Func<string, CancellationToken, Task<PrivateTagCommit>> define,
        Func<TagId, PrivateMediaTagTarget, bool, CancellationToken, Task<PrivateTagCommit>> set)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _define = define ?? throw new ArgumentNullException(nameof(define));
        _set = set ?? throw new ArgumentNullException(nameof(set));
        Reload();
    }

    internal PrivateMediaTagTarget Target { get; }
    /// <summary>Null while the tag index is unavailable; then nothing can be changed.</summary>
    internal PrivateTagStoreSnapshot? Snapshot { get; private set; }
    /// <summary>Tags on exactly this media version, in vocabulary order.</summary>
    internal IReadOnlyList<PrivateTagDefinition> Assigned { get; private set; } = Array.Empty<PrivateTagDefinition>();
    /// <summary>Existing tags not yet on this media version, offered for one-click assignment.</summary>
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
        HashSet<TagId> on = snapshot.TagsOf(Target).Select(tag => tag.Id).ToHashSet();
        Assigned = snapshot.Tags.Where(tag => on.Contains(tag.Id)).ToArray();
        Available = snapshot.Tags.Where(tag => !on.Contains(tag.Id)).ToArray();
    }

    /// <summary>Create-or-get by name, then assign. Both steps are idempotent.</summary>
    internal Task AddAsync(string? name, CancellationToken token = default) => RunAsync(async () =>
    {
        PrivateTagCommit defined = await _define(name ?? string.Empty, token);
        Adopt(defined.Snapshot);
        PrivateTagCommit assigned = await _set(defined.Tag, Target, true, token);
        Adopt(assigned.Snapshot);
        return assigned.Changed || defined.Changed;
    });

    internal Task AddExistingAsync(TagId tag, CancellationToken token = default) => RunAsync(async () =>
    {
        PrivateTagCommit commit = await _set(tag, Target, true, token);
        Adopt(commit.Snapshot);
        return commit.Changed;
    });

    internal Task RemoveAsync(TagId tag, CancellationToken token = default) => RunAsync(async () =>
    {
        PrivateTagCommit commit = await _set(tag, Target, false, token);
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
