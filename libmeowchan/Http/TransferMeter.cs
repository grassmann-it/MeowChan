using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace MeowChan.Http;

/// <summary>
/// Monotonic byte and request counters of one source. Every field is written with <see cref="System.Threading.Interlocked"/>
/// and must be read with <c>Volatile.Read</c>. Raw bytes only: formatting belongs to the consumer.
/// </summary>
internal sealed class SourceCounters
{
    /// <summary>Response body bytes delivered by the transport (after decompression where the transport decompresses).</summary>
    public long NetDown;
    /// <summary>Media bytes MeowChan wrote to disk itself.</summary>
    public long DiskWritten;
    /// <summary>Media bytes MeowChan read back from disk itself (hash and integrity reads).</summary>
    public long DiskRead;
    /// <summary>Requests that reached the transport, retries included.</summary>
    public long Requests;
    /// <summary>Transport exceptions (cancellation excluded), failed body reads and responses with status 400 or higher.</summary>
    public long Errors;
}

/// <summary>Keyed <see cref="SourceCounters"/> with a ceiling on distinct keys; later keys are summed into <see cref="TransferMeter.OtherKey"/>.</summary>
internal sealed class SourceRegistry(int maximumSources)
{
    private readonly ConcurrentDictionary<string, SourceCounters> _sources = new(StringComparer.Ordinal);

    /// <summary>The counters of <paramref name="key"/>, created on first use. A blank key counts as <see cref="TransferMeter.UnknownKey"/>.</summary>
    public SourceCounters For(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) key = TransferMeter.UnknownKey;
        if (_sources.TryGetValue(key, out SourceCounters? existing)) return existing;
        if (_sources.Count >= maximumSources) key = TransferMeter.OtherKey;
        return _sources.GetOrAdd(key, static _ => new SourceCounters());
    }

    /// <summary>A snapshot-safe view of every key seen so far. Enumerating while other threads count is allowed.</summary>
    public IReadOnlyCollection<KeyValuePair<string, SourceCounters>> All => _sources;
}

/// <summary>
/// Process-wide registry of <see cref="SourceCounters"/> by source key. Capture points resolve their counters once per
/// request or commit and then only perform <see cref="System.Threading.Interlocked"/> adds, so the hot path takes no lock
/// and allocates nothing.
/// </summary>
internal static class TransferMeter
{
    /// <summary>Documented ceiling of distinct keys; later keys are summed into <see cref="OtherKey"/>.</summary>
    public const int MaximumSources = 256;
    /// <summary>Disk work that belongs to no download source (local imports, candidate verification).</summary>
    public const string LocalKey = "local";
    public const string OtherKey = "other";
    public const string UnknownKey = "unknown";

    private static readonly SourceRegistry Shared = new(MaximumSources);

    /// <summary>The counters of <paramref name="key"/>, created on first use.</summary>
    public static SourceCounters For(string? key) => Shared.For(key);

    /// <summary>A snapshot-safe view of every key seen so far.</summary>
    public static IReadOnlyCollection<KeyValuePair<string, SourceCounters>> All => Shared.All;
}
