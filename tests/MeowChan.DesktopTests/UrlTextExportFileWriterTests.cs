using System.Text;
using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Testing;

namespace MeowChan.DesktopTests;

// Only caller-owned physical temporary directories. No profile/provider/picker fixture.
// Every writer task is joined (including in gate-test finally blocks) before cleanup.
public sealed class UrlTextExportFileWriterTests
{
    private const string PrivateMarker = "synthetic-private-writer-marker";
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);
    private static readonly byte[] Sentinel = Encoding.UTF8.GetBytes(PrivateMarker);

    private static ArchiveUrlTextExportPlan Plan(string source = "4chan", string board = "b", string thread = "123")
    {
        ResourceId id = new(source, board, thread);
        string url = source == "4chan" ? $"https://boards.4chan.org/{board}/thread/{thread}"
            : $"https://8kun.top/{board}/res/{thread}.html";
        ArchiveThreadSummary summary = new(id, url, PrivateMarker, "/private/profile/media",
            ThreadSourceState.NotFound, false, DateTimeOffset.UnixEpoch, null, 0, 1);
        var plan = ArchiveUrlTextExportPlan.TryCreate(id, summary, out var error);
        Assert.Equal(ArchiveUrlTextExportPlanError.None, error);
        return Assert.IsType<ArchiveUrlTextExportPlan>(plan);
    }

    [Theory]
    [InlineData(1, 1_048_576)]
    [InlineData(2, 2_097_152)]
    [InlineData(64, 67_108_864)]
    public void ConfiguredMiBBudgetUsesInclusiveByteBoundsWithoutLargeAllocations(int limitMiB, int expectedBytes)
    {
        var writer = new UrlTextExportFileWriter(limitMiB);
        Assert.Equal(expectedBytes, writer.LimitBytes);
        Assert.True(writer.IsWithinByteLimit(1));
        Assert.True(writer.IsWithinByteLimit(expectedBytes));
        Assert.False(writer.IsWithinByteLimit(expectedBytes + 1));
        Assert.False(writer.IsWithinByteLimit(0));
        Assert.False(writer.IsWithinByteLimit(-1));
        Assert.False(writer.IsWithinByteLimit(int.MinValue));
        Assert.False(writer.IsWithinByteLimit(int.MaxValue));
        // This tests the real writer's size policy, NOT a >1 MiB plan: the plan's independent format bounds remain.
        Assert.Equal(1_048_576, new UrlTextExportFileWriter().LimitBytes);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(2048)]
    [InlineData(int.MaxValue)]
    public void InvalidMiBBudgetsCannotOverflowIntoAnAcceptedByteLimit(int limitMiB) =>
        Assert.Equal("limitMiB", Assert.Throws<ArgumentOutOfRangeException>(() => new UrlTextExportFileWriter(limitMiB)).ParamName);

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public async Task ConfiguredWriterKeepsNoClobberAndPathGuards(int limitMiB)
    {
        using Workspace workspace = new();
        string destination = Path.Combine(workspace.Root, "out.txt");
        var writer = new UrlTextExportFileWriter(limitMiB);
        var plan = Plan();
        Assert.Equal(UrlTextExportFileWriteStatus.Saved, (await writer.WriteAsync(plan, destination)).Status);
        Assert.Equal(plan.CopyUtf8(), File.ReadAllBytes(destination));
        Assert.Equal(UrlTextExportFileWriteError.DestinationExists, (await writer.WriteAsync(plan, destination)).Error);
        Assert.Equal(plan.CopyUtf8(), File.ReadAllBytes(destination));
        Assert.Equal(UrlTextExportFileWriteError.InvalidDestination, (await writer.WriteAsync(plan, "../out.txt")).Error);
        Assert.Equal(UrlTextExportFileWriteError.InvalidPlan, (await writer.WriteAsync(null, destination)).Error);
        Assert.Single(Directory.GetFileSystemEntries(workspace.Root));
    }

    [Theory]
    [InlineData("4chan", "out.txt")]
    [InlineData("8kun", "out")]
    [InlineData("4chan", "chosen.other")]
    public async Task SuccessIsExactPlanBytesAndClosedFileWithoutExtensionRepair(string source, string filename)
    {
        using Workspace workspace = new();
        var plan = Plan(source);
        byte[] expected = Encoding.UTF8.GetBytes(plan.Url + "\n");
        string destination = Path.Combine(workspace.Root, filename);

        var result = await new UrlTextExportFileWriter().WriteAsync(plan, destination);

        Assert.Equal(new UrlTextExportFileWriteResult(UrlTextExportFileWriteStatus.Saved,
            UrlTextExportFileWriteError.None), result);
        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal(destination, Assert.Single(Directory.GetFileSystemEntries(workspace.Root)));
        // A successful result must not precede actual disposal of the exclusive handle.
        using FileStream reopened = new(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(expected.Length, reopened.Length);
    }

    [Fact]
    public async Task LargestSupportedIdentityStillWritesOnlyTheBoundedExactLine()
    {
        using Workspace workspace = new();
        var plan = Plan("8kun", new string('b', 256), new string('1', 256));
        string destination = Path.Combine(workspace.Root, "out.txt");
        var result = await new UrlTextExportFileWriter().WriteAsync(plan, destination);
        Assert.Equal(UrlTextExportFileWriteStatus.Saved, result.Status);
        Assert.Equal(plan.CopyUtf8(), File.ReadAllBytes(destination));
        Assert.InRange(new FileInfo(destination).Length, 1L, (long)UrlTextExportFileWriter.MaximumBytes);
    }

    [Fact]
    public async Task ExistingFileCollisionPreservesSentinelWithoutOpeningOrRetrying()
    {
        using Workspace workspace = new();
        string destination = Path.Combine(workspace.Root, "out.txt");
        File.WriteAllBytes(destination, Sentinel);
        var gate = new UrlTextExportAfterOpenTestGate();

        var result = await new UrlTextExportFileWriter(gate).WriteAsync(Plan(), destination);

        Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        Assert.Equal(UrlTextExportFileWriteError.DestinationExists, result.Error);
        Assert.Equal(Sentinel, File.ReadAllBytes(destination));
        Assert.False(gate.Opened.IsCompleted);
        Assert.Single(Directory.GetFileSystemEntries(workspace.Root));
        AssertOpaque(result, destination);
    }

    [Fact]
    public async Task ExistingDirectoryIsNeverOpenedOrReplaced()
    {
        using Workspace workspace = new();
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "out.txt")).FullName;
        string sentinel = Path.Combine(destination, "sentinel");
        File.WriteAllBytes(sentinel, Sentinel);

        var result = await new UrlTextExportFileWriter().WriteAsync(Plan(), destination);

        Assert.Equal(UrlTextExportFileWriteError.DestinationExists, result.Error);
        Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        Assert.Equal(Sentinel, File.ReadAllBytes(sentinel));
    }

    [Fact]
    public async Task MissingParentsAreNotCreated()
    {
        using Workspace workspace = new();
        string parent = Path.Combine(workspace.Root, "missing", "nested");
        var result = await new UrlTextExportFileWriter().WriteAsync(Plan(), Path.Combine(parent, "out.txt"));
        Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        Assert.Equal(UrlTextExportFileWriteError.MissingParent, result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public async Task FileInParentPositionIsRejectedWithoutTouchingItsBytes()
    {
        using Workspace workspace = new();
        string parent = Path.Combine(workspace.Root, "not-directory");
        File.WriteAllBytes(parent, Sentinel);
        var result = await new UrlTextExportFileWriter().WriteAsync(Plan(), Path.Combine(parent, "out.txt"));
        Assert.Equal(UrlTextExportFileWriteError.UnsafeParent, result.Error);
        Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        Assert.Equal(Sentinel, File.ReadAllBytes(parent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("out.txt")]
    [InlineData("./out.txt")]
    [InlineData("../out.txt")]
    [InlineData("https://host.invalid/private/out.txt")]
    [InlineData("smb://host.invalid/share/out.txt")]
    [InlineData("file:///tmp/out.txt")]
    [InlineData(@"\\server\share\out.txt")]
    [InlineData("//server/share/out.txt")]
    [InlineData(@"\\?\C:\out.txt")]
    [InlineData(@"\\?\UNC\server\share\out.txt")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\??\C:\out.txt")]
    public async Task RejectsRelativeRemoteAndNamespaceSyntaxWithoutFilesystemOpen(string? destination)
    {
        // Cross-platform lexical rejection, NOT a claim to emulate Windows I/O on Unix.
        var gate = new UrlTextExportAfterOpenTestGate();
        var result = await new UrlTextExportFileWriter(gate).WriteAsync(Plan(), destination);
        Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        Assert.Equal(UrlTextExportFileWriteError.InvalidDestination, result.Error);
        Assert.False(gate.Opened.IsCompleted);
        AssertOpaque(result, destination);
    }

    [Fact]
    public async Task RejectsNativeDirectoryTraversalControlAndInvalidUnicodeSyntax()
    {
        using Workspace workspace = new();
        string separator = Path.DirectorySeparatorChar.ToString();
        foreach (string destination in new[] { workspace.Root + separator,
            Path.Combine(workspace.Root, ".", "out.txt"), Path.Combine(workspace.Root, "..", "out.txt"),
            Path.Combine(workspace.Root, "out\0.txt"), Path.Combine(workspace.Root, "out\n.txt"),
            Path.Combine(workspace.Root, "out\uD800.txt"), Path.Combine(workspace.Root, "out\uDC00.txt") })
        {
            var result = await new UrlTextExportFileWriter().WriteAsync(Plan(), destination);
            Assert.Equal(UrlTextExportFileWriteError.InvalidDestination, result.Error);
            Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        }
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task PathCharacterAndComponentBoundsAreCheckedBeforeFilesystemWork()
    {
        using Workspace workspace = new();
        string tooLong = Path.Combine(workspace.Root,
            new string('x', UrlTextExportFileWriter.MaximumPathCharacters + 1));
        string tooDeep = Path.Combine(new[] { workspace.Root }.Concat(
            Enumerable.Repeat("x", UrlTextExportFileWriter.MaximumPathComponents + 1)).ToArray());
        foreach (string destination in new[] { tooLong, tooDeep })
        {
            var gate = new UrlTextExportAfterOpenTestGate();
            var result = await new UrlTextExportFileWriter(gate).WriteAsync(Plan(), destination);
            Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
            Assert.Equal(UrlTextExportFileWriteError.LimitExceeded, result.Error);
            Assert.False(gate.Opened.IsCompleted);
        }
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task NullPlanNeverCreatesAFile()
    {
        using Workspace workspace = new();
        var result = await new UrlTextExportFileWriter().WriteAsync(null, Path.Combine(workspace.Root, "out.txt"));
        Assert.Equal(UrlTextExportFileWriteError.InvalidPlan, result.Error);
        Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
    }

    [Fact]
    public async Task CancellationObservedBeforeCreateLeavesNoChange()
    {
        using Workspace workspace = new();
        string sentinel = Path.Combine(workspace.Root, "untouched");
        File.WriteAllBytes(sentinel, Sentinel);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        var gate = new UrlTextExportAfterOpenTestGate();
        var result = await new UrlTextExportFileWriter(gate).WriteAsync(Plan(),
            Path.Combine(workspace.Root, "out.txt"), cancellation.Token);
        Assert.Equal(UrlTextExportFileWriteStatus.Canceled, result.Status);
        Assert.Equal(UrlTextExportFileWriteError.Canceled, result.Error);
        Assert.False(gate.Opened.IsCompleted);
        Assert.Equal(sentinel, Assert.Single(Directory.GetFileSystemEntries(workspace.Root)));
        Assert.Equal(Sentinel, File.ReadAllBytes(sentinel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownCreateAttemptWithoutAcquiredHandleIsExplicitlyUncertain(bool cancellation)
    {
        var failure = cancellation ? UrlTextExportFileWriteError.Canceled : UrlTextExportFileWriteError.IoFailure;
        // Pure classification regression, NOT an injected/fabricated OS constructor
        // failure. The writer uses this same seam after disposal/failure observation.
        var result = UrlTextExportFileWriter.ClassifyFailure(createAttempted: true,
            handleAcquired: false, error: failure);
        Assert.Equal(UrlTextExportFileWriteStatus.PartialRetained, result.Status);
        Assert.Equal(UrlTextExportFileWriteError.CreationUncertain, result.Error);
        AssertOpaque(result, null);
    }

    [Theory]
    [InlineData(false, (int)UrlTextExportFileWriteError.IoFailure, (int)UrlTextExportFileWriteStatus.Failed)]
    [InlineData(false, (int)UrlTextExportFileWriteError.DestinationExists, (int)UrlTextExportFileWriteStatus.Failed)]
    [InlineData(false, (int)UrlTextExportFileWriteError.Canceled, (int)UrlTextExportFileWriteStatus.Canceled)]
    [InlineData(true, (int)UrlTextExportFileWriteError.IoFailure, (int)UrlTextExportFileWriteStatus.PartialRetained)]
    [InlineData(true, (int)UrlTextExportFileWriteError.Canceled, (int)UrlTextExportFileWriteStatus.PartialRetained)]
    public void KnownPreAttemptAndAcquiredHandleFailuresKeepTheirCodes(bool handleAcquired,
        int errorCode, int expectedStatus)
    {
        var error = (UrlTextExportFileWriteError)errorCode;
        var expected = (UrlTextExportFileWriteStatus)expectedStatus;
        var result = UrlTextExportFileWriter.ClassifyFailure(createAttempted: handleAcquired,
            handleAcquired: handleAcquired, error: error);
        Assert.Equal(expected, result.Status);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public async Task CancellationAfterCreateRetainsEmptyFileAndClosesBeforeResult()
    {
        using Workspace workspace = new();
        using CancellationTokenSource cancellation = new();
        string destination = Path.Combine(workspace.Root, "out.txt");
        var gate = new UrlTextExportAfterOpenTestGate();
        Task<UrlTextExportFileWriteResult> writing = new UrlTextExportFileWriter(gate)
            .WriteAsync(Plan(), destination, cancellation.Token);
        UrlTextExportFileWriteResult result;
        try
        {
            await gate.Opened.WaitAsync(Watchdog);
            Assert.True(File.Exists(destination));
            Assert.False(writing.IsCompleted);
            cancellation.Cancel();
        }
        finally
        {
            gate.Release();
            result = await writing; // Join the REAL task before fixture cleanup.
        }
        Assert.Equal(UrlTextExportFileWriteStatus.PartialRetained, result.Status);
        Assert.Equal(UrlTextExportFileWriteError.Canceled, result.Error);
        using (FileStream retained = new(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(0L, retained.Length);
        // No silent retry or overwrite of even an empty retained file.
        var retry = await new UrlTextExportFileWriter().WriteAsync(Plan(), destination);
        Assert.Equal(UrlTextExportFileWriteError.DestinationExists, retry.Error);
        Assert.Equal(0L, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task InjectedAfterOpenIoFaultIsRetainedNotSuccessOrAnEscapedException()
    {
        using Workspace workspace = new();
        string destination = Path.Combine(workspace.Root, PrivateMarker + ".txt");
        var gate = new UrlTextExportAfterOpenTestGate(failAfterOpen: true);
        Task<UrlTextExportFileWriteResult> writing = new UrlTextExportFileWriter(gate).WriteAsync(Plan(), destination);
        UrlTextExportFileWriteResult result;
        try
        {
            await gate.Opened.WaitAsync(Watchdog);
            Assert.False(writing.IsCompleted);
            Assert.True(File.Exists(destination));
        }
        finally { gate.Release(); result = await writing; }
        Assert.Equal(UrlTextExportFileWriteStatus.PartialRetained, result.Status);
        Assert.Equal(UrlTextExportFileWriteError.IoFailure, result.Error);
        AssertOpaque(result, destination);
        Assert.DoesNotContain("Synthetic URLtext after-open failure", result.ToString());
        using FileStream retained = new(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(0L, retained.Length);
    }

    [Fact]
    public async Task WriterUsesOnlyItsOwnCopyOfThePlanBytes()
    {
        using Workspace workspace = new();
        var plan = Plan();
        byte[] expected = plan.CopyUtf8();
        byte[] otherCallerCopy = plan.CopyUtf8();
        string destination = Path.Combine(workspace.Root, "out.txt");
        var gate = new UrlTextExportAfterOpenTestGate();
        Task<UrlTextExportFileWriteResult> writing = new UrlTextExportFileWriter(gate).WriteAsync(plan, destination);
        UrlTextExportFileWriteResult result;
        try
        {
            await gate.Opened.WaitAsync(Watchdog);
            Array.Fill(otherCallerCopy, (byte)0);
            Assert.False(writing.IsCompleted);
        }
        finally { gate.Release(); result = await writing; }
        Assert.Equal(UrlTextExportFileWriteStatus.Saved, result.Status);
        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal(expected, plan.CopyUtf8());
    }

    [UrlTextExportUnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedAndDanglingParentsAreRejectedBeforeInspectingDescendants(bool dangling)
    {
        using Workspace workspace = new();
        string target = Path.Combine(workspace.Root, "target");
        if (!dangling) Directory.CreateDirectory(target);
        string link = Path.Combine(workspace.Root, "linked-parent");
        Directory.CreateSymbolicLink(link, target);
        try
        {
            var gate = new UrlTextExportAfterOpenTestGate();
            var result = await new UrlTextExportFileWriter(gate).WriteAsync(Plan(), Path.Combine(link, "out.txt"));
            Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
            Assert.Equal(UrlTextExportFileWriteError.UnsafeParent, result.Error);
            Assert.False(gate.Opened.IsCompleted);
            Assert.Equal(target, new DirectoryInfo(link).LinkTarget);
            Assert.False(File.Exists(Path.Combine(target, "out.txt")));
            Assert.Equal(!dangling, Directory.Exists(target));
        }
        finally
        {
            // Native Unix unlink: remove only this fixture-owned symlink, even
            // when its directory target is absent. The real writer task is joined.
            File.Delete(link);
        }
    }

    [UrlTextExportUnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingLeafLinksNeverOpenOrChangeTheirTarget(bool dangling)
    {
        using Workspace workspace = new();
        string target = Path.Combine(workspace.Root, "target");
        if (!dangling) File.WriteAllBytes(target, Sentinel);
        string link = Path.Combine(workspace.Root, "out.txt");
        File.CreateSymbolicLink(link, target);
        try
        {
            var result = await new UrlTextExportFileWriter().WriteAsync(Plan(), link);
            Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
            Assert.Equal(UrlTextExportFileWriteError.DestinationExists, result.Error);
            Assert.Equal(target, new FileInfo(link).LinkTarget);
            if (dangling) Assert.False(File.Exists(target));
            else Assert.Equal(Sentinel, File.ReadAllBytes(target));
        }
        finally { File.Delete(link); }
    }

    [UrlTextExportUnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterPathReplacementNeverDeletesTheReplacement(bool cancellation)
    {
        // Unix allows unlink while open; FileShare.None is not an inode/pathname fence.
        using Workspace workspace = new();
        using CancellationTokenSource source = new();
        string destination = Path.Combine(workspace.Root, "out.txt");
        var gate = new UrlTextExportAfterOpenTestGate(failAfterOpen: !cancellation);
        Task<UrlTextExportFileWriteResult> writing = new UrlTextExportFileWriter(gate)
            .WriteAsync(Plan(), destination, source.Token);
        UrlTextExportFileWriteResult result;
        try
        {
            await gate.Opened.WaitAsync(Watchdog);
            File.Delete(destination); // Test owns this synthetic file, not production cleanup.
            File.WriteAllBytes(destination, Sentinel);
            if (cancellation) source.Cancel();
        }
        finally { gate.Release(); result = await writing; }
        Assert.Equal(UrlTextExportFileWriteStatus.PartialRetained, result.Status);
        Assert.Equal(cancellation ? UrlTextExportFileWriteError.Canceled : UrlTextExportFileWriteError.IoFailure, result.Error);
        Assert.Equal(Sentinel, File.ReadAllBytes(destination));
    }

    [UrlTextExportUnixTheory]
    [InlineData("NUL.txt")]
    [InlineData("CON")]
    [InlineData("colon:name")]
    public async Task UnixNativeFilenamesAreNotPretendedToHaveWindowsDeviceSemantics(string filename)
    {
        using Workspace workspace = new();
        string destination = Path.Combine(workspace.Root, filename);
        var plan = Plan();
        var result = await new UrlTextExportFileWriter().WriteAsync(plan, destination);
        Assert.Equal(UrlTextExportFileWriteStatus.Saved, result.Status);
        Assert.Equal(plan.CopyUtf8(), File.ReadAllBytes(destination));
    }

    [UrlTextExportWindowsTheory]
    [InlineData("NUL.txt")]
    [InlineData("CON")]
    [InlineData("CON .txt")]
    [InlineData("conin$")]
    [InlineData("COM1.log")]
    [InlineData("LPT\u00b9.txt")]
    [InlineData("out.txt:stream")]
    [InlineData("out.txt.")]
    [InlineData("out.txt ")]
    public async Task NativeWindowsDeviceAdsAndTrailingAliasesAreRejected(string filename)
    {
        using Workspace workspace = new();
        var result = await new UrlTextExportFileWriter().WriteAsync(Plan(), Path.Combine(workspace.Root, filename));
        Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        Assert.Equal(UrlTextExportFileWriteError.InvalidDestination, result.Error);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Root));
    }

    [UrlTextExportWindowsTheory]
    [InlineData(@"\/server/share/out.txt")]
    [InlineData(@"/\server\share\out.txt")]
    [InlineData(@"C:out.txt")]
    public async Task NativeWindowsMixedUncAndDriveRelativePathsAreRejected(string destination)
    {
        var gate = new UrlTextExportAfterOpenTestGate();
        var result = await new UrlTextExportFileWriter(gate).WriteAsync(Plan(), destination);
        Assert.Equal(UrlTextExportFileWriteError.InvalidDestination, result.Error);
        Assert.Equal(UrlTextExportFileWriteStatus.Failed, result.Status);
        Assert.False(gate.Opened.IsCompleted);
    }

    [UrlTextExportWindowsTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsExclusiveHandleIsHeldUntilRealTaskFinishes(bool fault)
    {
        using Workspace workspace = new();
        string destination = Path.Combine(workspace.Root, "out.txt");
        var gate = new UrlTextExportAfterOpenTestGate(failAfterOpen: fault);
        Task<UrlTextExportFileWriteResult> writing = new UrlTextExportFileWriter(gate).WriteAsync(Plan(), destination);
        UrlTextExportFileWriteResult result;
        try
        {
            await gate.Opened.WaitAsync(Watchdog);
            Assert.False(writing.IsCompleted);
            Assert.ThrowsAny<IOException>(() =>
            {
                using FileStream conflicting = new(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            });
        }
        finally { gate.Release(); result = await writing; }
        Assert.Equal(fault ? UrlTextExportFileWriteStatus.PartialRetained : UrlTextExportFileWriteStatus.Saved, result.Status);
        using FileStream reopened = new(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(fault ? 0L : Plan().ByteCount, reopened.Length);
    }

    private static void AssertOpaque(UrlTextExportFileWriteResult result, string? destination)
    {
        string text = result.ToString();
        Assert.DoesNotContain(PrivateMarker, text);
        Assert.DoesNotContain("https://", text);
        Assert.DoesNotContain("IOException", text);
        if (!string.IsNullOrWhiteSpace(destination)) Assert.DoesNotContain(destination, text);
    }

    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-urltext-writer-");
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

// Explicit skips rather than green no-op test returns. No native Windows symlink/
// junction, sharing, or device-path evidence is claimed from a Linux/macOS run.
public sealed class UrlTextExportUnixTheoryAttribute : TheoryAttribute
{
    public UrlTextExportUnixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Unix symbolic-link/filename semantics require Linux or macOS; no Windows coverage claimed.";
    }
}

public sealed class UrlTextExportWindowsTheoryAttribute : TheoryAttribute
{
    public UrlTextExportWindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Native Windows namespace/device/ADS/sharing semantics require Windows; not emulated on Unix.";
    }
}
