using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

// Source-order regression only: no window, profile, worker or HTTP. The native smoke still
// owns the held pause, one-shot pause failure and close-while-pending acceptance checks.
public sealed class BooruBusyFallbackFixtureTests
{
    [Theory]
    [InlineData("RunHeldPauseAsync")]
    [InlineData("RunClosingAsync")]
    public void FreshProfileSettingsPrecedeTheMediaSubdirectoryAndSession(string scenario)
    {
        string source = Source();
        int start = source.IndexOf("private static async Task " + scenario + "(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int session = source.IndexOf("PauseFixtureSource? source = null;", start, StringComparison.Ordinal);
        Assert.True(session > start);
        string setup = source[start..session];
        int profile = setup.IndexOf("TemporaryWorkspacePaths.CreateDirectory(", StringComparison.Ordinal);
        int save = setup.IndexOf("}.SaveConfig(profile);", StringComparison.Ordinal);
        int media = setup.IndexOf("Directory.CreateDirectory(media);", StringComparison.Ordinal);

        // Missing settings plus any child directory is not a fresh profile. Do not bypass
        // the recovery guard: seed settings before creating media, just like other fixtures.
        Assert.Contains("DownloadPath = media,", setup);
        Assert.True(profile >= 0 && save > profile && media > save,
            scenario + " must save settings into its empty profile before creating media or starting the session.");
    }

    private static string Source([CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!,
            "../MeowChan.DesktopSmoke/BooruBusyFallbackChecks.cs")));
}
