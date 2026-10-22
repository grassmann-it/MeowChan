using System.Net;
using System.Text;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

public sealed class UpdateSessionTests
{
    [Fact]
    public async Task StartupDelayDailyScheduleAndShutdownUseTheSameProfileChecker()
    {
        await using Fixture fixture = new();
        fixture.Session.Start();
        fixture.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, fixture.Handler.Requests);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await UntilAsync(() => fixture.Session.View.Update != null);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Null(fixture.Session.View.ManualResult);
        fixture.Time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(1, fixture.Handler.Requests);
        fixture.Time.Advance(TimeSpan.FromHours(1));
        await UntilAsync(() => fixture.Handler.Requests == 2);
        await fixture.Session.DisposeAsync();
        fixture.Time.Advance(TimeSpan.FromDays(10));
        Assert.Equal(2, fixture.Handler.Requests);
        Assert.True(fixture.Handler.Disposed);
    }

    [Fact]
    public async Task ManualClicksWhileAutomaticHttpIsPendingDoNotQueueDuplicateRequests()
    {
        await using Fixture fixture = new();
        fixture.Handler.ResponseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task automatic = fixture.Session.CheckAsync(UpdateCheckKind.Automatic);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Task manual = fixture.Session.CheckAsync();
        Assert.True(fixture.Session.View.IsChecking);
        await fixture.Session.CheckAsync();
        Assert.Equal(1, fixture.Handler.Requests);
        fixture.Handler.ResponseGate.SetResult();
        await Task.WhenAll(automatic, manual);
        Assert.Equal(2, fixture.Handler.Requests);
        Assert.False(fixture.Session.View.IsChecking);
        Assert.Equal(UpdateCheckStatus.Available, fixture.Session.View.ManualResult?.Status);
    }

    [Fact]
    public async Task SkipPersistsAcrossRestartButManualCheckCanRevealItAgain()
    {
        await using Fixture fixture = new();
        await fixture.Session.CheckAsync();
        await fixture.Session.SkipAsync();
        Assert.Null(fixture.Session.View.Update);
        Assert.Equal("2.0.0", new UpdateStateStore(fixture.Profile).Load().SkippedVersion?.ToString());
        await fixture.Session.DisposeAsync();
        await using UpdateSession restarted = new(fixture.Profile, new Config(), new HttpClient(new Handler()),
            fixture.Time, () => null, SemanticVersion.Parse("1.0.0"));
        restarted.Start();
        Assert.Null(restarted.View.Update);
        await restarted.CheckAsync();
        Assert.Equal("2.0.0", restarted.View.Update?.Version.ToString());
        Assert.Equal(UpdateCheckStatus.Available, restarted.View.ManualResult?.Status);
    }

    [Fact]
    public async Task LivePreferencesSelectPrereleaseEndpointAndFilterCachedHintsWithoutAnotherRequest()
    {
        await using Fixture fixture = new();
        fixture.Handler.Body = "[" + Release("2.1.0-beta.1", true) + "]";
        await fixture.Session.ConfigureAsync(new Config { IncludePrereleaseUpdates = true });
        await fixture.Session.CheckAsync();
        Assert.EndsWith("/releases?per_page=100", fixture.Handler.Url);
        Assert.Equal("2.1.0-beta.1", fixture.Session.View.Update?.Version.ToString());
        await fixture.Session.ConfigureAsync(new Config { IncludePrereleaseUpdates = false, AutomaticUpdateChecks = false });
        Assert.Null(fixture.Session.View.Update);
        await fixture.Session.CheckAsync(UpdateCheckKind.Automatic);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Null(fixture.Session.View.ManualResult);
    }

    [Fact]
    public async Task AutomaticFailureRemainsSilentWhileManualFailureIsAvailableForLocalization()
    {
        await using Fixture fixture = new();
        fixture.Handler.Status = HttpStatusCode.Forbidden;
        await fixture.Session.CheckAsync(UpdateCheckKind.Automatic);
        Assert.Null(fixture.Session.View.ManualResult);
        await fixture.Session.CheckAsync();
        Assert.Equal(UpdateCheckError.RateLimited, fixture.Session.View.ManualResult?.Error);
        Assert.False(fixture.Session.View.IsChecking);
    }

    [Fact]
    public async Task DistributionOverrideSuppressesCachedNoticeAndBothRequestKinds()
    {
        await using Fixture fixture = new();
        await fixture.Session.CheckAsync();
        fixture.Disabled = "1";
        await fixture.Session.ConfigureAsync(new Config());
        Assert.True(fixture.Session.View.EnvironmentDisabled);
        Assert.Null(fixture.Session.View.Update);
        await fixture.Session.CheckAsync();
        await fixture.Session.CheckAsync(UpdateCheckKind.Automatic);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Equal(UpdateDisabledReason.Environment, fixture.Session.View.ManualResult?.DisabledReason);
    }

    [Fact]
    public async Task SessionStopCancelsPendingHttpBeforeReleasingProfileLease()
    {
        string path = NewProfile();
        Handler handler = new() { Block = true };
        try
        {
            await using DesktopSessionHost host = new(path, [], updateSessionFactory: (profile, config) =>
                new UpdateSession(profile, config, new HttpClient(handler), disableEnvironment: () => null));
            await host.StartAsync();
            Task check = host.Updates.CheckAsync();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
            Assert.True(handler.Cancelled);
            Assert.True(handler.Disposed);
            using MeowChan.Persistence.ProfileLease lease = MeowChan.Persistence.ProfileLease.Acquire(path);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void ConfigDefaultsRoundTripAndSettingsMergeKeepDirectEditorChanges()
    {
        string path = NewProfile();
        try
        {
            Config live = Config.LoadConfig(path);
            Assert.True(live.AutomaticUpdateChecks);
            Assert.False(live.IncludePrereleaseUpdates);
            Assert.Equal(1, live.MinimumFreeSpaceGiB);
            Config draft = live.Clone();
            draft.AutomaticUpdateChecks = false;
            draft.IncludePrereleaseUpdates = true;
            draft.MinimumFreeSpaceGiB = 3;
            draft.PluginMaximumProductionWorkers = 2;
            live.PluginEditorExecutable = "/fixture/editor";
            live.PluginEditorArguments = ["--line", "{line}", "{file}"];
            live.CopySettingsFieldsFrom(draft);
            live.SaveConfig();
            Config loaded = Config.LoadConfig(path);
            Assert.False(loaded.AutomaticUpdateChecks);
            Assert.True(loaded.IncludePrereleaseUpdates);
            Assert.Equal(3, loaded.MinimumFreeSpaceGiB);
            Assert.Equal(2, loaded.PluginMaximumProductionWorkers);
            Assert.Equal(live.PluginEditorArguments, loaded.PluginEditorArguments);
            Assert.Equal(live.PluginEditorExecutable, loaded.PluginEditorExecutable);
            Config cloned = loaded.Clone();
            cloned.PluginEditorArguments[0] = "changed";
            Assert.Equal("--line", loaded.PluginEditorArguments[0]);
        }
        finally { Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData(-1, 4, 5, 256, 512)]
    [InlineData(1, 9, 5, 256, 512)]
    [InlineData(1, 4, 0, 256, 512)]
    [InlineData(1, 4, 5, 127, 512)]
    [InlineData(1, 4, 5, 256, 2049)]
    public void InvalidBudgetsCannotBePersisted(int reserve, int workers, int idle, int source, int filter)
    {
        Config config = new() { MinimumFreeSpaceGiB = reserve, PluginMaximumProductionWorkers = workers,
            PluginWorkerIdleMinutes = idle, PluginSourceHeapMiB = source, PluginFilterHeapMiB = filter };
        Assert.Throws<ArgumentOutOfRangeException>(config.Validate);
    }

    private static string NewProfile() => Path.Combine(Path.GetTempPath(), "meowchan-update-session-" + Guid.NewGuid().ToString("N"));
    private static string Release(string version, bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, html_url = "https://github.com/tfgrass/MeowChan/releases/tag/v" + version,
        draft = false, prerelease, assets = Array.Empty<object>()
    });
    private static async Task UntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
        await Task.Yield();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Profile { get; } = NewProfile();
        internal Handler Handler { get; } = new();
        internal UpdateServiceTests.FakeTime Time { get; } = new();
        internal string? Disabled;
        internal UpdateSession Session { get; }
        internal Fixture() => Session = new(Profile, new Config(), new HttpClient(Handler), Time,
            () => Disabled, SemanticVersion.Parse("1.0.0"));
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            if (Directory.Exists(Profile)) Directory.Delete(Profile, true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Requests;
        internal string Body = Release("2.0.0");
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal string? Url;
        internal bool Block;
        internal bool Cancelled;
        internal bool Disposed;
        internal TaskCompletionSource? ResponseGate;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Url = request.RequestUri!.AbsoluteUri;
            Entered.TrySetResult();
            if (ResponseGate != null) await ResponseGate.Task.WaitAsync(cancellationToken);
            if (Block)
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            return new(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
