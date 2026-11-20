using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

/// <summary>
/// Synthetic P01 read-boundary regressions. FIFO operations (including lease
/// disposal) live only in a watchdog-owned child vstest process, following the
/// SessionStoreTests lease-probe pattern. No writer/rescue handle exists on the
/// passing path, and the parent never owns the child's ProfileLease monitor.
/// </summary>
public sealed class NativePrivateMetadataSpecialFileTests(NativePrivateMetadataSpecialFileTests.FifoProcessFixture fifo)
    : IClassFixture<NativePrivateMetadataSpecialFileTests.FifoProcessFixture>
{
    internal const string ProbeVariable = "MEOWCHAN_PRIVATE_METADATA_FIFO_PROBE";

    private const string PrivateMarker = "synthetic-private-profile-and-note-marker";
    private static readonly ResourceId Thread = new("synthetic", "b", "100");
    private static readonly TimeSpan PromptLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ChildLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReapLimit = TimeSpan.FromSeconds(10);

    [NativePrivateUnixTheory]
    [InlineData("media-favorites.json", false)]
    [InlineData("media-favorites.json", true)]
    [InlineData("thread-favorites.json", false)]
    [InlineData("thread-favorites.json", true)]
    [InlineData("post-bookmarks.json", false)]
    [InlineData("post-bookmarks.json", true)]
    [InlineData("media-favorites.json.bak", false)]
    [InlineData("media-favorites.json.bak", true)]
    [InlineData("thread-favorites.json.bak", false)]
    [InlineData("thread-favorites.json.bak", true)]
    public Task SelectedNoWriterFifoFailsPromptlyAndLeavesLeaseUsable(string name, bool recheck) =>
        RunFifoChildAsync("selected", name, recheck);

    [NativePrivateUnixTheory]
    [InlineData("media-favorites.json")]
    [InlineData("thread-favorites.json")]
    [InlineData("post-bookmarks.json")]
    [InlineData("media-favorites.json.bak")]
    [InlineData("thread-favorites.json.bak")]
    public Task UnselectedNoWriterFifoIsUntouchedOnCaptureAndRecheck(string name) =>
        RunFifoChildAsync("unselected", name, recheck: false);

    [NativePrivateUnixTheory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void RenamedProfileDirectoryFailsSelectedCaptureAndRecheckInsteadOfBecomingEmpty(int category)
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, PrivateMarker);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        SeedPendingEvidence(profile);
        string[] before = Inventory(profile);
        NativePrivateMetadataSelection selection = new(category == 0, category == 1, category == 2);
        NativePrivateMetadataSnapshot empty = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection);
        string moved = profile + "-moved";
        Directory.Move(profile, moved);
        try
        {
            IOException capture = Assert.Throws<IOException>(() =>
                NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection));
            AssertOpaque(capture, profile);
            IOException recheck = Assert.Throws<IOException>(() => empty.IsCurrent(lease));
            AssertOpaque(recheck, profile);
        }
        finally
        {
            Directory.Move(moved, profile);
        }
        Assert.True(empty.IsCurrent(lease));
        Assert.Equal(before, Inventory(profile));
    }

    [NativePrivateSharingTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MediaFavoriteSharingErrorsAreOpaqueForPrimaryAndAbsentPrimaryBackup(bool backup)
    {
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, PrivateMarker);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        var selection = new NativePrivateMetadataSelection(MediaFavorites: true);
        NativePrivateMetadataSnapshot empty = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection);
        string path = MediaPath(profile, backup);
        SeedMediaFaultFile(path);
        SeedPendingEvidence(profile);
        string[] before = Inventory(profile);

        using (FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Establish the actual sharing failure independently of the snapshot seam
            // (Windows kernel sharing / Unix managed exclusive flock). Parser or
            // backup-recovery rejection must not satisfy this test.
            Assert.ThrowsAny<IOException>(() => { using var control = OpenControl(path); });
            AssertMediaAccessFailures(lease, empty, selection, profile, backup, unauthorized: false);
        }

        Assert.Equal(before, Inventory(profile));
        File.Delete(path); // Remove only the deliberately injected fixture.
        AssertLeaseUsable(lease, empty);
    }

    [NativePrivatePermissionTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MediaFavoriteAccessDeniedIsOpaqueForPrimaryAndAbsentPrimaryBackup(bool backup)
    {
        // NativePrivatePermissionTheoryAttribute skips Windows discovery. Keep a
        // runtime guard as well so CA1416 can prove this Unix-only body is never
        // entered on Windows; this is a fail-closed guard, not a successful skip.
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix permission-denial coverage cannot run on Windows.");

        // This attribute reports an actual SKIP on root/unsupported OS, not a
        // successful early return. A non-root runner that bypasses mode bits fails
        // the control assertion explicitly rather than claiming denial coverage.
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, PrivateMarker);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        var selection = new NativePrivateMetadataSelection(MediaFavorites: true);
        NativePrivateMetadataSnapshot empty = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection);
        string path = MediaPath(profile, backup);
        SeedMediaFaultFile(path);
        SeedPendingEvidence(profile);
        string[] before = Inventory(profile);
        UnixFileMode mode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
            Assert.Throws<UnauthorizedAccessException>(() => { using var control = OpenControl(path); });
            AssertMediaAccessFailures(lease, empty, selection, profile, backup, unauthorized: true);
            Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(path));
        }
        finally
        {
            File.SetUnixFileMode(path, mode);
        }

        Assert.Equal(before, Inventory(profile));
        File.Delete(path);
        AssertLeaseUsable(lease, empty);
    }

    // A separate discovered test is required by the existing child-vstest pattern.
    // It is explicitly skipped without the parent's probe environment, and the
    // parent requires a completion receipt, so a zero-tests/skip exit cannot pass.
    [NativePrivateFifoProbeFact]
    public void FifoChildProbe()
    {
        using NamedPipeClientStream pipe = new(".", Environment.GetEnvironmentVariable(ProbeVariable)!, PipeDirection.InOut);
        pipe.Connect((int)ChildLimit.TotalMilliseconds);
        using StreamReader reader = new(pipe, leaveOpen: true);
        using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        while (reader.ReadLine() is { } line)
        {
            FifoRequest request = JsonSerializer.Deserialize<FifoRequest>(line)!;
            NativePrivateMetadataSelection selected = SelectionFor(request.Name);
            string profile = Path.Combine(request.Root, PrivateMarker);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            {
                SeedPendingEvidence(profile);
                if (request.Scenario == "selected")
                    CheckSelectedFifo(lease, request.Name, selected, request.Recheck);
                else
                {
                    Assert.Equal("unselected", request.Scenario);
                    CheckUnselectedFifo(lease, request.Name, selected);
                }
            } // Receipt is sent only AFTER lease.Dispose(), still covered by the parent watchdog.
            File.WriteAllText(Path.Combine(request.Root, "completed"), "fifo-probe-completed");
            writer.WriteLine(request.Root);
        }
    }

    private sealed record FifoRequest(string Scenario, string Name, bool Recheck, string Root);

    private static void CheckSelectedFifo(ProfileLease lease, string name,
        NativePrivateMetadataSelection selection, bool recheck)
    {
        // In particular, recheck must retain an empty selected snapshot BEFORE
        // injection. A FIFO must throw, never become a current-empty/false result.
        NativePrivateMetadataSnapshot empty = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection);
        AssertEmpty(empty);
        Assert.True(empty.IsCurrent(lease));
        string path = Path.Combine(lease.ProfileDirectory, name);
        string[] before = Inventory(lease.ProfileDirectory);
        FifoStamp stamp = CreateNoWriterFifo(path);
        string[] injectedNames = Names(lease.ProfileDirectory);
        try
        {
            Exception? error = Prompt(() => Record.Exception(() =>
            {
                if (recheck) _ = empty.IsCurrent(lease);
                else _ = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection);
            }));
            Assert.IsType<InvalidDataException>(error);
            AssertOpaque(error!, lease.ProfileDirectory);
            Assert.Equal(stamp, ReadFifoStamp(path));
            Assert.Equal(injectedNames, Names(lease.ProfileDirectory));
            Assert.Equal(before, Inventory(lease.ProfileDirectory, excludedFifo: name));
            // Prove the same lease is usable while the bad private path is present.
            Prompt(() =>
            {
                var publicOnly = NativePrivateMetadataSnapshot.Capture(lease, [Thread]);
                Assert.True(publicOnly.IsCurrent(lease));
                return true;
            });
        }
        finally
        {
            File.Delete(path); // Unlink only; never open/drain a FIFO in cleanup.
        }
        Assert.Equal(before, Inventory(lease.ProfileDirectory));
        Prompt(() => { AssertLeaseUsable(lease, empty); return true; });
    }

    private static void CheckUnselectedFifo(ProfileLease lease, string name, NativePrivateMetadataSelection excluded)
    {
        // Exercise both public-only and all OTHER private categories. Each retained
        // snapshot predates the FIFO, so IsCurrent cannot simply use a cached read.
        NativePrivateMetadataSelection[] selections =
            [default, new(!excluded.ThreadFavorites, !excluded.MediaFavorites, !excluded.PostBookmarksAndNotes)];
        NativePrivateMetadataSnapshot[] empty = selections.Select(selection =>
            NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection)).ToArray();
        string path = Path.Combine(lease.ProfileDirectory, name);
        string[] before = Inventory(lease.ProfileDirectory);
        FifoStamp stamp = CreateNoWriterFifo(path);
        string[] injectedNames = Names(lease.ProfileDirectory);
        try
        {
            for (int i = 0; i < selections.Length; i++)
            {
                NativePrivateMetadataSelection selection = selections[i];
                var captured = Prompt(() => NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection));
                Assert.Equal(selection, captured.Selection);
                AssertEmpty(captured);
                Assert.True(Prompt(() => captured.IsCurrent(lease)));
                Assert.True(Prompt(() => empty[i].IsCurrent(lease)));
                Assert.Equal(stamp, ReadFifoStamp(path)); // Still the same FIFO inode, mode and timestamps.
                Assert.Equal(injectedNames, Names(lease.ProfileDirectory));
                Assert.Equal(before, Inventory(lease.ProfileDirectory, excludedFifo: name));
            }
        }
        finally { File.Delete(path); }
        Assert.Equal(before, Inventory(lease.ProfileDirectory));
        Prompt(() => { AssertLeaseUsable(lease, empty[0]); return true; });
    }

    private static void AssertMediaAccessFailures(ProfileLease lease, NativePrivateMetadataSnapshot empty,
        NativePrivateMetadataSelection selection, string profile, bool backup, bool unauthorized)
    {
        foreach (bool recheck in new[] { false, true })
        {
            Exception? error = Prompt(() => Record.Exception(() =>
            {
                if (recheck) _ = empty.IsCurrent(lease);
                else _ = NativePrivateMetadataSnapshot.Capture(lease, [Thread], selection);
            }));
            if (unauthorized) Assert.IsType<UnauthorizedAccessException>(error);
            else Assert.IsType<IOException>(error);
            AssertOpaque(error!, profile);
            if (backup) Assert.False(File.Exists(MediaPath(profile, backup: false)));
            // Do this after EACH failure, while the controlled fault is still held.
            AssertLeaseUsable(lease);
        }
    }

    private static void AssertOpaque(Exception error, string profile)
    {
        // Full ToString includes all inner exceptions, not merely Message.
        string diagnostic = error.ToString();
        Assert.DoesNotContain(profile, diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PrivateMarker, diagnostic, StringComparison.Ordinal);
    }

    private static void AssertLeaseUsable(ProfileLease lease, NativePrivateMetadataSnapshot? selectedEmpty = null)
    {
        Assert.True(lease.RunExclusive(() => true));
        NativePrivateMetadataSnapshot fresh = NativePrivateMetadataSnapshot.Capture(lease, [Thread]);
        Assert.True(fresh.IsCurrent(lease));
        if (selectedEmpty != null)
        {
            Assert.True(selectedEmpty.IsCurrent(lease));
            var all = NativePrivateMetadataSnapshot.Capture(lease, [Thread], new(true, true, true));
            AssertEmpty(all);
            Assert.True(all.IsCurrent(lease));
        }
    }

    private static void AssertEmpty(NativePrivateMetadataSnapshot snapshot)
    {
        Assert.Empty(snapshot.ThreadFavorites);
        Assert.Empty(snapshot.MediaFavorites);
        Assert.Empty(snapshot.PostBookmarksAndNotes);
    }

    private static NativePrivateMetadataSelection SelectionFor(string name) => name switch
    {
        "media-favorites.json" or "media-favorites.json.bak" => new(MediaFavorites: true),
        "thread-favorites.json" or "thread-favorites.json.bak" => new(ThreadFavorites: true),
        "post-bookmarks.json" => new(PostBookmarksAndNotes: true),
        _ => throw new ArgumentOutOfRangeException(nameof(name), "Unknown synthetic FIFO target.")
    };

    private static string MediaPath(string profile, bool backup) =>
        Path.Combine(profile, "media-favorites.json" + (backup ? ".bak" : ""));

    private static void SeedMediaFaultFile(string path)
    {
        FavoriteEntry entry = new(new(Thread, "media", "original", new string('a', 64)), 12, PrivateMarker + ".jpg");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, Entries = new[] { entry } }));
    }

    private static void SeedPendingEvidence(string profile) => File.WriteAllText(
        Path.Combine(profile, "post-bookmarks.json.pending-" + Guid.NewGuid().ToString("N")), PrivateMarker);

    private static FileStream OpenControl(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static T Prompt<T>(Func<T> action)
    {
        // For FIFOs this synchronous call exists ONLY inside the isolated child.
        // Stopwatch covers each call separately; the parent supplies the hard stop
        // if control never returns (including during a follow-up lease operation).
        Stopwatch elapsed = Stopwatch.StartNew();
        T result = action();
        Assert.True(elapsed.Elapsed < PromptLimit, "The synthetic private metadata operation exceeded the 5-second prompt-failure bound.");
        return result;
    }

    private static string[] Names(string profile) => Directory.GetFileSystemEntries(profile)
        .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal).ToArray();

    private static string[] Inventory(string profile, string? excludedFifo = null) =>
        Directory.GetFileSystemEntries(profile).Where(path => Path.GetFileName(path) != excludedFifo)
            .Order(StringComparer.Ordinal).Select(path =>
            {
                string name = Path.GetFileName(path);
                FileInfo info = new(path);
                // Only the two exact exclusive lease files omit byte reads. Pending
                // evidence and every other ordinary file keep their hash and mtime.
                string hash = name is ".meowchan-profile.lock" or ".ochan-profile.lock"
                    ? "exclusive-lock" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                return $"{name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}:{hash}";
            }).ToArray();

    private static FifoStamp CreateNoWriterFifo(string path)
    {
        Assert.DoesNotContain(Path.GetFileName(path), Names(Path.GetDirectoryName(path)!));
        Assert.Equal(0, MkFifo(path, 0x180 /* 0600 */));
        return ReadFifoStamp(path);
    }

    private static FifoStamp ReadFifoStamp(string path)
    {
        Assert.Equal(0, LStat(path, out FileStatus status));
        Assert.Equal(0x1000, status.Mode & 0xF000); // S_IFIFO, not a replaced regular file.
        return new(status.Dev, status.Ino, status.Mode, status.Size,
            status.ATime, status.ATimeNsec, status.MTime, status.MTimeNsec, status.CTime, status.CTimeNsec);
    }

    private readonly record struct FifoStamp(long Device, long Inode, int Mode, long Size,
        long AccessSeconds, long AccessNanos, long ModifiedSeconds, long ModifiedNanos, long ChangedSeconds, long ChangedNanos);

    private Task RunFifoChildAsync(string scenario, string name, bool recheck) =>
        fifo.RunAsync(scenario, name, recheck);

    // Only the test-platform process is shared. Every request has a fresh workspace,
    // profile lease, FIFO and completion receipt; no snapshot or product state is reused.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001",
        Justification = "xUnit invokes IAsyncLifetime.DisposeAsync, which reaps the child and disposes the pipe, streams and gate.")]
    public sealed class FifoProcessFixture : IAsyncLifetime
    {
        private Process? child;
        private Exception? poisoned;
        private readonly List<TempDirectory> unreapedWorkspaces = [];
        private NamedPipeServerStream? pipe;
        private StreamReader? reader;
        private StreamWriter? writer;
        private Task<string>? output, errors;
        private readonly SemaphoreSlim gate = new(1, 1);

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task RunAsync(string scenario, string name, bool recheck)
        {
            await gate.WaitAsync();
            try
            {
                Assert.True(poisoned == null, $"A previous FIFO child could not be reaped; refusing another probe. {poisoned}");
                TempDirectory temp = new(); // Parent never acquires the profile lease.
                using CancellationTokenSource timeout = new(ChildLimit);
                try
                {
                    if (child == null)
                        await StartAsync(timeout.Token);
                    await writer!.WriteLineAsync(JsonSerializer.Serialize(new FifoRequest(scenario, name, recheck, temp.Path))
                        .AsMemory(), timeout.Token);
                    string? receipt = await reader!.ReadLineAsync(timeout.Token);
                    Assert.Equal(temp.Path, receipt);
                    Assert.Equal("fifo-probe-completed", File.ReadAllText(Path.Combine(temp.Path, "completed")));
                }
                catch (Exception error)
                {
                    // A blocked FIFO open OR lease disposal must kill the whole process
                    // tree before workspace cleanup. Do not rescue/open/drain the FIFO.
                    Exception? cleanup = null;
                    try { await StopAsync(kill: true); }
                    catch (Exception failure) { cleanup = failure; }
                    Assert.Fail($"FIFO {scenario}/{name}/recheck={recheck} failed within the 30-second child watchdog. " +
                        $"{error}\nCleanup: {cleanup}\n" +
                        $"{await DiagnosticAsync(output)}\n{await DiagnosticAsync(errors)}");
                }
                finally
                {
                    if (poisoned == null) temp.Dispose();
                    else unreapedWorkspaces.Add(temp); // Never delete a workspace beneath a possibly live child.
                }
            }
            finally { gate.Release(); }
        }

        private async Task StartAsync(CancellationToken cancellation)
        {
            string pipeName = "meowchan-fifo-" + Guid.NewGuid().ToString("N");
            // macOS runner TMPDIR can make the Unix-domain socket path exceed sockaddr_un.
            if (OperatingSystem.IsMacOS()) pipeName = Path.Combine("/tmp", pipeName);
            pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("vstest"); // Already built; no restore/build, only one discovery per class.
            start.ArgumentList.Add(typeof(NativePrivateMetadataSpecialFileTests).Assembly.Location);
            start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=MeowChan.Tests.Transfer.NativePrivateMetadataSpecialFileTests.FifoChildProbe");
            start.Environment[ProbeVariable] = pipeName;
            child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the private metadata FIFO probe.");
            // Collect diagnostics until the watchdog has stopped and reaped the child.
            output = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
            errors = child.StandardError.ReadToEndAsync(CancellationToken.None);
            await pipe.WaitForConnectionAsync(cancellation);
            reader = new(pipe, leaveOpen: true);
            writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        }

        private static async Task<string> DiagnosticAsync(Task<string>? capture)
        {
            try { return capture == null ? "" : await capture.WaitAsync(ReapLimit); }
            catch (Exception error) { return $"Could not capture child diagnostics: {error}"; }
        }

        private async Task StopAsync(bool kill)
        {
            List<Exception> failures = [];
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception error) { failures.Add(error); }
            }
            void Kill()
            {
                if (child != null && !child.HasExited) child.Kill(entireProcessTree: true);
            }
            // Kill BEFORE closing IPC on failure. A broken-pipe flush must never
            // bypass termination/reap or let workspace cleanup race a live lease.
            if (kill) Attempt(Kill);
            Attempt(() => writer?.Dispose());
            Attempt(() => reader?.Dispose());
            Attempt(() => pipe?.Dispose());
            writer = null;
            reader = null;
            pipe = null;
            if (child != null)
            {
                try
                {
                    if (failures.Count != 0) Attempt(Kill);
                    try { await child.WaitForExitAsync().WaitAsync(ReapLimit); }
                    catch (Exception error)
                    {
                        failures.Add(error);
                        Attempt(Kill);
                        try { await child.WaitForExitAsync().WaitAsync(ReapLimit); }
                        catch (Exception reapError) { failures.Add(reapError); }
                    }
                    if (!kill && child.HasExited && child.ExitCode != 0)
                        failures.Add(new InvalidOperationException(
                            $"FIFO child failed.\n{await DiagnosticAsync(output)}\n{await DiagnosticAsync(errors)}"));
                }
                finally
                {
                    bool exited = false;
                    Attempt(() => exited = child.HasExited);
                    if (exited) { child.Dispose(); child = null; }
                    else
                    {
                        poisoned = new InvalidOperationException("FIFO child termination could not be proven.");
                        failures.Add(poisoned);
                        // Keep the handle for fixture teardown to retry; never start
                        // another child while this one's termination is unproven.
                    }
                }
            }
            if (failures.Count != 0) throw new AggregateException("FIFO child cleanup failed.", failures);
        }

        public async Task DisposeAsync()
        {
            try { await StopAsync(kill: false); }
            finally
            {
                try
                {
                    if (child == null)
                        foreach (TempDirectory workspace in unreapedWorkspaces) workspace.Dispose();
                }
                finally { gate.Dispose(); }
            }
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    // Same normalized .NET 10 PAL stat ABI used by the existing candidate checks,
    // rather than a platform-dependent libc struct stat. lstat never opens the FIFO.
    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        internal int Flags, Mode;
        internal uint Uid, Gid;
        internal long Size, ATime, ATimeNsec, MTime, MTimeNsec, CTime, CTimeNsec;
        internal long BirthTime, BirthTimeNsec, Dev, RDev, Ino;
        internal uint UserFlags;
    }
}

// xUnit 2 does not support Assert.Skip/dynamic SkipException. Use discovery-time
// attributes so unsupported OS/privilege cases are reported as skipped, not passed.
public sealed class NativePrivateUnixTheoryAttribute : TheoryAttribute
{
    public NativePrivateUnixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "FIFO probes require Linux or macOS; no Windows FIFO coverage is claimed.";
    }
}

public sealed class NativePrivateSharingTheoryAttribute : TheoryAttribute
{
    public NativePrivateSharingTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Requires a supported Windows or Unix PAL file-sharing implementation.";
    }
}

public sealed class NativePrivatePermissionTheoryAttribute : TheoryAttribute
{
    public NativePrivatePermissionTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Unix mode-bit denial requires Linux or macOS; Windows sharing has a separate test.";
        else if (GetEffectiveUserId() == 0)
            Skip = "Effective UID 0 can bypass Unix mode bits; access-denial coverage requires a nonprivileged runner.";
    }

    [DllImport("libc", EntryPoint = "geteuid", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetEffectiveUserId();
}

public sealed class NativePrivateFifoProbeFactAttribute : FactAttribute
{
    public NativePrivateFifoProbeFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "FIFO child requires Linux or macOS.";
        else if (Environment.GetEnvironmentVariable(NativePrivateMetadataSpecialFileTests.ProbeVariable) == null)
            Skip = "Watchdog subprocess entry point, exercised by the FIFO parent theories (not a standalone case).";
    }
}
