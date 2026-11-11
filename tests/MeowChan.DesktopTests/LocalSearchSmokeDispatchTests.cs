using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

// Root dispatch source guards only: these tests do not execute a native helper.
public sealed class LocalSearchSmokeDispatchTests
{
    [Theory]
    [InlineData("MEOWCHAN_FILENAME_SEARCH_ONLY", "FilenameSearchChecks", "filename search standalone dialog:")]
    [InlineData("MEOWCHAN_MEDIA_HASH_SEARCH_ONLY", "MediaHashSearchChecks", "SHA-256 search standalone dialog:")]
    public void StandaloneSearchModesHaveExclusivePreProfileGuardAndAwaitTheirOwnHelper(
        string mode, string helper, string claim)
    {
        string source = Source("tests/MeowChan.DesktopSmoke/Program.cs");
        int modes = source.IndexOf("string[] focusedModes =", StringComparison.Ordinal);
        int guard = source.IndexOf("if (focusedModes.Any", modes, StringComparison.Ordinal);
        int profile = source.IndexOf("Directory.CreateDirectory(profile)", StringComparison.Ordinal);
        Assert.True(modes >= 0 && guard > modes && profile > guard);
        Assert.Equal(1, Count(source[modes..guard], "\"" + mode + "\""));
        Assert.Contains("enabledModes.Length != 1 || args.Length != 0", source[guard..profile]);
        int ready = source.IndexOf("SMOKE MainWindow visible with empty isolated session.", StringComparison.Ordinal);
        int dispatch = source.IndexOf("if (Environment.GetEnvironmentVariable(\"" + mode + "\") == \"1\")", ready, StringComparison.Ordinal);
        int call = source.IndexOf("await " + helper + ".RunAsync(mainWindow);", dispatch, StringComparison.Ordinal);
        int close = source.IndexOf("_checksPassed = true; mainWindow.Close(); return;", call, StringComparison.Ordinal);
        int end = source.IndexOf('}', dispatch);
        Assert.True(ready >= 0 && dispatch > ready && call > dispatch && close > call && end > close);
        Assert.Equal(1, Count(source, "await " + helper + ".RunAsync(mainWindow);"));
        Assert.DoesNotContain("host navigation acceptance", source[dispatch..end]); // no claim in execution branch
    }

    [Fact]
    public void HostSearchModeAwaitsBothRealHelpersBeforeItsOwnCompletion()
    {
        const string mode = "MEOWCHAN_FILENAME_SEARCH_HOST_ONLY";
        string source = Source("tests/MeowChan.DesktopSmoke/Program.cs");
        int modes = source.IndexOf("string[] focusedModes =", StringComparison.Ordinal);
        int guard = source.IndexOf("if (focusedModes.Any", modes, StringComparison.Ordinal);
        int profile = source.IndexOf("Directory.CreateDirectory(profile)", StringComparison.Ordinal);
        Assert.True(modes >= 0 && guard > modes && profile > guard);
        Assert.Equal(1, Count(source[modes..guard], "\"" + mode + "\""));
        Assert.Contains("enabledModes.Length != 1 || args.Length != 0", source[guard..profile]);
        int ready = source.IndexOf("SMOKE MainWindow visible with empty isolated session.", StringComparison.Ordinal);
        int dispatch = source.IndexOf("if (Environment.GetEnvironmentVariable(\"" + mode + "\") == \"1\")", ready, StringComparison.Ordinal);
        int navigation = source.IndexOf("await FilenameSearchHostNavigationChecks.RunAsync(mainWindow);", dispatch, StringComparison.Ordinal);
        int lifetime = source.IndexOf("await FilenameSearchHostLifetimeChecks.RunAsync(mainWindow);", dispatch, StringComparison.Ordinal);
        int close = source.IndexOf("_checksPassed = true; mainWindow.Close(); return;", lifetime, StringComparison.Ordinal);
        int end = source.IndexOf('}', dispatch);
        Assert.True(ready >= 0 && dispatch > ready && navigation > dispatch && lifetime > navigation
            && close > lifetime && end > close);
        Assert.Equal(1, Count(source, "await FilenameSearchHostNavigationChecks.RunAsync(mainWindow);"));
        Assert.Equal(1, Count(source, "await FilenameSearchHostLifetimeChecks.RunAsync(mainWindow);"));
        Assert.DoesNotContain("FilenameSearchChecks.RunAsync", source[dispatch..end]);
        Assert.DoesNotContain("MediaHashSearchChecks.RunAsync", source[dispatch..end]);
    }

    private static string Source(string relative, [CallerFilePath] string file = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "../..", relative)));

    private static int Count(string source, string value)
    {
        int count = 0, offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        { count++; offset += value.Length; }
        return count;
    }
}
