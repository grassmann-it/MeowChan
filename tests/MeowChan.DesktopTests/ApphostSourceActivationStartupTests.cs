using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

public sealed class ApphostSourceActivationStartupTests
{
    [Theory]
    [InlineData("acquire")]
    [InlineData("offline")]
    public async Task SelectedDuplicateCandidatesRequireExactlyTheirTwoAdvisories(string phase)
    {
        using Fixture fixture = new();
        var (state, warnings) = await fixture.SelectedAsync();
        MainWindow.RequireAuthorStartupState(state, warnings, true, phase);
        Assert.Equal(2, state.Plugins.Count(plugin => plugin.Problem != null));
        Assert.All(state.Plugins, plugin => Assert.Null(plugin.Pending));
    }

    [Theory]
    [InlineData("unpaused")]
    [InlineData("missing-warning")]
    [InlineData("extra-warning")]
    [InlineData("duplicate-warning")]
    [InlineData("wrong-id-problem")]
    [InlineData("wrong-selected")]
    [InlineData("pending")]
    [InlineData("third-candidate")]
    public async Task UnexpectedStartupStateFailsClosed(string fault)
    {
        using Fixture fixture = new();
        var (state, warnings) = await fixture.SelectedAsync();
        var plugins = state.Plugins.ToArray();
        int target = Array.FindIndex(plugins, plugin => plugin.PluginId == "meowchan.source.lainchan");
        int other = Array.FindIndex(plugins, plugin => plugin.PluginId == "other.plugin");
        if (fault == "missing-warning") warnings = warnings[..1];
        if (fault == "extra-warning") warnings = [.. warnings, "unrelated warning"];
        if (fault == "duplicate-warning") warnings = [warnings[0], warnings[0]];
        if (fault == "wrong-id-problem") plugins[other] = plugins[other] with { Problem = plugins[target].Problem };
        if (fault == "wrong-selected") plugins[target] = plugins[target] with { SelectedCandidateId = "wrong" };
        if (fault == "pending") plugins[target] = plugins[target] with { Pending = plugins[target].Active };
        if (fault == "third-candidate") plugins[target] = plugins[target] with { Candidates = [.. plugins[target].Candidates, plugins[target].Candidates[0] with { CandidateId = "third" }] };
        state = state with { Plugins = plugins };
        Assert.Throws<InvalidOperationException>(() => MainWindow.RequireAuthorStartupState(state, warnings, fault != "unpaused", "acquire"));
    }

    [Fact]
    public async Task InitialInstallStillRequiresZeroWarningsAndZeroProblems()
    {
        using Fixture fixture = new();
        var state = await fixture.Store.ApplyStartupAsync(fixture.Prebuilt, 0);
        MainWindow.RequireAuthorStartupState(state, [], true, "install");
        Assert.Throws<InvalidOperationException>(() => MainWindow.RequireAuthorStartupState(state, ["unrelated warning"], true, "install"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string profile = Path.Combine(Path.GetTempPath(), "meowchan-author-startup-tests-" + Guid.NewGuid().ToString("N"));
        internal PluginProfileStore Store { get; }
        internal PluginCandidateObservation[] Prebuilt { get; } = [Candidate("meowchan.source.lainchan", "prebuilt"), Candidate("meowchan.example.image-dimensions", "prebuilt"), Candidate("other.plugin", "prebuilt")];
        internal Fixture() => Store = new(profile);
        internal async Task<(PluginProfileSnapshot, string[])> SelectedAsync()
        {
            var state = await Store.ApplyStartupAsync(Prebuilt, 0);
            PluginCandidateObservation[] authors = [Candidate("meowchan.source.lainchan", "author"), Candidate("meowchan.example.image-dimensions", "author")];
            foreach (var author in authors)
            {
                state = await Store.ObserveCandidateAsync(author, state.Revision);
                state = await Store.SelectCandidateAsync(author.PluginId, author.CandidateId, state.Revision);
            }
            state = await Store.ApplyStartupAsync([.. Prebuilt, .. authors], state.Revision);
            string[] warnings = state.Plugins.Where(plugin => plugin.Problem != null).Select(plugin => plugin.Active!.Description.Name + ": " + plugin.Problem).ToArray();
            return (state, warnings);
        }
        private static PluginCandidateObservation Candidate(string id, string build)
        {
            SettingsSchema empty = new("1", []);
            PluginDescription description = new(id, id, "1.0", SdkContract.Version, "Fixture", [],
                [new("dimensions", "Dimensions", "Fixture", FilterDataAccess.CandidateMetadata, empty, ["image/png"])], empty, []);
            PluginArtifact artifact = new("code-" + build, build, "/fixture/" + build + ".dll", new string('a', 64), "sdk", "compiler", []);
            return new(id + ":" + build, id, artifact, description);
        }
        public void Dispose() { if (Directory.Exists(profile)) Directory.Delete(profile, true); }
    }
}
