using System.Globalization;
using System.Net;
using System.Net.Http;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>
/// Additional no-UI regressions for the already-implemented first-run Settings hydration.
/// They use the coordinator/draft seams and one deterministic local host/provider fixture.
/// </summary>
public sealed class FirstRunSettingsExtraRegressionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task DurableActivationFailureStillCompletesSetupWithWarningAndRetiresOffer()
    {
        using TempDirectory directory = new();
        string chosen = Path.Combine(directory.Path, "chosen");
        new Config
        {
            FirstRunSetupPending = true,
            DownloadsPaused = true,
            SaveUrlsOnExit = false,
            AutomaticUpdateChecks = false,
            MinimumFreeSpaceGiB = 0
        }.SaveConfig(directory.Path);
        Directory.CreateDirectory(chosen);

        using HttpClient client = new(new ActivationFailureTransport());
        ActivationFailureProvider provider = new(client);
        await using DesktopSessionHost host = new(directory.Path, new IImageBoard[] { provider });
        await host.StartAsync().WaitAsync(Timeout);
        await host.AddAsync("https://boards.4chan.org/g/thread/123").WaitAsync(Timeout);
        await WaitUntilAsync(() => host.GetView().Threads.Any(thread => thread.Snapshot.Id.Thread == "123"));

        // Registry.ApplyOptionsAsync commits settings before notifying active watches.
        // This forced late provider failure models the production activation-warning path.
        provider.Thread.FailActivation = true;
        FirstRunSetupCommitResult result = await host.CompleteFirstRunSetupAsync(new(chosen, 2_097_152, 3))
            .WaitAsync(Timeout);

        Assert.True(result.Persisted);
        Assert.Equal("Synthetic activation failure after durable setup commit.", result.ActivationWarning);
        Assert.False(host.Config.FirstRunSetupPending);
        Assert.Equal(1, host.Config.FirstRunSetupVersion);
        Assert.Equal(chosen, host.Config.DownloadPath);
        Assert.Equal(2_097_152, host.Config.BandwidthLimiter);
        Assert.Equal(3, host.Config.MaxParallelDownloads);
        Assert.False(FirstRunSetupCoordinator.CanOffer(host.Config, host.State));

        Config saved = Config.LoadConfig(directory.Path);
        Assert.False(saved.FirstRunSetupPending);
        Assert.Equal(1, saved.FirstRunSetupVersion);
        Assert.Equal(chosen, saved.DownloadPath);
        Assert.Equal(2_097_152, saved.BandwidthLimiter);
        Assert.Equal(3, saved.MaxParallelDownloads);
    }

    [Fact]
    public async Task UnconfirmedCommitFailureDoesNotAdoptLaterDraftNotifications()
    {
        using TempDirectory directory = new();
        new Config { FirstRunSetupPending = true }.SaveConfig(directory.Path);
        Config config = Config.LoadConfig(directory.Path);
        FakeSession session = new(config) { Completion = _ => new(false) };
        var acceptedDraft = new FirstRunSetupRawDraft(Path.Combine(directory.Path, "accepted"), "2.5", "3");
        var lateDraft = new FirstRunSetupRawDraft(Path.Combine(directory.Path, "late"), "0", "999");
        FirstRunSetupDraftState state = new(acceptedDraft);

        FirstRunSetupRunResult result = await FirstRunSetupCoordinator.RunCoreAsync(session,
            async (_, accept, _) =>
            {
                Assert.True(FirstRunSetupCoordinator.TryParseLimits(acceptedDraft.Bandwidth, acceptedDraft.Parallel,
                    CultureInfo.InvariantCulture, out long bytes, out int parallel));
                FirstRunSetupValues accepted = new(acceptedDraft.Folder!, bytes, parallel);
                FirstRunSetupCommitResult commit = await accept(accepted);
                Assert.False(commit.Persisted);

                // A queued text/cancel notification can change the draft state afterwards,
                // but it must never mutate the exact values already sent to the host.
                Assert.True(state.Synchronize(lateDraft));
                Assert.Equal(lateDraft, state.Current);
                Assert.Equal(accepted, session.CapturedValues);
                return new(FirstRunSetupOutcome.Deferred);
            }).WaitAsync(Timeout);

        Assert.Equal(FirstRunSetupOutcome.Deferred, result.Outcome);
        Assert.Equal(1, session.Completions);
        Assert.True(config.FirstRunSetupPending);
        Assert.Equal(0, config.FirstRunSetupVersion);
        Assert.NotEqual(lateDraft.Folder, config.DownloadPath);
        Assert.Equal(new Config().BandwidthLimiter, config.BandwidthLimiter);
        Assert.Equal(new Config().MaxParallelDownloads, config.MaxParallelDownloads);
    }

    [Fact]
    public async Task DialogCancellationBeforeAcceptKeepsPendingSetupAndExactConfigIntact()
    {
        using TempDirectory directory = new();
        new Config { FirstRunSetupPending = true }.SaveConfig(directory.Path);
        Config config = Config.LoadConfig(directory.Path);
        Config before = config.Clone();
        FakeSession session = new(config);
        using CancellationTokenSource cancellation = new();

        FirstRunSetupRunResult result = await FirstRunSetupCoordinator.RunCoreAsync(session,
            (_, _, cancellationToken) =>
            {
                Assert.False(cancellationToken.IsCancellationRequested);
                cancellation.Cancel();
                return Task.FromResult(new FirstRunSetupRunResult(FirstRunSetupOutcome.Deferred));
            }, cancellation.Token).WaitAsync(Timeout);

        Assert.Equal(FirstRunSetupOutcome.Deferred, result.Outcome);
        Assert.Equal(0, session.Completions);
        Assert.True(config.FirstRunSetupPending);
        Assert.Equal(0, config.FirstRunSetupVersion);
        Assert.Equal(before.DownloadPath, config.DownloadPath);
        Assert.Equal(before.BandwidthLimiter, config.BandwidthLimiter);
        Assert.Equal(before.MaxParallelDownloads, config.MaxParallelDownloads);
        Assert.True(FirstRunSetupCoordinator.CanOffer(config, DesktopSessionState.Ready));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("The private first-run fixture condition was not reached.");
    }

    private sealed class ActivationFailureProvider : FourChanImageBoard
    {
        internal ActivationFailureProvider(HttpClient client) : base(client)
        {
            Thread = new(GetBoard(new Uri("https://boards.4chan.org/g/")),
                new Uri("https://boards.4chan.org/g/thread/123"));
        }
        internal ActivationFailureThread Thread { get; }

        public override IThread GetThread(Uri threadUri)
            => threadUri.AbsolutePath == "/g/thread/123" ? Thread : base.GetThread(threadUri);
    }

    private sealed class ActivationFailureThread(IBoard board, Uri uri) : FourChanThread(board, uri), IThread
    {
        internal volatile bool FailActivation;

        void IThread.UpdateMonitoringInterval(TimeSpan interval)
        {
            if (FailActivation) throw new InvalidOperationException("Synthetic activation failure after durable setup commit.");
            base.UpdateMonitoringInterval(interval);
        }
    }

    private sealed class ActivationFailureTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string json = request.RequestUri!.AbsolutePath switch
            {
                "/boards.json" => "{\"boards\":[{\"board\":\"g\",\"title\":\"Technology\"}]}",
                "/g/thread/123.json" => "{\"posts\":[{\"no\":123,\"sub\":\"Activation fixture\"}]}",
                _ => throw new InvalidOperationException("Unexpected fixture request: " + request.RequestUri)
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class FakeSession(Config config) : IFirstRunSetupSession
    {
        public DesktopSessionState State { get; set; } = DesktopSessionState.Ready;
        public Config Snapshot => config.Clone();
        public int Completions { get; private set; }
        public FirstRunSetupValues? CapturedValues { get; private set; }
        public Func<FirstRunSetupValues, FirstRunSetupCommitResult>? Completion { get; set; }

        public Task PersistPendingAsync()
        {
            config.FirstRunSetupPending = true;
            return Task.CompletedTask;
        }

        public Task<FirstRunSetupCommitResult> CompleteAsync(FirstRunSetupValues values)
        {
            CapturedValues = values;
            Completions++;
            return Task.FromResult(Completion?.Invoke(values) ?? new(false));
        }
    }
}
