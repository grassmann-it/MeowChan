using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using Newtonsoft.Json;
using MeowChan.Downloader;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan;

/// <summary>A canonical provider/board/thread identity, independent of display names.</summary>
public sealed record ResourceId(string Source, string Board, string? Thread = null);

public enum FailureKind { Http, Network, Timeout, InvalidData, Storage, Unknown }

/// <summary>A machine-readable failure category with a human-readable diagnostic.</summary>
public sealed record FailureInfo(FailureKind Kind, string Message, HttpStatusCode? HttpStatusCode = null)
{
    internal static FailureInfo FromException(Exception exception, bool media)
    {
        return exception switch
        {
            HttpRequestException request when request.StatusCode != null => new FailureInfo(FailureKind.Http, request.Message, request.StatusCode),
            HttpRequestException => new FailureInfo(FailureKind.Network, exception.Message),
            HttpIOException or System.Net.Sockets.SocketException => new FailureInfo(FailureKind.Network, exception.Message),
            TimeoutException or OperationCanceledException => new FailureInfo(FailureKind.Timeout, exception.Message),
            JsonException or System.Text.Json.JsonException or InvalidDataException or FormatException or OverflowException or InvalidCastException => new FailureInfo(FailureKind.InvalidData, exception.Message),
            UnauthorizedAccessException => new FailureInfo(FailureKind.Storage, exception.Message),
            IOException => new FailureInfo(media ? FailureKind.Storage : FailureKind.Network, exception.Message),
            _ => new FailureInfo(FailureKind.Unknown, exception.Message)
        };
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
