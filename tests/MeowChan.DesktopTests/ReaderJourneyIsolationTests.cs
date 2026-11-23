using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MeowChan.DesktopSmoke;

namespace MeowChan.DesktopTests;

// Source/selector contracts only, not evidence that native GUI journeys passed.
public sealed class ReaderJourneyIsolationTests
{
    [Theory]
    [InlineData("reader-owned-navigation")]
    [InlineData("reader-gallery-roundtrip")]
    [InlineData("reader-list-gallery-return")]
    [InlineData("video-lifecycle")]
    [InlineData("window-minimum")]
    public void EachJourneyIsOneIndependentReaderAlternative(string id)
    {
        SmokeScenario entry = Assert.Single(SmokeScenarios.All, entry => entry.Id == id);
        Assert.Equal("--reader-ux", entry.Group);
        Assert.Null(entry.NightlySkip);
        Assert.Empty(entry.NightlyEnvironment);
        var invocation = SmokeScenarios.Invocation(entry);
        Assert.Equal(new[] { "--reader-ux" }, invocation.Args);
        Assert.Equal(new[] { entry.Selector + "=1" }, invocation.Environment);
        Dictionary<string, string?> env = new() { [entry.Selector] = "1" };
        Assert.Null(SmokeSelectors.Check(invocation.Args, env, false, out _, out _));
        Assert.Equal(id, SmokeScenarios.ReaderAlternative(name => env.GetValueOrDefault(name)));
        SmokeScenario mode = Assert.Single(SmokeScenarios.All, entry => entry.Id == "reader-ux");
        Assert.StartsWith("reader-ux scoped " + id + ":", mode.Pass!(name => env.GetValueOrDefault(name)));
        Assert.Equal(SmokeScenarios.NormalPass, mode.Pass!(_ => null));
        env["MEOWCHAN_READER_RETURNS_ONLY"] = "1";
        Assert.NotNull(SmokeSelectors.Check(invocation.Args, env, false, out _, out _));
    }

    [Theory]
    [InlineData("MEOWCHAN_GALLERY_TRANSITION_REVIEW", "observe")]
    [InlineData("MEOWCHAN_GALLERY_TRANSITION_REVIEW", "1")]
    [InlineData("MEOWCHAN_GALLERY_HELP_REVIEW", "observe")]
    [InlineData("MEOWCHAN_GALLERY_HELP_REVIEW", "1")]
    public void ObservationalReviewCannotOverrideASelectedReaderJourney(string review, string value)
    {
        foreach (SmokeScenario entry in SmokeScenarios.All.Where(entry => entry.Group == "--reader-ux"))
        {
            Dictionary<string, string?> env = new() { [entry.Selector] = "1", [review] = value };
            Assert.Contains("cannot be combined", SmokeSelectors.Check(["--reader-ux"], env, false, out _, out _)!);
        }
        Dictionary<string, string?> debug = new() { [review] = value };
        Assert.Null(SmokeSelectors.Check(["--reader-ux"], debug, false, out _, out _));
    }

    [Fact]
    public void PlainReaderRemainsUnsplitAndSelectedReaderReturnsBeforeOuterChecks()
    {
        Assert.Null(SmokeScenarios.ReaderAlternative(_ => null));
        string source = Read("Program.cs");
        int selected = source.IndexOf("if (readerAlternative != null && readerAlternative != \"gallery-close\")", StringComparison.Ordinal);
        int lifecycle = source.IndexOf("await RunOptionalCheckAsync(\"VideoLifecycleChecks\")", selected, StringComparison.Ordinal);
        string isolated = source[selected..lifecycle];
        Assert.Contains("await SmokeScenarios.RunReaderAlternativeAsync(readerAlternative, mainWindow);", isolated);
        Assert.Contains("_checksPassed = true;", isolated);
        Assert.Contains("mainWindow.Close();", isolated);
        Assert.Contains("return;", isolated);
        Assert.Contains("await WindowMinimumChecks.RunAsync(mainWindow);", source[lifecycle..]);
        Assert.Contains("if (videoFixture) await VideoGalleryChecks.RunAsync(mainWindow);", source[lifecycle..]);
        Assert.Contains("await GalleryCloseChecks.RunAsync(mainWindow, videoFixture,", source[lifecycle..]);
    }

    // Lock the reviewed journey bodies. Owned navigation uses the continuous-list assertion
    // from 8be37b24a; the two gallery journeys still match ba1842af4 lines 249–425.
    [Theory]
    [InlineData("CheckOwnedNavigationAsync", "1EFD10E3B439832D5F147A1CEA7F85B873A611DAA702562866708B86D5DFF028")]
    [InlineData("CheckGalleryRoundtripAsync", "D56D7315DB3A2C848CB94F63F856CEA96B48A1208130A002C93AD2CF64F97F7C")]
    [InlineData("CheckListGalleryReturnAsync", "BDEFAB3078B96B9D0F39958B9C6B69A28F72E1C55EAF8E24690F5EF9FE483292")]
    public void ExtractedJourneyRetainsEveryReviewedStatement(string method, string expected)
    {
        string source = Read("ReaderUxChecks.cs");
        int start = source.IndexOf("private static async Task " + method, StringComparison.Ordinal);
        start = source.IndexOf('\n', source.IndexOf('{', start)) + 1;
        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        string body = source[start..end];
        if (method == "CheckListGalleryReturnAsync")
            body = body[(body.IndexOf("        Click(page.FindControl<Button>(\"NextThreadsButton\")!);", StringComparison.Ordinal))..];
        string normalized = string.Join("\n", body.Replace("\r", "").Split('\n').Select(line => line.TrimEnd())).TrimEnd();
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    [Fact]
    public void FreshSetupUsesRealQueryAndReaderRatherThanAssertedState()
    {
        string source = Read("ReaderUxChecks.cs");
        string setup = source[source.IndexOf("internal static async Task RunJourneyAsync", StringComparison.Ordinal)..source.IndexOf("    private static async Task CheckOwnedNavigationAsync", StringComparison.Ordinal)];
        Assert.Contains("await page.ApplySearchAsync();", setup);
        Assert.Contains("Click(page.FindControl<Button>(\"ArchiveReadPosts\")!);", setup);
        Assert.Contains("await page.MediaLoadTask;", setup);
        Assert.DoesNotContain("RestoreState", setup);
        Assert.DoesNotContain("LoadThreadAsync", setup);
        Assert.DoesNotContain("FocusPostAsync", setup);
        Assert.DoesNotContain("catch", setup);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(setup, "case \\\"reader-").Count);
    }

    [Theory]
    [InlineData("gif-playback-core", "GifPlaybackChecks.RunPlaybackAsync(mainWindow)")]
    [InlineData("gif-successors", "GifPlaybackChecks.RunSuccessorsAsync(mainWindow)")]
    [InlineData("gif-verified-failures", "GifPlaybackChecks.RunVerifiedFailuresAsync(mainWindow)")]
    [InlineData("gif-reader-association", "GifPlaybackChecks.RunReaderAssociationAsync(mainWindow)")]
    [InlineData("gif-ownership", "GifOwnershipChecks.RunAsync(mainWindow)")]
    [InlineData("gallery-errors-retirement-clip", "GalleryErrorInteractionChecks.RunRetirementClipAsync(mainWindow)")]
    [InlineData("gallery-errors-layout-roots", "GalleryErrorInteractionChecks.RunLayoutRootsAsync(mainWindow)")]
    [InlineData("gallery-errors-notifications", "GalleryErrorInteractionChecks.RunNotificationsAsync(mainWindow)")]
    [InlineData("gallery-errors-failure-reader-return", "GalleryErrorInteractionChecks.RunFailureReaderReturnAsync(mainWindow)")]
    public void HelperJourneysHaveIndependentRequiredRootsAndExactBindings(string id, string call)
    {
        SmokeScenario entry = Assert.Single(SmokeScenarios.All, entry => entry.Id == id);
        Assert.Equal(SmokeScenarioKind.Root, entry.Kind);
        Assert.Equal("MEOWCHAN_" + id.ToUpperInvariant().Replace('-', '_') + "_ONLY", entry.Selector);
        Assert.Empty(entry.Requires);
        Assert.Null(entry.NightlySkip);
        var invocation = SmokeScenarios.Invocation(entry);
        Assert.Empty(invocation.Args);
        Assert.Contains(entry.Selector + "=1", invocation.Environment);
        Assert.Contains(entry.NightlyEnvironment, item => item.StartsWith("MEOWCHAN_VIDEO_FIXTURE="));
        if (id.StartsWith("gif-"))
            Assert.Contains(entry.NightlyEnvironment, item => item.StartsWith("MEOWCHAN_MP4_FIXTURE="));
        else
            Assert.Contains(SmokeScenarios.FunctionalAudio, entry.NightlyEnvironment);
        Assert.Contains("[\"" + id + "\"] = new(SmokeStage.Ready, mainWindow => " + call + ")", Read("SmokeScenarios.Runs.cs"));
    }

    [Fact]
    public void ReaderOuterScopesBindOnlyTheirRequiredChecks()
    {
        string source = Read("SmokeScenarios.Runs.cs");
        string dispatch = source[source.IndexOf("internal static Task RunReaderAlternativeAsync", StringComparison.Ordinal)..source.IndexOf("    // Exactly the Root", StringComparison.Ordinal)];
        Assert.Contains("\"video-lifecycle\" => VideoLifecycleChecks.RunAsync(),", dispatch);
        Assert.Contains("\"window-minimum\" => WindowMinimumChecks.RunAsync(window),", dispatch);
        Assert.Contains("=> ReaderUxChecks.RunJourneyAsync(window, id)", dispatch);
        Assert.DoesNotContain("GalleryCloseChecks", dispatch);
        Assert.DoesNotContain("VideoGalleryChecks", dispatch);
        Assert.DoesNotContain("catch", dispatch);
    }

    private static string Read(string file, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "../MeowChan.DesktopSmoke", file));
}
