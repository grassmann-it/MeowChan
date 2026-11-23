using System.Reflection;
using System.Runtime.CompilerServices;
using MeowChan;

namespace MeowChan.DesktopTests;

// No window or fixture state mutation: native focus behavior remains covered by
// both missing-anchor cases in the download-session smoke.
public sealed class ReaderPassiveFocusFixtureTests
{
    [Fact]
    public void MissingAnchorFixtureInvokesTheParameterlessProductionRender()
    {
        MethodInfo render = typeof(ArchivePage).GetMethod("RenderThread",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Empty(render.GetParameters());
        string source = Source();
        Assert.Contains("GetMethod(\"RenderThread\", Private)!.Invoke(page, null);", source);
    }

    [Fact]
    public void MissingAnchorStillExercisesGuardedRealRestoreAndBothFocusOutcomes()
    {
        string source = Source();
        Assert.Contains("[page, false]", source);
        Assert.Contains("[id, \"__pf05_missing_post__\", 0d, selected]", source);
        Assert.Contains(".Invoke(page, [anchor, true, intent, null, null, false])", source);
        Assert.Contains("await LocalizationCase(\"missing-anchor-new-search\", search, missing: true);", source);
        Assert.Contains("await LocalizationCase(\"missing-anchor-replace-own-post\", null, missing: true);", source);
        Assert.Contains("expected.IsKeyboardFocusWithin && latePostFocus == 0", source);
        Assert.Contains("!ReferenceEquals(original, replacement) && replacement.IsKeyboardFocusWithin", source);
        Assert.Contains("Math.Abs(replacement.TranslatePoint(default, posts)!.Value.Y - before) <= 2", source);
    }

    private static string Source([CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!,
            "../MeowChan.DesktopSmoke/ReaderPassiveFocusChecks.cs")));
}
