using System;
using MeowChan.Persistence;

namespace MeowChan.Archive;

public sealed partial class ArchiveCatalog
{
    // Entry validation only. The Registry lane owns the token across the complete
    // awaited operation; SQL still pins real I/O with WriteTransferAdmittedAsync.
    // Never wrap an asynchronous callback in this synchronous lease boundary.
    internal void EnsureTransferAdmission(ProfileAdmissionToken admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        if (admission.Kind != ProfileAdmissionKind.Exclusive)
            throw new InvalidOperationException("Transfer requires exclusive profile admission.");
        _lease.RunPrivateStoreCommit(admission, () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return true;
        });
    }
}
