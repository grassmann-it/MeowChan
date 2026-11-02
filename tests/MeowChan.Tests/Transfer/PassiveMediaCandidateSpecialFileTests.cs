using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

/// <summary>
/// Bounded negative coverage for candidate preflight. Every input is made in a fresh,
/// synthetic temp tree and the real stage-to-materializer path is used to create the
/// otherwise-valid candidate first.
/// </summary>
public sealed class PassiveMediaCandidateSpecialFileTests
{
    private static readonly ResourceId Resource = new("4chan", "b", "404");
    private static readonly DateTimeOffset ImportedAt = new(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid Operation = Guid.Parse("c2b5dd20-94b7-4b2e-a2d2-33f4c3e3b512");
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("passive candidate special-file payload\0\x01\x02\x03");

    [Theory]
    [InlineData("lease")]
    [InlineData("journal")]
    [InlineData("manifest")]
    [InlineData("finalized-payload")]
    [InlineData("own-looking-temp")]
    public async Task UnixFifoAtAnyCandidateSpecialFileIsInvalidWithinWatchdog(string target)
    {
        // The fixture is intentionally Unix-only: mkfifo is not a Windows candidate format.
        if (OperatingSystem.IsWindows()) return;

        await using StageFixture fixture = await StageFixture.CreateAsync();
        string candidateRoot = fixture.NewCandidateRoot();
        string candidateDirectory;
        if (target == "own-looking-temp")
        {
            // Retain a REAL producer-owned .part in a copying candidate. In a ready
            // candidate an extra .part would already be invalid regardless of its type.
            PassiveMediaCandidateWriteException failure = await Assert.ThrowsAsync<PassiveMediaCandidateWriteException>(
                () => fixture.Store.MaterializePassiveImportCandidateAsync(fixture.Stage, Resource,
                    fixture.SelectedKeys, candidateRoot, Operation, ImportedAt,
                    checkpoint: (phase, _) =>
                    {
                        if (phase == PassiveMediaCandidatePhase.PayloadFlushed)
                            throw new IOException("synthetic stop with an owned payload temporary");
                    }));
            candidateDirectory = failure.CandidateDirectory;
        }
        else
        {
            PassiveMediaImportCandidate candidate = await fixture.Store.MaterializePassiveImportCandidateAsync(
                fixture.Stage, Resource, fixture.SelectedKeys, candidateRoot, Operation, ImportedAt);
            candidateDirectory = candidate.Directory;
        }

        PassiveMediaCandidateInspection before = await InspectWithinWatchdogAsync(candidateDirectory, target + " control");
        Assert.Equal(target == "own-looking-temp" ? PassiveMediaCandidateStatus.Incomplete
            : PassiveMediaCandidateStatus.Ready, before.Status);

        string fifoPath = target switch
        {
            "lease" => Path.Combine(candidateDirectory, ".meowchan-store.lock"),
            "journal" => Path.Combine(candidateDirectory, ".meowchan-import-candidate.json"),
            "manifest" => Path.Combine(candidateDirectory, ".meowchan-manifest.json"),
            "finalized-payload" => Directory.EnumerateFiles(candidateDirectory, "*.original.bin",
                SearchOption.TopDirectoryOnly).Single(),
            "own-looking-temp" => Directory.EnumerateFiles(candidateDirectory, ".meowchan-payload-*.part",
                SearchOption.TopDirectoryOnly).Single(),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, null)
        };
        ReplaceWithFifo(fifoPath);

        PassiveMediaCandidateInspection result = await InspectWithinWatchdogAsync(candidateDirectory, target);
        Assert.Equal(PassiveMediaCandidateStatus.Invalid, result.Status);
        Assert.Null(result.Candidate);
    }

    private static async Task<PassiveMediaCandidateInspection> InspectWithinWatchdogAsync(string directory, string target)
    {
        // Task.Run is essential: opening a FIFO can block synchronously before Inspect
        // returns its Task. A fresh inspector also excludes producer-local caches/state.
        Task<PassiveMediaCandidateInspection> inspection = Task.Run(
            () => new MediaStore().InspectPassiveImportCandidateAsync(directory));
        Task completed = await Task.WhenAny(inspection, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(completed, inspection),
            $"Inspect did not finish within 5 seconds at synthetic candidate '{target}'. " +
            "Possible blocking FIFO open; the blocked task is NOT awaited. Root's outer timeout contains regressions.");
        // This await is reachable only after inspection has completed. Never drain a
        // blocked task in finally or with an assertion helper on the timeout path.
        return await inspection;
    }

    private static void ReplaceWithFifo(string path)
    {
        File.Delete(path);
        int result = MkFifo(path, 0x180u /* 0600: owner read/write */);
        Assert.True(result == 0,
            $"mkfifo failed for synthetic candidate path '{path}' (errno {Marshal.GetLastPInvokeError()}).");
    }

    [DllImport("libc", EntryPoint = "mkfifo", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);

    private sealed class StageFixture : IAsyncDisposable
    {
        private StageFixture(string root, NativeTransferImportStage stage, IReadOnlySet<string> selectedKeys)
        {
            Root = root;
            Stage = stage;
            SelectedKeys = selectedKeys;
        }

        internal string Root { get; }
        internal NativeTransferImportStage Stage { get; }
        internal IReadOnlySet<string> SelectedKeys { get; }
        internal MediaStore Store { get; } = new();

        internal string NewCandidateRoot()
        {
            string path = Path.Combine(Root, "candidate-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        internal static async Task<StageFixture> CreateAsync()
        {
            string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-passive-special-files-");
            try
            {
                string staging = Path.Combine(root, "stage");
                Directory.CreateDirectory(staging);
                string selected = MediaRevisionKeys.CreateAttachmentKey(Resource, "404", 0, Provider());
                TransferAttachment attachment = Attachment(selected);
                TransferPost post = new(
                    new("404", 0, PostFieldPresence.All, ImportedAt, "", "synthetic",
                        "<p>synthetic special-file candidate</p>",
                        Attachments: ImmutableArray.Create(attachment.Metadata)),
                    ImmutableArray.Create(attachment));
                NativeTransferCatalog catalog = new(ImportedAt,
                    ImmutableArray.Create(new TransferThread(Resource,
                        "https://boards.4chan.org/b/thread/404", "synthetic special-file candidate",
                        ThreadSourceState.NotFound, ImportedAt, null, null, true,
                        ImmutableArray.Create(post))));
                string sourcePath = Path.Combine(root, "package.meowchan");
                await using FileStream output = new(sourcePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                List<TransferPayloadSource> sources =
                    [new(new(Payload.Length, Hash(Payload)), _ =>
                        Task.FromResult<Stream>(new MemoryStream(Payload, writable: false)))];
                TransferWriteResult write = await NativeTransferPackageWriter.WriteAsync(
                    output, catalog, sources, Operation);
                Assert.True(write.IsComplete, write.Error.ToString());
                output.Position = 0;
                NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(output, staging);
                NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
                return new StageFixture(root, stage,
                    new HashSet<string>(new[] { selected }, StringComparer.Ordinal));
            }
            catch
            {
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Stage.DisposeAsync();
            }
            finally
            {
                // Directory.Delete recursively unlinks the owned synthetic FIFO; it does not open it.
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            }
        }

        private static TransferAttachment Attachment(string key)
        {
            MediaProviderMetadata provider = Provider();
            CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
                provider.OriginalFileName, ExpectedSize: provider.ExpectedSize,
                ExpectedMd5: provider.ExpectedMd5)
            { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key };
            return new(metadata, TransferMediaRole.Original, TransferContentStatus.Contained,
                Hash(Payload), null);
        }

        private static MediaProviderMetadata Provider()
            => new("media-special-files", "original", "https://i.4cdn.org/b/404.jpg",
                "special-files.bin", Payload.Length, Convert.ToBase64String(MD5.HashData(Payload)), Hash(Payload));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
