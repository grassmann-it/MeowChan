using System;

namespace MeowChan.Desktop.Stats;

/// <summary>One sampling interval: rates in bytes per second (requests per second for <see cref="Requests"/>).</summary>
internal readonly record struct IoSample(float NetDown, float DiskWrite, float DiskRead, float Requests)
{
    internal bool IsZero => NetDown == 0 && DiskWrite == 0 && DiskRead == 0 && Requests == 0;
}

/// <summary>An oldest-first copy of the ring; every row has <see cref="Count"/> values.</summary>
internal sealed class IoSeries
{
    internal static readonly IoSeries Empty = new(0, 0, [], [], [], []);

    internal IoSeries(int capacity, int count, float[] netDown, float[] diskWrite, float[] diskRead, float[] requests)
    {
        Capacity = capacity; Count = count; NetDown = netDown; DiskWrite = diskWrite; DiskRead = diskRead; Requests = requests;
    }

    /// <summary>The configured history length when the copy was taken; <see cref="Count"/> is how many samples exist yet.</summary>
    internal int Capacity { get; }
    internal int Count { get; }
    internal float[] NetDown { get; }
    internal float[] DiskWrite { get; }
    internal float[] DiskRead { get; }
    internal float[] Requests { get; }
}

/// <summary>
/// Four rows of rate samples in one wrap-around buffer. The length can change at any time; the newest samples survive.
/// Not thread-safe: <see cref="IoStatsHub"/> guards it with its own lock.
/// </summary>
internal sealed class IoRing
{
    private float[] _net, _write, _read, _requests;
    private int _head; // index of the next write
    private int _count;

    internal IoRing(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        (_net, _write, _read, _requests) = (new float[capacity], new float[capacity], new float[capacity], new float[capacity]);
    }

    internal int Capacity => _net.Length;
    internal int Count => _count;

    internal void Add(in IoSample sample)
    {
        _net[_head] = sample.NetDown;
        _write[_head] = sample.DiskWrite;
        _read[_head] = sample.DiskRead;
        _requests[_head] = sample.Requests;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>Changes the length. Growing keeps everything; shrinking keeps the newest samples.</summary>
    internal void Resize(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (capacity == Capacity) return;
        IoSeries current = ToSeries();
        int keep = Math.Min(current.Count, capacity);
        int skip = current.Count - keep;
        (_net, _write, _read, _requests) = (new float[capacity], new float[capacity], new float[capacity], new float[capacity]);
        Array.Copy(current.NetDown, skip, _net, 0, keep);
        Array.Copy(current.DiskWrite, skip, _write, 0, keep);
        Array.Copy(current.DiskRead, skip, _read, 0, keep);
        Array.Copy(current.Requests, skip, _requests, 0, keep);
        _count = keep;
        _head = keep % capacity;
    }

    internal void Clear()
    {
        Array.Clear(_net); Array.Clear(_write); Array.Clear(_read); Array.Clear(_requests);
        _head = 0; _count = 0;
    }

    internal IoSeries ToSeries()
    {
        float[] net = new float[_count], write = new float[_count], read = new float[_count], requests = new float[_count];
        int start = (_head - _count + Capacity) % Capacity;
        int firstPart = Math.Min(_count, Capacity - start);
        void Copy(float[] from, float[] to)
        {
            Array.Copy(from, start, to, 0, firstPart);
            if (firstPart < _count) Array.Copy(from, 0, to, firstPart, _count - firstPart);
        }
        Copy(_net, net); Copy(_write, write); Copy(_read, read); Copy(_requests, requests);
        return new IoSeries(Capacity, _count, net, write, read, requests);
    }
}
