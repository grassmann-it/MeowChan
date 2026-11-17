using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

/// <summary>
/// Decision #130: after a start moved a pinned bundled release to its rebuilt build of the same version, the plugin's card
/// says so once, calmly (a hint, not a problem). The store's one-start note is the only evidence; fixtures only.
/// </summary>
[Collection("Localization")]
public sealed class PinnedReleaseUpdateNoticeTests : IDisposable
{
    private const string PluginId = "meowchan.official.danbooru";
    private const string CandidateId = "danbooru-candidate";

    public PinnedReleaseUpdateNoticeTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    [Theory]
    [InlineData("en", "This start updated the pinned release 1.2.0 in place to the current bundled build of the same version. It stays pinned; the previous build is kept for rollback.")]
    [InlineData("de", "Dieser Start hat die angeheftete Version 1.2.0 an Ort und Stelle auf den aktuellen mitgelieferten Build derselben Version aktualisiert. Sie bleibt angeheftet; der vorherige Build bleibt für einen Rückfall erhalten.")]
    public void TheFirstStartAfterAPinnedInPlaceUpdateSaysSoOnceAndNotAsAProblem(string language, string expected)
    {
        Texts.SetLanguage(language);
        PluginReleaseSnapshot current = Release("current"), previous = Release("previous");
        PluginEntryPresentation view = PluginEntryPresentation.Create(Entry(current, previous, PluginActivationProblems.PinnedReleaseUpdatedInPlace));
        Assert.True(view.IsPinnedReleaseUpdatedInPlace);
        Assert.True(view.IsRollbackPinned);
        Assert.Null(view.VisibleActivationProblem);
        Assert.False(view.NeedsRepairReview);
        Assert.DoesNotContain(Texts.T("plugins.problem"), PluginContentPresentation.DescribeStatus(view));
        string hint = PluginContentPresentation.DescribeStatusHint(view);
        Assert.Contains(expected, hint);
        Assert.DoesNotContain(Texts.T("plugins.problemHint"), hint);
        Assert.Single(hint.Split('\n'), line => line == expected);

        // The next start clears the store's note: the card returns to the ordinary pinned text, without the notice.
        PluginEntryPresentation later = PluginEntryPresentation.Create(Entry(current, previous, problem: null));
        Assert.False(later.IsPinnedReleaseUpdatedInPlace);
        Assert.DoesNotContain(expected, PluginContentPresentation.DescribeStatusHint(later));
        Assert.Contains(Texts.T("plugins.rollbackPinnedHint"), PluginContentPresentation.DescribeStatusHint(later));
    }

    [Fact]
    public void TheNoteIsOnlyANoticeForThePinnedActiveBuild()
    {
        PluginReleaseSnapshot current = Release("current"), previous = Release("previous");
        // Pinned somewhere else than the running build, or no running build: never claimed, and shown as it is.
        PluginEntryPresentation stale = PluginEntryPresentation.Create(Entry(current, previous, PluginActivationProblems.PinnedReleaseUpdatedInPlace, pinned: previous));
        Assert.False(stale.IsPinnedReleaseUpdatedInPlace);
        Assert.Equal(PluginActivationProblems.PinnedReleaseUpdatedInPlace, stale.VisibleActivationProblem);
        PluginEntryPresentation inactive = PluginEntryPresentation.Create(Entry(null, previous, PluginActivationProblems.PinnedReleaseUpdatedInPlace, pinned: current));
        Assert.False(inactive.IsPinnedReleaseUpdatedInPlace);
        Assert.False(PluginActivationProblems.RequiresRepairReview(PluginActivationProblems.PinnedReleaseUpdatedInPlace));
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("it")]
    [InlineData("ja")]
    [InlineData("ko")]
    [InlineData("pl")]
    [InlineData("pt-BR")]
    [InlineData("ru")]
    [InlineData("zh-Hans")]
    public void EveryBundledPackCarriesTheNoticeWithItsVersionPlaceholder(string language)
    {
        string english = Texts.T("plugins.pinnedReleaseUpdatedHint", "9.9.9");
        Texts.SetLanguage(language);
        string localized = Texts.T("plugins.pinnedReleaseUpdatedHint", "9.9.9");
        Assert.Contains("9.9.9", localized);
        Assert.NotEqual(english, localized);
    }

    private static PluginReleaseSnapshot Release(string execution) => new(CandidateId,
        new PluginArtifact("compile-" + execution, execution, "/fixture/plugin.dll", "assembly-hash", "sdk-hash", "compiler-hash", []),
        new PluginDescription(PluginId, "Danbooru", "1.2.0", SdkContract.Version, "Booru source", [], [],
            new SettingsSchema("1", []), []),
        new("config", new("1", []), new Dictionary<string, SettingsSnapshot>()));

    private static PluginCatalogEntry Entry(PluginReleaseSnapshot? active, PluginReleaseSnapshot lastGood, string? problem,
        PluginReleaseSnapshot? pinned = null)
    {
        PluginCandidateSnapshot candidate = new(CandidateId, PluginId, (active ?? lastGood).Artifact, (active ?? lastGood).Description,
            true, null, "/fixture/plugins/MeowChan.Plugin.Danbooru.plugin.json");
        PluginActivationSnapshot state = new(PluginId, true, 1, CandidateId, active, null, lastGood, pinned ?? active,
            [candidate], problem);
        return new(CandidateId, PluginId, CandidateId, "/fixture/plugins/MeowChan.Plugin.Danbooru.plugin.json",
            candidate.Description, state, candidate, Array.Empty<PluginBuildDiagnostic>());
    }
}
