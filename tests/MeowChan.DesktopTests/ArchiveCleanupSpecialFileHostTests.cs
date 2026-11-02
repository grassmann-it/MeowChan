using System.Runtime.InteropServices;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using Microsoft.Win32.SafeHandles;
using static MeowChan.DesktopTests.ArchiveCleanupFixtures;

namespace MeowChan.DesktopTests;

public sealed class ArchiveCleanupSpecialFileHostTests
{
    private const string PrivateMarker = "synthetic-private-note-and-profile-marker";
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [ArchiveCleanupUnixTheory]
    [InlineData("thread-favorites.json", false)]
    [InlineData("media-favorites.json", false)]
    [InlineData("media-ratings.json", false)]
    [InlineData("post-bookmarks.json", false)]
    [InlineData("reader-groups.json", false)]
    [InlineData("reading-position.json", false)]
    [InlineData("thread-favorites.json.bak", false)]
    [InlineData("thread-favorites.json.bak", true)]
    [InlineData("media-favorites.json.bak", false)]
    [InlineData("media-favorites.json.bak", true)]
    public async Task FifoWithoutWriterIsPromptlyUnknownAndCannotDelete(string name, bool primaryPresent)
    {
        using Fixture fixture = new();
        await fixture.SeedAsync();
        if (primaryPresent)
            File.WriteAllText(fixture.ProtectionPath(name[..^4]), name.StartsWith("thread-", StringComparison.Ordinal)
                ? "{\"Version\":1,\"Revision\":0,\"Threads\":[]}"
                : "{\"Version\":1,\"Entries\":[]}");
        await using DesktopSessionHost host = await fixture.StartAsync();
        ArchiveCleanupExecutionPlan confirmed = await ConfirmAsync(host);
        var retained = fixture.ProtectionEvidence();
        string fifo = fixture.ProtectionPath(name);
        // Deliberately no writer or rescue handle exists on the passing path.
        Assert.Equal(0, MkFifo(fifo, 0x180)); // 0600
        try
        {
            await AssertFaultClosesCleanupAsync(host, fixture, confirmed, fifo);
            Assert.Equal(retained, fixture.ProtectionEvidence(name));
        }
        finally { File.Delete(fifo); }
        await AssertReadyAgainAsync(host, fixture);
    }

    [ArchiveCleanupUnixTheory]
    [InlineData("media-favorites.json")]
    [InlineData("media-favorites.json.bak")]
    [InlineData("post-bookmarks.json")]
    public async Task MissingProfileParentIsAnOpaqueReadFailureNotAnAbsentProtection(string name)
    {
        using Fixture fixture = new();
        await fixture.SeedAsync();
        // Exercise only the optional-reader classification, not full cleanup
        // admission or behavior of a host whose live working directory moved.
        await using DesktopSessionHost host = new(fixture.Profile, Array.Empty<IImageBoard>());
        var reader = typeof(DesktopSessionHost).GetMethod("ReadCleanupOptionalFile",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.Null(reader.Invoke(host, [name, 8_000_000, CancellationToken.None]));
        string moved = fixture.Profile + "-moved";
        Directory.Move(fixture.Profile, moved);
        try
        {
            var failure = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
                reader.Invoke(host, [name, 8_000_000, CancellationToken.None]));
            var readFailure = Assert.IsType<IOException>(failure.InnerException);
            Assert.DoesNotContain(fixture.Profile, readFailure.ToString());
            Assert.DoesNotContain(PrivateMarker, readFailure.ToString());
            Assert.Null(readFailure.InnerException);
        }
        finally { Directory.Move(moved, fixture.Profile); }
        Assert.Null(reader.Invoke(host, [name, 8_000_000, CancellationToken.None]));
        await fixture.AssertArchiveRetainedAsync();
    }

    [ArchiveCleanupSharingTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharingViolationKeepsFullErrorsOpaqueAndDoesNotRecover(bool backup)
    {
        // Windows sharing is enforced by CreateFile. Unix advisory flock behavior
        // is not equivalent; use the real permission-denial cases below there.
        using Fixture fixture = new();
        await fixture.SeedAsync();
        fixture.SeedFavorites();
        await using DesktopSessionHost host = await fixture.StartAsync();
        ArchiveCleanupExecutionPlan confirmed = await ConfirmAsync(host);
        var retained = fixture.ProtectionEvidence();
        string path = fixture.ProtectionPath("media-favorites.json" + (backup ? ".bak" : ""));
        using (FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => { using var probe = File.OpenRead(path); });
            await AssertFaultClosesCleanupAsync(host, fixture, confirmed);
        }
        Assert.Equal(retained, fixture.ProtectionEvidence());
        await AssertReadyAgainAsync(host, fixture);
    }

    [ArchiveCleanupPermissionTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccessDeniedKeepsFullErrorsOpaqueAndDoesNotRecover(bool backup)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix permission test was invoked on Windows.");
        using Fixture fixture = new();
        await fixture.SeedAsync();
        fixture.SeedFavorites();
        await using DesktopSessionHost host = await fixture.StartAsync();
        ArchiveCleanupExecutionPlan confirmed = await ConfirmAsync(host);
        var retained = fixture.ProtectionEvidence();
        string path = fixture.ProtectionPath("media-favorites.json" + (backup ? ".bak" : ""));
        UnixFileMode mode = File.GetUnixFileMode(path);
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
            // Supported nonroot runners must establish actual denial; a runner
            // that bypasses mode bits fails instead of claiming denial coverage.
            Assert.Throws<UnauthorizedAccessException>(() => { using var probe = File.OpenRead(path); });
            await AssertFaultClosesCleanupAsync(host, fixture, confirmed);
        }
        finally { File.SetUnixFileMode(path, mode); }
        Assert.Equal(retained, fixture.ProtectionEvidence());
        await AssertReadyAgainAsync(host, fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryIsInvalidRatherThanMissing(bool backup)
    {
        using Fixture fixture = new();
        await fixture.SeedAsync();
        await using DesktopSessionHost host = await fixture.StartAsync();
        ArchiveCleanupExecutionPlan confirmed = await ConfirmAsync(host);
        string path = fixture.ProtectionPath("media-favorites.json" + (backup ? ".bak" : ""));
        Directory.CreateDirectory(path);
        string sentinel = Path.Combine(path, PrivateMarker);
        File.WriteAllText(sentinel, PrivateMarker);
        try
        {
            await AssertFaultClosesCleanupAsync(host, fixture, confirmed);
            Assert.Equal(PrivateMarker, File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(path, recursive: true); }
        await AssertReadyAgainAsync(host, fixture);
    }

    [ArchiveCleanupUnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DanglingLinkIsInvalidRatherThanMissing(bool backup)
    {
        // Creating Windows links can require an elevated privilege. Do not claim
        // Windows link coverage merely because its suite passes.
        using Fixture fixture = new();
        await fixture.SeedAsync();
        await using DesktopSessionHost host = await fixture.StartAsync();
        ArchiveCleanupExecutionPlan confirmed = await ConfirmAsync(host);
        string path = fixture.ProtectionPath("media-favorites.json" + (backup ? ".bak" : ""));
        string missingTarget = fixture.ProtectionPath(PrivateMarker + "-missing");
        File.CreateSymbolicLink(path, missingTarget);
        try
        {
            await AssertFaultClosesCleanupAsync(host, fixture, confirmed);
            Assert.Equal(missingTarget, new FileInfo(path).LinkTarget);
            Assert.False(File.Exists(missingTarget));
        }
        finally { File.Delete(path); }
        await AssertReadyAgainAsync(host, fixture);
    }

    private static async Task<ArchiveCleanupExecutionPlan> ConfirmAsync(DesktopSessionHost host)
    {
        var preview = await BoundedAsync(() => host.PrepareArchiveCleanupAsync());
        Assert.True(preview.Complete, string.Join(";", preview.Errors));
        Assert.Equal(ArchiveCleanupEligibility.Candidate, Assert.Single(preview.Rows).Eligibility);
        var plan = await BoundedAsync(() => host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]));
        Assert.True(plan.CanExecute, string.Join(";", plan.Reasons));
        return plan;
    }

    private static async Task AssertFaultClosesCleanupAsync(DesktopSessionHost host, Fixture fixture,
        ArchiveCleanupExecutionPlan confirmed, string? fifo = null)
    {
        var preview = await BoundedAsync(() => host.PrepareArchiveCleanupAsync(), fifo);
        Assert.False(preview.Complete);
        var row = Assert.Single(preview.Rows);
        Assert.Equal(ArchiveCleanupEligibility.Unknown, row.Eligibility);
        Assert.Contains(ArchiveCleanupReason.ProtectionUnknown, row.Reasons);
        AssertOpaque(preview.Errors, fixture);

        var rejected = await BoundedAsync(() => host.PrepareArchiveCleanupExecutionAsync(preview, [Id("100")]), fifo);
        Assert.False(rejected.CanExecute);
        AssertOpaque(rejected.Reasons, fixture);
        // Also reject a formerly valid confirmation, not just the Unknown preview.
        var result = await BoundedAsync(() => host.ExecuteArchiveCleanupAsync(confirmed), fifo);
        Assert.Empty(result.DeletedThreads);
        Assert.Equal(new[] { Id("100") }, result.RemainingThreads);
        Assert.Equal(0, result.DeletedFileCount);
        Assert.Equal(0L, result.DeletedBytes);
        Assert.True(result.RequiresNewPreview);
        Assert.False(result.Canceled);
        AssertOpaque(result.Errors, fixture);
        await fixture.AssertArchiveRetainedAsync();
    }

    private static void AssertOpaque(IReadOnlyList<string> errors, Fixture fixture)
    {
        Assert.NotEmpty(errors);
        // Check EVERY complete diagnostic, not a substring of the first Message.
        // Serializing the complete Errors collection also detects extra entries.
        string fullErrors = string.Join("\n", errors) + JsonSerializer.Serialize(errors);
        Assert.DoesNotContain(fixture.Profile, fullErrors, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Root, fullErrors, StringComparison.Ordinal);
        Assert.DoesNotContain(PrivateMarker, fullErrors, StringComparison.Ordinal);
    }

    private static async Task AssertReadyAgainAsync(DesktopSessionHost host, Fixture fixture)
    {
        // Missing primary + missing backup is still empty, and a failed preview
        // must release command/lease ownership for another admitted operation.
        var fresh = await BoundedAsync(() => host.PrepareArchiveCleanupAsync());
        Assert.True(fresh.Complete, string.Join(";", fresh.Errors));
        Assert.Empty(fresh.Errors);
        Assert.Equal(ArchiveCleanupEligibility.Candidate, Assert.Single(fresh.Rows).Eligibility);
        await fixture.AssertArchiveRetainedAsync();
    }

    private static async Task<T> BoundedAsync<T>(Func<Task<T>> action, string? fifo = null)
    {
        // Start outside the test continuation: even a synchronous blocking open
        // before the first await must leave the watchdog able to run.
        Task<T> operation = Task.Run(action);
        if (await Task.WhenAny(operation, Task.Delay(Watchdog)) == operation)
            return await operation;
        if (fifo == null)
            return await operation.WaitAsync(Watchdog);

        // Red-control escape hatch for the old read-only blocking open. O_RDWR +
        // O_NONBLOCK itself never waits for a peer. Hold it through BOTH guard
        // passes so repeated opens can drain, then fail even if cleanup returned.
        int nonblocking = OperatingSystem.IsMacOS() ? 0x4 : 0x800;
        using SafeFileHandle rescue = new((IntPtr)Open(fifo, 2 | nonblocking), ownsHandle: true);
        Assert.False(rescue.IsInvalid, "FIFO watchdog could not create its nonblocking rescue handle.");
        try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (error is not TimeoutException) { /* Observe the drained failing operation. */ }
        Assert.Fail("Cleanup exceeded the FIFO watchdog; a rescue writer was required to unblock it.");
        return default!;
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        internal string Profile => Path.Combine(_temp.Path, PrivateMarker, "profile");
        internal string Root => Path.Combine(_temp.Path, PrivateMarker, "media");
        private string Media => DirectoryFor(Root, Id("100"));
        private byte[] _manifest = [];

        internal async Task SeedAsync()
        {
            Config(Profile, Root);
            await AssetsAsync(Media, new Asset("asset", "original", "asset.bin", "asset.bin", [1, 2, 3]));
            _manifest = File.ReadAllBytes(Path.Combine(Media, ".meowchan-manifest.json"));
            await RegisterAsync(Profile, Root, Id("100"));
            File.WriteAllText(ProtectionPath("post-bookmarks.json.pending-synthetic"), PrivateMarker);
        }

        internal void SeedFavorites()
        {
            FavoriteEntry favorite = new(new(Id("unrelated"), "private-asset", "original", new string('a', 64)), 3, PrivateMarker);
            string json = JsonSerializer.Serialize(new { Version = 1, Entries = new[] { favorite } });
            File.WriteAllText(ProtectionPath("media-favorites.json"), json);
            File.WriteAllText(ProtectionPath("media-favorites.json.bak"), json);
        }

        internal string ProtectionPath(string name) => Path.Combine(Profile, name);
        internal async Task<DesktopSessionHost> StartAsync()
        {
            DesktopSessionHost host = new(Profile, Array.Empty<IImageBoard>());
            await host.StartAsync();
            return host;
        }

        internal string[] ProtectionEvidence(string? excluded = null) => Directory.EnumerateFileSystemEntries(Profile)
            .Where(path => Path.GetFileName(path) != excluded && IsProtectionEvidence(Path.GetFileName(path)))
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetFileName(path) + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToHexString(File.ReadAllBytes(path)))
            .ToArray();

        private static bool IsProtectionEvidence(string name) => name.StartsWith("thread-favorites", StringComparison.Ordinal)
            || name.StartsWith("media-favorites", StringComparison.Ordinal) || name.StartsWith("media-ratings", StringComparison.Ordinal)
            || name.StartsWith("post-bookmarks", StringComparison.Ordinal) || name.StartsWith("reader-groups", StringComparison.Ordinal)
            || name.StartsWith("reading-position", StringComparison.Ordinal) || name == "settings.json" || name == "saved_urls.json";

        internal async Task AssertArchiveRetainedAsync()
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(Media, "asset.bin")));
            Assert.Equal(_manifest, File.ReadAllBytes(Path.Combine(Media, ".meowchan-manifest.json")));
            Assert.Equal(PrivateMarker, File.ReadAllText(ProtectionPath("post-bookmarks.json.pending-synthetic")));
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(Profile);
            Assert.NotNull(await reader.ReadThreadAsync(Id("100")));
        }

        public void Dispose() => _temp.Dispose();
    }
}

// xUnit 2 requires discovery-time Skip attributes: unsupported platform and
// privilege cases must be reported as skipped, never as successful early returns.
public sealed class ArchiveCleanupUnixTheoryAttribute : TheoryAttribute
{
    public ArchiveCleanupUnixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Unix FIFO/link cases require Linux or macOS; no Windows coverage is claimed.";
    }
}

public sealed class ArchiveCleanupSharingTheoryAttribute : TheoryAttribute
{
    public ArchiveCleanupSharingTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Requires Windows kernel file-sharing enforcement; Unix access denial has separate tests.";
    }
}

public sealed class ArchiveCleanupPermissionTheoryAttribute : TheoryAttribute
{
    public ArchiveCleanupPermissionTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Unix mode-bit denial requires Linux or macOS; Windows sharing has separate tests.";
        else if (GetEffectiveUserId() == 0)
            Skip = "Effective UID 0 can bypass Unix mode bits; access-denial coverage requires a nonprivileged runner.";
    }

    [DllImport("libc", EntryPoint = "geteuid", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint GetEffectiveUserId();
}
