using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan;

/// <summary>A canonical provider/board/thread identity, independent of display names.</summary>
public sealed record ResourceId(string Source, string Board, string? Thread = null);

public enum FailureKind { Http, Network, Timeout, InvalidData, Storage, Unknown }

/// <summary>
/// A machine-readable failure category with a human-readable diagnostic.
/// Optional evidence describes the current observation; it is not added to durable work state.
/// </summary>
public sealed record FailureInfo(FailureKind Kind, string Message, HttpStatusCode? HttpStatusCode = null)
{
    /// <summary>Optional producer-supplied code, for display only; not retry or action authority.</summary>
    public string? DiagnosticCode { get; init; }

    /// <summary>Optional reported retry time, not a scheduling instruction.</summary>
    public DateTimeOffset? RetryAt { get; init; }

    /// <summary>Optional typed transport evidence, not a measured phase or a remote root cause.</summary>
    public FailureNetworkStage? NetworkStage { get; init; }

    internal static FailureInfo FromException(Exception exception, bool media)
    {
        // Retain the existing call shape, but a media operation can fail while reading
        // the network OR writing storage. The flag alone cannot establish an I/O cause.
        return FailureDiagnostics.FromException(exception);
    }
}

/// <summary>Copied state for one resource. Queue counts describe actual waiting and occupied worker slots.</summary>
public sealed record ThreadSnapshot(ResourceId Id, string Url, string Title, string DownloadDirectory,
    ThreadActivity Activity, ThreadSourceState SourceState, RequestPolicyProgress? RequestProgress,
    int KnownMedia, int CompletedMedia, IReadOnlyDictionary<string, FailureInfo> MediaFailures,
    FailureInfo? LastCheckFailure, DownloadCounts Downloads)
{
    /// <summary>Null for legacy providers without capture/selection support.</summary>
    public Archive.ThreadContentStatus? Content { get; init; }
    public ThreadWorkSnapshot? Work { get; init; }
    public int SelectedMedia => Math.Max(0, KnownMedia - (Content?.ExcludedMedia ?? 0));
}

/// <summary>Durable source, intent, and media work, also available after automatic completion.</summary>
public sealed record ThreadWorkSnapshot(string Url, WorkIntent Intent, ThreadSourceStateValue Source,
    DateTimeOffset? EndObservedAt, bool CapturePending, IReadOnlyList<MediaWork> Media)
{
    public DateTimeOffset? Deadline => EndObservedAt?.AddHours(12);
    public int SavedMedia => Media.Count(item => item.Status == MediaWorkStatus.Succeeded);
    public int MissingMedia => Media.Count(item => item.Status != MediaWorkStatus.Succeeded);
    public DateTimeOffset? NextDueAt => Media.Where(item => item.Status == MediaWorkStatus.Waiting)
        .Select(item => item.DueAt).Where(value => value != null).Min();
}

public sealed record BoardSnapshot(ResourceId Id, string Url, string Name, string DownloadDirectory,
    RequestPolicyProgress? RequestProgress, FailureInfo? LastCheckFailure);

/// <summary>
/// Defensive state copies. Membership, each resource and the queue are captured coherently;
/// independent workers are not paused to create a global transaction. Warnings retain the latest 32 entries.
/// </summary>
public sealed record DownloadSessionSnapshot(IReadOnlyList<ThreadSnapshot> Threads,
    IReadOnlyList<BoardSnapshot> Boards, bool IsStopping, IReadOnlyList<string> Warnings)
{
    public bool IsPaused { get; init; }
}

/// <summary>Read-only membership capture retaining exact resource handles alongside defensive state copies.</summary>
public sealed record RegistryView(IReadOnlyDictionary<IThread, ThreadSnapshot> Threads,
    IReadOnlyDictionary<IBoard, BoardSnapshot> Boards, bool IsStopping, IReadOnlyList<string> Warnings)
{
    public bool IsPaused { get; init; }
}
