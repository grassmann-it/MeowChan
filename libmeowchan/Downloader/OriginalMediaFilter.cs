using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Downloader;

internal interface IOriginalMediaFilter
{
    bool CanStart { get; }
    event Action? CapacityChanged;
    Task<IOriginalMediaFilterLease> AcquireAsync(DownloadItem item, CancellationToken cancellationToken);
}

internal interface IOriginalMediaFilterLease : IAsyncDisposable
{
    Task<MediaCommitResult> CommitAsync(DownloadItem item, Stream source, MediaStore store,
        Func<int, CancellationToken, Task> throttle, CancellationToken cancellationToken);
}

internal interface IOriginalMediaFilterFactory
{
    IOriginalMediaFilter Create(DownloadItem item, CandidateDecision decision, Func<bool> isCurrent,
        Func<bool, string, CancellationToken, Task> recordOmission);
}

internal sealed class MediaFilterOmissionException : Exception
{
    internal bool IsNoMatch { get; }
    internal string ReasonCode { get; }
    internal MediaFilterOmissionException(bool isNoMatch, string reasonCode, string? message = null)
        : base(message ?? reasonCode) { IsNoMatch = isNoMatch; ReasonCode = reasonCode; }
}
