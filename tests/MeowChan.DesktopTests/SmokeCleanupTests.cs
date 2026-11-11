using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.DesktopSmoke;

namespace MeowChan.DesktopTests;

public sealed class SmokeCleanupTests
{
    [Fact]
    public async Task SuccessfulCleanupRunsExactlyOnceWithoutAReport()
    {
        int calls = 0;
        await SmokeCleanup.RunAsync(null, () => { calls++; return Task.CompletedTask; }, _ => Assert.Fail("No failure expected."));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CleanupFailureAfterAPassingCheckRemainsAFailure()
    {
        var cleanup = new InvalidOperationException("cleanup");
        Exception? reported = null;
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SmokeCleanup.RunAsync(null, () => Task.FromException(cleanup), ex => reported = ex));
        Assert.Same(cleanup, actual);
        Assert.Same(cleanup, reported);
    }

    [Fact]
    public async Task CleanupFailureCannotReplaceThePrimaryCheckFailure()
    {
        var primary = new InvalidOperationException("check failed first");
        var cleanup = new TimeoutException("restore timed out");
        Exception? reported = null;
        Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            try { throw primary; }
            finally { await SmokeCleanup.RunAsync(primary, () => Task.FromException(cleanup), ex => reported = ex); }
        });
        Assert.Same(primary, actual);
        Assert.Same(cleanup, reported);
    }

    [Fact]
    public async Task BrokenDiagnosticCannotReplacePrimaryOrCleanupFailure()
    {
        var primary = new InvalidOperationException("primary");
        var cleanup = new TimeoutException("cleanup");
        await SmokeCleanup.RunAsync(primary, () => throw cleanup, _ => throw new Exception("diagnostic"));
        Exception actual = await Assert.ThrowsAsync<TimeoutException>(() =>
            SmokeCleanup.RunAsync(null, () => throw cleanup, _ => throw new Exception("diagnostic")));
        Assert.Same(cleanup, actual);
    }

    [Fact]
    public void BrokenScreenshotIsOnlyADiagnosticFailure()
    {
        SmokeCleanup.Diagnose(() => throw new InvalidOperationException("no renderer"));
    }

    // A wait in a plain finally replaces the check's own exception when it times out after a failure.
    // These two already record the primary failure and catch each cleanup failure themselves.
    [Fact]
    public void FinallyBlocksAwaitCleanupWaitsOnlyThroughSmokeCleanup()
    {
        string[] ownHandling = ["ArchiveExportStatusLanguageRefreshChecks.cs", "PreferenceStartupChecks.cs"];
        List<string> unguarded = [];
        foreach (string file in Directory.EnumerateFiles(SmokeRoot(), "*.cs"))
        {
            if (ownHandling.Contains(Path.GetFileName(file))) continue;
            string source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, "\\bfinally\\s*\\{"))
            {
                int end = match.Index + match.Length, depth = 1;
                while (depth > 0) depth += source[end++] switch { '{' => 1, '}' => -1, _ => 0 };
                string body = source[match.Index..end];
                if (Regex.IsMatch(body, "await\\s+[\\w.]*UntilAsync\\(") && !body.Contains("SmokeCleanup.RunAsync(", StringComparison.Ordinal))
                    unguarded.Add(Path.GetFileName(file) + ":" + (source[..match.Index].Count(c => c == '\n') + 1));
            }
        }
        Assert.Empty(unguarded);
    }

    private static string SmokeRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../MeowChan.DesktopSmoke"));
}
