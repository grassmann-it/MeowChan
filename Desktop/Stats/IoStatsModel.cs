using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using MeowChan.Http;

namespace MeowChan.Desktop.Stats;

/// <summary>Raw byte and request totals. Units are applied by the display (<see cref="ByteUnits"/>), never here.</summary>
internal readonly record struct IoCounts(long NetDown, long NetUp, long DiskWritten, long DiskRead, long Requests, long Errors)
{
    internal static IoCounts Zero => default;
    internal bool IsZero => this == default;

    internal IoCounts Plus(in IoCounts other) => new IoCounts(NetDown: Sat(NetDown, other.NetDown), NetUp: Sat(NetUp, other.NetUp),
        DiskWritten: Sat(DiskWritten, other.DiskWritten), DiskRead: Sat(DiskRead, other.DiskRead),
        Requests: Sat(Requests, other.Requests), Errors: Sat(Errors, other.Errors));

    private static long Sat(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}

/// <summary>Per second: bytes for the first four, requests for the last.</summary>
internal readonly record struct IoRates(double NetDown, double NetUp, double DiskWrite, double DiskRead, double Requests)
{
    internal static IoRates Zero => default;
}

/// <summary>Download queue occupancy read when a snapshot is taken; never counted.</summary>
internal readonly record struct IoActivity(int Active, int Pending, int WaitingAdmission);

/// <summary>One source's monotonic counters at one moment, as read from the transport side.</summary>
internal readonly record struct IoCounterReading(string Key, long NetDown, long NetUp, long DiskWritten, long DiskRead, long Requests, long Errors);

/// <summary>Where the hub reads its counters. The product source is <see cref="TransferMeterSource"/>.</summary>
internal interface IIoCounterSource
{
    /// <summary>Appends one reading per known source. Must not block and must not allocate per source beyond the readings.</summary>
    void Read(List<IoCounterReading> into);
}

/// <summary>Reads <see cref="TransferMeter"/>, the process-wide counters of the transport and media store capture points.</summary>
internal sealed class TransferMeterSource : IIoCounterSource
{
    public void Read(List<IoCounterReading> into)
    {
        foreach (KeyValuePair<string, SourceCounters> pair in TransferMeter.All)
        {
            SourceCounters c = pair.Value;
            into.Add(new IoCounterReading(Key: pair.Key, NetDown: System.Threading.Volatile.Read(ref c.NetDown),
                NetUp: System.Threading.Volatile.Read(ref c.NetUp), DiskWritten: System.Threading.Volatile.Read(ref c.DiskWritten),
                DiskRead: System.Threading.Volatile.Read(ref c.DiskRead), Requests: System.Threading.Volatile.Read(ref c.Requests),
                Errors: System.Threading.Volatile.Read(ref c.Errors)));
        }
    }
}

internal static class IoStatsKeys
{
    /// <summary>Sources beyond <c>MaxSources</c> are summed under this key. The same key the transport uses for its own overflow.</summary>
    internal const string Other = "other";
    internal const string Unknown = "unknown";
    internal const int MaximumKeyLength = 128;

    /// <summary>Keys are stored and compared as written; blank keys become <see cref="Unknown"/>, overlong keys are cut.</summary>
    internal static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Unknown;
        return key.Length > MaximumKeyLength ? key[..MaximumKeyLength] : key;
    }
}

/// <summary>Who wants samples. The hub's timers follow the strongest subscription, never the other way round.</summary>
internal enum IoStatsVisibility
{
    /// <summary>Wants no timer; reads on demand with <see cref="IoStatsHub.Peek"/>.</summary>
    None,
    /// <summary>The window is hidden and the tray tooltip wants current rates: a slow timer, only while downloads are active.</summary>
    Tray,
    /// <summary>The window (header graph or statistics page) is visible: the sampling interval of the settings.</summary>
    Window
}

/// <summary>Per-day totals by source, newest day last. All mutation happens under the hub's lock.</summary>
internal sealed class IoDays
{
    private readonly SortedDictionary<DateOnly, Dictionary<string, IoCounts>> _days = new();

    internal int Count => _days.Count;
    internal IEnumerable<KeyValuePair<DateOnly, Dictionary<string, IoCounts>>> Days => _days;

    internal bool TryGet(DateOnly day, out Dictionary<string, IoCounts> sources) => _days.TryGetValue(day, out sources!);

    internal void Clear() => _days.Clear();

    /// <summary>Adds to the day, folding a source that would exceed <paramref name="maxSources"/> keys into <see cref="IoStatsKeys.Other"/>.</summary>
    internal void Add(DateOnly day, string key, in IoCounts counts, int maxSources)
    {
        if (!_days.TryGetValue(day, out Dictionary<string, IoCounts>? sources))
            _days[day] = sources = new Dictionary<string, IoCounts>(StringComparer.Ordinal);
        AddTo(sources, key, counts, maxSources);
    }

    internal static void AddTo(Dictionary<string, IoCounts> sources, string key, in IoCounts counts, int maxSources)
    {
        if (!sources.ContainsKey(key) && key != IoStatsKeys.Other)
        {
            int named = sources.Count - (sources.ContainsKey(IoStatsKeys.Other) ? 1 : 0);
            if (named >= maxSources - 1) key = IoStatsKeys.Other;
        }
        ref IoCounts slot = ref CollectionsMarshal.GetValueRefOrAddDefault(sources, key, out _);
        slot = slot.Plus(counts);
    }

    /// <summary>Adds a whole day (loading, merging); the source limit applies like for live additions.</summary>
    internal void Merge(DateOnly day, IEnumerable<KeyValuePair<string, IoCounts>> sources, int maxSources)
    {
        foreach ((string key, IoCounts counts) in sources.OrderByDescending(pair => pair.Value.NetDown).ThenBy(pair => pair.Key, StringComparer.Ordinal))
            Add(day, key, counts, maxSources);
    }

    /// <summary>Drops every day before <paramref name="oldestKept"/>. Returns whether anything was removed.</summary>
    internal bool Prune(DateOnly oldestKept)
    {
        List<DateOnly>? old = null;
        foreach (DateOnly day in _days.Keys)
        {
            if (day >= oldestKept) break;
            (old ??= []).Add(day);
        }
        if (old == null) return false;
        foreach (DateOnly day in old) _days.Remove(day);
        return true;
    }

    /// <summary>Re-applies a (possibly smaller) source limit to every day, largest downloads first.</summary>
    internal bool Renormalize(int maxSources)
    {
        bool changed = false;
        foreach (DateOnly day in _days.Keys.ToArray())
        {
            Dictionary<string, IoCounts> sources = _days[day];
            if (sources.Count <= maxSources) continue;
            Dictionary<string, IoCounts> rebuilt = new(StringComparer.Ordinal);
            foreach ((string key, IoCounts counts) in sources.OrderByDescending(pair => pair.Value.NetDown)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal))
                AddTo(rebuilt, key, counts, maxSources);
            _days[day] = rebuilt;
            changed = true;
        }
        return changed;
    }

    /// <summary>Totals per source over the days in <paramref name="from"/> to <paramref name="to"/> (inclusive).</summary>
    internal Dictionary<string, IoCounts> Sum(DateOnly from, DateOnly to)
    {
        Dictionary<string, IoCounts> total = new(StringComparer.Ordinal);
        foreach ((DateOnly day, Dictionary<string, IoCounts> sources) in _days)
        {
            if (day < from || day > to) continue;
            foreach ((string key, IoCounts counts) in sources)
            {
                ref IoCounts slot = ref CollectionsMarshal.GetValueRefOrAddDefault(total, key, out _);
                slot = slot.Plus(counts);
            }
        }
        return total;
    }
}
