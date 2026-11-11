using MeowChan.DesktopSmoke;

namespace MeowChan.DesktopTests;

// The scenario registry only; these tests launch no window and are not PASS evidence.
public sealed class SmokeScenariosTests
{
    // Formerly guarded by Program's own "exactly one *_ONLY and no command-line scenario" list.
    private static readonly string[] FormerlyFocused =
    [
        "MEOWCHAN_DIAGNOSTIC_EVIDENCE_ONLY", "MEOWCHAN_INTERRUPTED_STARTUP_ONLY", "MEOWCHAN_HTML_EXPORT_LABELS_ONLY",
        "MEOWCHAN_HTML_EXPORT_LANGUAGE_ONLY", "MEOWCHAN_SECONDARY_SETTINGS_LANGUAGE_ONLY", "MEOWCHAN_GALLERY_HIDDEN_FAVORITE_ONLY",
        "MEOWCHAN_NATIVE_TRANSFER_PREVIEW_ONLY", "MEOWCHAN_READER_GROUPS_BINDING_ONLY", "MEOWCHAN_SHORTCUT_ISSUE_LANGUAGE_ONLY",
        "MEOWCHAN_EXPORT_STATUS_LANGUAGE_ONLY", "MEOWCHAN_HTML_EXPORT_ACTION_LAYOUT_ONLY", "MEOWCHAN_FIRST_RUN_REOPEN_ONLY",
        "MEOWCHAN_GALLERY_VOLUME_ROUTED_ONLY", "MEOWCHAN_URLTEXT_EXPORT_ONLY", "MEOWCHAN_URLTEXT_PAGE_BOUNDARY_ONLY",
        "MEOWCHAN_READER_FIND_ONLY", "MEOWCHAN_FILENAME_SEARCH_ONLY", "MEOWCHAN_MEDIA_HASH_SEARCH_ONLY",
        "MEOWCHAN_FILENAME_SEARCH_HOST_ONLY", "MEOWCHAN_FILENAME_SEARCH_LARGE_READER_ONLY", "MEOWCHAN_LOCAL_SEARCH_LANGUAGE_ONLY",
        "MEOWCHAN_PRIVATE_TAGS_ONLY", "MEOWCHAN_GALLERY_MEDIA_TAGS_ONLY", "MEOWCHAN_MEDIA_PAGE_ONLY", "MEOWCHAN_DIALOG_FRAME_ONLY",
        "MEOWCHAN_STARTUP_FIRST_FRAME_ONLY"
    ];

    [Fact]
    public void EachIdIsUniqueAndEachSelectorIsRegisteredExactlyOnce()
    {
        Assert.All(SmokeScenarios.All, entry => Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)*$", entry.Id));
        Assert.Equal(SmokeScenarios.All.Length, SmokeScenarios.All.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(SmokeScenarios.All.Length, SmokeScenarios.All.Select(entry => entry.Selector).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(SmokeSelectors.NormalMode, Assert.Single(SmokeScenarios.All, entry => entry.Selector.Length == 0).Id);
    }

    [Fact]
    public void EachKindHasTheSelectorShapeAndClaimItsDispatchNeeds()
    {
        HashSet<string> conditions = SmokeScenarios.All.Select(entry => entry.Selector).ToHashSet(StringComparer.Ordinal);
        foreach (SmokeScenario entry in SmokeScenarios.All)
        {
            switch (entry.Kind)
            {
                case SmokeScenarioKind.Mode:
                    Assert.True(entry.Selector.Length == 0 || entry.Selector == "--" + entry.Id, entry.Id);
                    break;
                case SmokeScenarioKind.Root:
                    Assert.Matches("^MEOWCHAN_[A-Z0-9_]+_ONLY$", entry.Selector);
                    break;
                case SmokeScenarioKind.Branch:
                    Assert.Matches("^MEOWCHAN_[A-Z0-9_]+(=[a-z0-9]+)?$", entry.Selector);
                    Assert.DoesNotContain("_ONLY", entry.Selector, StringComparison.Ordinal);
                    break;
                case SmokeScenarioKind.Nested:
                    Assert.Matches("^MEOWCHAN_[A-Z0-9_]+_ONLY$", entry.Selector);
                    Assert.NotEmpty(entry.Requires);
                    Assert.All(entry.Requires, requirement => Assert.Contains(requirement, conditions));
                    break;
            }
            // A nested selector's wording belongs to its parent's claim, which reads it.
            Assert.Equal(entry.Kind != SmokeScenarioKind.Nested, entry.Pass != null);
            Assert.True(entry.Kind == SmokeScenarioKind.Nested || entry.Requires.Length == 0, entry.Id);
        }
    }

    [Fact]
    public void ModesKeepTheirHistoricalUsageOrder()
    {
        Assert.Equal(["--locked-profile", "--no-tray", "--tray-failure", "--download-session", "--tray-lifecycle", "--visual",
            "--reader-ux", "--selection", "--tray-host", "--about-links", "--board-admission", "--reading-position",
            "--accessibility-probe", "--window-geometry", "--video-contrast", "--plugins", "--archive-cleanup", "--shell-load"],
            SmokeScenarios.All.Where(entry => entry.Kind == SmokeScenarioKind.Mode && entry.Selector.Length != 0)
                .Select(entry => entry.Selector));
    }

    [Theory]
    [InlineData("reader-find", "reader find: actual offline404 native controls, whole-thread hits and managed routed keys; no OS keyboard or Windows/macOS acceptance")]
    [InlineData("filename-search", "filename search standalone dialog:")]
    [InlineData("media-hash-search", "SHA-256 search standalone dialog:")]
    [InlineData("filename-search-host", "filename search host: actual native page/modal,")]
    public void FocusedSearchScenariosOwnTheirClaim(string id, string claim)
    {
        Assert.StartsWith(claim, Assert.Single(SmokeScenarios.All, scenario => scenario.Id == id).Pass!(_ => null), StringComparison.Ordinal);
        Assert.Single(SmokeScenarios.All, scenario => scenario.Pass?.Invoke(_ => null).StartsWith(claim, StringComparison.Ordinal) == true);
    }

    [Fact]
    public void FormerlyFocusedSelectorsStillRejectAnySecondSelectorOrMode()
    {
        string[] modes = SmokeScenarios.All.Where(entry => entry.Kind == SmokeScenarioKind.Mode && entry.Selector.Length != 0)
            .Select(entry => entry.Selector).ToArray();
        foreach (string focused in FormerlyFocused)
        {
            Assert.Contains(focused, SmokeSelectors.RootNames);
            foreach (string other in SmokeSelectors.KnownNames.Where(name => name != focused))
                Assert.NotNull(SmokeSelectors.Check([], Variables((focused, "1"), (other, "1")), false, out _));
            foreach ((string name, string value) in SmokeSelectors.BranchSelectors)
                Assert.NotNull(SmokeSelectors.Check([], Variables((focused, "1"), (name, value)), false, out _));
            foreach (string mode in modes)
                Assert.NotNull(SmokeSelectors.Check([mode], Variables((focused, "1")), false, out _));
        }
    }

    [Theory]
    [InlineData(new string[0], "", "normal")]
    [InlineData(new[] { "--reader-ux" }, "MEOWCHAN_GALLERY_CLOSE_ONLY", "reader-ux")]
    [InlineData(new string[0], "MEOWCHAN_READER_FIND_ONLY", "reader-find")]
    [InlineData(new string[0], "MEOWCHAN_ARCHIVE_COMPARISON MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY", "archive-comparison")]
    [InlineData(new string[0], "MEOWCHAN_URSA_NATIVE_SUITE=groups", "ursa-native-suite-groups")]
    [InlineData(new string[0], "MEOWCHAN_MEDIA_RATINGS_ONLY MEOWCHAN_RATINGS_PRESENTATION_ONLY", "media-ratings")]
    public void CheckSelectsTheRootOrBranchElseTheMode(string[] args, string variables, string expected)
    {
        (string, string)[] pairs = variables.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(variable => variable.Split('=') is [string name, string value] ? (name, value) : (variable, "1")).ToArray();
        Assert.Null(SmokeSelectors.Check(args, Variables(pairs), false, out _, out SmokeScenario? selected));
        Assert.Equal(expected, selected?.Id);
    }

    [Fact]
    public void NestedSelectorsChangeOnlyTheirParentsClaim()
    {
        Func<string, string?> Env(params string[] names) => name => names.Contains(name) ? "1" : null;
        SmokeScenario ratings = Assert.Single(SmokeScenarios.All, entry => entry.Id == "media-ratings");
        Assert.StartsWith("ratings: real controls,", ratings.Pass!(Env()), StringComparison.Ordinal);
        Assert.StartsWith("ratings: 12-cell", ratings.Pass!(Env("MEOWCHAN_RATINGS_PRESENTATION_ONLY")), StringComparison.Ordinal);
        Assert.StartsWith("ratings: targeted", ratings.Pass!(Env("MEOWCHAN_RATINGS_LATE_ERROR_ONLY")), StringComparison.Ordinal);
        SmokeScenario playback = Assert.Single(SmokeScenarios.All, entry => entry.Id == "gallery-playback-features");
        Assert.StartsWith("native edge-hover panels:", playback.Pass!(Env("MEOWCHAN_GALLERY_EDGE_ONLY")), StringComparison.Ordinal);
        Assert.StartsWith("gallery playback features:", playback.Pass!(Env()), StringComparison.Ordinal);
        SmokeScenario frame = Assert.Single(SmokeScenarios.All, entry => entry.Id == "dialog-frame");
        Assert.Contains("group=all;", frame.Pass!(Env()), StringComparison.Ordinal);
        Assert.Contains("group=search;", frame.Pass!(name => name == "MEOWCHAN_DIALOG_FRAME_GROUP" ? "search" : null), StringComparison.Ordinal);
    }

    private static KeyValuePair<string, string?>[] Variables(params (string Name, string Value)[] variables) =>
        variables.Select(variable => KeyValuePair.Create(variable.Name, (string?)variable.Value)).ToArray();
}
