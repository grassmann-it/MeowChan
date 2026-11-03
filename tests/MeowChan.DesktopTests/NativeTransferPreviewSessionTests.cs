using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeowChan;
using System.Text;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

// Synthetic native packages only. These tests exercise the real writer and stage; no profile,
// catalog writer, store, favorite state, plugin host, network, or personal export is involved.
public sealed class NativeTransferPreviewSessionTests
{
    private static readonly DateTimeOffset When = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly ResourceId ThreadId = new("4chan", "b", "7001");
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("synthetic native transfer preview");
    private static readonly string PayloadHash = Hash(Payload);

    [Fact]
    public async Task ValidWriterPackageProducesOnlyBoundedInventoryAndCleansPrivateStage()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog());
        byte[] before = File.ReadAllBytes(source);

        await using NativeTransferPreviewSession session = new(workspace.Work);
        NativeTransferPreviewSnapshot result = await session.InspectAsync(source).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewState.Inventory, result.State);
        Assert.Equal(NativeTransferPreviewError.None, result.Error);
        TransferImportPreview inventory = Assert.IsType<TransferImportPreview>(result.Inventory);
        Assert.Single(inventory.Threads);
        Assert.Equal(1, inventory.Posts);
        Assert.Equal(6, inventory.CurrentOriginals.Total);
        Assert.Equal(6, inventory.CurrentPreviews.Total);
        Assert.Equal(1, inventory.UniquePayloads);
        Assert.Equal((long)Payload.Length, inventory.UniquePayloadBytes);
        Assert.Equal(new TransferImportStatusCounts(1, 1, 1, 1, 1, 1), inventory.CurrentOriginals);
        Assert.Equal(new TransferImportStatusCounts(1, 1, 1, 1, 1, 1), inventory.CurrentPreviews);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Work));
    }

    [Fact]
    public async Task PrivateMetadataIsTypedAsUnsupportedAndLeavesNoStageDirectory()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog(), privateMetadata: true);
        await using NativeTransferPreviewSession session = new(workspace.Work);

        NativeTransferPreviewSnapshot result = await session.InspectAsync(source).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewState.Rejected, result.State);
        Assert.Equal(NativeTransferPreviewError.PrivateMetadataNotYetSupported, result.Error);
        Assert.Null(result.Inventory);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Work));
    }

    [Fact]
    public async Task TruncatedAndTamperedPackagesFailWithoutRawDiagnostics()
    {
        using Workspace workspace = new();
        string valid = await workspace.WritePackageAsync(AllStatusesCatalog());
        byte[] bytes = File.ReadAllBytes(valid);
        string truncated = Path.Combine(workspace.Root, "truncated.mcw");
        File.WriteAllBytes(truncated, bytes[..^1]);
        string tampered = Path.Combine(workspace.Root, "tampered.mcw");
        File.WriteAllBytes(tampered, RewritePayload(bytes));

        await using NativeTransferPreviewSession session = new(workspace.Work);
        NativeTransferPreviewSnapshot one = await session.InspectAsync(truncated).WaitAsync(TimeSpan.FromSeconds(10));
        NativeTransferPreviewSnapshot two = await session.InspectAsync(tampered).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewError.InvalidPackage, one.Error);
        Assert.Equal(NativeTransferPreviewError.InvalidPackage, two.Error);
        Assert.DoesNotContain("truncated.mcw", one.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("tampered.mcw", two.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Work));
    }

    [Fact]
    public async Task ContainerLimitIsReportedSeparatelyFromMalformedInput()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog());
        await using NativeTransferPreviewSession session = new(workspace.Work,
            new TransferEnvelopeLimits { ContainerBytes = 128 });

        NativeTransferPreviewSnapshot result = await session.InspectAsync(source).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewState.Rejected, result.State);
        Assert.Equal(NativeTransferPreviewError.LimitExceeded, result.Error);
        Assert.Null(result.Inventory);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Work));
    }

    [Theory]
    [InlineData("relative-source")]
    [InlineData("missing-source")]
    [InlineData("directory-source")]
    public async Task InvalidSourcesAreRejectedBeforeAnyInventory(string kind)
    {
        using Workspace workspace = new();
        string valid = await workspace.WritePackageAsync(AllStatusesCatalog());
        string path = kind switch
        {
            "relative-source" => "relative-transfer.mcw",
            "missing-source" => Path.Combine(workspace.Root, "missing-transfer.mcw"),
            _ => workspace.Root
        };
        await using NativeTransferPreviewSession session = new(workspace.Work);

        NativeTransferPreviewSnapshot result = await session.InspectAsync(path).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewState.Rejected, result.State);
        Assert.Equal(NativeTransferPreviewError.InvalidSource, result.Error);
        Assert.Null(result.Inventory);
        Assert.Equal(1, Directory.GetFiles(workspace.Root, "*.mcw").Length);
        Assert.True(File.Exists(valid));
    }

    [Fact]
    public async Task FinalSymlinkSourceIsRejectedWithoutFollowingIt()
    {
        // Creating a link on Windows depends on developer-mode/admin privileges.
        if (OperatingSystem.IsWindows()) return;
        using Workspace workspace = new();
        string valid = await workspace.WritePackageAsync(AllStatusesCatalog());
        byte[] before = File.ReadAllBytes(valid);
        string link = Path.Combine(workspace.Root, "source-link.mcw");
        try { File.CreateSymbolicLink(link, valid); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        await using NativeTransferPreviewSession session = new(workspace.Work);

        NativeTransferPreviewSnapshot result = await session.InspectAsync(link).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewError.InvalidSource, result.Error);
        Assert.Null(result.Inventory);
        Assert.Equal(before, File.ReadAllBytes(valid));
    }

    [Fact]
    public async Task InvalidWorkParentDoesNotTouchSourceOrUnrelatedFile()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog());
        byte[] before = File.ReadAllBytes(source);
        string fileParent = Path.Combine(workspace.Root, "not-a-directory");
        File.WriteAllText(fileParent, "unrelated sentinel");
        string missingParent = Path.Combine(workspace.Root, "missing-work");

        await using NativeTransferPreviewSession first = new(missingParent);
        NativeTransferPreviewSnapshot missing = await first.InspectAsync(source).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(NativeTransferPreviewError.InvalidWorkDirectory, missing.Error);
        await first.DisposeAsync();

        await using NativeTransferPreviewSession second = new(fileParent);
        NativeTransferPreviewSnapshot file = await second.InspectAsync(source).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(NativeTransferPreviewError.InvalidWorkDirectory, file.Error);
        Assert.Equal("unrelated sentinel", File.ReadAllText(fileParent));
        Assert.Equal(before, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task StageDoesNotReopenSourceAfterAcceptance()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog());
        await using NativeTransferPreviewSession session = new(workspace.Work);
        session.AfterStageForTests = _ =>
        {
            File.Delete(source);
            File.WriteAllText(source, "not the staged package");
            return Task.CompletedTask;
        };

        NativeTransferPreviewSnapshot result = await session.InspectAsync(source).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewState.Inventory, result.State);
        Assert.NotNull(result.Inventory);
        Assert.Equal("not the staged package", File.ReadAllText(source));
    }

    [Fact]
    public async Task CancellationBeforeOpenPublishesNoLateInventory()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using NativeTransferPreviewSession session = new(workspace.Work);
        session.BeforeOpenForTests = async cancellationToken =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };

        Task<NativeTransferPreviewSnapshot> pending = session.InspectAsync(source);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Cancel();
        NativeTransferPreviewSnapshot result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewState.Canceled, result.State);
        Assert.Null(result.Inventory);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Work));
    }

    [Fact]
    public async Task DisposeWaitsForHeldRequestAndPreventsLatePublication()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog());
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using NativeTransferPreviewSession session = new(workspace.Work);
        session.BeforeOpenForTests = async _ =>
        {
            entered.SetResult();
            await release.Task;
        };

        Task<NativeTransferPreviewSnapshot> pending = session.InspectAsync(source);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task disposed = session.DisposeAsync().AsTask();
        try { Assert.False(disposed.IsCompleted); }
        finally { release.TrySetResult(); }
        await disposed.WaitAsync(TimeSpan.FromSeconds(10));
        NativeTransferPreviewSnapshot result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(NativeTransferPreviewState.Closed, result.State);
        Assert.Null(result.Inventory);
        Assert.Equal(NativeTransferPreviewState.Closed, session.Current.State);
    }

    [Fact]
    public async Task ReplacementCancelsOlderGenerationWithoutReturningItsInventory()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog());
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using NativeTransferPreviewSession session = new(workspace.Work);
        session.BeforeOpenForTests = async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.SetResult();
                await releaseFirst.Task;
            }
        };

        Task<NativeTransferPreviewSnapshot> first = session.InspectAsync(source);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<NativeTransferPreviewSnapshot> second = session.InspectAsync(source);
        releaseFirst.SetResult();
        NativeTransferPreviewSnapshot[] results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.DoesNotContain(results, item => item.Inventory is not null && item.Generation == 1);
        Assert.Equal(NativeTransferPreviewState.Inventory, results[1].State);
        Assert.NotNull(results[1].Inventory);
        Assert.Equal(NativeTransferPreviewState.Inventory, session.Current.State);
    }

    [Fact]
    public async Task CleanupWarningIsStickyAndNeverDeletesForeignChildRecursively()
    {
        using Workspace workspace = new();
        string source = await workspace.WritePackageAsync(AllStatusesCatalog());
        string? firstStage = null;
        await using NativeTransferPreviewSession session = new(workspace.Work);
        session.BeforeDirectoryCleanupForTests = directory =>
        {
            firstStage = directory;
            Assert.Empty(Directory.GetFileSystemEntries(directory)); // Stage.DisposeAsync already finished.
            File.WriteAllText(Path.Combine(directory, "foreign.marker"), "keep");
        };

        NativeTransferPreviewSnapshot first = await session.InspectAsync(source).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(first.CleanupRetained);
        Assert.NotNull(firstStage);
        Assert.True(File.Exists(Path.Combine(firstStage!, "foreign.marker")));
        Assert.Single(Directory.GetDirectories(workspace.Work));

        session.BeforeDirectoryCleanupForTests = null;
        NativeTransferPreviewSnapshot second = await session.InspectAsync(source).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(second.CleanupRetained);
        Assert.True(File.Exists(Path.Combine(firstStage!, "foreign.marker")));
    }

    private static NativeTransferCatalog AllStatusesCatalog()
    {
        ImmutableArray<TransferAttachment>.Builder attachments = ImmutableArray.CreateBuilder<TransferAttachment>();
        TransferContentStatus[] statuses = Enum.GetValues<TransferContentStatus>();
        for (int i = 0; i < statuses.Length; i++)
        {
            attachments.Add(Media(i * 2, statuses[i], TransferMediaRole.Original));
            attachments.Add(Media(i * 2 + 1, statuses[i], TransferMediaRole.Preview));
        }
        TransferPost post = new(new("15", long.MaxValue, PostFieldPresence.All, Subject: "", Html: "<b>inert</b>",
            Attachments: attachments.Select(item => item.Metadata).ToImmutableArray()), attachments.ToImmutable());
        TransferThread thread = new(ThreadId, null, "synthetic", ThreadSourceState.NotFound, When,
            null, null, null, [post]);
        return new(When, [thread]);
    }

    private static TransferAttachment Media(int position, TransferContentStatus status, TransferMediaRole role) =>
        new(new(position, (100 + position).ToString(), "original",
            $"https://i.4cdn.org/b/{100 + position}.jpg", $"synthetic-{position}.jpg", ExpectedSize: Payload.Length),
            role, status, status == TransferContentStatus.Contained ? PayloadHash : null, null);

    private static async Task<byte[]> PackageBytesAsync(NativeTransferCatalog catalog,
        NativePrivateMetadataProjection? privateMetadata = null)
    {
        using MemoryStream output = new();
        TransferPayloadSource[] payloads =
        [new(new(Payload.Length, PayloadHash), _ => Task.FromResult<Stream>(new MemoryStream(Payload, writable: false)))];
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(output, catalog, payloads,
            Guid.Parse("5d7750f7-1c3a-4093-b770-7c854fcfb117"), privateMetadata: privateMetadata);
        Assert.True(result.IsComplete, result.Error.ToString());
        return output.ToArray();
    }

    private static byte[] RewritePayload(byte[] bytes)
    {
        using MemoryStream output = new();
        using (ZipArchive input = new(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read, leaveOpen: true))
        using (ZipArchive rewritten = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in input.Entries)
            {
                ZipArchiveEntry target = rewritten.CreateEntry(entry.FullName, CompressionLevel.NoCompression);
                using Stream destination = target.Open();
                if (entry.FullName.StartsWith("payload/", StringComparison.Ordinal))
                {
                    byte[] tampered = Encoding.UTF8.GetBytes("tampered payload");
                    destination.Write(tampered);
                }
                else
                {
                    using Stream source = entry.Open();
                    source.CopyTo(destination);
                }
            }
        }
        return output.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class Workspace : IDisposable
    {
        internal string Root { get; } = TemporaryWorkspacePaths.CreateDirectory("meowchan-transfer-preview-");
        internal string Work { get; }

        internal Workspace() => Work = Directory.CreateDirectory(Path.Combine(Root, "work")).FullName;

        internal async Task<string> WritePackageAsync(NativeTransferCatalog catalog, bool privateMetadata = false)
        {
            NativePrivateMetadataProjection? projection = privateMetadata
                ? new([ThreadId], new(true, false, false), [ThreadId], [], [])
                : null;
            string path = Path.Combine(Root, privateMetadata ? "private.mcw" : "source.mcw");
            File.WriteAllBytes(path, await PackageBytesAsync(catalog, projection));
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
