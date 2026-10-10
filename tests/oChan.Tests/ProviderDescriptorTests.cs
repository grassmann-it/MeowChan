using oChan.Archive;
using oChan.Boards.FourChan;
using oChan.Boards.EightKun;
using oChan.Downloader;
using oChan.Interfaces;
using oChan.Persistence;
using Serilog;

namespace oChan.Tests;

public sealed class ProviderDescriptorTests
{
    [Fact]
    public async Task ReadingBuiltInDescriptionsDoesNotResolveOrRequestAnythingAndCaptureNeedsServices()
    {
        using TempDirectory directory = new TempDirectory();
        using RejectNetworkHandler handler = new RejectNetworkHandler();
        using HttpClient client = new HttpClient(handler);
        FourChanImageBoard fourChan = new FourChanImageBoard(client);
        EightKunImageBoard eightKun = new EightKunImageBoard(client);
        await using Registry registry = new Registry([fourChan, eightKun], new DownloadQueue(2, 1024), directory.Path,
            TimeSpan.FromSeconds(60), 60, persistSession: false);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            IReadOnlyList<ProviderDescriptor> sources = registry.GetProviderDescriptors();
            Assert.Equal(new[] { "4chan", "8kun" }, sources.Select(source => source.SourceId));
            Assert.All(sources, source =>
            {
                Assert.True(source.Capabilities!.Value.HasFlag(ProviderCapabilities.PostCapture));
                Assert.False(source.Capabilities.Value.HasFlag(ProviderCapabilities.Previews));
                Assert.False(source.CaptureAvailable);
                Assert.Null(source.AdapterVersion);
            });
            Assert.Equal(new[] { "boards.4chan.org", "boards.4channel.org" }, sources[0].SupportedHosts);
        }
        Assert.Empty(registry.Threads);
        Assert.Empty(registry.Boards);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task ExistingCustomProviderHasUnspecifiedCapabilitiesAndOptionalThreadOnlyDescriptionUsesNormalAdmission()
    {
        using TempDirectory directory = new TempDirectory();
        using StubImageBoard plain = new StubImageBoard();
        await using (Registry registry = new Registry([plain], new DownloadQueue(1, 1024), directory.Path,
            TimeSpan.FromSeconds(60), 60, persistSession: false))
        {
            ProviderDescriptor unknown = Assert.Single(registry.GetProviderDescriptors());
            Assert.Null(unknown.Capabilities);
            Assert.Empty(unknown.SupportedHosts);
            Assert.Empty(registry.Threads);
        }
        using StubImageBoard backend = new StubImageBoard();
        ThreadOnlyDescription provider = new ThreadOnlyDescription(backend);
        await using Registry described = new Registry([provider], new DownloadQueue(1, 1024), directory.Path,
            TimeSpan.FromSeconds(60), 60, persistSession: false);
        ProviderDescriptor source = Assert.Single(described.GetProviderDescriptors());
        Assert.Equal(ProviderCapabilities.ThreadWatch, source.Capabilities);
        Assert.Equal("test-adapter-1", source.AdapterVersion);
        Assert.Equal(AdmissionStatus.Added, (await described.AddAsync("https://stub.test/g/thread/123")).Status);
        Assert.Equal(AdmissionStatus.Unsupported, (await described.AddAsync("https://unknown.test/g/thread/123")).Status);
        Assert.Single(described.Threads);
    }

    [Fact]
    public async Task EffectiveCaptureBecomesAvailableAfterInitializingTheSessionContentRuntime()
    {
        using TempDirectory directory = new TempDirectory();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        using RejectNetworkHandler handler = new RejectNetworkHandler();
        using HttpClient client = new HttpClient(handler);
        await using ThreadContentRuntime content = new ThreadContentRuntime(directory.Path, lease);
        await using Registry registry = new Registry([new FourChanImageBoard(client)], new DownloadQueue(1, 1024), directory.Path,
            TimeSpan.FromSeconds(60), 60, persistSession: false, contentRuntime: content);
        Assert.False(Assert.Single(registry.GetProviderDescriptors()).CaptureAvailable);
        await content.InitializeAsync();
        Assert.True(Assert.Single(registry.GetProviderDescriptors()).CaptureAvailable);
        await content.DisposeAsync();
        Assert.False(Assert.Single(registry.GetProviderDescriptors()).CaptureAvailable);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => content.InitializeAsync());
        Assert.Equal(0, handler.Requests);
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("Passive descriptions must not make network calls.");
        }
    }

    private sealed class ThreadOnlyDescription(StubImageBoard backend) : IImageBoard, IProviderDescriptorSource
    {
        public string Name => backend.Name;
        public string NiceName => backend.NiceName;
        public Uri BaseUri => backend.BaseUri;
        public ILogger Logger => ((IImageBoard)backend).Logger;
        public ProviderCapabilities Capabilities => ProviderCapabilities.ThreadWatch;
        public IReadOnlyList<string> SupportedHosts => new[] { "stub.test" };
        public string AdapterVersion => "test-adapter-1";
        public bool CanHandle(Uri uri) => backend.CanHandle(uri);
        public bool IsThreadUri(Uri uri) => backend.IsThreadUri(uri);
        public bool IsBoardUri(Uri uri) => false;
        public IBoard GetBoard(Uri uri) => throw new NotSupportedException();
        public IThread GetThread(Uri uri) => backend.GetThread(uri);
        public HttpClient GetHttpClient() => backend.GetHttpClient();
    }
}
