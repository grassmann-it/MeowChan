using System.Net;
using System.Collections.Concurrent;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Http;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Http;
using MeowChan.PluginHost.Installation;
using MeowChan.PluginSdk;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeowChan.DesktopTests;

// Bounded, process-free probes/store/HTTP fixtures. Historical Windows upgrade and
// delivered-worker/session restore acceptance belong to the integration runner.
public sealed class PluginStartupIsolationTests
{
    [Theory]
    [InlineData("candidate-validation")]
    [InlineData("build")]
    [InlineData("import")]
    [InlineData("describe")]
    public async Task CandidateFailureKeepsLegacyWarningAndAccurateStructuredPhase(string phase)
    {
        using TempDirectory directory = new();
        var original = Candidate("broken", "old");
        var store = new PluginProfileStore(directory.Path);
        var state = await store.ApplyStartupAsync([original], 0);
        PluginActivationSnapshot previous = Assert.Single(state.Plugins);
        Capture sink = new();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var candidate = Entry(original, phase == "import" ? PluginCandidateKind.PrebuiltManifest : PluginCandidateKind.Source);
        if (phase == "candidate-validation") candidate = candidate with { Diagnostics = [new("fixture", "controlled failure")] };
        Exception failure = phase == "describe" ? new PluginWorkerException("worker_start", "controlled failure") : new IOException("controlled failure");
        int loads = 0, descriptions = 0;
        var observed = await PluginStartupDiagnostics.ObserveAsync(candidate, previous,
            _ => { loads++; return phase is "build" or "import" ? Task.FromException<PluginBuildResult>(failure) : Task.FromResult(Build(original)); },
            (_, _) => { descriptions++; return Task.FromException<PluginDescription>(failure); }, logger, CancellationToken.None);
        Assert.Equal(Path.GetFileName(candidate.Path) + ": controlled failure", observed.Problem);
        Assert.Equal(original.PluginId, observed.PluginId);
        Assert.Equal(candidate.CandidateId, observed.CandidateId);
        Assert.Equal(candidate.Path, observed.CandidatePath);
        Assert.Null(observed.Artifact);
        Assert.Null(observed.Description);
        Assert.Equal(phase == "candidate-validation" ? 0 : 1, loads);
        Assert.Equal(phase == "describe" ? 1 : 0, descriptions);
        var diagnostic = Assert.Single(sink.Events, item => item.Level == LogEventLevel.Warning);
        Assert.Equal(phase, Value(diagnostic, "PluginStartupPhase"));
        Assert.Equal(original.CandidateId, Value(diagnostic, "PluginStartupCandidateId"));
        Assert.Equal(original.PluginId, Value(diagnostic, "PluginStartupPluginId"));
        Assert.Equal(phase == "candidate-validation" ? typeof(InvalidDataException).FullName : failure.GetType().FullName,
            Value(diagnostic, "PluginStartupErrorType"));
        Assert.Equal(phase == "describe" ? "worker_start" : null, Value(diagnostic, "PluginStartupWorkerCode"));
        Assert.Equal("broken", Assert.IsType<ScalarValue>(Assert.Single(Assert.IsType<SequenceValue>(diagnostic.Properties["PluginStartupSourceIds"]).Elements)).Value);
        if (phase != "candidate-validation")
        {
            var measured = sink.Events.Last(item => item.Properties.ContainsKey("PluginStartupCompleted"));
            Assert.Equal(phase, Value(measured, "PluginStartupPhase"));
            Assert.Equal(false, Value(measured, "PluginStartupCompleted"));
        }
    }

    [Theory]
    [InlineData(PluginCandidateKind.Source, "build")]
    [InlineData(PluginCandidateKind.PrebuiltManifest, "import")]
    public async Task RejectedBuildResultIsNotLoggedAsSuccessfulAndUnknownIdentityIsNotInvented(PluginCandidateKind kind, string phase)
    {
        Capture sink = new();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var candidate = Entry(Candidate("unknown", "bad"), kind);
        var failed = new PluginBuildResult(PluginBuildStatus.IntegrityError, null, false, null, null, [new("integrity", "controlled rejection")]);
        var observed = await PluginStartupDiagnostics.ObserveAsync(candidate, null, _ => Task.FromResult(failed),
            (_, _) => throw new InvalidOperationException("Describe must not run"), logger, CancellationToken.None);
        Assert.Equal("unavailable-" + candidate.CandidateId, observed.PluginId);
        Assert.Equal(Path.GetFileName(candidate.Path) + ": controlled rejection", observed.Problem);
        var measured = Assert.Single(sink.Events, item => item.Properties.ContainsKey("PluginStartupCompleted"));
        Assert.Equal(phase, Value(measured, "PluginStartupPhase"));
        Assert.Equal(false, Value(measured, "PluginStartupCompleted"));
        var warning = Assert.Single(sink.Events, item => item.Level == LogEventLevel.Warning);
        Assert.Equal("IntegrityError", Value(warning, "PluginStartupBuildStatus"));
        Assert.Equal("integrity", Assert.IsType<ScalarValue>(Assert.Single(Assert.IsType<SequenceValue>(warning.Properties["PluginStartupDiagnosticCodes"]).Elements)).Value);
        Assert.Null(Value(warning, "PluginStartupPluginId"));
        Assert.Empty(Assert.IsType<SequenceValue>(warning.Properties["PluginStartupSourceIds"]).Elements);
    }

    [Fact]
    public async Task SuccessfulProbeRecordsBothPhasesWithoutAnAdvisoryOrPolicyChange()
    {
        var original = Candidate("healthy", "good");
        Capture sink = new();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var observed = await PluginStartupDiagnostics.ObserveAsync(Entry(original), null,
            _ => Task.FromResult(Build(original)), (_, _) => Task.FromResult(original.Description!), logger, CancellationToken.None);
        Assert.Equal(original, observed);
        Assert.Equal(new[] { "build", "describe" }, sink.Events.Select(item => Value(item, "PluginStartupPhase")));
        Assert.All(sink.Events, item => Assert.Equal(true, Value(item, "PluginStartupCompleted")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupCancellationPropagatesWithoutAnUnavailableObservation(bool duringDescribe)
    {
        using var token = new CancellationTokenSource();
        Capture sink = new();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var original = Candidate("cancel", "old");
        if (!duringDescribe) token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginStartupDiagnostics.ObserveAsync(Entry(original), null,
            _ => Task.FromResult(Build(original)), (_, ct) => { token.Cancel(); return Task.FromCanceled<PluginDescription>(ct); }, logger, token.Token));
        Assert.DoesNotContain(sink.Events, item => item.Level == LogEventLevel.Warning);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("describe")]
    public async Task UnavailableCandidateRetainsDeclarationsWithoutFallbackAndLeavesOtherSourceAndArchiveIntact(string fault)
    {
        using TempDirectory directory = new();
        var broken = Candidate("broken", "old");
        var healthy = Candidate("healthy", "good");
        var store = new PluginProfileStore(directory.Path);
        var state = await store.ApplyStartupAsync([broken, healthy], 0);
        state = await store.SelectCandidateAsync(broken.PluginId, broken.CandidateId, state.Revision);
        state = await store.SetGrantAsync(broken.PluginId, "filter", new("broken", "board"), FilterDataAccess.CandidateMetadata, state.Revision);
        var before = state;
        ResourceId id = new("broken", "board", "123");
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://broken.test/board/thread/123", "Local archive",
            ThreadSourceState.Active, DateTimeOffset.UtcNow, directory.Path, [new("123", 0, PostFieldPresence.All, Html: "retained content")]));
        Capture sink = new();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        var observations = new List<PluginCandidateObservation> { healthy };
        if (fault == "describe") observations.Add(await PluginStartupDiagnostics.ObserveAsync(Entry(broken), state.Plugins.Single(p => p.PluginId == broken.PluginId),
            _ => Task.FromResult(Build(broken)), (_, _) => Task.FromException<PluginDescription>(new PluginWorkerException("worker_start", "controlled describe failure")), logger, CancellationToken.None));
        // Repeat the same controlled inventory: retained declarations are still not execution consent.
        for (int restart = 0; restart < 2; restart++)
            state = await store.ApplyStartupAsync(observations, state.Revision);
        var unavailable = state.Plugins.Single(p => p.PluginId == broken.PluginId);
        Assert.Null(unavailable.Active);
        Assert.Null(unavailable.Pinned);
        Assert.NotNull(unavailable.Problem);
        Assert.Equal(SdkJson.Serialize(broken.Description!), SdkJson.Serialize(unavailable.LastGood!.Description));
        Assert.Equal("old", unavailable.LastGood.ExecutionRevision);
        Assert.Equal(broken.CandidateId, unavailable.SelectedCandidateId);
        Assert.Equal(before.SelectedSourceOwners, state.SelectedSourceOwners);
        Assert.Equal(before.Grants, state.Grants);
        Assert.Equal("good", state.Plugins.Single(p => p.PluginId == healthy.PluginId).Active!.ExecutionRevision);
        PluginStartupDiagnostics.LogActivationProblem(logger, unavailable);
        var activation = Assert.Single(sink.Events, item => item.Properties.ContainsKey("PluginStartupActivationAvailable"));
        Assert.Equal("activation", Value(activation, "PluginStartupPhase"));
        Assert.Equal(false, Value(activation, "PluginStartupActivationAvailable"));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
        var archived = await reader.ReadThreadAsync(id);
        Assert.Equal("Local archive", archived!.Summary.Title);
        Assert.Equal("retained content", Assert.Single(archived.GetPosts()).Html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleCandidateAdvisoryReportsActivationAvailabilityWithoutChangingExplicitEnableChoice(bool enabled)
    {
        using TempDirectory directory = new();
        var first = Candidate("healthy", "first");
        var other = first with { CandidateId = "other.cs", Artifact = first.Artifact! with { ExecutionRevision = "other" } };
        var store = new PluginProfileStore(directory.Path);
        var state = await store.ApplyStartupAsync([first], 0);
        state = await store.SetEnabledAsync(first.PluginId, enabled, state.Revision);
        state = await store.ApplyStartupAsync([first, other], state.Revision);
        var selected = Assert.Single(state.Plugins);
        Assert.NotNull(selected.Active);
        Assert.Equal("Multiple candidates exist; the selected candidate is retained.", selected.Problem);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        Capture sink = new();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        PluginStartupDiagnostics.LogActivationProblem(logger, selected);
        var advisory = Assert.Single(sink.Events);
        Assert.Equal("activation", Value(advisory, "PluginStartupPhase"));
        Assert.Equal(enabled, Value(advisory, "PluginStartupActivationAvailable"));
        Assert.Equal(selected.Problem, Value(advisory, "PluginStartupProblem"));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
    }

    [Theory]
    [InlineData("network")]
    [InlineData("timeout")]
    [InlineData("io")]
    public async Task MetadataFailureAfterStartupDoesNotBecomeAnActivationFailureOrAffectOtherSource(string fault)
    {
        using TempDirectory directory = new();
        var broken = Candidate("broken", "old");
        var healthy = Candidate("healthy", "good");
        var store = new PluginProfileStore(directory.Path);
        var state = await store.ApplyStartupAsync([broken, healthy], 0);
        byte[] before = await File.ReadAllBytesAsync(store.StorePath);
        using var handler = new TerminalTransport(fault);
        using HttpClient transport = new(handler);
        using var http = new PluginHostHttp(transport, new RequestPolicyState(), [broken.Description!.Sources[0], healthy.Description!.Sources[0]]);
        var result = await http.SendAsync(new("broken", HostHttpMethod.Get, new("https://broken.test/catalog")));
        if (fault == "io") Assert.Equal("http-network", Assert.IsType<HostHttpFailure>(result).Failure.Code);
        else
        {
            // The request policy turns replayable network/timeout failures into an explicit
            // cooldown, not a plugin activation error or a hidden transport retry loop.
            var deferred = Assert.IsType<HostHttpDeferred>(result);
            Assert.Equal("cooldown", deferred.ReasonCode);
            Assert.True(deferred.RetryAt > DateTimeOffset.UtcNow);
        }
        var success = Assert.IsType<HostHttpResponse>(await http.SendAsync(new("healthy", HostHttpMethod.Get, new("https://healthy.test/catalog"))));
        Assert.Equal(200, success.StatusCode);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.StorePath));
        Assert.All((await store.ReadAsync()).Plugins, p => { Assert.NotNull(p.Active); Assert.Null(p.Problem); });
        Assert.Empty(state.Grants);
    }

    [Fact]
    public async Task CorruptPluginProfileRemainsFatalToDesktopRecoveryWithoutResetOrNetwork()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "plugin-state", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] corrupt = "not a valid plugin profile"u8.ToArray();
        await File.WriteAllBytesAsync(path, corrupt);
        using var handler = new TerminalTransport("network");
        Capture sink = new();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        await using DesktopSessionHost host = new(directory.Path, logger: logger, pluginTransportFactory: () => new HttpClient(handler, disposeHandler: false));
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DesktopSessionState.RecoveryFailed, host.State);
        Assert.NotNull(host.StartupError);
        Assert.Contains(sink.Events, item => item.Exception is PluginProfileCorruptException);
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, handler.Calls);
        Assert.Empty(host.Plugins!.Sources);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.AddAsync("https://healthy.test/board/thread/123"));
        var profileRead = Assert.Single(sink.Events, item => item.Properties.TryGetValue("PluginStartupPhase", out var value) && value is ScalarValue { Value: "profile-read" });
        Assert.Equal(false, Value(profileRead, "PluginStartupCompleted"));
        await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, handler.Calls);
    }

    private static object? Value(LogEvent item, string key) => Assert.IsType<ScalarValue>(item.Properties[key]).Value;
    private sealed class Capture : ILogEventSink
    {
        internal ConcurrentQueue<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Enqueue(logEvent);
    }
    private sealed class TerminalTransport(string fault) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.RequestUri!.Host == "broken.test")
                return Task.FromException<HttpResponseMessage>(fault == "timeout" ? new TimeoutException("controlled timeout") : fault == "io" ? new IOException("controlled I/O failure") : new HttpRequestException("controlled offline"));
            Assert.Equal("healthy.test", request.RequestUri.Host);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent("[]") });
        }
    }
    private static PluginBuildResult Build(PluginCandidateObservation observation) => new(PluginBuildStatus.Success, observation.Artifact, false, "source", "build", []);
    private static PluginInstallationCandidate Entry(PluginCandidateObservation observation, PluginCandidateKind kind = PluginCandidateKind.Source) =>
        new(observation.CandidateId, observation.CandidatePath!, kind, 100, []);
    private static PluginCandidateObservation Candidate(string sourceId, string revision)
    {
        SettingsSchema empty = new("1", []);
        SourceDescription source = new(sourceId, sourceId, new("https://" + sourceId + ".test"), SourceCapabilities.ThreadRead,
            [new(SourceResourceKind.Thread, sourceId + ".test", "/{board}/thread/{thread}", IsCanonical: true)],
            [new(sourceId + ".test", HttpHostPurpose.Metadata)], empty);
        PluginDescription description = new(sourceId + ".plugin", sourceId, "fixture", SdkContract.Version, "Synthetic startup fixture", [source],
            [new("filter", "filter", "fixture", FilterDataAccess.CandidateMetadata, empty, ["image/png"])], empty, []);
        PluginArtifact artifact = new("code-" + revision, revision, "/fixture/" + revision + ".dll", new string('a', 64), "sdk", "compiler", []);
        return new(sourceId + ".cs", description.PluginId, artifact, description, CandidatePath: "/fixture/" + sourceId + ".cs");
    }
}
