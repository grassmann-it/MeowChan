namespace MeowChan.Downloader;

using MeowChan.Persistence;
using MeowChan.Storage;

/// <summary>
/// A queue owner whose durable admission and recovery live outside the chan work journal, for
/// example the additive Booru catalog. Its items share the existing queue's capacity, fairness,
/// pause/interruption, cancellation, bandwidth limit, target gate and verified
/// <see cref="MediaStore"/> commit, but they never create <see cref="WorkResource"/> or
/// <see cref="MediaWork"/> rows, are never bound with <see cref="DownloadQueue.BindWork"/> and are
/// therefore never restored by the Registry as chan watches. The owner itself must not be handed
/// to the Registry, session or archive; it is only the queue's runtime job owner.
/// </summary>
internal interface IExternalMediaWorkOwner
{
    /// <summary>
    /// Called once after a queue execution of <paramref name="item"/> produced a verified, atomically
    /// committed (or strictly restored) media receipt. It runs outside queue locks while the job's
    /// producer admission is still held; <paramref name="admission"/> is that exact token (null only
    /// for a queue without a profile lease). Exceptions are logged by the queue and never change the
    /// media commit, so the owner must record and surface its own follow-up failure.
    /// </summary>
    void OnMediaCommitted(DownloadItem item, MediaCommitResult committed, ProfileAdmissionToken? admission);

    /// <summary>
    /// Called when a queue execution for <paramref name="item"/> is cancelled (explicit thread cancel,
    /// queue stop or shutdown). It is the external owner's only cancellation notification, so it must
    /// bring its completion task to a terminal, non-success state. It runs outside queue locks.
    /// </summary>
    void OnMediaCancelled(DownloadItem item);
}
