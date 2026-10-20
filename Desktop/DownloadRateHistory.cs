using System;
using System.Collections.Generic;

namespace MeowChan.Desktop;

internal sealed class DownloadRateHistory(long initialBytes)
{
    private long _previousBytes = initialBytes;
    private readonly List<double> _samples = new(60);
    internal IReadOnlyList<double> Samples => _samples;
    internal double BytesPerSecond { get; private set; }

    internal void Sample(long receivedBytes, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero) return;
        BytesPerSecond = Math.Max(0, receivedBytes - _previousBytes) / elapsed.TotalSeconds;
        _previousBytes = receivedBytes;
        if (_samples.Count == 60) _samples.RemoveAt(0);
        _samples.Add(BytesPerSecond);
    }
}
