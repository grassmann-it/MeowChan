namespace MeowChan.Downloader;

using MeowChan.Persistence;
using MeowChan.Storage;

/// <summary>Why a queue execution of an external owner's item ended without a commit or failure.</summary>
internal enum ExternalMediaCancellation
{
    /// <summary>
    /// The owner itself was cancelled (<see cref="DownloadQueue.CancelDownloadsForThreadAsync"/>). This is a
    /// deliberate decision and terminal for the owner's durable request.
    /// </summary>
    Cancelled,
    /// <summary>
    /// The queue stopped (shutdown, profile close) while the item was waiting, interrupted or running. This
    /// is not a user decision: the owner must keep its durable request so a restart can recover it.
    /// </summary>
    Stopped
}

/// <summary>
/// A queue owner whose durable admission and recovery live outside the chan work journal, for
/// example the additive Booru catalog. Its items share the existing queue's capacity, fairness,
/// pause/interruption, cancellation, bandwidth limit, target gate and verified
/// <see cref="MediaStore"/> commit, but they never create <see cref="WorkResource"/> or
/// <see cref="MediaWork"/> rows, are never bound with <see cref="DownloadQueue.BindWork"/> and are
/// therefore never restored by the Registry as chan watches. The owner itself must not be handed
/// to the Registry, session or archive; it is only the queue's runtime job owner.
/// <para>
/// Every admitted item ends in exactly one owner notification: <see cref="OnMediaCommitted"/>, a
/// <c>IThread.ReportMediaFailure</c> or <see cref="OnMediaCancelled"/>. A pause or target wait
/// that re-queues the same job is not an end and reports nothing.
/// </para>
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

    /// <summary>The committed or failed item no longer holds its queue identity and may be retried.</summary>
    void OnMediaReleased(DownloadItem item);

    /// <summary>
    /// Called when an admitted item of this owner leaves the queue without a commit or failure: an explicit
    /// owner cancel (<see cref="ExternalMediaCancellation.Cancelled"/>) or a queue stop
    /// (<see cref="ExternalMediaCancellation.Stopped"/>), whether the item was running, waiting or
    /// interrupted. It is the external owner's only notification for that end, so it must bring its
    /// completion to a terminal, non-success state. It always runs outside queue locks.
    /// <paramref name="admission"/> is the running job's producer admission, or null when the item never
    /// reached a worker.
    /// </summary>
    void OnMediaCancelled(DownloadItem item, ExternalMediaCancellation reason, ProfileAdmissionToken? admission);
}
