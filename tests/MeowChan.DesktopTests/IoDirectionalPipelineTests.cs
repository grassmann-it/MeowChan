using System.Net;
using System.Net.Http;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Stats;
using MeowChan.Http;

namespace MeowChan.DesktopTests;

/// <summary>
/// Synthetic end-to-end coverage for the transport meter to desktop statistics path. The handler below is entirely local:
/// no socket, listener or live source is involved.
/// </summary>
public sealed class IoDirectionalPipelineTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    private static IoStatsOptions Options => new() { Persist = false, SampleIntervalMs = 2000, HistorySamples = 30 };

    [Fact]
    public async Task CompletedLocalUploadReachesNetUpAndGetWithoutBodyAddsNoUpload()
    {
        string sourceKey = "io-pipeline-upload-" + Guid.NewGuid().ToString("N");
        IoManualClock clock = new(Noon);
        using IoStatsHub hub = new(new TransferMeterSource(), clock, Options);
        using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);

        LocalHandler transport = new();
        using HttpClient client = new(new MeteringHandler(transport));
        byte[][] chunks = [[1, 2, 3], [4, 5], [6, 7, 8, 9]];
        using (HttpRequestMessage upload = Request(sourceKey, HttpMethod.Post, new ChunkedContent(chunks)))
        using (HttpResponseMessage response = await client.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        using (HttpRequestMessage get = Request(sourceKey, HttpMethod.Get))
        using (HttpResponseMessage response = await client.SendAsync(get, HttpCompletionOption.ResponseHeadersRead))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        byte[] completedUpload = Assert.Single(transport.UploadBodies);
        Assert.Equal(chunks.SelectMany(static chunk => chunk), completedUpload);
        long uploadedBytes = completedUpload.LongLength;

        clock.Advance(TimeSpan.FromSeconds(2));
        IoStatsSnapshot snapshot = hub.Snapshot();

        Assert.Equal(new IoCounts(0, uploadedBytes, 0, 0, 2, 0), snapshot.Total.Session);
        Assert.Equal(new IoCounts(0, uploadedBytes, 0, 0, 2, 0), Assert.Single(snapshot.Sources).Session);
        Assert.Equal(new IoRates(0, uploadedBytes / 2d, 0, 0, 1), snapshot.Rate);
        Assert.Equal([0f], snapshot.Series.NetDown);
        Assert.Equal([uploadedBytes / 2f], snapshot.Series.NetUp);
        Assert.Equal([0f], snapshot.Series.DiskWrite);
        Assert.Equal([0f], snapshot.Series.DiskRead);
    }

    [Fact]
    public void DiskReadOnlyCounterReachesDiskIoWithoutInventingNetworkOrSummingDirections()
    {
        string sourceKey = "io-pipeline-disk-read-" + Guid.NewGuid().ToString("N");
        IoManualClock clock = new(Noon);
        using IoStatsHub hub = new(new TransferMeterSource(), clock, Options);
        using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);

        SourceCounters counters = TransferMeter.For(sourceKey);
        const long readBytes = 8192;
        Interlocked.Add(ref counters.DiskRead, readBytes);

        clock.Advance(TimeSpan.FromSeconds(2));
        IoStatsSnapshot snapshot = hub.Snapshot();

        Assert.Equal(new IoCounts(0, 0, 0, readBytes, 0, 0), snapshot.Total.Session);
        Assert.Equal(new IoRates(0, 0, 0, readBytes / 2d, 0), snapshot.Rate);
        Assert.Equal([0f], snapshot.Series.NetDown);
        Assert.Equal([0f], snapshot.Series.NetUp);
        Assert.Equal([0f], snapshot.Series.DiskWrite);
        Assert.Equal([readBytes / 2f], snapshot.Series.DiskRead);

        IoGraph graph = new() { Source = IoGraphSource.DiskIo };
        graph.SetSeries(snapshot.Series);
        Assert.True(graph.HasTraffic);
        Assert.Equal(0, graph.PrimaryTrafficPeak);
        Assert.Equal(readBytes / 2d, graph.SecondaryTrafficPeak);
        Assert.Equal(readBytes / 2d, graph.TrafficPeak); // max direction, never write + read or network + disk
    }

    private static HttpRequestMessage Request(string sourceKey, HttpMethod method, HttpContent? content = null)
    {
        HttpRequestMessage request = new(method, "https://synthetic.invalid/io") { Content = content };
        request.Options.Set(MeteringHandler.SourceKey, sourceKey);
        return request;
    }

    private sealed class LocalHandler : HttpMessageHandler
    {
        internal List<byte[]> UploadBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is { } content)
            {
                using MemoryStream body = new();
                await content.CopyToAsync(body, cancellationToken);
                UploadBodies.Add(body.ToArray());
            }

            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new ByteArrayContent([]) };
        }
    }

    /// <summary>Unknown-length request content so metering observes the bytes actually written by each chunk.</summary>
    private sealed class ChunkedContent(byte[][] chunks) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            foreach (byte[] chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Write(chunk, 0, chunk.Length);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            foreach (byte[] chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await stream.WriteAsync(chunk.AsMemory(), cancellationToken);
            }
        }
    }
}
