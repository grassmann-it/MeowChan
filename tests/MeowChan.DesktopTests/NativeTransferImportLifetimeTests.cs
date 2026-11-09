using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Transfer;
using Microsoft.Data.Sqlite;

namespace MeowChan.DesktopTests;

// Real host startup, package writer, retained stage, read-only planner and admitted
// consumer. No reflection, replacement outcomes, private Ready setters or tokens.
public sealed class NativeTransferImportLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset At = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId Resource = new("4chan", "g", "7001");
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("W30 synthetic retained native import lifetime");
    private static readonly string PayloadHash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();

    [Theory]
    [InlineData("private")]
    [InlineData("multithread")]
    [InlineData("preview-only")]
    public async Task UnsupportedValidatedShapesNeverGainApplyCapabilityOrMutateTarget(string shape)
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync(shape);
        string before = f.TargetSnapshot();
        int commits = 0;
        f.Host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);

        NativeTransferImportSnapshot inspected = await session.InspectAsync(source).WaitAsync(Deadline);

        Assert.Equal(NativeTransferImportState.Unsupported, inspected.State);
        Assert.Equal(shape == "private" ? NativeTransferImportError.PrivateMetadataUnsupported
            : NativeTransferImportError.UnsupportedPackage, inspected.Error);
        Assert.Null(inspected.Plan);
        Assert.Null(inspected.Result);
        AssertOpaque(inspected, f, source);
        NativeTransferImportSnapshot attempted = await session.ApplyAsync(inspected.Generation).WaitAsync(Deadline);
        Assert.Equal(inspected, attempted);
        Assert.Equal(0, Volatile.Read(ref commits));
        Assert.Empty(Directory.GetFileSystemEntries(f.Work));
        Assert.Equal(before, f.TargetSnapshot());
        Assert.Equal(DesktopSessionState.Ready, f.Host.State);
    }

    [Fact]
    public async Task PreCanceledAndCanceledPreparedInspectionPublishNoInventoryOrPlanAndNeverApply()
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync();
        string before = f.TargetSnapshot();
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);
        int opens = 0, commits = 0;
        session.BeforeSourceOpenForTests = _ => { Interlocked.Increment(ref opens); return Task.CompletedTask; };
        f.Host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };
        using CancellationTokenSource precanceled = new();
        precanceled.Cancel();
        NativeTransferImportSnapshot early = await session.InspectAsync(source, precanceled.Token).WaitAsync(Deadline);
        Assert.Equal(NativeTransferImportState.Canceled, early.State);
        Assert.Null(early.Inventory);
        Assert.Null(early.Plan);
        Assert.Null(early.Result);
        Assert.Equal(0, Volatile.Read(ref opens));
        Assert.Empty(Directory.GetFileSystemEntries(f.Work));

        AsyncGate prepared = new();
        session.AfterPreparationForTests = _ => prepared.HoldAsync(); // Deliberately ignores cancellation.
        using CancellationTokenSource cancellation = new();
        Task<NativeTransferImportSnapshot> pending = session.InspectAsync(source, cancellation.Token);
        try
        {
            await prepared.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(NativeTransferImportState.Reading, session.Current.State);
            Assert.Null(session.Current.Inventory);
            Assert.Null(session.Current.Plan);
            Assert.Single(Directory.GetDirectories(f.Work)); // A real stage and owner-issued plan exist.
            cancellation.Cancel();
            Assert.False(pending.IsCompleted); // Cancellation is not settlement of the owned prepare.
            prepared.Release.TrySetResult();
            NativeTransferImportSnapshot result = await pending.WaitAsync(Deadline);
            Assert.Equal(NativeTransferImportState.Canceled, result.State);
            Assert.Equal(NativeTransferImportError.None, result.Error);
            Assert.Null(result.Inventory);
            Assert.Null(result.Plan);
            Assert.Null(result.Result);
            Assert.Equal(result, session.Current);
            Assert.Equal(result, await session.ApplyAsync(result.Generation).WaitAsync(Deadline));
        }
        finally { prepared.Release.TrySetResult(); await JoinForCleanupAsync(pending); }
        Assert.Equal(1, Volatile.Read(ref opens));
        Assert.Equal(0, Volatile.Read(ref commits));
        Assert.Empty(Directory.GetFileSystemEntries(f.Work));
        Assert.Equal(before, f.TargetSnapshot());
    }

    [Fact]
    public async Task CancelReadyRevokesCapabilityBeforeCleanupAndDisposeJoinsTheOwnedCleanup()
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync();
        string before = f.TargetSnapshot();
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);
        NativeTransferImportSnapshot ready = await InspectReadyAsync(session, source);
        string child = Assert.Single(Directory.GetDirectories(f.Work));
        Assert.NotEmpty(Directory.GetFileSystemEntries(child));
        AsyncGate cleanup = new();
        int commits = 0;
        f.Host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };
        session.BeforeDirectoryCleanupForTests = directory =>
        {
            Assert.Equal(child, directory);
            Assert.Empty(Directory.GetFileSystemEntries(directory)); // Stage disposal precedes parent cleanup.
            cleanup.HoldAsync().WaitAsync(Deadline).GetAwaiter().GetResult();
        };
        Task? disposed = null;
        try
        {
            session.Cancel();
            await cleanup.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(NativeTransferImportState.Canceled, session.Current.State);
            Assert.Null(session.Current.Inventory);
            Assert.Null(session.Current.Plan);
            Assert.True(session.Current.Generation > ready.Generation);
            NativeTransferImportSnapshot rejected = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);
            Assert.Equal(NativeTransferImportState.Canceled, rejected.State);
            Assert.Null(rejected.Result);
            Assert.Equal(0, Volatile.Read(ref commits));
            Assert.Equal(before, f.TargetSnapshot());
            disposed = session.DisposeAsync().AsTask();
            Assert.False(disposed.IsCompleted);
            Assert.Equal(NativeTransferImportState.Closed, session.Current.State);
            Assert.True(Directory.Exists(child));
        }
        finally
        {
            cleanup.Release.TrySetResult();
            await JoinForCleanupAsync(disposed ?? session.DisposeAsync().AsTask());
        }
        Assert.False(Directory.Exists(child));
        Assert.Empty(Directory.GetFileSystemEntries(f.Work));
        Assert.False(session.Current.CleanupRetained);
        Assert.Equal(before, f.TargetSnapshot());
    }

    [Fact]
    public async Task DisposeDuringActualPreparedReadJoinsBeforeCleanupAndCannotPublishReady()
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync();
        string before = f.TargetSnapshot();
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);
        AsyncGate prepared = new();
        session.AfterPreparationForTests = _ => prepared.HoldAsync();
        Task<NativeTransferImportSnapshot> pending = session.InspectAsync(source);
        Task? disposed = null;
        try
        {
            await prepared.Entered.Task.WaitAsync(Deadline);
            string child = Assert.Single(Directory.GetDirectories(f.Work));
            string[] staged = Directory.GetFiles(child);
            Assert.NotEmpty(staged);
            Assert.Equal(before, f.TargetSnapshot());
            disposed = session.DisposeAsync().AsTask();
            Assert.Same(disposed, session.DisposeAsync().AsTask());
            Assert.False(disposed.IsCompleted);
            Assert.False(pending.IsCompleted);
            Assert.Equal(NativeTransferImportState.Closed, session.Current.State);
            Assert.Null(session.Current.Plan);
            Assert.All(staged, path => Assert.True(File.Exists(path)));
            prepared.Release.TrySetResult();
            await disposed.WaitAsync(Deadline);
            NativeTransferImportSnapshot result = await pending.WaitAsync(Deadline);
            Assert.Equal(NativeTransferImportState.Closed, result.State);
            Assert.Null(result.Inventory);
            Assert.Null(result.Plan);
            Assert.Null(result.Result);
            Assert.Empty(Directory.GetFileSystemEntries(f.Work));
            Assert.Equal(before, f.TargetSnapshot());
        }
        finally
        {
            prepared.Release.TrySetResult();
            await JoinForCleanupAsync(pending);
            await JoinForCleanupAsync(disposed ?? session.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeDuringRealApplyWaitsAndPreservesReconciledDispositionWithoutReplacementInspect(bool committed)
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync();
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);
        NativeTransferImportSnapshot ready = await InspectReadyAsync(session, source);
        string child = Assert.Single(Directory.GetDirectories(f.Work));
        AsyncGate checkpoint = new();
        int commits = 0, reopenAttempts = 0, preparations = 0;
        f.Host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };
        session.BeforeSourceOpenForTests = _ => { Interlocked.Increment(ref reopenAttempts); return Task.CompletedTask; };
        session.AfterPreparationForTests = _ => { Interlocked.Increment(ref preparations); return Task.CompletedTask; };
        f.Host.NativeImportCheckpointForTests = phase =>
        {
            if (phase == (committed ? NativeMediaImportPhase.SqlCommitted : NativeMediaImportPhase.PreparingSaved))
                checkpoint.HoldAsync().WaitAsync(Deadline).GetAwaiter().GetResult();
        };
        Task<NativeTransferImportSnapshot> applying = session.ApplyAsync(ready.Generation);
        Task? disposed = null;
        try
        {
            await checkpoint.Entered.Task.WaitAsync(Deadline);
            Assert.Equal(NativeTransferImportState.Applying, session.Current.State);
            Assert.False(applying.IsCompleted);
            Assert.Equal(1L, f.Count("archive_media_import_operations"));
            Assert.Equal(committed ? 1L : 0L, f.Count("archive_media_import_receipts"));
            Assert.Equal(committed ? 1L : 0L, f.Count("archive_threads"));
            string durableBeforeDispose = f.TargetSnapshot();
            // A bogus replacement source would be observable if a competing read began.
            NativeTransferImportSnapshot replacement = await session.InspectAsync(Path.Combine(f.Root.Path, "must-not-open.mcw")).WaitAsync(Deadline);
            Assert.Equal(NativeTransferImportState.Applying, replacement.State);
            Assert.Equal(ready.Generation, replacement.Generation);
            Assert.Equal(ready.Plan, replacement.Plan);
            Assert.Equal(0, Volatile.Read(ref reopenAttempts));
            Assert.Equal(0, Volatile.Read(ref preparations));
            Assert.Equal(1, Volatile.Read(ref commits));
            Task<NativeTransferImportSnapshot> duplicate = session.ApplyAsync(ready.Generation);
            Assert.Same(applying, duplicate); // Join actual work, do not await before releasing its checkpoint.
            Assert.False(duplicate.IsCompleted);

            session.Cancel();
            Assert.Equal(NativeTransferImportState.Applying, session.Current.State);
            Assert.False(applying.IsCompleted);
            disposed = session.DisposeAsync().AsTask();
            Assert.Same(disposed, session.DisposeAsync().AsTask());
            Assert.False(disposed.IsCompleted);
            Assert.False(applying.IsCompleted);
            Assert.True(Directory.Exists(child));
            Assert.NotEmpty(Directory.GetFiles(child));
            checkpoint.Release.TrySetResult();
            await disposed.WaitAsync(Deadline);
            NativeTransferImportSnapshot result = await applying.WaitAsync(Deadline);
            Assert.Equal(result, await duplicate.WaitAsync(Deadline));
            Assert.Equal(NativeTransferImportState.Closed, result.State);
            NativeTransferImportResult actual = Assert.IsType<NativeTransferImportResult>(result.Result);
            Assert.Equal(committed ? NativeMediaImportCommitStatus.Committed : NativeMediaImportCommitStatus.ReconciliationRequired, actual.Status);
            Assert.Equal(committed ? 1 : 0, actual.NewThreads);
            Assert.Equal(committed ? 1 : 0, actual.NewPosts);
            Assert.Equal(committed ? 1 : 0, actual.NewAttachments);
            Assert.Equal(0, actual.UpdatedPosts);
            Assert.Equal(0, actual.Conflicts);
            Assert.Equal(actual, session.Current.Result);
            Assert.Equal(committed ? DesktopSessionState.Ready : DesktopSessionState.RecoveryFailed, f.Host.State);
            Assert.Equal(committed ? NativeTransferImportError.None : NativeTransferImportError.ApplyFailure, result.Error);
            Assert.Equal(durableBeforeDispose, f.TargetSnapshot()); // Neither receipt nor unfinished journal is erased.
            Assert.Empty(Directory.GetFileSystemEntries(f.Work));
            Assert.Equal(0, Volatile.Read(ref reopenAttempts));
            Assert.Equal(0, Volatile.Read(ref preparations));
            Assert.Equal(1, Volatile.Read(ref commits));
            AssertOpaque(result, f, source);
        }
        finally
        {
            checkpoint.Release.TrySetResult();
            await JoinForCleanupAsync(applying);
            await JoinForCleanupAsync(disposed ?? session.DisposeAsync().AsTask());
        }
    }

    [Fact]
    public async Task StaleGenerationApplyCannotConsumeTheNewReadyPlanOrCreateAnyTargetWork()
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync();
        string before = f.TargetSnapshot();
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);
        NativeTransferImportSnapshot first = await InspectReadyAsync(session, source);
        string oldChild = Assert.Single(Directory.GetDirectories(f.Work));
        NativeTransferImportSnapshot second = await InspectReadyAsync(session, source);
        Assert.True(second.Generation > first.Generation);
        Assert.False(Directory.Exists(oldChild));
        string newChild = Assert.Single(Directory.GetDirectories(f.Work));
        Assert.NotEqual(oldChild, newChild);
        int commits = 0;
        f.Host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };

        NativeTransferImportSnapshot stale = await session.ApplyAsync(first.Generation).WaitAsync(Deadline);

        Assert.Equal(second, stale);
        Assert.Equal(second, session.Current);
        Assert.NotNull(stale.Plan);
        Assert.Null(stale.Result);
        Assert.Equal(0, Volatile.Read(ref commits));
        Assert.True(Directory.Exists(newChild));
        Assert.NotEmpty(Directory.GetFiles(newChild));
        Assert.Equal(before, f.TargetSnapshot());
        await session.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.Empty(Directory.GetFileSystemEntries(f.Work));
    }

    [Fact]
    public async Task CleanupRemovesOnlyOwnedStageAndKeepsUnknownChildrenAndSiblingWithStickyWarning()
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync();
        string before = f.TargetSnapshot();
        string sibling = Path.Combine(f.Work, "unrelated-sibling");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "sentinel"), "keep sibling");
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);
        await InspectReadyAsync(session, source);
        string? ownChild = null;
        session.BeforeDirectoryCleanupForTests = directory =>
        {
            ownChild = directory;
            Assert.NotEqual(sibling, directory);
            Assert.Empty(Directory.GetFileSystemEntries(directory));
            Directory.CreateDirectory(Path.Combine(directory, "unknown-child"));
            File.WriteAllText(Path.Combine(directory, "unknown-child", "sentinel"), "keep unknown child");
        };
        session.Cancel();
        await session.DisposeAsync().AsTask().WaitAsync(Deadline); // Joins the queued Ready cancellation cleanup.

        Assert.NotNull(ownChild);
        Assert.Equal("keep unknown child", File.ReadAllText(Path.Combine(ownChild!, "unknown-child", "sentinel")));
        Assert.Equal("keep sibling", File.ReadAllText(Path.Combine(sibling, "sentinel")));
        Assert.True(session.Current.CleanupRetained);
        Assert.Equal(NativeTransferImportState.Closed, session.Current.State);
        Assert.Null(session.Current.Plan);
        await session.DisposeAsync().AsTask().WaitAsync(Deadline);
        Assert.True(session.Current.CleanupRetained);
        Assert.Equal(2, Directory.GetDirectories(f.Work).Length);
        Assert.Equal(before, f.TargetSnapshot());
        AssertOpaque(session.Current, f, source);
    }

    [Fact]
    public async Task RealPreparationFailureReportsOnlyOpaqueErrorAndCleansWithoutApplying()
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync();
        string before = f.TargetSnapshot();
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);
        const string secret = "synthetic-diagnostic-must-stay-private";
        int prepared = 0, commits = 0;
        session.AfterPreparationForTests = _ =>
        {
            Interlocked.Increment(ref prepared);
            throw new IOException(secret + ": " + source + ": " + f.Profile);
        };
        f.Host.BeforeNativeImportCommitForTests = () => { Interlocked.Increment(ref commits); return Task.CompletedTask; };

        NativeTransferImportSnapshot failed = await session.InspectAsync(source).WaitAsync(Deadline);

        Assert.Equal(1, Volatile.Read(ref prepared));
        Assert.Equal(NativeTransferImportState.Failed, failed.State);
        Assert.Equal(NativeTransferImportError.ReadFailure, failed.Error);
        Assert.Null(failed.Result);
        Assert.DoesNotContain(secret, failed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(IOException), failed.ToString(), StringComparison.Ordinal);
        AssertOpaque(failed, f, source);
        Assert.Equal(failed, await session.ApplyAsync(failed.Generation).WaitAsync(Deadline));
        Assert.Equal(0, Volatile.Read(ref commits));
        Assert.Empty(Directory.GetFileSystemEntries(f.Work));
        Assert.Equal(before, f.TargetSnapshot());
        Assert.Equal(DesktopSessionState.Ready, f.Host.State);
    }

    [Fact]
    public async Task ApplyHookFailureKeepsPublicHostStartupErrorOpaqueAndRequiresRecoveryWithoutTargetWrites()
    {
        await using Fixture f = await Fixture.StartAsync();
        string source = await f.WritePackageAsync();
        string before = f.TargetSnapshot();
        await using NativeTransferImportSession session = f.Host.CreateNativeTransferImportSession(f.Work);
        NativeTransferImportSnapshot ready = await InspectReadyAsync(session, source);
        const string secret = "synthetic-exclusive-error-must-stay-private";
        int entries = 0, checkpoints = 0;
        f.Host.BeforeNativeImportCommitForTests = () =>
        {
            Interlocked.Increment(ref entries);
            throw new IOException(secret + ": " + source + ": " + f.Profile);
        };
        f.Host.NativeImportCheckpointForTests = _ => Interlocked.Increment(ref checkpoints);

        NativeTransferImportSnapshot failed = await session.ApplyAsync(ready.Generation).WaitAsync(Deadline);

        Assert.Equal(1, Volatile.Read(ref entries));
        Assert.Equal(0, Volatile.Read(ref checkpoints));
        Assert.Equal(NativeTransferImportState.RecoveryRequired, failed.State);
        Assert.Equal(NativeTransferImportError.ApplyFailure, failed.Error);
        NativeTransferImportResult actual = Assert.IsType<NativeTransferImportResult>(failed.Result);
        Assert.Equal(NativeMediaImportCommitStatus.Unknown, actual.Status);
        Assert.Equal(0, actual.NewThreads);
        Assert.Equal(0, actual.NewPosts);
        Assert.Equal(0, actual.UpdatedPosts);
        Assert.Equal(0, actual.NewAttachments);
        Assert.Equal(0, actual.Conflicts);
        Assert.Equal(DesktopSessionState.RecoveryFailed, f.Host.State);
        Assert.Equal(DesktopSessionState.RecoveryFailed, f.Host.GetView().State);
        string startupError = Assert.IsType<string>(f.Host.StartupError);
        Assert.False(string.IsNullOrWhiteSpace(startupError));
        Assert.Equal(startupError, f.Host.GetView().StartupError);
        foreach (string raw in new[] { secret, source, f.Profile, f.Root.Path, nameof(IOException) })
        {
            Assert.DoesNotContain(raw, startupError, StringComparison.Ordinal);
            Assert.DoesNotContain(raw, failed.ToString(), StringComparison.Ordinal);
        }
        AssertOpaque(failed, f, source);
        Assert.Equal(before, f.TargetSnapshot());
        Assert.Empty(Directory.GetFileSystemEntries(f.Work));
        // Terminal cancellation is not a new outcome and cannot hide recovery.
        session.Cancel();
        Assert.Equal(failed, session.Current);
        Assert.Equal(failed, await session.ApplyAsync(ready.Generation).WaitAsync(Deadline));
        Assert.Equal(1, Volatile.Read(ref entries));
        Assert.Equal(startupError, f.Host.StartupError);
    }

    private static async Task<NativeTransferImportSnapshot> InspectReadyAsync(NativeTransferImportSession session, string source)
    {
        NativeTransferImportSnapshot result = await session.InspectAsync(source).WaitAsync(Deadline);
        Assert.Equal(NativeTransferImportState.Ready, result.State);
        Assert.Equal(NativeTransferImportError.None, result.Error);
        Assert.NotNull(result.Inventory);
        Assert.Equal(new NativeTransferImportPlanSummary(Resource, false, 1, 0, 1, 0), result.Plan);
        Assert.Null(result.Result);
        return result;
    }

    private static void AssertOpaque(NativeTransferImportSnapshot snapshot, Fixture f, string source)
    {
        string description = snapshot.ToString();
        foreach (string privatePath in new[] { f.Root.Path, f.Profile, f.Work, source, Path.GetFileName(source) })
            Assert.DoesNotContain(privatePath, description, StringComparison.Ordinal);
    }

    private static async Task JoinForCleanupAsync(Task? task)
    {
        if (task == null) return;
        try { await task; }
        catch when (task.IsCompleted) { /* Teardown joins the actual task, not a timeout surrogate. */ }
    }

    private sealed class AsyncGate
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal async Task HoldAsync()
        {
            Entered.TrySetResult();
            await Release.Task; // Ordered by explicit release; timeouts elsewhere are watchdogs only.
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal TempDirectory Root { get; } = new();
        internal string Profile { get; }
        internal string Work { get; }
        internal string Media { get; }
        internal DesktopSessionHost Host { get; }

        private Fixture()
        {
            Profile = Directory.CreateDirectory(Path.Combine(Root.Path, "profile")).FullName;
            Work = Directory.CreateDirectory(Path.Combine(Root.Path, "trusted-work")).FullName;
            Media = Path.Combine(Root.Path, "media");
            new Config
            {
                AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true,
                FirstRunSetupPending = true, DownloadPath = Media
            }.SaveConfig(Profile);
            Host = new(Profile, Array.Empty<IImageBoard>());
        }

        internal static async Task<Fixture> StartAsync()
        {
            Fixture f = new();
            try
            {
                await f.Host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, f.Host.State);
                Assert.Equal(0L, f.Count("archive_threads"));
                Assert.Equal(0L, f.Count("archive_media_import_operations"));
                Assert.Equal(0L, f.Count("archive_media_merge_operations"));
                return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }

        internal async Task<string> WritePackageAsync(string shape = "original")
        {
            TransferThread Thread(ResourceId id)
            {
                MediaProviderMetadata provider = new("8001", "original", "https://i.4cdn.org/g/8001.jpg",
                    "synthetic.jpg", Payload.Length, null, PayloadHash);
                CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
                    provider.OriginalFileName, ExpectedSize: Payload.Length)
                {
                    ExpectedSha256 = PayloadHash,
                    AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(id, "15", 0, provider)
                };
                TransferAttachment attachment = new(metadata, shape == "preview-only" ? TransferMediaRole.Preview : TransferMediaRole.Original,
                    TransferContentStatus.Contained, PayloadHash, null);
                if (shape == "v4") attachment = attachment with
                {
                    OriginalByteVersion = new(Payload.Length, PayloadHash, Convert.ToBase64String(MD5.HashData(Payload)),
                        Convert.ToBase64String(SHA1.HashData(Payload)))
                };
                TransferPost post = new(new("15", 1, PostFieldPresence.All, Html: "synthetic lifetime post",
                    Attachments: [metadata]), [attachment]);
                return new(id, $"https://boards.4chan.org/{id.Board}/thread/{id.Thread}", "synthetic lifetime", ThreadSourceState.Active,
                    At, null, null, null, [post]);
            }
            NativeTransferCatalog catalog = new(At, shape == "multithread"
                ? [Thread(Resource), Thread(new("4chan", "g", "7002"))] : [Thread(Resource)]);
            NativePrivateMetadataProjection? privateMetadata = shape == "private"
                ? new([Resource], new(true, false, false), [Resource], [], []) : null;
            TransferPayloadSource[] payloads = [new(new(Payload.Length, PayloadHash),
                _ => Task.FromResult<Stream>(new MemoryStream(Payload, writable: false)))];
            using MemoryStream bytes = new();
            TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(bytes, catalog, payloads,
                Guid.NewGuid(), privateMetadata: privateMetadata).WaitAsync(Deadline);
            Assert.True(written.IsComplete, written.Error.ToString());
            Assert.Equal(shape == "v4", written.Package!.IsChanV4);
            string path = Path.Combine(Root.Path, "synthetic-" + shape + ".mcw");
            await File.WriteAllBytesAsync(path, bytes.ToArray());
            return path;
        }

        internal long Count(string table)
        {
            using SqliteConnection connection = OpenReadOnly();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table}"; // Literal test-owned table names only.
            return (long)command.ExecuteScalar()!;
        }

        internal string TargetSnapshot()
        {
            using SqliteConnection connection = OpenReadOnly();
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            List<string> snapshot = [];
            foreach (string table in new[] { "archive_threads", "archive_posts", "archive_attachments", "archive_media_directories",
                "archive_media_import_operations", "archive_media_import_receipts", "archive_media_merge_operations",
                "archive_media_merge_receipts", "archive_import_candidates", "archive_import_receipts", "media_decisions" })
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"SELECT count(*) FROM {table}";
                snapshot.Add(table + ":" + command.ExecuteScalar());
            }
            using (SqliteCommand fence = connection.CreateCommand())
            {
                fence.Transaction = transaction;
                fence.CommandText = "SELECT instance || ':' || generation FROM archive_import_fence WHERE singleton=1";
                snapshot.Add("fence:" + fence.ExecuteScalar());
            }
            using (SqliteCommand journal = connection.CreateCommand())
            {
                journal.Transaction = transaction;
                journal.CommandText = "SELECT operation_id || ':' || state FROM archive_media_import_operations ORDER BY operation_id";
                using SqliteDataReader reader = journal.ExecuteReader();
                while (reader.Read()) snapshot.Add("operation:" + reader.GetString(0));
            }
            transaction.Commit();
            foreach (string root in new[] { Media, Path.Combine(Profile, "import-media-operations"), Path.Combine(Profile, "merge-media-operations") })
            {
                snapshot.Add(root + ":" + Directory.Exists(root));
                if (!Directory.Exists(root)) continue;
                foreach (string path in Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                    // The real SqlCommitted checkpoint still owns this lease with
                    // FileShare.None. Observe its presence without opening its bytes.
                    snapshot.Add(Path.GetRelativePath(root, path) + (Directory.Exists(path) ? "/"
                        : Path.GetFileName(path) == ".meowchan-store.lock" ? ":lease-file"
                        : ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
            }
            return string.Join("\n", snapshot);
        }

        private SqliteConnection OpenReadOnly()
        {
            SqliteConnection connection = new(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Profile, "archive.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        public async ValueTask DisposeAsync()
        {
            Host.BeforeNativeImportCommitForTests = null;
            Host.NativeImportCheckpointForTests = null;
            await Host.DisposeAsync();
            Assert.True(Host.IsProfileReleased);
            Root.Dispose();
        }
    }
}
