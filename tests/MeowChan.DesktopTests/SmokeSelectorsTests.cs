using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.DesktopSmoke;

namespace MeowChan.DesktopTests;

// The smoke driver's entry guard only; these tests launch no window and are not PASS evidence.
public sealed class SmokeSelectorsTests
{
    private static readonly string[] Modes =
    [
        "--locked-profile", "--no-tray", "--tray-failure", "--download-session", "--tray-lifecycle", "--visual",
        "--reader-ux", "--selection", "--tray-host", "--about-links", "--board-admission", "--reading-position",
        "--accessibility-probe", "--window-geometry", "--video-contrast", "--plugins", "--archive-cleanup", "--shell-load"
    ];

    [Theory]
    [InlineData("MEOWCHAN_READER_FIDN_ONLY")] // misspelled
    [InlineData("MEOWCHAN_ARCHIVE_EXACT_IDS_ONLY")] // never registered
    [InlineData("meowchan_reader_find_only")] // wrong case on a case-sensitive platform
    public void UnknownSelectorIsRejected(string name)
    {
        string? error = Check([], (name, "1"));
        Assert.NotNull(error);
        Assert.Contains("unknown selector " + name, error);
    }

    [Fact]
    public void WindowsComparesSelectorNamesCaseInsensitivelyAndReportsTheCanonicalName()
    {
        Assert.Null(SmokeSelectors.Check([], Variables(("meowchan_reader_find_only", "1")), true, out string scenario));
        Assert.Equal("normal+MEOWCHAN_READER_FIND_ONLY", scenario);
        Assert.Null(SmokeSelectors.Check(["--reader-ux"], Variables(("Meowchan_Gallery_Close_Only", "1")), true, out scenario));
        Assert.Equal("reader-ux+MEOWCHAN_GALLERY_CLOSE_ONLY", scenario);
    }

    [Theory]
    [InlineData("MEOWCHAN_READER_GROUPS_ONLY", new string[0])]
    [InlineData("MEOWCHAN_GALLERY_CLOSE_ONLY", new[] { "--selection" })]
    [InlineData("MEOWCHAN_PLUGIN_RESELECTION_ONLY", new string[0])]
    [InlineData("MEOWCHAN_SHELL_PREFERENCES_ONLY", new[] { "--reader-ux" })]
    [InlineData("MEOWCHAN_ACCESSIBILITY_NUMERIC_ONLY", new string[0])]
    [InlineData("MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY", new string[0])] // without MEOWCHAN_ARCHIVE_COMPARISON=1
    [InlineData("MEOWCHAN_RATINGS_LATE_ERROR_ONLY", new string[0])] // without MEOWCHAN_MEDIA_RATINGS_ONLY
    [InlineData("MEOWCHAN_GALLERY_EDGE_ONLY", new string[0])]
    public void SelectorWithoutItsModeOrParentIsRejected(string name, string[] args)
    {
        string? error = SmokeSelectors.Check(args, Variables((name, "1")), false, out _);
        Assert.NotNull(error);
        Assert.StartsWith(name + " has no effect without ", error);
    }

    [Theory]
    [InlineData("MEOWCHAN_READER_FIND_ONLY", "--reader-ux")]
    [InlineData("MEOWCHAN_SHORTCUT_PRESENTATION_ONLY", "--window-geometry")]
    [InlineData("MEOWCHAN_SECTION_COMPARISON_ONLY", "--selection")]
    public void TopLevelSelectorWithACommandLineScenarioIsRejected(string name, string mode)
    {
        Assert.Equal(name + " runs only without a command-line scenario, not with " + mode,
            SmokeSelectors.Check([mode], Variables((name, "1")), false, out _));
    }

    [Theory]
    [InlineData("MEOWCHAN_READER_FIND_ONLY", "MEOWCHAN_MEDIA_PAGE_ONLY")]
    [InlineData("MEOWCHAN_SECTION_COMPARISON_ONLY", "MEOWCHAN_SUGGESTIONS_ONLY")]
    [InlineData("MEOWCHAN_ARCHIVE_COMPARISON", "MEOWCHAN_POWERUSER_ONLY")]
    public void TwoTopLevelSelectorsAtOnceAreRejected(string first, string second)
    {
        string? error = Check([], (first, "1"), (second, "1"));
        Assert.NotNull(error);
        Assert.StartsWith("scenario selectors are mutually exclusive, but 2 are set:", error);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("yes")]
    public void KnownSelectorWithAnIgnoredValueIsRejected(string value)
    {
        Assert.Equal("MEOWCHAN_READER_FIND_ONLY=" + value + " is ignored by the driver; use 1",
            Check([], ("MEOWCHAN_READER_FIND_ONLY", value)));
    }

    [Fact]
    public void ClearedSelectorsAndOtherVariablesDoNotChangeTheNormalScene()
    {
        Assert.Null(SmokeSelectors.Check([], Variables(("MEOWCHAN_READER_FIDN_ONLY", "0"), ("MEOWCHAN_MEDIA_PAGE_ONLY", ""),
            ("MEOWCHAN_VIDEO_FIXTURE", "/tmp/x.webm"), ("MEOWCHAN_URSA_NATIVE_SUITE", "plugins"), ("PATH", "/usr/bin")),
            false, out string scenario));
        Assert.Equal("normal", scenario);
    }

    [Fact]
    public void EveryModeAloneAndEveryTopLevelSelectorInTheNormalSceneIsValid()
    {
        Assert.Null(Check([]));
        foreach (string mode in Modes)
        {
            Assert.Null(SmokeSelectors.Check([mode], [], false, out string scenario));
            Assert.Equal(mode[2..], scenario);
        }
        foreach (string root in SmokeSelectors.RootNames)
        {
            Assert.Null(SmokeSelectors.Check([], Variables((root, "1")), false, out string scenario));
            Assert.Equal("normal+" + root, scenario);
        }
        foreach ((string name, string value) in SmokeSelectors.BranchSelectors)
            Assert.Null(Check([], (name, value)));
    }

    [Fact]
    public void EveryNestedSelectorIsValidWithEachOfItsRequirements()
    {
        int checkedCombinations = 0;
        foreach ((string name, string[] requires) in SmokeSelectors.ChildRequirements)
        {
            foreach (string requirement in requires)
            {
                List<string> args = [];
                List<(string, string)> variables = [(name, "1")];
                Satisfy(requirement, args, variables);
                string? error = SmokeSelectors.Check(args, Variables(variables.ToArray()), false, out string scenario);
                Assert.True(error == null, name + " with " + requirement + ": " + error);
                Assert.StartsWith(args.Count == 0 ? "normal+" : args[0][2..] + "+", scenario);
                Assert.Contains("+" + name, scenario);
                checkedCombinations++;
            }
        }
        Assert.True(checkedCombinations >= 33);
    }

    // Invocations in .github/workflows/dotnet-desktop.yml, scripts/ and the coordination handoffs.
    [Theory]
    [InlineData("plugins", "plugins+MEOWCHAN_PLUGIN_RESELECTION_ONLY", "MEOWCHAN_PLUGIN_RESELECTION_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_FIRST_RUN_SETUP_ONLY", "MEOWCHAN_FIRST_RUN_SETUP_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_PREFERENCE_STARTUP_ONLY", "MEOWCHAN_PREFERENCE_STARTUP_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_COMPACT_DIAGNOSTICS_ONLY", "MEOWCHAN_COMPACT_DIAGNOSTICS_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_GIF_PLAYBACK_ONLY", "MEOWCHAN_GIF_PLAYBACK_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_GALLERY_ERROR_ONLY", "MEOWCHAN_GALLERY_ERROR_ONLY")]
    [InlineData("reader-ux", "reader-ux+MEOWCHAN_READER_MEDIA_INPUT_ONLY", "MEOWCHAN_READER_MEDIA_INPUT_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY", "MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_MEDIA_INTERACTION_ONLY", "MEOWCHAN_MEDIA_INTERACTION_ONLY")]
    [InlineData("reader-ux", "reader-ux+MEOWCHAN_GALLERY_CLOSE_ONLY", "MEOWCHAN_GALLERY_CLOSE_ONLY")]
    [InlineData("accessibility-probe", "accessibility-probe+MEOWCHAN_ACCESSIBILITY_NUMERIC_ONLY", "MEOWCHAN_ACCESSIBILITY_NUMERIC_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY", "MEOWCHAN_SOURCE_INTEGRITY_GUI_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_SAVED_SEARCHES_ONLY", "MEOWCHAN_SAVED_SEARCHES_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_SECTION_COMPARISON_ONLY", "MEOWCHAN_SECTION_COMPARISON_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_SECTION_COMPARISON_ONLY+MEOWCHAN_SECTION_PRESENTATION_ONLY", "MEOWCHAN_SECTION_COMPARISON_ONLY MEOWCHAN_SECTION_PRESENTATION_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_DIALOG_FRAME_ONLY", "MEOWCHAN_DIALOG_FRAME_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_FILENAME_SEARCH_LARGE_READER_ONLY", "MEOWCHAN_FILENAME_SEARCH_LARGE_READER_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_ARCHIVE_COMPARISON+MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY", "MEOWCHAN_ARCHIVE_COMPARISON MEOWCHAN_ARCHIVE_COMPARISON_EXACT_IDS_ONLY")]
    [InlineData("normal", "normal+MEOWCHAN_GALLERY_EDGE_ONLY+MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY", "MEOWCHAN_GALLERY_PLAYBACK_FEATURES_ONLY MEOWCHAN_GALLERY_EDGE_ONLY")]
    [InlineData("selection", "selection")]
    [InlineData("tray-host", "tray-host")]
    public void DocumentedInvocationsRemainValid(string mode, string expected, string selectors = "")
    {
        string[] args = mode == "normal" ? [] : ["--" + mode];
        Assert.Null(SmokeSelectors.Check(args, Variables(selectors.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(name => (name, "1")).ToArray()), false, out string scenario));
        Assert.Equal(expected, scenario);
    }

    // Root selectors are dispatched through SmokeScenarios.Runs; every name a check still reads
    // directly must be registered, and every nested selector must still be read by its parent's checks.
    [Fact]
    public void RegistryListsExactlyTheSelectorsTheSmokeDriverReads()
    {
        string root = Path.Combine(SourceRoot(), "tests/MeowChan.DesktopSmoke");
        SortedSet<string> read = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
            foreach (Match match in Regex.Matches(File.ReadAllText(file), "GetEnvironmentVariable\\(\"(MEOWCHAN_[A-Z0-9_]+_ONLY)\"\\)"))
                read.Add(match.Groups[1].Value);
        Assert.Empty(read.Except(SmokeSelectors.KnownNames, StringComparer.Ordinal));
        Assert.Empty(SmokeSelectors.ChildRequirements.Select(child => child.Name).Except(read, StringComparer.Ordinal));
        Assert.Equal(SmokeSelectors.KnownNames.Count(), SmokeSelectors.KnownNames.Distinct(StringComparer.Ordinal).Count());
    }

    private static void Satisfy(string requirement, List<string> args, List<(string, string)> variables)
    {
        if (requirement.StartsWith("--", StringComparison.Ordinal)) { args.Add(requirement); return; }
        int separator = requirement.IndexOf('=');
        string name = separator < 0 ? requirement : requirement[..separator];
        variables.Add((name, separator < 0 ? "1" : requirement[(separator + 1)..]));
        foreach ((string child, string[] requires) in SmokeSelectors.ChildRequirements)
            if (child == name) Satisfy(requires[0], args, variables);
    }

    private static string? Check(string[] args, params (string Name, string Value)[] variables) =>
        SmokeSelectors.Check(args, Variables(variables), false, out _);

    private static KeyValuePair<string, string?>[] Variables(params (string Name, string Value)[] variables) =>
        variables.Select(variable => KeyValuePair.Create(variable.Name, (string?)variable.Value)).ToArray();

    private static string SourceRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
