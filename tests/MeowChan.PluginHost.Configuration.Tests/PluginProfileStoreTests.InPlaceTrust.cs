using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeowChan.PluginHost.Configuration.Tests;

/// <summary>
/// Decision #130 (16.11.2026): an in-place update of a bundled plugin from the app's own plugins folder (same shipped
/// manifest path, verified current delivery proof, an already established release) carries the plugin's existing rights
/// over and accepts a declaration change that only narrows. Synthetic delivered fixtures under a temporary profile only.
/// </summary>
public sealed partial class PluginProfileStoreTests
{
    [Fact]
    public async Task EndchanLikeMismatchExceptionSurvivesRepeatedInPlaceExecutableUpdatesWithoutReview()
    {
        CapturedLog log = new();
        var old = await Delivered("endchan-app", "one", source: true);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants), owners = SdkJson.Serialize(state.SelectedSourceOwners);
        byte[] configuration = SdkJson.Serialize(Active(state).Configuration);
        string previous = old.Candidate.Artifact!.ExecutionRevision;
        foreach (string build in new[] { "two", "three" })
        {
            var next = await Delivered("endchan-app", build, source: true);
            Assert.Equal(old.Candidate.CandidateId, next.Candidate.CandidateId);
            for (int restart = 0; restart < 2; restart++)
            {
                state = await LoggedStore(log).ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
                var plugin = Assert.Single(state.Plugins);
                Assert.Equal(next.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
                Assert.Equal(previous, plugin.LastGood!.ExecutionRevision);
                Assert.Null(plugin.Problem); Assert.Null(plugin.Pending); Assert.Null(plugin.Pinned);
                Assert.Equal(old.Candidate.CandidateId, plugin.SelectedCandidateId);
                Assert.Equal(configuration, SdkJson.Serialize(Active(state).Configuration));
                Assert.Equal(grants, SdkJson.Serialize(state.Grants));
                Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
                Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
            }
            previous = next.Candidate.Artifact!.ExecutionRevision;
        }
        // One Information line per carried-over update (two builds), none for the unchanged restarts.
        Assert.Equal(2, log.CarryOvers.Count);
        Assert.All(log.CarryOvers, entry =>
        {
            string message = entry.RenderMessage();
            Assert.Contains("test.plugin", message, StringComparison.Ordinal);
            Assert.Contains("1 filter grant(s)", message, StringComparison.Ordinal);
            Assert.Contains("size/MD5 exception(s) for shared", message, StringComparison.Ordinal);
            Assert.Contains("without a repair review", message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("display")]
    [InlineData("source-display")]
    [InlineData("narrowed")]
    public async Task VichanLikeDescriptionChangeWithoutWideningUpdatesSilently(string change)
    {
        CapturedLog log = new();
        var old = await Delivered("vichan-app", "one", source: true);
        if (change == "narrowed")
            old = await Redeclared(old, d => d with
            {
                Sources = [d.Sources[0] with
                {
                    Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture,
                    Routes = [.. d.Sources[0].Routes, new(SourceResourceKind.Thread, "fixture.test", "/{board}/res/{thread}.html")],
                    HttpHosts = [new("fixture.test", HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia),
                        new("media.fixture.test", HttpHostPurpose.OriginalMedia)]
                }],
                Filters = [d.Filters[0] with { SupportedMimeTypes = ["image/png", "image/jpeg"] },
                    d.Filters[0] with { Key = "second", Name = "Second filter" }]
            });
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        var next = await Delivered("vichan-app", "two", source: true);
        next = await Redeclared(next, d => change switch
        {
            "display" => d with
            {
                Name = "Synthetic plugin family", PluginVersion = "1.1", Description = "Reads several sites of one engine.",
                Author = "MeowChan", DocumentationUrl = new("https://docs.fixture.test/family"),
                Information = [new("support", "Accepted links", InformationKind.Text, "Accepts links from fixture.test.", SourceId: "shared")]
            },
            "source-display" => d with { Sources = [d.Sources[0] with { Name = "Renamed site", Maturity = SourceMaturity.Experimental }] },
            // Back to the base declaration: one route, one metadata host, one filter for PNG only, fewer capabilities.
            _ => d
        });
        for (int restart = 0; restart < 2; restart++)
        {
            state = await LoggedStore(log).ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
            Assert.Equal(next.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal(SdkJson.Serialize(next.Candidate.Description!), SdkJson.Serialize(Active(state).Description));
            Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Null(state.Plugins[0].Problem);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
        }
        Assert.Contains("a narrowing-only description change", Assert.Single(log.CarryOvers).RenderMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("rename-and-host")]
    [InlineData("rename-and-capability")]
    [InlineData("changed-route-option")]
    public async Task DescriptionAddingAHostOrAnyOtherWideningNeedsReview(string change)
    {
        CapturedLog log = new();
        var old = await Delivered("widening-app", "one", source: true);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        var next = await Delivered("widening-app", "two", source: true);
        next = await Redeclared(next, d => change switch
        {
            "host" => d with { Sources = [d.Sources[0] with { HttpHosts = [.. d.Sources[0].HttpHosts, new("cdn.fixture.test", HttpHostPurpose.OriginalMedia)] }] },
            "rename-and-host" => d with { Name = "Renamed", Sources = [d.Sources[0] with { HttpHosts = [.. d.Sources[0].HttpHosts, new("cdn.fixture.test", HttpHostPurpose.Metadata)] }] },
            "rename-and-capability" => d with { Name = "Renamed", Sources = [d.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.Previews }] },
            // Same host and template, one changed option: not an exact former route, so never treated as narrowing.
            _ => d with { Sources = [d.Sources[0] with { Routes = [d.Sources[0].Routes[0] with { ThreadIdentifierKind = RouteIdentifierKind.Decimal }] }] }
        });
        for (int restart = 0; restart < 2; restart++)
        {
            state = await LoggedStore(log).ApplyBundledStartupAsync([next.Candidate], [next.Proof], ["shared"], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Equal(old.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Equal(PluginActivationProblems.ExecutableChanged, state.Plugins[0].Problem);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        }
        var row = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
        Assert.Equal("description-changed", row.BlockedReason);
        Assert.True(row.CanReviewChanges);
        Assert.False(row.CanConfirm);
        Assert.Empty(log.CarryOvers);
    }

    [Fact]
    public async Task SamePluginFromAWorktreePathNeedsReviewAndOneConsentFixesIt()
    {
        CapturedLog log = new();
        var app = await Delivered("app", "one", source: true);
        var state = await Store().ApplyBundledStartupAsync([app.Candidate], [app.Proof], ["shared"], 0);
        state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants), owners = SdkJson.Serialize(state.SelectedSourceOwners);
        // The same plugin, an identical declaration and the same manifest file name, delivered from another build folder.
        var worktree = await Delivered("worktree", "two", source: true);
        Assert.NotEqual(app.Candidate.CandidateId, worktree.Candidate.CandidateId);
        Assert.Equal(app.Proof.SlotId, worktree.Proof.SlotId);
        for (int restart = 0; restart < 2; restart++)
        {
            state = await LoggedStore(log).ApplyBundledStartupAsync([worktree.Candidate], [worktree.Proof], ["shared"], state.Revision);
            Assert.Null(state.Plugins[0].Active);
            Assert.Equal(app.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(app.Candidate.Artifact!.ExecutionRevision, state.Plugins[0].LastGood!.ExecutionRevision);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        }
        Assert.Empty(log.CarryOvers);
        var preview = await Store().PreviewBundledRepairsAsync();
        var row = Assert.Single(preview.Rows);
        Assert.Equal("inherited-grants", row.BlockedReason);
        Assert.True(row.CanReauthorize);
        Assert.Equal(app.Candidate.CandidatePath, row.OldPath);
        Assert.Equal(worktree.Candidate.CandidatePath, row.NewPath);
        // "Review this repair": one explicit consent, then the next start runs the relocated build with the same rights.
        state = await Store().ReauthorizeBundledRepairAsync(row, preview.ProfileRevision);
        state = await Store().ApplyBundledStartupAsync([worktree.Candidate], [worktree.Proof], ["shared"], state.Revision);
        Assert.Equal(worktree.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
        Assert.Null(state.Plugins[0].Problem);
        Assert.Equal(grants, SdkJson.Serialize(state.Grants));
        Assert.Equal(owners, SdkJson.Serialize(state.SelectedSourceOwners));
        Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
    }

    [Fact]
    public async Task InPlaceNarrowingAfterAReviewedRelocationKeepsTheBindingValidAcrossRestarts()
    {
        var old = await Delivered("reviewed-app", "A", source: true);
        var state = await Store().ApplyBundledStartupAsync([old.Candidate], [old.Proof], ["shared"], 0);
        var configuration = Active(state).Configuration;
        state = await Store().SetGrantAsync("test.plugin", "dimensions", new("shared", "board", "rule"), FilterDataAccess.OriginalImage, state.Revision);
        byte[] grants = SdkJson.Serialize(state.Grants);
        // A relocated build with a widened declaration goes through the explicit declaration + rights review.
        var reviewed = await Redeclared(await Delivered("reviewed-worktree", "B", source: true), d => d with
            { Sources = [d.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture }] });
        state = await Store().ApplyBundledStartupAsync([reviewed.Candidate], [reviewed.Proof], ["shared"], state.Revision);
        var row = Assert.Single((await Store().PreviewBundledRepairsAsync()).Rows);
        Assert.True(row.CanReviewChanges);
        state = await Store().ConfirmBundledChangeAsync(row, configuration, acknowledgeChanges: true, authorizePermissions: true, state.Revision);
        state = await Store().ApplyBundledStartupAsync([reviewed.Candidate], [reviewed.Proof], ["shared"], state.Revision);
        Assert.Equal(reviewed.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
        // Rebuilt in place in that folder with a display-only rename: carried over, and the reviewed
        // declaration on the binding follows, so later starts do not report the confirmed target as changed.
        var renamed = await Redeclared(await Delivered("reviewed-worktree", "C", source: true), d => d with
        {
            Name = "Renamed after review",
            Sources = [d.Sources[0] with { Capabilities = SourceCapabilities.ThreadRead | SourceCapabilities.PostCapture }]
        });
        for (int restart = 0; restart < 3; restart++)
        {
            state = await Store().ApplyBundledStartupAsync([renamed.Candidate], [renamed.Proof], ["shared"], state.Revision);
            Assert.Equal(renamed.Candidate.Artifact!.ExecutionRevision, Active(state).ExecutionRevision);
            Assert.Equal("Renamed after review", Active(state).Description.Name);
            Assert.Null(state.Plugins[0].Problem);
            Assert.Equal(old.Candidate.CandidateId, state.Plugins[0].SelectedCandidateId);
            Assert.Equal(grants, SdkJson.Serialize(state.Grants));
            Assert.Empty((await Store().PreviewBundledRepairsAsync()).Rows);
        }
    }

    private PluginProfileStore LoggedStore(CapturedLog log) => new(profile) { Logger = log.Logger };

    private sealed class CapturedLog : ILogEventSink
    {
        private readonly List<LogEvent> events = new();
        internal CapturedLog() => Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();
        internal Serilog.ILogger Logger { get; }
        public void Emit(LogEvent logEvent) { lock (events) events.Add(logEvent); }
        internal IReadOnlyList<LogEvent> CarryOvers
        {
            get
            {
                lock (events)
                    return events.Where(e => e.Level == LogEventLevel.Information
                        && e.MessageTemplate.Text.Contains("automatic in-place bundled update", StringComparison.Ordinal)).ToArray();
            }
        }
    }
}
