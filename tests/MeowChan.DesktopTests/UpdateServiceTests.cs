using System.Net;
using System.Text;
using System.Text.Json;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("1.1.0", UpdateCheckStatus.Available)]
    [InlineData("v1.0.0", UpdateCheckStatus.UpToDate)]
    [InlineData("0.9.9", UpdateCheckStatus.UpToDate)]
    public async Task StableComparisonAndRequestContainOnlyTheProductUserAgent(string tag, UpdateCheckStatus expected)
    {
        using Fixture fixture = new(Release(tag));
        UpdateCheckResult result = await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(expected, result.Status);
        Assert.Equal("https://api.github.com/repos/grassmann-it/MeowChan/releases/latest", fixture.Handler.Url);
        Assert.Equal(new[] { "User-Agent" }, fixture.Handler.Headers.Keys);
        Assert.Equal("MeowChan/1.0.0", fixture.Handler.Headers["User-Agent"]);
        Assert.Equal(fixture.Time.GetUtcNow(), fixture.Store.Load().LastCheckedUtc);
    }

    [Fact]
    public async Task PrereleaseEndpointChoosesHighestSemverAndIgnoresDrafts()
    {
        string releases = "[" + string.Join(',', Release("1.1.0-beta.10", prerelease: true),
            Release("1.1.0-beta.2", prerelease: true), Release("1.0.9"), Release("9.0.0", draft: true)) + "]";
        using Fixture fixture = new(releases, new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0"), IncludePrereleases = true });
        UpdateCheckResult result = await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal("1.1.0-beta.10", result.Update?.Version.ToString());
        Assert.Equal("https://api.github.com/repos/grassmann-it/MeowChan/releases?per_page=100", fixture.Handler.Url);
    }

    [Theory]
    [InlineData("not-json", UpdateCheckError.InvalidResponse)]
    [InlineData("{}", UpdateCheckError.InvalidResponse)]
    [InlineData("[]", UpdateCheckError.InvalidResponse)]
    [InlineData("{\"tag_name\":\"2.0.0\",\"draft\":false,\"prerelease\":false,\"assets\":{}}", UpdateCheckError.InvalidResponse)]
    public async Task InvalidResponsesAreTypedAndNeverLeakRawServerText(string body, UpdateCheckError expected)
    {
        using Fixture fixture = new(body);
        UpdateCheckResult result = await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(expected, result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UpdateCheckError.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateCheckError.RateLimited)]
    [InlineData(HttpStatusCode.MovedPermanently, UpdateCheckError.Http)]
    [InlineData(HttpStatusCode.Redirect, UpdateCheckError.Http)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateCheckError.Http)]
    public async Task HttpFailuresAreTypedAndAlsoThrottleAutomaticRetries(HttpStatusCode status, UpdateCheckError expected)
    {
        using Fixture fixture = new(Release("2.0.0"));
        fixture.Handler.Status = status;
        Assert.Equal(expected, (await fixture.Service.CheckAsync(UpdateCheckKind.Automatic)).Error);
        Assert.Equal(UpdateCheckStatus.Throttled, (await fixture.Service.CheckAsync(UpdateCheckKind.Automatic)).Status);
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"tag_name\":\"2.0.0\",\"draft\":\"false\",\"prerelease\":false}]")]
    [InlineData("[{\"tag_name\":2,\"draft\":false,\"prerelease\":false}]")]
    public async Task MalformedPrereleaseEntriesFailAndPreserveThePreviousFoundRelease(string malformed)
    {
        using Fixture fixture = new("[" + Release("2.0.0") + "]", new UpdateServiceOptions
            { CurrentVersion = SemanticVersion.Parse("1.0.0"), IncludePrereleases = true });
        Assert.Equal(UpdateCheckStatus.Available, (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Status);
        fixture.Handler.Body = malformed;
        Assert.Equal(UpdateCheckError.InvalidResponse, (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Error);
        Assert.Equal("2.0.0", fixture.Store.Load().FoundUpdate?.Version.ToString());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"tag_name\":\"9.0.0\",\"draft\":true,\"prerelease\":false}]")]
    [InlineData("[{\"tag_name\":\"nightly\",\"draft\":false,\"prerelease\":true}]")]
    public async Task ValidEmptyOrExcludedReleaseListsAreUpToDate(string releases)
    {
        using Fixture fixture = new(releases, new UpdateServiceOptions
            { CurrentVersion = SemanticVersion.Parse("1.0.0"), IncludePrereleases = true });
        Assert.Equal(UpdateCheckStatus.UpToDate, (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Status);
    }

    [Fact]
    public async Task AutomaticIntervalSurvivesRestartAndAllowsExactlyTwentyFourHours()
    {
        using Fixture fixture = new(Release("2.0.0"));
        Assert.Equal(UpdateCheckStatus.Available, (await fixture.Service.CheckAsync(UpdateCheckKind.Automatic)).Status);
        UpdateService restarted = fixture.NewService();
        fixture.Time.Advance(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        Assert.Equal(UpdateCheckStatus.Throttled, (await restarted.CheckAsync(UpdateCheckKind.Automatic)).Status);
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(UpdateCheckStatus.Available, (await restarted.CheckAsync(UpdateCheckKind.Automatic)).Status);
        Assert.Equal(2, fixture.Handler.Requests);
        Assert.Equal(UpdateCheckStatus.Available, (await restarted.CheckAsync(UpdateCheckKind.Manual)).Status);
        Assert.Equal(3, fixture.Handler.Requests);
    }

    [Fact]
    public async Task SkipPersistsAndSuppressesOnlyAutomaticAnnouncementsOfThatExactVersion()
    {
        using Fixture fixture = new(Release("2.0.0"));
        await fixture.Service.SkipVersionAsync(SemanticVersion.Parse("2.0.0"));
        Assert.Equal(UpdateCheckStatus.Skipped, (await fixture.NewService().CheckAsync(UpdateCheckKind.Automatic)).Status);
        Assert.Equal(UpdateCheckStatus.Available, (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Status);
        fixture.Time.Advance(TimeSpan.FromHours(24));
        fixture.Handler.Body = Release("2.0.1");
        Assert.Equal(UpdateCheckStatus.Available, (await fixture.Service.CheckAsync(UpdateCheckKind.Automatic)).Status);
        Assert.Equal("2.0.0", fixture.Store.Load().SkippedVersion?.ToString());
        Assert.Equal("2.0.1", fixture.Store.Load().FoundUpdate?.Version.ToString());
    }

    [Fact]
    public async Task EnvironmentDisablesManualAndAutomaticChecksWithoutWritingState()
    {
        using Fixture fixture = new(Release("2.0.0"));
        fixture.Disable = "1";
        foreach (UpdateCheckKind kind in Enum.GetValues<UpdateCheckKind>())
        {
            UpdateCheckResult result = await fixture.Service.CheckAsync(kind);
            Assert.Equal(UpdateCheckStatus.Disabled, result.Status);
            Assert.Equal(UpdateDisabledReason.Environment, result.DisabledReason);
        }
        Assert.Equal(UpdateCheckStatus.Disabled, (await fixture.Service.CheckAfterStartupAsync()).Status);
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.False(File.Exists(fixture.Store.FilePath));
    }

    [Fact]
    public async Task AutomaticPreferenceDoesNotDisableManualCheck()
    {
        using Fixture fixture = new(Release("2.0.0"), new UpdateServiceOptions
            { CurrentVersion = SemanticVersion.Parse("1.0.0"), AutomaticChecksEnabled = false });
        Assert.Equal(UpdateDisabledReason.AutomaticChecksOff, (await fixture.Service.CheckAsync(UpdateCheckKind.Automatic)).DisabledReason);
        Assert.Equal(UpdateCheckStatus.Available, (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Status);
    }

    [Fact]
    public async Task StartupDelayUsesInjectedClockAndCanBeCancelled()
    {
        using Fixture fixture = new(Release("2.0.0"));
        Task<UpdateCheckResult> pending = fixture.Service.CheckAfterStartupAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, fixture.Handler.Requests);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(UpdateCheckStatus.Available, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        using CancellationTokenSource cancellation = new();
        Task<UpdateCheckResult> cancelled = fixture.Service.CheckAfterStartupAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task RequestTimeoutUsesInjectedClockWhileCallerCancellationPropagates()
    {
        using Fixture fixture = new(Release("2.0.0"));
        fixture.Handler.Block = true;
        Task<UpdateCheckResult> pending = fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Time.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(UpdateCheckError.Timeout, (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Error);
        using CancellationTokenSource cancellation = new();
        Task<UpdateCheckResult> cancelled = fixture.Service.CheckAsync(UpdateCheckKind.Manual, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task ConcurrentChecksCoalesceAndConcurrentSkipCannotLoseItsUpdate()
    {
        using Fixture fixture = new(Release("2.0.0"));
        fixture.Handler.Block = true;
        Task<UpdateCheckResult> first = fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        await fixture.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<UpdateCheckResult> second = fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Task skip = fixture.Service.SkipVersionAsync(SemanticVersion.Parse("2.0.0"));
        fixture.Handler.Release.TrySetResult();
        Assert.Equal(UpdateCheckStatus.Available, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        Assert.Equal(UpdateCheckStatus.Available, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        await skip.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Equal("2.0.0", fixture.Store.Load().SkippedVersion?.ToString());
        Assert.Equal("2.0.0", fixture.Store.Load().FoundUpdate?.Version.ToString());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":999}")]
    [InlineData("{\"SchemaVersion\":1,\"SkippedVersion\":\"invalid\"}")]
    [InlineData("{\"SchemaVersion\":1,\"FoundVersion\":\"2.0.0\"}")]
    [InlineData("{\"SchemaVersion\":1,\"ReleaseUrl\":\"https://evil.invalid/\"}")]
    [InlineData("{\"SchemaVersion\":1,\"FoundVersion\":\"2.0.0\",\"ReleaseUrl\":\"https://evil.invalid/\",\"DownloadUrl\":\"https://evil.invalid/\"}")]
    public async Task CorruptStateIsPreservedAndReportedWithoutAnyRequest(string contents)
    {
        using Fixture fixture = new(Release("2.0.0"));
        File.WriteAllText(fixture.Store.FilePath, contents);
        UpdateCheckResult result = await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal(UpdateCheckError.Persistence, result.Error);
        Assert.Equal(contents, File.ReadAllText(fixture.Store.FilePath));
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Fact]
    public async Task AmbientAuthorizationIsNeverSent()
    {
        using Fixture fixture = new(Release("2.0.0"));
        fixture.Client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "fixture");
        Assert.Equal(UpdateCheckError.InvalidResponse, (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Error);
        Assert.Equal(0, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResponseLimitAppliesWithAndWithoutContentLength(bool lengthKnown)
    {
        using Fixture fixture = new(new string('x', 2048), new UpdateServiceOptions
            { CurrentVersion = SemanticVersion.Parse("1.0.0"), MaximumResponseBytes = 128 });
        fixture.Handler.LengthKnown = lengthKnown;
        Assert.Equal(UpdateCheckError.InvalidResponse, (await fixture.Service.CheckAsync(UpdateCheckKind.Manual)).Error);
    }

    [Fact]
    public async Task FailedCheckPreservesPreviouslyFoundReleaseAndProfileStoresAreIndependent()
    {
        using Fixture fixture = new(Release("2.0.0"));
        await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        fixture.Handler.Status = HttpStatusCode.Forbidden;
        await fixture.Service.CheckAsync(UpdateCheckKind.Manual);
        Assert.Equal("2.0.0", fixture.Store.Load().FoundUpdate?.Version.ToString());
        UpdateStateStore other = new(Path.Combine(fixture.DirectoryPath, "other-profile"));
        Assert.Null(other.Load().LastCheckedUtc);
        Assert.False(Directory.Exists(Path.GetDirectoryName(other.FilePath)));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    internal static string Release(string tag, bool prerelease = false, bool draft = false, object[]? assets = null) =>
        JsonSerializer.Serialize(new { tag_name = tag, prerelease, draft, html_url = "https://evil.invalid/", assets = assets ?? [] });

    internal sealed class Fixture : IDisposable
    {
        internal string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "meowchan-update-" + Guid.NewGuid().ToString("N"));
        internal FakeTime Time { get; } = new();
        internal FakeHandler Handler { get; }
        internal HttpClient Client { get; }
        internal UpdateStateStore Store { get; }
        internal UpdateService Service { get; }
        internal string? Disable { get; set; }
        private readonly UpdateServiceOptions _options;
        internal Fixture(string body, UpdateServiceOptions? options = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            Handler = new FakeHandler { Body = body };
            Client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Store = new UpdateStateStore(DirectoryPath);
            _options = options ?? new UpdateServiceOptions { CurrentVersion = SemanticVersion.Parse("1.0.0"), RuntimeIdentifier = "linux-x64" };
            Service = NewService();
        }
        internal UpdateService NewService() => new(Client, Store, _options, Time, () => Disable, (_, _) => { });
        public void Dispose() { Client.Dispose(); Directory.Delete(DirectoryPath, true); }
    }

    internal sealed class FakeHandler : HttpMessageHandler
    {
        internal string Body { get; set; } = "";
        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        internal int Requests { get; private set; }
        internal string? Url { get; private set; }
        internal Dictionary<string, string> Headers { get; private set; } = [];
        internal bool Block { get; set; }
        internal bool LengthKnown { get; set; } = true;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Url = request.RequestUri?.AbsoluteUri;
            Headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(' ', header.Value));
            Started.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            HttpContent content = LengthKnown ? new StringContent(Body) : new UnknownLengthContent(Body);
            return new HttpResponseMessage(Status) { Content = content };
        }
    }

    private sealed class UnknownLengthContent(string body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    internal sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        private readonly List<FakeTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _now.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            FakeTimer timer = new(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        internal void Advance(TimeSpan duration)
        {
            _now += duration;
            foreach (FakeTimer timer in _timers.ToArray()) timer.FireIfDue();
        }
        private sealed class FakeTimer(FakeTime owner, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                _period = period;
                return true;
            }
            internal void FireIfDue()
            {
                if (_due is null || _due > owner._now) return;
                _due = _period == Timeout.InfiniteTimeSpan ? null : owner._now + _period;
                callback(state);
            }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
