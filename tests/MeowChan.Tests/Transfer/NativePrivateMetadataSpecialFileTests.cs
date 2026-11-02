using System.Diagnostics;
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
public sealed class NativePrivateMetadataSpecialFileTests
{
    internal const string ProbeVariable = "MEOWCHAN_PRIVATE_METADATA_FIFO_PROBE";
    private const string RootVariable = "MEOWCHAN_PRIVATE_METADATA_FIFO_ROOT";
    private const string TargetVariable = "MEOWCHAN_PRIVATE_METADATA_FIFO_TARGET";
    private const string RecheckVariable = "MEOWCHAN_PRIVATE_METADATA_FIFO_RECHECK";
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
        string scenario = Environment.GetEnvironmentVariable(ProbeVariable)!;
        string root = Environment.GetEnvironmentVariable(RootVariable)
            ?? throw new InvalidOperationException("FIFO probe root was not supplied.");
        string name = Environment.GetEnvironmentVariable(TargetVariable)
            ?? throw new InvalidOperationException("FIFO probe target was not supplied.");
        NativePrivateMetadataSelection selected = SelectionFor(name); // Validates the exact target name.
        string profile = Path.Combine(root, PrivateMarker);
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            SeedPendingEvidence(profile);
            if (scenario == "selected")
            {
                bool recheck = Environment.GetEnvironmentVariable(RecheckVariable) == "true";
                CheckSelectedFifo(lease, name, selected, recheck);
            }
            else
            {
                Assert.Equal("unselected", scenario);
                CheckUnselectedFifo(lease, name, selected);
            }
        } // The parent also contains a regression that blocks lease.Dispose().
        File.WriteAllText(Path.Combine(root, "completed"), "fifo-probe-completed");
    }

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

    private static async Task RunFifoChildAsync(string scenario, string name, bool recheck)
    {
        using TempDirectory temp = new(); // Parent owns only the synthetic workspace, never its profile lease.
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("vstest"); // Runs the already-built assembly; never builds/restores.
        start.ArgumentList.Add(typeof(NativePrivateMetadataSpecialFileTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=MeowChan.Tests.Transfer.NativePrivateMetadataSpecialFileTests.FifoChildProbe");
        start.Environment[ProbeVariable] = scenario;
        start.Environment[RootVariable] = temp.Path;
        start.Environment[TargetVariable] = name;
        start.Environment[RecheckVariable] = recheck ? "true" : "false";
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the private metadata FIFO probe.");
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> errors = child.StandardError.ReadToEndAsync();
        try
        {
            using CancellationTokenSource timeout = new(ChildLimit);
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // Kill the testhost as well as vstest. Never await blocked product
                // work or call its lease.Dispose() from the parent timeout path.
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(ReapLimit);
                Assert.Fail($"FIFO {scenario}/{name}/recheck={recheck} exceeded the 30-second child watchdog; " +
                    $"the isolated process tree was killed. A blocking open or held ProfileLease may have regressed.\n" +
                    $"{await output.WaitAsync(ReapLimit)}\n{await errors.WaitAsync(ReapLimit)}");
            }
            Assert.True(child.ExitCode == 0,
                $"FIFO child failed ({scenario}/{name}/recheck={recheck}).\n{await output.WaitAsync(ReapLimit)}\n{await errors.WaitAsync(ReapLimit)}");
            Assert.Equal("fifo-probe-completed", File.ReadAllText(Path.Combine(temp.Path, "completed")));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(ReapLimit);
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
