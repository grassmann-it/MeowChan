using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MeowChan.Favorites;
using MeowChan.Gallery;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    // Protected by _admission, together with the existing interactive slot/drain.
    // Membership is authority for this frozen favorite only, not a final-write lane.
    private readonly Dictionary<FavoriteWriteSequence, ProfileAdmissionToken> _favoriteWriteSequences = new();

    /// <summary>
    /// Admit one complete logical favorite command before shutdown, including later
    /// intents drained by its caller. Hold this lease until that caller's queue has
    /// completed. Admission performs no product IO and consumes an ordinary slot.
    /// </summary>
    internal FavoriteWriteSequence AdmitFavoriteSequence(GalleryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        FavoriteEntry candidate = FavoriteStore.From(entry.Key.Thread, entry.Asset);
        var key = (nameof(SetFavoriteAsync), candidate.Key.ToString());
        var sequence = new FavoriteWriteSequence(this, candidate, key);
        ProfileAdmissionToken? admission = null;
        try
        {
            // Match BeginShutdown's lifecycle -> admission order. Never acquire a
            // product lock, validate Config, or execute a delegate under _admission.
            lock (_lifecycle)
            {
                EnsureReady();
                lock (_admission)
                {
                    if (_admissionClosed)
                        throw new InvalidOperationException("The desktop session is stopping.");
                    if (_acceptedCommands.Count >= InteractiveCommandLimit || _acceptedCommands.Contains(key))
                        throw new DesktopSessionBusyException(nameof(SetFavoriteAsync));
                    // The whole sequence owns one token, including gaps between sets.
                    // Refusal must precede all host command/sequence bookkeeping.
                    admission = _profileLease.Admission.EnterOrdinaryWriter();
                    _acceptedCommands.Add(key);
                    _commandsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _favoriteWriteSequences.Add(sequence, admission);
                }
            }
            return sequence;
        }
        catch
        {
            if (admission != null)
            {
                try
                {
                    lock (_admission)
                    {
                        _favoriteWriteSequences.Remove(sequence);
                        _acceptedCommands.Remove(key);
                        CompleteCommandDrainIfEmpty();
                    }
                }
                finally { admission.Dispose(); }
            }
            throw;
        }
    }

    /// <summary>
    /// One frozen favorite identity and one in-flight write at a time. Await SetAsync
    /// before the next set; the gallery's latest-intent queue owns coalescing.
    /// Dispose closes further sets immediately, but releases admission only after
    /// the actual worker/persistence/publication has returned, including on error.
    /// </summary>
    internal sealed class FavoriteWriteSequence : IDisposable
    {
        internal readonly DesktopSessionHost Owner;
        internal readonly FavoriteEntry Candidate;
        internal readonly (string Action, string Target) AdmissionKey;
        internal bool Closed;
        internal bool Writing;

        internal FavoriteWriteSequence(DesktopSessionHost owner, FavoriteEntry candidate,
            (string Action, string Target) admissionKey)
        { Owner = owner; Candidate = candidate; AdmissionKey = admissionKey; }

        internal Task<bool> SetAsync(bool favorite) => Owner.SetFavoriteSequenceAsync(this, favorite);
        public void Dispose() => Owner.CloseFavoriteSequence(this);
    }

    private Task<bool> SetFavoriteSequenceAsync(FavoriteWriteSequence sequence, bool favorite)
    {
        lock (_admission)
        {
            if (sequence.Closed || !_favoriteWriteSequences.ContainsKey(sequence))
                return Task.FromException<bool>(new ObjectDisposedException(nameof(FavoriteWriteSequence)));
            // A lease cannot be used to queue unbounded parallel work behind one
            // slot. The caller awaits this task before draining its next intent.
            if (sequence.Writing)
                return Task.FromException<bool>(new DesktopSessionBusyException(nameof(SetFavoriteAsync)));
            sequence.Writing = true;
        }
        // Even an immediately available _commands semaphore and synchronous store
        // must execute on a worker, never on the dispatcher/admitting caller.
        try
        {
            return Task.Run(async () =>
            {
                try
                {
                    bool changed = await SetFavoriteAdmittedCoreAsync(sequence.Candidate, favorite,
                        sequence: sequence).ConfigureAwait(false);
                    await CaptureProjectionAsync().ConfigureAwait(false);
                    return changed;
                }
                finally { CompleteFavoriteSequenceWrite(sequence); }
            });
        }
        catch
        {
            // No worker owns completion if dispatch itself fails.
            CompleteFavoriteSequenceWrite(sequence);
            throw;
        }
    }

    private void CompleteFavoriteSequenceWrite(FavoriteWriteSequence sequence)
    {
        ProfileAdmissionToken? admission;
        lock (_admission)
        {
            sequence.Writing = false;
            admission = ReleaseFavoriteSequenceIfClosed(sequence);
        }
        admission?.Dispose();
    }

    private void EnsureFavoriteSequenceWriteAllowed(FavoriteWriteSequence sequence, FavoriteEntry candidate)
    {
        lock (_lifecycle)
        {
            Config.EnsureOrdinaryWritesAllowed();
            if (_disposed || (_state != DesktopSessionState.Ready && _state != DesktopSessionState.Stopping))
                throw new InvalidOperationException("The desktop session is not ready to save this favorite.");
            lock (_admission)
            {
                // Closed leases remain active until their previously accepted
                // write ends. Dispose must not revoke that write at the IO gate.
                if (!_favoriteWriteSequences.ContainsKey(sequence) || !sequence.Writing
                    || !ReferenceEquals(sequence.Owner, this) || !ReferenceEquals(sequence.Candidate, candidate))
                    throw new ObjectDisposedException(nameof(FavoriteWriteSequence));
            }
        }
    }

    private void CloseFavoriteSequence(FavoriteWriteSequence sequence)
    {
        ProfileAdmissionToken? admission;
        lock (_admission)
        {
            sequence.Closed = true;
            admission = ReleaseFavoriteSequenceIfClosed(sequence);
        }
        admission?.Dispose();
    }

    // Caller holds _admission. Detach ownership here, but release the token outside
    // the lock only after both the caller closed the sequence and its last worker ended.
    private ProfileAdmissionToken? ReleaseFavoriteSequenceIfClosed(FavoriteWriteSequence sequence)
    {
        if (!sequence.Closed || sequence.Writing || !_favoriteWriteSequences.Remove(sequence, out var admission)) return null;
        _acceptedCommands.Remove(sequence.AdmissionKey);
        CompleteCommandDrainIfEmpty();
        return admission;
    }
}
