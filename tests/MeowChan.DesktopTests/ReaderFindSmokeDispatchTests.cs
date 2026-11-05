using System.Runtime.CompilerServices;

namespace MeowChan.DesktopTests;

// Source-wiring regressions for the Root-owned smoke dispatch only. They do not
// launch the helper and are not native UI, OS keyboard, Windows/macOS, or PASS evidence.
public sealed class ReaderFindSmokeDispatchTests
{
    private const string Mode = "MEOWCHAN_READER_FIND_ONLY";

    [Fact]
    public void ReaderFindModeFeedsTheFocusedConflictGuardBeforeProfileCreation()
    {
        string program = Source("tests/MeowChan.DesktopSmoke/Program.cs");
        string focusedModes = Between(program, "string[] focusedModes =", "if (focusedModes.Any");

        Assert.Equal(1, Count(focusedModes, "\"" + Mode + "\""));
        int modeDeclaration = program.IndexOf("string[] focusedModes =", StringComparison.Ordinal);
        int profileCreation = program.IndexOf("Directory.CreateDirectory(profile)", StringComparison.Ordinal);
        Assert.True(modeDeclaration >= 0 && profileCreation > modeDeclaration,
            "The focused-mode conflict guard must be declared before the smoke profile is created");
        Assert.Contains("focusedModes.Any(name => Environment.GetEnvironmentVariable(name) == \"1\")", program);
        Assert.Contains("enabledModes.Length != 1 || args.Length != 0", program);
    }

    [Fact]
    public void ReaderFindDispatchUsesOnlyItsHelperAndDeclaresCloseAndReturn()
    {
        string program = Source("tests/MeowChan.DesktopSmoke/Program.cs");
        int runAsyncStart = program.IndexOf("private static async Task RunAsync()", StringComparison.Ordinal);
        Assert.True(runAsyncStart >= 0, "Missing RunAsync entry point");
        string runAsync = program[runAsyncStart..];
        string dispatch = Between(runAsync,
            "if (Environment.GetEnvironmentVariable(\"" + Mode + "\") == \"1\")",
            "if (Environment.GetEnvironmentVariable(\"MEOWCHAN_URLTEXT_EXPORT_ONLY\") == \"1\")");

        const string call = "await ReaderThreadFindChecks.RunAsync(mainWindow);";
        Assert.Equal(1, Count(program, call));
        Assert.Equal(1, Count(dispatch, call));
        string completion = "_checksPassed = true; mainWindow.Close(); return;";
        int callIndex = dispatch.IndexOf(call, StringComparison.Ordinal);
        int completionIndex = dispatch.IndexOf(completion, callIndex + call.Length, StringComparison.Ordinal);
        Assert.True(completionIndex > callIndex, "Reader-find dispatch must complete, close, and return after its helper");
    }

    [Fact]
    public void FinalScenarioWordingBelongsExclusivelyToReaderFindMode()
    {
        string program = Source("tests/MeowChan.DesktopSmoke/Program.cs");
        string scenario = Between(program,
            "string scenario = Environment.GetEnvironmentVariable(\"" + Mode + "\") == \"1\"",
            "Console.WriteLine(\"SMOKE PASS: \" + scenario");
        string readerClaim = Between(scenario,
            "Environment.GetEnvironmentVariable(\"" + Mode + "\") == \"1\"",
            "Environment.GetEnvironmentVariable(\"MEOWCHAN_URLTEXT_EXPORT_ONLY\") == \"1\"");

        const string wording = "reader find: actual offline404 native controls, whole-thread hits and managed routed keys";
        Assert.Contains(wording, readerClaim, StringComparison.Ordinal);
        Assert.Equal(1, Count(program, wording, StringComparison.Ordinal));
        Assert.Contains("no OS keyboard or Windows/macOS acceptance", readerClaim, StringComparison.Ordinal);
    }

    private static string Source(string relative, [CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Missing source boundary: " + start);
        int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, "Missing source boundary: " + end);
        return source[from..to];
    }

    private static int Count(string source, string value, StringComparison comparison = StringComparison.Ordinal)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, comparison)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
