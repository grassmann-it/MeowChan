using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real synthetic package -> owned stage -> inactive Journal2 candidate -> fresh inspector.
// No SQL, profile activation, network, external fixtures, or claim of full restart/merge acceptance.
public sealed class TransferArchiveDerivedVerificationTests
{
    private const string JournalName = ".meowchan-import-candidate.json";
    private const string ManifestName = ".meowchan-manifest.json";
    private static readonly ResourceId Resource = new("4chan", "b", "404");
    private static readonly Guid Operation = Guid.Parse("32b132b1-aca1-4a41-b111-000000000001");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 8, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic original; not a decoder fixture");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("synthetic ordinary preview bytes");

    private static async Task<(MediaAssetSnapshot Snapshot, MediaDerivedRepresentation Member)> Read(Fixture f)
    {
        var candidate = await f.Materialize();
        var snapshot = await MediaAssetReader.OpenAsync(candidate.Directory);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        return (snapshot, Assert.Single(snapshot.DerivedRepresentations));
    }

    private static MediaAssetSnapshot View(MediaAssetSnapshot snapshot, MediaDerivedRepresentation member,
        string? generation = null) => new(snapshot.Directory, snapshot.Status, 6,
            generation ?? snapshot.Generation, snapshot.GetPage(),
            originalEvidence: snapshot.OriginalEvidence, derivedRepresentations: [member]);

    [Fact]
    public async Task ExactValueMemberDeduplicatesOnlyWithinOneOperation()
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        var cache = new TransferArchiveVerificationCache(Preview.Length);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(snapshot, member, default));
        var equal = member with { Blob = member.Blob! with { Actual = member.Blob.Actual with { } } };
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(snapshot, equal, default));
        File.WriteAllBytes(Path.Combine(snapshot.Directory, member.Blob!.RelativeFileName), new byte[Preview.Length]);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(snapshot, member, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => new TransferArchiveVerificationCache()
            .VerifyDerivedAsync(snapshot, member, default));
        Assert.Empty(snapshot.GetPage()); // derived-only never manufactures an Original
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task OriginalAndDerivedShareOneBudgetInEitherOrder(bool originalFirst)
    {
        await using var f = await Fixture.CreateAsync(containedOriginal: true);
        var (snapshot, member) = await Read(f);
        var asset = Assert.Single(snapshot.GetPage());
        var cache = new TransferArchiveVerificationCache(Original.Length + Preview.Length - 1);
        if (originalFirst)
        {
            Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(snapshot, asset, default));
            Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyDerivedAsync(snapshot, member, default));
        }
        else
        {
            Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(snapshot, member, default));
            Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyAsync(snapshot, asset, default));
        }
        var exact = new TransferArchiveVerificationCache(Original.Length + Preview.Length);
        Assert.Equal(TransferAssetVerification.Available, await exact.VerifyAsync(snapshot, asset, default));
        Assert.Equal(TransferAssetVerification.Available, await exact.VerifyDerivedAsync(snapshot, member, default));
    }

    [Fact]
    public async Task SameDigestInDistinctRootsAndGenerationsIsChargedAgain()
    {
        await using var f = await Fixture.CreateAsync();
        await using var other = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        var (second, secondMember) = await Read(other);
        Assert.Equal(member, secondMember);
        Assert.NotEqual(snapshot.Directory, second.Directory);
        var cache = new TransferArchiveVerificationCache(Preview.Length);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(snapshot, member, default));
        Assert.Equal(TransferAssetVerification.Limit, await cache.VerifyDerivedAsync(second, secondMember, default));
        Assert.Equal(TransferAssetVerification.Limit,
            await cache.VerifyDerivedAsync(View(snapshot, member, "different-generation"), member, default));
    }

    [Theory]
    [InlineData("parent")] [InlineData("source")] [InlineData("kind")]
    [InlineData("status")] [InlineData("origin")] [InlineData("path")]
    [InlineData("md5")] [InlineData("sha1")] [InlineData("sha256")] [InlineData("length")]
    public async Task ForgedMemberCannotBorrowCachedSuccessEvenAtExhaustedBudget(string field)
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        var cache = new TransferArchiveVerificationCache(Preview.Length);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(snapshot, member, default));
        var forged = field switch
        {
            "parent" => member with { Parent = member.Parent with { PostId = "405" } },
            "source" => member with { SourceVersion = member.SourceVersion with { Sha1 = "changed" } },
            "kind" => member with { Kind = MediaDerivedKind.VideoPosterPng },
            "status" => member with { IncomingStatus = MediaDerivedStatus.Omitted },
            "origin" => member with { Origin = member.Origin with { ImportedAtUtc = At.AddSeconds(1) } },
            "path" => member with { Blob = member.Blob! with { RelativeFileName = "other.bin" } },
            "md5" => member with { Blob = member.Blob! with { Actual = member.Blob.Actual with { Md5 = "changed" } } },
            "sha1" => member with { Blob = member.Blob! with { Actual = member.Blob.Actual with { Sha1 = null } } },
            "sha256" => member with { Blob = member.Blob! with { Actual = member.Blob.Actual with { Sha256 = Hash([9]) } } },
            "length" => member with { Blob = member.Blob! with { Actual = member.Blob.Actual with { Length = 1 } } },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyDerivedAsync(snapshot, forged, default));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NoncontainedExactMemberRejectsBeforeBudget(bool unavailable)
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        member = member with { Blob = null, IncomingStatus = unavailable ? MediaDerivedStatus.Unavailable : MediaDerivedStatus.Omitted };
        await Assert.ThrowsAsync<InvalidDataException>(() => new TransferArchiveVerificationCache(0)
            .VerifyDerivedAsync(View(snapshot, member), member, default));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingFileOrDirectoryIsUnavailableAndStillCharged(bool directory)
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        if (directory) Directory.Move(snapshot.Directory, snapshot.Directory + "-moved");
        else File.Delete(Path.Combine(snapshot.Directory, member.Blob!.RelativeFileName));
        var cache = new TransferArchiveVerificationCache(Preview.Length);
        Assert.Equal(TransferAssetVerification.Unavailable, await cache.VerifyDerivedAsync(snapshot, member, default));
        Assert.Equal(TransferAssetVerification.Unavailable, await cache.VerifyDerivedAsync(snapshot, member, default));
        Assert.Equal(TransferAssetVerification.Limit,
            await cache.VerifyDerivedAsync(View(snapshot, member, "next"), member, default));
    }

    [Fact]
    public async Task PreviewSuccessCannotAuthorizeSameBytesAsPoster()
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        var poster = member with { Kind = MediaDerivedKind.VideoPosterPng };
        // Deliberate immutable reader fixture: exact measured digests but invalid PNG.
        var both = new MediaAssetSnapshot(snapshot.Directory, snapshot.Status, 6, snapshot.Generation, [],
            originalEvidence: snapshot.OriginalEvidence, derivedRepresentations: [member, poster]);
        var cache = new TransferArchiveVerificationCache(Preview.Length * 2);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(both, member, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyDerivedAsync(both, poster, default));
    }

    [Fact]
    public async Task MatchingOriginalDigestCannotAuthorizeMissingDerivedFile()
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        string path = Path.Combine(snapshot.Directory, "synthetic-original.bin");
        await File.WriteAllBytesAsync(path, Preview);
        var asset = new MediaAssetReference("original", "111", "original", "synthetic-original.bin",
            path, Preview.Length, Hash(Preview), "https://i.4cdn.org/b/111.webm", "original.webm", true);
        var both = new MediaAssetSnapshot(snapshot.Directory, snapshot.Status, 6, snapshot.Generation, [asset],
            originalEvidence: snapshot.OriginalEvidence, derivedRepresentations: [member]);
        var cache = new TransferArchiveVerificationCache(Preview.Length * 2);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyAsync(both, asset, default));
        File.Delete(Path.Combine(snapshot.Directory, member.Blob!.RelativeFileName));
        Assert.Equal(TransferAssetVerification.Unavailable, await cache.VerifyDerivedAsync(both, member, default));
    }

    [Theory]
    [InlineData("bytes")] [InlineData("directory")] [InlineData("symlink")]
    public async Task InvalidContentAndNonRegularFilesFailRatherThanBecomeUnavailable(string fault)
    {
        if (fault == "symlink" && OperatingSystem.IsWindows()) return;
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        string path = Path.Combine(snapshot.Directory, member.Blob!.RelativeFileName);
        if (fault == "bytes") await File.WriteAllBytesAsync(path, new byte[Preview.Length]);
        else
        {
            File.Delete(path);
            if (fault == "directory") Directory.CreateDirectory(path);
            else
            {
                string target = Path.Combine(f.Root, "target.bin");
                await File.WriteAllBytesAsync(target, Preview);
                File.CreateSymbolicLink(path, target);
            }
        }
        var error = await Record.ExceptionAsync(() => new TransferArchiveVerificationCache()
            .VerifyDerivedAsync(snapshot, member, default));
        Assert.NotNull(error);
        Assert.True(error is InvalidDataException or IOException or UnauthorizedAccessException);
        Assert.False(error is FileNotFoundException or DirectoryNotFoundException);
    }

    [Fact]
    public async Task CancellationPrecedesCachedResultAndDoesNotReserveOnPrecancel()
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var cache = new TransferArchiveVerificationCache(Preview.Length);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.VerifyDerivedAsync(snapshot, member, cancel.Token));
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(snapshot, member, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.VerifyDerivedAsync(snapshot, member, cancel.Token));
    }

    [Theory]
    [InlineData(-1L)] [InlineData(0L)] [InlineData(2147483649L)]
    public async Task InvalidOrInsufficientBudgetReturnsLimit(long maximum)
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        Assert.Equal(TransferAssetVerification.Limit,
            await new TransferArchiveVerificationCache(maximum).VerifyDerivedAsync(snapshot, member, default));
    }

    [Theory]
    [InlineData("md5")] [InlineData("sha1")]
    public async Task ExactMemberWithChangedSecondaryDigestDoesNotReuseSuccess(string digest)
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        var cache = new TransferArchiveVerificationCache(Preview.Length * 2);
        Assert.Equal(TransferAssetVerification.Available, await cache.VerifyDerivedAsync(snapshot, member, default));
        var actual = member.Blob!.Actual;
        actual = digest == "md5" ? actual with { Md5 = Md5([9]) }
            : actual with { Sha1 = Convert.ToBase64String(SHA1.HashData(new byte[] { 9 })) };
        var changed = member with { Blob = member.Blob with { Actual = actual } };
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.VerifyDerivedAsync(View(snapshot, changed), changed, default));
    }

    [Fact]
    public async Task ValidPosterUsesVerifiedDerivedReaderAndReleasesHandle()
    {
        await using var f = await Fixture.CreateAsync();
        var (snapshot, member) = await Read(f);
        byte[] bytes = CreatePoster();
        var actual = new MediaActualIntegrity(bytes.Length, Md5(bytes), Hash(bytes))
            { Sha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };
        member = member with { Kind = MediaDerivedKind.VideoPosterPng, Blob = new(Hash(bytes) + ".derived.bin", actual) };
        string path = Path.Combine(snapshot.Directory, member.Blob.RelativeFileName);
        await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal(TransferAssetVerification.Available, await new TransferArchiveVerificationCache(bytes.Length)
            .VerifyDerivedAsync(View(snapshot, member), member, default));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(bytes.Length, exclusive.Length);
    }

    private static byte[] CreatePoster()
    {
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), 1);
        header[8] = 8;
        static byte[] Chunk(string type, byte[] data)
        {
            byte[] chunk = new byte[data.Length + 12];
            BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
            Encoding.ASCII.GetBytes(type).CopyTo(chunk, 4);
            data.CopyTo(chunk, 8);
            uint crc = uint.MaxValue;
            foreach (byte value in chunk.AsSpan(4, data.Length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(chunk.Length - 4), ~crc);
            return chunk;
        }
        // Critical structure/CRC fixture only. No decoder, rendering or source-video claim.
        return [137, 80, 78, 71, 13, 10, 26, 10, .. Chunk("IHDR", header),
            .. Chunk("IDAT", [0x78, 0x9c, 0x63, 0x60, 0, 0, 0, 2, 0, 1]), .. Chunk("IEND", [])];
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; }
        internal string Parent { get; }
        internal NativeTransferImportStage Stage { get; }
        private readonly NativeMediaMaterializationPlan? plan;
        internal NativeMediaMaterializationPlan Plan => plan ?? throw new InvalidOperationException("Legacy fixture has no v4 plan.");
        private string SentinelRoot { get; }
        private Tree Sentinel { get; }
        private Fixture(string root, NativeTransferImportStage stage)
        {
            Root = root; Stage = stage;
            Parent = Path.Combine(root, "inactive-candidates"); Directory.CreateDirectory(Parent);
            SentinelRoot = Path.Combine(root, "unrelated-media"); Directory.CreateDirectory(SentinelRoot);
            File.WriteAllBytes(Path.Combine(SentinelRoot, "keep.bin"), [9, 8, 7]);
            Sentinel = Tree.Capture(SentinelRoot);
            if (stage.Package.IsChanV4) plan = NativeMediaMaterializationPlan.Create(stage, Resource);
        }
        internal Task<PassiveMediaImportCandidate> Materialize(NativeMediaCandidateSelection? selection = null,
            Action<PassiveMediaCandidatePhase, string>? checkpoint = null, CancellationToken ct = default) =>
            new MediaStore().MaterializePassiveImportCandidateAsync(Plan, selection ?? Plan.FullSelection,
                Parent, Operation, At, ct, checkpoint);
        internal string OnlyCandidate() => Assert.Single(Directory.GetDirectories(Parent, ".meowchan-passive-*"));
        internal void AssertInactive()
        {
            Sentinel.AssertUnchanged(SentinelRoot);
            Assert.Empty(Directory.GetFiles(Root, "archive.db*", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Parent));
            Assert.All(Directory.GetDirectories(Parent), p => Assert.StartsWith(".meowchan-passive-", Path.GetFileName(p)));
        }
        internal static async Task<Fixture> CreateAsync(bool containedOriginal = false, bool legacy = false)
        {
            string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-v4-journal-");
            NativeTransferImportStage? stage = null;
            try
            {
                var originalVersion = new TransferOriginalByteVersion(Original.Length, Hash(Original), Md5(Original), null);
                var provider = new MediaProviderMetadata("111", "original", "https://i.4cdn.org/b/111.webm", "original.webm",
                    Original.Length, Md5(Original), Hash(Original));
                string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "404", 0, provider);
                var metadata = new CapturedAttachment(0, provider.MediaId, "original", provider.Url, provider.OriginalFileName,
                    ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
                { AttachmentRevisionKey = key, ExpectedSha256 = Hash(Original) };
                bool contained = containedOriginal || legacy;
                var attachment = new TransferAttachment(metadata, TransferMediaRole.Original,
                    contained ? TransferContentStatus.Contained : TransferContentStatus.Omitted,
                    contained ? Hash(Original) : null, null);
                if (!legacy) attachment = attachment with
                {
                    OriginalByteVersion = originalVersion,
                    DerivedRepresentations = [new(TransferDerivedRepresentationKind.Preview, key, originalVersion,
                        TransferContentStatus.Contained, Hash(Preview))]
                };
                var post = new TransferPost(new CapturedPost("404", 0, PostFieldPresence.All,
                    Html: "<p>synthetic journal</p>", Attachments: [metadata]), [attachment]);
                var catalog = new NativeTransferCatalog(At, [new TransferThread(Resource,
                    "https://boards.4chan.org/b/thread/404", "synthetic candidate", ThreadSourceState.NotFound,
                    At, null, 0, false, [post])]);
                byte[][] payloads = legacy ? [Original] : contained ? [Original, Preview] : [Preview];
                var sources = payloads.Select(bytes => new TransferPayloadSource(new(bytes.Length, Hash(bytes)),
                    _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)))).ToArray();
                using var package = new MemoryStream();
                var written = await NativeTransferPackageWriter.WriteAsync(package, catalog, sources, Operation);
                Assert.True(written.IsComplete, written.ToString());
                Assert.Equal(!legacy, written.Package!.IsChanV4);
                package.Position = 0;
                string stageRoot = Path.Combine(root, "staging"); Directory.CreateDirectory(stageRoot);
                var staged = await NativeTransferImportStage.CreateAsync(package, stageRoot);
                stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
                return new Fixture(root, stage);
            }
            catch
            {
                if (stage != null) await stage.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            try { AssertInactive(); }
            finally
            {
                try { await Stage.DisposeAsync(); }
                finally { Directory.Delete(Root, recursive: true); }
            }
        }
    }

    private sealed record Tree(string[] Directories, SortedDictionary<string, string> Files)
    {
        internal static Tree Capture(string root) => new(
            Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(p => Path.GetRelativePath(root, p))
                .OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            new SortedDictionary<string, string>(Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(p => Path.GetRelativePath(root, p), p => Hash(File.ReadAllBytes(p))), StringComparer.Ordinal));
        internal void AssertUnchanged(string root)
        {
            var actual = Capture(root);
            Assert.Equal(Directories, actual.Directories);
            Assert.Equal(Files.ToArray(), actual.Files.ToArray());
        }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
}
