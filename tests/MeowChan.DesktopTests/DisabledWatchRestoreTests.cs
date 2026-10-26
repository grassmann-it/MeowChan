using System.Net;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class DisabledWatchRestoreTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private const string ThreadUrl = "https://boards.4chan.org/g/thread/123";
    private const string BoardUrl = "https://boards.4chan.org/g/";

    [Fact]
    public async Task RestartWithoutWatchRestoreKeepsBoardSuppressionUntilExplicitThreadAdd()
    {
        using TempDirectory directory = new();
        new Config { SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        using CatalogTransport firstTransport = new();
        using HttpClient firstClient = new(firstTransport);
        ObservedCatalogProvider firstProvider = new(firstClient);
        await using (DesktopSessionHost first = new(directory.Path, [firstProvider], (_, _) => new UnusedSessionStore()))
        {
            await first.StartAsync().WaitAsync(Timeout);
            await first.AddAsync(BoardUrl).WaitAsync(Timeout);
            await UntilAsync(() => firstProvider.Board.FinishedPolls == 1);
            // Poll completion settles discovery; it does not await the passive host projection.
            DesktopSessionProjection discoveredFrame = await first.CaptureProjectionAsync().WaitAsync(Timeout);
            Assert.False(discoveredFrame.IsStale, discoveredFrame.CaptureError);
            DesktopThreadEntry discovered = Assert.Single(discoveredFrame.View.Threads);
            Assert.Equal("123", discovered.Snapshot.Id.Thread);
            await first.RemoveThreadAsync(discovered.WatchId).WaitAsync(Timeout);
            await first.AddAsync("https://boards.4chan.org/g/thread/456").WaitAsync(Timeout);
            Assert.Equal("456", Assert.Single(first.GetView().Threads).Snapshot.Id.Thread);
        }
        using CatalogTransport restoredTransport = new();
        using HttpClient restoredClient = new(restoredTransport);
        ObservedCatalogProvider restoredProvider = new(restoredClient);
        await using DesktopSessionHost restored = new(directory.Path, [restoredProvider], (_, _) => new UnusedSessionStore());
        await restored.StartAsync().WaitAsync(Timeout);
        Assert.Empty(restored.GetView().Threads);
        Assert.Empty(restored.GetView().Boards);
        Assert.Equal(0, restoredTransport.Requests);
        await restored.AddAsync(BoardUrl).WaitAsync(Timeout);
        await UntilAsync(() => restoredProvider.Board.FinishedPolls == 1);
        DesktopSessionProjection restoredFrame = await restored.CaptureProjectionAsync().WaitAsync(Timeout);
        Assert.False(restoredFrame.IsStale, restoredFrame.CaptureError);
        Assert.Empty(restoredFrame.View.Threads);
        Assert.Equal(0, restoredTransport.Thread123Requests);
        DesktopAdmissionResult explicitAdd = await restored.AddAsync(ThreadUrl).WaitAsync(Timeout);
        Assert.Equal(AdmissionStatus.Added, explicitAdd.Status);
        Assert.Equal("123", Assert.Single(restored.GetView().Threads).Snapshot.Id.Thread);
        await UntilAsync(() => restoredTransport.Thread123Requests == 1);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource deadline = new(Timeout);
        while (!condition()) await Task.Delay(5, deadline.Token);
    }

    private sealed class ObservedCatalogProvider : FourChanImageBoard
    {
        public ObservedCatalogProvider(HttpClient client) : base(client) => Board = new ObservedBoard(this);
        public ObservedBoard Board { get; }
        public override IBoard GetBoard(Uri uri) => Board;
    }

    private sealed class ObservedBoard(IImageBoard provider) : FourChanBoard(provider, new Uri(BoardUrl))
    {
        private int _finishedPolls;
        public int FinishedPolls => Volatile.Read(ref _finishedPolls);
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _finishedPolls);
            return Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class CatalogTransport : HttpMessageHandler
    {
        private int _requests, _thread123Requests;
        public int Requests => Volatile.Read(ref _requests);
        public int Thread123Requests => Volatile.Read(ref _thread123Requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            string json = request.RequestUri!.AbsolutePath switch
            {
                "/boards.json" => "{\"boards\":[{\"board\":\"g\",\"title\":\"Fixture\"}]}",
                "/g/catalog.json" => "[{\"page\":1,\"threads\":[{\"no\":123}]}]",
                "/g/thread/123.json" => "{\"posts\":[{\"no\":123}]}",
                "/g/thread/456.json" => "{\"posts\":[{\"no\":456}]}",
                _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri)
            };
            if (request.RequestUri.AbsolutePath == "/g/thread/123.json") Interlocked.Increment(ref _thread123Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class UnusedSessionStore : ISessionStore
    {
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Disabled watch restore must not load session URLs.");
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Disabled watch persistence must not write session URLs.");
    }
}
