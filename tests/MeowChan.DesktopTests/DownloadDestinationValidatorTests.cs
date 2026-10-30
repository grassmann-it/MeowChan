using MeowChan.Desktop;
using MeowChan.Testing;

namespace MeowChan.DesktopTests;

public sealed class DownloadDestinationValidatorTests
{
    private static readonly string Base = Path.GetFullPath(TemporaryWorkspacePaths.TempRoot);
    private static readonly string Target = Path.Combine(Base, "destination-fixture");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid\0path")]
    public void SyntaxFailuresAreTypedWithoutDependingOnExceptionParamName(string? path)
    {
        DownloadDestinationResult result = DownloadDestinationValidator.Normalize(path, Base);
        Assert.Equal(DownloadDestinationStatus.InvalidSyntax, result.Status);
        Assert.Null(result.NormalizedPath);
        Assert.False(result.CanApply);
    }

    [Fact]
    public void LexicalUnicodeRelativePreviewUsesFrozenBaseAndIsNotPhysicalSuccess()
    {
        string raw = "relative ü 日本/../folder %20 $HOME";
        DownloadDestinationResult result = DownloadDestinationValidator.Normalize(raw, Base);
        Assert.Equal(Path.GetFullPath(raw, Base), result.NormalizedPath);
        Assert.Equal(DownloadDestinationStatus.NotChecked, result.Status);
        Assert.False(result.CanApply);
        Assert.Contains("$HOME", result.NormalizedPath!);
    }

    [Fact]
    public void ConfigBaselineIsTheSameOneUsedForManualDraftNormalization()
    {
        Config config = new();
        Config candidate = config.Clone();
        candidate.DownloadPath = "relative ü 日本";
        string expected = DownloadDestinationValidator.Normalize(candidate.DownloadPath, config.DownloadPathBaseDirectory).NormalizedPath!;
        config.CopySettingsFieldsFrom(candidate);
        Assert.Equal(expected, config.DownloadPath);
    }

    [Theory]
    [InlineData(null, 0, DownloadDestinationStatus.Unknown)]
    [InlineData(-1L, 0, DownloadDestinationStatus.Unknown)]
    [InlineData(0L, 0, DownloadDestinationStatus.Full)]
    [InlineData(9L, 10L, DownloadDestinationStatus.BelowReserve)]
    [InlineData(10L, 10L, DownloadDestinationStatus.Ready)]
    [InlineData(long.MaxValue, long.MaxValue, DownloadDestinationStatus.Ready)]
    public void CapacityIsHonestAndDoesNotOverflow(long? available, long reserve, DownloadDestinationStatus expected)
    {
        DownloadDestinationResult result = DownloadDestinationValidator.Capacity(Target, available, reserve);
        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == DownloadDestinationStatus.Ready, result.CanApply);
    }

    [Fact]
    public void OwnCreateNewProbeDeletesOnlyTheOwnedUnpredictableFile()
    {
        var fs = new FakeFileSystem();
        DownloadDestinationResult result = Probe(fs);
        Assert.True(result.CanApply);
        Assert.Equal(fs.Created, fs.Deleted);
        Assert.Single(fs.Created);
        Assert.Equal(Target, Path.GetDirectoryName(fs.Created[0]));
        string name = Path.GetFileName(fs.Created[0]);
        Assert.StartsWith(".meowchan-writecheck-", name);
        Assert.True(Guid.TryParseExact(name[".meowchan-writecheck-".Length..], "N", out _));
        Assert.Equal(1, fs.Content!.Length);
        Assert.Empty(fs.OpenedExisting);
    }

    [Fact]
    public void CollisionNeverAcquiresOwnershipOrDeletesAnything()
    {
        var fs = new FakeFileSystem { CreateError = new IOException("fixture collision") };
        Assert.Equal(DownloadDestinationStatus.Unknown, Probe(fs).Status);
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void MissingAndFileTargetsNeverCreateOrDelete()
    {
        var fs = new FakeFileSystem { AttributeError = new DirectoryNotFoundException() };
        Assert.Equal(DownloadDestinationStatus.Missing, Probe(fs).Status);
        fs.AttributeError = null;
        fs.FolderAttributes = FileAttributes.Normal;
        Assert.Equal(DownloadDestinationStatus.NotDirectory, Probe(fs).Status);
        Assert.Empty(fs.Created);
        Assert.Empty(fs.Deleted);
    }

    [Fact]
    public void PermissionAndOfflineAreNotReportedAsWritable()
    {
        var fs = new FakeFileSystem { CreateError = new UnauthorizedAccessException() };
        Assert.Equal(DownloadDestinationStatus.AccessDenied, Probe(fs).Status);
        Assert.Empty(fs.Deleted);
        fs.AttributeError = new DriveNotFoundException();
        Assert.Equal(DownloadDestinationStatus.Unavailable, Probe(fs).Status);
    }

    [Fact]
    public void QuotaOrFullIsReportedAndWriteFailureStillCleansOwnedFile()
    {
        var error = new IOException("fixture quota", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);
        var fs = new FakeFileSystem { WriteError = error };
        Assert.Equal(DownloadDestinationStatus.Full, Probe(fs).Status);
        Assert.Equal(fs.Created, fs.Deleted);
        Assert.Single(fs.Deleted);
    }

    [Fact]
    public void UnknownCapacityAndCleanupFailureNeverCertifySuccess()
    {
        var fs = new FakeFileSystem { Available = null };
        Assert.Equal(DownloadDestinationStatus.Unknown, Probe(fs).Status);
        Assert.Single(fs.Deleted);
        fs.CleanupError = new IOException("fixture cleanup");
        Assert.Equal(DownloadDestinationStatus.CleanupFailed, Probe(fs).Status);
    }

    [Fact]
    public async Task RepeatedTimeoutAndCancelDoNotReleasePhysicalSlotsOrQueueMoreWorkers()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var validator = new DownloadDestinationValidator(1, (path, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            release.Wait();
            return new(DownloadDestinationStatus.Ready, path);
        });
        try
        {
            Task<DownloadDestinationResult> first = validator.CheckAsync(Target, Base, 0, TimeSpan.FromMilliseconds(100));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(DownloadDestinationStatus.TimedOut, (await first).Status);
            for (int i = 0; i < 12; i++)
                Assert.Equal(DownloadDestinationStatus.Busy, (await validator.CheckAsync(Target, Base, 0, TimeSpan.FromMilliseconds(10))).Status);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Equal(DownloadDestinationStatus.Cancelled, (await validator.CheckAsync(Target, Base, 0, TimeSpan.FromSeconds(1), cancelled.Token)).Status);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task CancellingAnActiveWaitRetainsPhysicalSlotAndLateCleanupFailureIsReported()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validator = new DownloadDestinationValidator(1, (path, _) =>
        {
            entered.TrySetResult();
            release.Wait();
            return new(DownloadDestinationStatus.CleanupFailed, path);
        });
        validator.CleanupFailed += () => cleanup.TrySetResult();
        using var cancel = new CancellationTokenSource();
        try
        {
            Task<DownloadDestinationResult> waiting = validator.CheckAsync(Target, Base, 0, TimeSpan.FromSeconds(5), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            Assert.Equal(DownloadDestinationStatus.Cancelled, (await waiting).Status);
            Assert.Equal(DownloadDestinationStatus.Busy, (await validator.CheckAsync(Target, Base, 0, TimeSpan.FromSeconds(1))).Status);
            release.Set();
            await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(validator.HasCleanupFailure);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task PhysicalFixtureLeavesExistingFilesByteIdenticalAndNeverCreatesMissingFolder()
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-destination-unit-");
        try
        {
            string existing = Path.Combine(root, "existing.tmp");
            byte[] bytes = [1, 2, 3, 4];
            File.WriteAllBytes(existing, bytes);
            // Avoid claiming capacity support for every CI mount; own-file lifecycle is
            // proven regardless of an honest Unknown capacity result on linked mounts.
            var validator = new DownloadDestinationValidator();
            DownloadDestinationResult result = await validator.CheckAsync(root, Base, 0, TimeSpan.FromSeconds(5));
            Assert.Contains(result.Status, new[] { DownloadDestinationStatus.Ready, DownloadDestinationStatus.Unknown });
            Assert.Equal(bytes, File.ReadAllBytes(existing));
            Assert.Equal(new[] { existing }, Directory.GetFiles(root));
            string missing = Path.Combine(root, "missing");
            Assert.Equal(DownloadDestinationStatus.Missing, (await validator.CheckAsync(missing, Base, 0, TimeSpan.FromSeconds(5))).Status);
            Assert.False(Directory.Exists(missing));
            Assert.Equal(DownloadDestinationStatus.NotDirectory, (await validator.CheckAsync(existing, Base, 0, TimeSpan.FromSeconds(5))).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static DownloadDestinationResult Probe(FakeFileSystem fs)
        => DownloadDestinationValidator.ProbeFileSystem(Target, 10, fs);

    private sealed class FakeFileSystem : DownloadDestinationValidator.IProbeFileSystem
    {
        public FileAttributes FolderAttributes = FileAttributes.Directory;
        public long? Available = 100;
        public Exception? AttributeError, CreateError, CleanupError, WriteError;
        public readonly List<string> Created = new(), Deleted = new(), OpenedExisting = new();
        public MemoryStream? Content;
        public FileAttributes Attributes(string path)
        {
            if (AttributeError != null) throw AttributeError;
            return FolderAttributes;
        }
        public long? AvailableBytes(string path) => Available;
        public Stream CreateNew(string path)
        {
            if (CreateError != null) throw CreateError;
            Created.Add(path);
            Content = new RetainedStream(WriteError);
            return Content;
        }
        public void DeleteOwned(string path)
        {
            if (CleanupError != null) throw CleanupError;
            Deleted.Add(path);
        }
    }

    private sealed class RetainedStream(Exception? error) : MemoryStream
    {
        public override void WriteByte(byte value)
        {
            if (error != null) throw error;
            base.WriteByte(value);
        }
        protected override void Dispose(bool disposing) { /* allow fixture content assertions */ }
    }
}
