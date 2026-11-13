using System.Diagnostics;

namespace MeowChan.PluginRuntime.Tests;

// Test-local helpers for the synthetic reauthorization process probe: portable `dotnet` host resolution,
// a private process environment, an evidence root with explicit ownership, and a child runner that always
// kills/drains the child before anything is deleted. No product code is involved.
internal static class PluginProbeEnvironment
{
    internal const string EvidenceVariable = "OCHAN_REAUTH_EVIDENCE";

    // Every location a probe (or the dotnet host it runs under) could use to reach the user's real profile.
    internal static readonly string[] PrivateDirectoryVariables =
    [
        "HOME", "TMPDIR", "TMP", "TEMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "DOTNET_CLI_HOME",
        "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR"
    ];

    internal static string ResolveDotnetHost() => ResolveDotnetHost(
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), Environment.ProcessPath,
        Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(), File.Exists);

    // Order: a valid DOTNET_HOST_PATH, the running process when it really is the dotnet host, then PATH.
    // "Valid" means an existing file that is named dotnet; the test host executable (testhost, an apphost)
    // is NOT a dotnet host and must never be used to run `<probe>.dll`. There is deliberately no hard-coded
    // install location: an unresolvable host is an explicit error, not a guess.
    internal static string ResolveDotnetHost(string? hostPathVariable, string? processPath, string? pathVariable,
        bool isWindows, Func<string, bool> fileExists)
    {
        if (IsUsableHost(hostPathVariable, isWindows, fileExists)) return hostPathVariable!;
        if (IsUsableHost(processPath, isWindows, fileExists)) return processPath!;
        string fileName = isWindows ? "dotnet.exe" : "dotnet";
        foreach (string raw in (pathVariable ?? string.Empty).Split(Path.PathSeparator))
        {
            string directory = raw.Trim().Trim('"');
            if (directory.Length == 0 || !Path.IsPathRooted(directory)) continue; // Never run a host relative to the cwd.
            string candidate = Path.Combine(directory, fileName);
            if (fileExists(candidate)) return candidate;
        }
        throw new InvalidOperationException(
            "No usable dotnet host: DOTNET_HOST_PATH is unset/invalid, the running process is not the dotnet host and PATH has no '" +
            fileName + "'.");
    }

    private static bool IsUsableHost(string? path, bool isWindows, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return false;
        string name = Path.GetFileName(path);
        bool named = isWindows
            ? string.Equals(name, "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, "dotnet", StringComparison.Ordinal);
        return named && fileExists(path);
    }

    // Points every profile/temp location of the child at an empty directory below root.
    internal static void ApplyPrivateEnvironment(ProcessStartInfo start, string root)
    {
        foreach (string name in PrivateDirectoryVariables)
        {
            string directory = Path.Combine(root, "private", name);
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); // XDG_RUNTIME_DIR must be 0700.
            start.Environment[name] = directory;
        }
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
    }

    internal sealed record ChildResult(int Pid, int ExitCode, string Stdout, string Stderr);

    // Runs one child. On normal exit, timeout or any other failure the child process tree has been killed and
    // both pipes are drained (bounded) before this returns or throws, so the caller may delete its directory.
    internal static async Task<ChildResult> RunChildAsync(ProcessStartInfo start, TimeSpan timeout, Action<int>? started = null)
    {
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start private probe.");
        int pid = child.Id;
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
        try
        {
            started?.Invoke(pid);
            await child.WaitForExitAsync().WaitAsync(timeout);
        }
        finally
        {
            await StopAndDrain(child, stdout, stderr);
        }
        return new ChildResult(pid, child.ExitCode,
            await stdout.WaitAsync(TimeSpan.FromSeconds(5)), await stderr.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // Best effort and exception-free so it never masks the primary failure; only this newly owned process
    // tree is touched, never any unrelated dotnet process.
    private static async Task StopAndDrain(Process child, Task<string> stdout, Task<string> stderr)
    {
        try { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
        foreach (Task pipe in new[] { stdout, stderr })
        {
            try { await pipe.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }
}

// A uniquely named synthetic profile/evidence directory. Without OCHAN_REAUTH_EVIDENCE it lives in the temp
// directory and is owned (deleted on dispose); below an explicit user-chosen evidence directory it is kept.
internal sealed class PluginProbeEvidenceRoot : IAsyncDisposable
{
    internal string RootPath { get; }
    internal bool OwnsRoot { get; }
    private bool disposed;

    private PluginProbeEvidenceRoot(string rootPath, bool ownsRoot) { RootPath = rootPath; OwnsRoot = ownsRoot; }

    internal static PluginProbeEvidenceRoot Create() =>
        Create(Environment.GetEnvironmentVariable(PluginProbeEnvironment.EvidenceVariable), Path.GetTempPath());

    internal static PluginProbeEvidenceRoot Create(string? configuredEvidence, string tempPath)
    {
        bool userChosen = !string.IsNullOrWhiteSpace(configuredEvidence);
        string parent = userChosen ? configuredEvidence! : tempPath;
        string root = Path.Combine(parent, "reauth-process-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new PluginProbeEvidenceRoot(root, ownsRoot: !userChosen);
    }

    // The caller must have finished every child (see RunChildAsync) before disposing.
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        if (!OwnsRoot) return; // Explicitly requested evidence is never deleted.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (Directory.Exists(RootPath)) Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                    throw new IOException("Could not remove temporary synthetic profile '" + RootPath + "'.", exception);
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt)); // Windows may release handles late.
            }
        }
    }
}
