using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;
using MeowChan.Storage;

namespace MeowChan.Desktop;

internal sealed record SourceMediaIntegrityPolicyView(SourceMediaIntegrityPolicySnapshot Snapshot,
    bool IsGranted, bool IsActive, SourceMaturity Maturity);

/// <summary>
/// One profile/owner/source authority. Publication and revocation hold the same gate;
/// the persisted consent survives normal updates, but inactive adapters have no authority.
/// </summary>
internal sealed class SourceMediaIntegrityPolicyService : ISourceMediaIntegrityPolicy
{
    private sealed record PolicyState(SourceMediaIntegrityPolicySnapshot Snapshot, bool Granted, bool Active, bool Retired);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _authoritySync = new();
    private PolicyState _state;

    internal SourceMediaIntegrityPolicyService(string profileId, string pluginOwnerId, string sourceId,
        SourceMediaIntegrityPermission? saved = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        _ = SourceMediaIntegrityPermission.CreateKey(pluginOwnerId, sourceId);
        if (saved != null && (saved.PluginOwnerId != pluginOwnerId || saved.SourceId != sourceId || saved.Revision < 0))
            throw new InvalidDataException("Source media permission belongs to another authority.");
        _state = new(new(profileId, pluginOwnerId, sourceId, false, saved?.Revision ?? 0),
            saved?.AllowProviderSizeMd5Mismatch ?? false, false, false);
    }

    public SourceMediaIntegrityPolicySnapshot GetSnapshot() => Volatile.Read(ref _state).Snapshot;

    internal SourceMediaIntegrityPolicyView GetView(SourceMaturity maturity = SourceMaturity.Unspecified)
    {
        PolicyState state = Volatile.Read(ref _state);
        return new(state.Snapshot, state.Granted, state.Active && !state.Retired, maturity);
    }

    public async ValueTask<MediaAcceptanceLease> AcquireAcceptanceLeaseAsync(long expectedRevision, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_authoritySync)
            {
                PolicyState state = _state;
                if (!state.Active || state.Retired || !state.Snapshot.AllowProviderSizeMd5Mismatch || state.Snapshot.Revision != expectedRevision)
                    throw new InvalidDataException("Source media permission changed before commit.");
                return new AcceptanceLease(state.Snapshot, _gate);
            }
        }
        catch { _gate.Release(); throw; }
    }

    // Lifecycle invalidation does not wait for an already linearized commit lease.
    internal void SetActive(bool active)
    {
        lock (_authoritySync)
        {
            PolicyState state = _state;
            if (state.Retired) return;
            if (state.Active == active) return;
            if (active && state.Snapshot.Revision == long.MaxValue)
                throw new InvalidDataException("Source media permission revision is exhausted.");
            Publish(state with { Active = active, Snapshot = state.Snapshot with
            {
                AllowProviderSizeMd5Mismatch = active && state.Granted,
                Revision = state.Snapshot.Revision == long.MaxValue ? long.MaxValue : state.Snapshot.Revision + 1
            } });
        }
    }

    internal void Retire()
    {
        lock (_authoritySync)
        {
            PolicyState state = _state;
            if (state.Retired) return;
            Publish(state with { Active = false, Retired = true, Snapshot = state.Snapshot with
            {
                AllowProviderSizeMd5Mismatch = false, Revision = state.Snapshot.Revision == long.MaxValue ? long.MaxValue : state.Snapshot.Revision + 1
            } });
        }
    }

    /// <summary>Caller serializes Config commands. Persist must complete before live authority changes.</summary>
    internal async Task SetPermissionAsync(bool allow, long expectedRevision, Action<SourceMediaIntegrityPermission> persist,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persist);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_authoritySync)
            {
                PolicyState state = _state;
                if (state.Retired || state.Snapshot.Revision != expectedRevision || allow && !state.Active)
                    throw new InvalidDataException("Source media permission changed before commit.");
                if (state.Granted == allow) return;
                long revision = checked(state.Snapshot.Revision + 1);
                SourceMediaIntegrityPermission permission = new(state.Snapshot.PluginOwnerId, state.Snapshot.SourceId, allow, revision);
                persist(permission);
                Publish(state with { Granted = allow, Snapshot = state.Snapshot with
                {
                    AllowProviderSizeMd5Mismatch = allow && state.Active, Revision = revision
                } });
            }
        }
        finally { _gate.Release(); }
    }

    private void Publish(PolicyState state) => Volatile.Write(ref _state, state);

    private sealed class AcceptanceLease(SourceMediaIntegrityPolicySnapshot snapshot, SemaphoreSlim gate) : MediaAcceptanceLease(snapshot)
    {
        private SemaphoreSlim? _heldGate = gate;
        public override ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _heldGate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
