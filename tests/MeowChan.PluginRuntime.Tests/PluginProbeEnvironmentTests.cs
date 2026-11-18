using System.Diagnostics;
using MeowChan.TestSupport;

namespace MeowChan.PluginRuntime.Tests;

// Covers the test-side helpers of PluginReauthorizationProcessTests. Only a trivial `dotnet --list-runtimes`
// child is started; the heavy reauthorization process E2E is in PluginReauthorizationProcessTests.
public sealed class PluginProbeEnvironmentTests
{
    private static readonly string Bin = Path.Combine(Path.DirectorySeparatorChar.ToString(), "fake", "bin");
    private static readonly string Other = Path.Combine(Path.DirectorySeparatorChar.ToString(), "fake", "other");
    private static string Join(params string[] directories) => string.Join(Path.PathSeparator, directories);
    private static Func<string, bool> Exists(params string[] files) => files.ToHashSet(StringComparer.Ordinal).Contains;

    [Fact]
    public void ValidDotnetHostPathWinsOverRunningProcessAndPath()
    {
        string configured = Path.Combine(Bin, "dotnet"), running = Path.Combine(Other, "dotnet");
        string resolved = PluginProbeEnvironment.ResolveDotnetHost(configured, running, Join(Other), false,
            Exists(configured, running, Path.Combine(Other, "dotnet")));
        Assert.Equal(configured, resolved);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("testhost")]
    [InlineData("relative")]
    [InlineData("")]
    public void InvalidDotnetHostPathFallsThroughToRunningDotnetHost(string kind)
    {
        string running = Path.Combine(Other, "dotnet");
        string? configured = kind switch
        {
            "missing" => Path.Combine(Bin, "dotnet"),                  // Rooted, named dotnet, but not on disk.
            "testhost" => Path.Combine(Bin, "testhost"),               // Exists, but is not a dotnet host.
            "relative" => Path.Combine("sdk", "dotnet"),               // Never relative to the cwd.
            _ => string.Empty
        };
        string resolved = PluginProbeEnvironment.ResolveDotnetHost(configured, running, null, false,
            Exists(running, Path.Combine(Bin, "testhost"), Path.Combine("sdk", "dotnet")));
        Assert.Equal(running, resolved);
    }

    [Fact]
    public void TestHostExecutableIsNotUsedAndPathIsSearchedInOrder()
    {
        string testhost = Path.Combine(Bin, "testhost"), first = Path.Combine(Other, "dotnet"), second = Path.Combine(Bin, "dotnet");
        string resolved = PluginProbeEnvironment.ResolveDotnetHost(null, testhost,
            Join("", "relative", "\"" + Other + "\"", Bin), false, Exists(testhost, first, second));
        Assert.Equal(first, resolved);
    }

    [Fact]
    public void WindowsHostIsMatchedAsDotnetExeCaseInsensitively()
    {
        string running = Path.Combine(Bin, "DOTNET.EXE"), onPath = Path.Combine(Other, "dotnet.exe");
        Assert.Equal(running, PluginProbeEnvironment.ResolveDotnetHost(null, running, Join(Other), true, Exists(running, onPath)));
        Assert.Equal(onPath, PluginProbeEnvironment.ResolveDotnetHost(null, Path.Combine(Bin, "testhost.exe"), Join(Other), true,
            Exists(Path.Combine(Bin, "testhost.exe"), onPath)));
        // A POSIX-style `dotnet` file is not what a Windows run resolves.
        Assert.Throws<InvalidOperationException>(() => PluginProbeEnvironment.ResolveDotnetHost(null, null, Join(Other), true,
            Exists(Path.Combine(Other, "dotnet"))));
    }

    [Fact]
    public void UnresolvableHostFailsExplicitlyInsteadOfGuessingAnInstallLocation()
    {
        var failure = Assert.Throws<InvalidOperationException>(() =>
            PluginProbeEnvironment.ResolveDotnetHost(null, Path.Combine(Bin, "testhost"), Join(Bin, Other), false, _ => false));
        Assert.Contains("dotnet", failure.Message);
        Assert.DoesNotContain("/usr/share", failure.Message);
    }

    [Fact]
    public void DefaultResolutionUsesTheRealEnvironmentAndFindsAnExistingDotnetHost()
    {
        string host = PluginProbeEnvironment.ResolveDotnetHost();
        Assert.True(File.Exists(host), host);
        Assert.Equal(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", Path.GetFileName(host), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void PrivateEnvironmentRedirectsEveryProfileAndTempLocationBelowTheRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "probe-env-" + Guid.NewGuid().ToString("N"));
        try
        {
            ProcessStartInfo start = new("dotnet");
            PluginProbeEnvironment.ApplyPrivateEnvironment(start, root);
            foreach (string name in PluginProbeEnvironment.PrivateDirectoryVariables)
            {
                string value = Assert.IsType<string>(start.Environment[name]);
                Assert.Equal(Path.Combine(root, "private", name), value);
                Assert.True(Directory.Exists(value), name);
            }
            Assert.NotEqual(Environment.GetEnvironmentVariable("HOME"), start.Environment["HOME"]);
            Assert.Equal("1", start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]);
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(start.Environment["XDG_RUNTIME_DIR"]!));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DefaultTemporaryEvidenceRootIsOwnedAndRemovedWithItsContents()
    {
        string temp = Path.Combine(Path.GetTempPath(), "probe-temp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var evidence = PluginProbeEvidenceRoot.Create(null, temp);
            Assert.True(evidence.OwnsRoot);
            Assert.Equal(temp, Path.GetDirectoryName(evidence.RootPath));
            Assert.StartsWith("reauth-process-", Path.GetFileName(evidence.RootPath), StringComparison.Ordinal);
            Directory.CreateDirectory(Path.Combine(evidence.RootPath, "profile", "plugin-state"));
            await File.WriteAllTextAsync(Path.Combine(evidence.RootPath, "profile", "plugin-state", "state.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(evidence.RootPath, "01-seed.json"), "{}");
            await evidence.DisposeAsync();
            Assert.False(Directory.Exists(evidence.RootPath));
            Assert.True(Directory.Exists(temp));
            await evidence.DisposeAsync(); // Idempotent.
            Assert.Empty(Directory.GetFileSystemEntries(temp));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task ExplicitlyConfiguredEvidenceRootIsPreserved()
    {
        string requested = Path.Combine(Path.GetTempPath(), "probe-user-" + Guid.NewGuid().ToString("N"));
        string temp = Path.Combine(Path.GetTempPath(), "probe-unused-" + Guid.NewGuid().ToString("N"));
        try
        {
            var evidence = PluginProbeEvidenceRoot.Create(requested, temp);
            Assert.False(evidence.OwnsRoot);
            Assert.Equal(requested, Path.GetDirectoryName(evidence.RootPath));
            Assert.False(Directory.Exists(temp));
            string kept = Path.Combine(evidence.RootPath, "01-seed.json");
            await File.WriteAllTextAsync(kept, "{}");
            await evidence.DisposeAsync();
            Assert.True(File.Exists(kept));
        }
        finally { if (Directory.Exists(requested)) Directory.Delete(requested, recursive: true); }
    }

    [Fact]
    public async Task ChildRunnerReturnsExitCodeAndOutputOfTheResolvedHostUnderPrivateEnvironment()
    {
        await using var evidence = PluginProbeEvidenceRoot.Create(null, Path.GetTempPath());
        ProcessStartInfo start = new(PluginProbeEnvironment.ResolveDotnetHost()) { WorkingDirectory = evidence.RootPath };
        start.ArgumentList.Add("--list-runtimes");
        PluginProbeEnvironment.ApplyPrivateEnvironment(start, evidence.RootPath);
        int reported = 0;
        var result = await PluginProbeEnvironment.RunChildAsync(start, TimeSpan.FromSeconds(30), pid => reported = pid);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(reported, result.Pid);
        Assert.Contains("Microsoft.NETCore.App", result.Stdout);
    }

    [Fact]
    public async Task TimedOutChildIsKilledBeforeTheOwnedRootIsRemoved()
    {
        var evidence = PluginProbeEvidenceRoot.Create(null, Path.GetTempPath());
        ProcessStartInfo start = new(PluginProbeEnvironment.ResolveDotnetHost()) { WorkingDirectory = evidence.RootPath };
        start.ArgumentList.Add(BuiltTestBinaries.AssemblyOf(Path.Combine("tests", "MeowChan.PluginRuntime.Fixture")));
        start.ArgumentList.Add("child");
        PluginProbeEnvironment.ApplyPrivateEnvironment(start, evidence.RootPath);
        int pid = 0;
        // The fixture's child mode is intentionally uncooperative. Use a real deadline rather than zero, whose
        // outcome races with process startup when the child is already complete.
        await Assert.ThrowsAsync<TimeoutException>(() =>
            PluginProbeEnvironment.RunChildAsync(start, TimeSpan.FromMilliseconds(100), started => pid = started));
        Assert.NotEqual(0, pid);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid)); // Gone, not merely signalled.
        await evidence.DisposeAsync();
        Assert.False(Directory.Exists(evidence.RootPath));
    }
}
