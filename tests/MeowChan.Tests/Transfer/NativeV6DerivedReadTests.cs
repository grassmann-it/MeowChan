using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Storage;

namespace MeowChan.Tests.Transfer;

// Contract-first W32a tests. Synthetic v6 manifests are reader fixtures, NOT import receipts.
// Root provides the internal snapshot inventory and OpenVerifiedDerivedReadAsync API.
public sealed class NativeV6DerivedReadTests
{
    private static readonly ResourceId Resource = new("stub", "g", "123");
    private static readonly DateTimeOffset At = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("unavailable synthetic Original; not a local asset");
    // More than two ordinary 81920-byte read buffers; tail mutation must not escape hashing.
    private static readonly byte[] Preview = Enumerable.Range(0, 196645).Select(i => (byte)(i * 31 + 7)).ToArray();
    private static readonly byte[] Poster = CreatePoster();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContainedMemberReturnsReadOnlyRegularHandleAtZeroWithoutCreatingOriginal(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, poster);
        byte[] manifest = await File.ReadAllBytesAsync(fixture.ManifestPath);
        string[] files = Directory.GetFileSystemEntries(temp.Path).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEqual(fixture.Member.SourceVersion, fixture.Member.Blob!.Actual);

        await using (Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member))
        {
            FileStream file = Assert.IsType<FileStream>(stream);
            Assert.False(file.SafeFileHandle.IsInvalid);
            Assert.False(file.SafeFileHandle.IsClosed);
            Assert.Equal(Path.GetFullPath(fixture.PayloadPath), Path.GetFullPath(file.Name));
            Assert.True(stream.CanRead);
            Assert.True(stream.CanSeek);
            Assert.False(stream.CanWrite);
            Assert.Equal(0L, stream.Position);
            Assert.Equal((long)fixture.Bytes.Length, stream.Length);
            Assert.Equal(fixture.Bytes, await ReadAllAsync(stream));
            Assert.Equal(stream.Length, stream.Position);
            stream.Position = 0;
            Assert.Equal(fixture.Bytes[0], (byte)stream.ReadByte());
        }

        AssertOriginalAbsent(fixture);
        Assert.Equal(manifest, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(files, Directory.GetFileSystemEntries(temp.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.PayloadPath));
    }

    [Fact]
    public async Task EqualSemanticCopyBelongsEvenWhenEveryNestedRecordIsANewObject()
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp);
        MediaDerivedRepresentation member = fixture.Member;
        MediaDerivedRepresentation clone = member with
        {
            Parent = member.Parent with { Resource = member.Parent.Resource with { } },
            SourceVersion = member.SourceVersion with { },
            Blob = member.Blob! with { Actual = member.Blob!.Actual with { } },
            Origin = member.Origin with { }
        };
        Assert.NotSame(member, clone);
        Assert.NotSame(member.Parent, clone.Parent);
        Assert.NotSame(member.Blob, clone.Blob);
        Assert.Equal(member, clone);
        await using Stream verified = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(clone);
        Assert.Equal(0L, verified.Position);
        Assert.Equal(fixture.Bytes, await ReadAllAsync(verified));
    }

    [Theory]
    [InlineData("source")] [InlineData("board")] [InlineData("thread")]
    [InlineData("post")] [InlineData("position")] [InlineData("revision")] [InlineData("null-revision")]
    [InlineData("source-length")] [InlineData("source-sha256")] [InlineData("source-md5")]
    [InlineData("source-sha1")] [InlineData("source-null-sha1")]
    [InlineData("kind")] [InlineData("status")] [InlineData("blob-path")]
    [InlineData("blob-length")] [InlineData("blob-sha256")] [InlineData("blob-md5")]
    [InlineData("blob-sha1")] [InlineData("blob-null-sha1")] [InlineData("null-blob")]
    [InlineData("operation")] [InlineData("fingerprint")] [InlineData("import-time")]
    public async Task KnownBlobAndHashDoNotAuthorizeAForgedSemanticMember(string change)
    {
        using TempDirectory temp = new();
        // Both representation kinds can read these valid PNG bytes, so kind rejection
        // cannot pass accidentally merely because a forged poster fails PNG parsing.
        Fixture fixture = await CreateAsync(temp, payload: Poster);
        MediaDerivedRepresentation m = fixture.Member;
        MediaDerivedBlob b = m.Blob!;
        MediaDerivedRepresentation forged = change switch
        {
            "source" => m with { Parent = m.Parent with { Resource = Resource with { Source = "other" } } },
            "board" => m with { Parent = m.Parent with { Resource = Resource with { Board = "other" } } },
            "thread" => m with { Parent = m.Parent with { Resource = Resource with { Thread = "124" } } },
            "post" => m with { Parent = m.Parent with { PostId = "124" } },
            "position" => m with { Parent = m.Parent with { Position = 1 } },
            "revision" => m with { Parent = m.Parent with { OriginalRevisionKey = "att-v1:" + new string('a', 64) } },
            "null-revision" => m with { Parent = m.Parent with { OriginalRevisionKey = null } },
            "source-length" => m with { SourceVersion = m.SourceVersion with { Length = m.SourceVersion.Length + 1 } },
            "source-sha256" => m with { SourceVersion = m.SourceVersion with { Sha256 = Hash([9]) } },
            "source-md5" => m with { SourceVersion = m.SourceVersion with { Md5 = Measure([9]).Md5 } },
            "source-sha1" => m with { SourceVersion = m.SourceVersion with { Sha1 = Measure([9]).Sha1 } },
            "source-null-sha1" => m with { SourceVersion = m.SourceVersion with { Sha1 = null } },
            "kind" => m with { Kind = MediaDerivedKind.VideoPosterPng },
            "status" => m with { IncomingStatus = MediaDerivedStatus.Unavailable },
            "blob-path" => m with { Blob = b with { RelativeFileName = "../" + b.RelativeFileName } },
            "blob-length" => m with { Blob = b with { Actual = b.Actual with { Length = b.Actual.Length + 1 } } },
            "blob-sha256" => m with { Blob = b with { Actual = b.Actual with { Sha256 = Hash([9]) } } },
            "blob-md5" => m with { Blob = b with { Actual = b.Actual with { Md5 = Measure([9]).Md5 } } },
            "blob-sha1" => m with { Blob = b with { Actual = b.Actual with { Sha1 = Measure([9]).Sha1 } } },
            "blob-null-sha1" => m with { Blob = b with { Actual = b.Actual with { Sha1 = null } } },
            "null-blob" => m with { Blob = null },
            "operation" => m with { Origin = m.Origin with { OperationId = "b0d87049-d264-42f1-9288-e6b37b864e19" } },
            "fingerprint" => m with { Origin = m.Origin with { PackageFingerprint = Hash([9]) } },
            "import-time" => m with { Origin = m.Origin with { ImportedAtUtc = At.AddTicks(1) } },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.NotEqual(m, forged);
        await AssertRejectedAsync(() => fixture.Snapshot.OpenVerifiedDerivedReadAsync(forged));
        // A failed forged call must not poison a real member or mutate snapshot inventory.
        Assert.Equal(m, Assert.Single(fixture.Snapshot.DerivedRepresentations));
        await using Stream real = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(m);
        Assert.Equal(fixture.Bytes, await ReadAllAsync(real));
    }

    [Fact]
    public async Task MemberFromAnotherSnapshotCannotBeReadByHashOrPathAlone()
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp);
        string otherDirectory = Path.Combine(temp.Path, "missing-snapshot");
        MediaAssetSnapshot other = await MediaAssetReader.OpenAsync(otherDirectory);
        Assert.Equal(MediaManifestStatus.Missing, other.Status);
        await AssertRejectedAsync(() => other.OpenVerifiedDerivedReadAsync(fixture.Member));
        Assert.False(Directory.Exists(otherDirectory));
        await AssertRejectedAsync(() => fixture.Snapshot.OpenVerifiedDerivedReadAsync(null!));
    }

    [Fact]
    public async Task ValidLaterSnapshotWithSameBlobDoesNotOwnEarlierProvenance()
    {
        using TempDirectory temp = new();
        Fixture earlier = await CreateAsync(temp);
        Fixture later = await CreateAsync(temp, operation: "b0d87049-d264-42f1-9288-e6b37b864e19");
        Assert.Equal(earlier.PayloadPath, later.PayloadPath);
        Assert.Equal(earlier.Member.Blob, later.Member.Blob);
        Assert.NotEqual(earlier.Snapshot.Generation, later.Snapshot.Generation);
        Assert.NotEqual(earlier.Member.Origin, later.Member.Origin);
        await AssertRejectedAsync(() => later.Snapshot.OpenVerifiedDerivedReadAsync(earlier.Member));
        // The immutable earlier view still owns its exact descriptor; no current-manifest lookup.
        await using Stream old = await earlier.Snapshot.OpenVerifiedDerivedReadAsync(earlier.Member);
        Assert.Equal(earlier.Bytes, await ReadAllAsync(old));
        await using Stream current = await later.Snapshot.OpenVerifiedDerivedReadAsync(later.Member);
        Assert.Equal(later.Bytes, await ReadAllAsync(current));
    }

    [Theory]
    [InlineData("length", false)] [InlineData("sha256", false)]
    [InlineData("md5", false)] [InlineData("sha1", false)]
    [InlineData("length", true)] [InlineData("sha256", true)]
    [InlineData("md5", true)] [InlineData("sha1", true)]
    public async Task EachRecordedBlobIntegrityComponentIsVerifiedIndependently(string component, bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, poster, alterActual: actual => component switch
        {
            "length" => actual with { Length = actual.Length + 1 },
            "sha256" => actual with { Sha256 = Hash([9]) },
            "md5" => actual with { Md5 = Measure([9]).Md5 },
            "sha1" => actual with { Sha1 = Measure([9]).Sha1 },
            _ => throw new ArgumentOutOfRangeException(nameof(component))
        });
        // Wire shape and exact membership are valid; only actual on-disk bytes disagree.
        byte[] manifest = await File.ReadAllBytesAsync(fixture.ManifestPath);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        });
        Assert.Equal(manifest, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.PayloadPath));
        AssertOriginalAbsent(fixture);
        // Verification failure must release its handle, including on Windows.
        using FileStream exclusive = new(fixture.PayloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("tail-same-length")] [InlineData("truncate")] [InlineData("append")]
    public async Task ChangedBytesAfterSnapshotAreRejectedEvenWithPreservedWriteTimestamp(string change)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp);
        DateTime stamp = File.GetLastWriteTimeUtc(fixture.PayloadPath);
        byte[] changed = change switch
        {
            "tail-same-length" => fixture.Bytes.ToArray(),
            "truncate" => fixture.Bytes[..^1],
            "append" => [.. fixture.Bytes, 42],
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        if (change == "tail-same-length") changed[^1] ^= 0xff;
        await File.WriteAllBytesAsync(fixture.PayloadPath, changed);
        File.SetLastWriteTimeUtc(fixture.PayloadPath, stamp);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        });
        Assert.Equal(changed, await File.ReadAllBytesAsync(fixture.PayloadPath));
        Assert.Equal(fixture.Member, Assert.Single(fixture.Snapshot.DerivedRepresentations));
    }

    [Fact]
    public async Task SuccessfulReadDoesNotCacheVerificationForLaterChangedBytes()
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp);
        await using (Stream first = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member))
            Assert.Equal(fixture.Bytes, await ReadAllAsync(first));
        byte[] changed = fixture.Bytes.ToArray();
        changed[^1] ^= 0xff;
        await File.WriteAllBytesAsync(fixture.PayloadPath, changed);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        });
    }

    [Fact]
    public async Task MissingPromisedFileRejectsWithoutRepairOrDroppingInventory()
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp);
        File.Delete(fixture.PayloadPath);
        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await using Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        });
        Assert.False(File.Exists(fixture.PayloadPath));
        Assert.Equal(fixture.Member, Assert.Single(fixture.Snapshot.DerivedRepresentations));
        AssertOriginalAbsent(fixture);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task RealNoncontainedMemberCannotBorrowAnUnlistedFile(bool unavailable, bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, poster, noncontained: unavailable
            ? MediaDerivedStatus.Unavailable : MediaDerivedStatus.Omitted);
        Assert.Null(fixture.Member.Blob);
        Assert.True(File.Exists(fixture.PayloadPath)); // tempting but unlisted matching bytes
        await AssertRejectedAsync(() => fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member));
        AssertOriginalAbsent(fixture);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReturnedHandleKeepsVerifiedObjectWhenUnixPathIsReplaced(bool poster)
    {
        // Windows FileShare.Read prevents managed replacement; this is a Unix object-identity test.
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, poster);
        await using Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        Assert.IsType<FileStream>(stream);
        string replacement = Path.Combine(temp.Path, "replacement.bin");
        byte[] other = [9, 8, 7];
        await File.WriteAllBytesAsync(replacement, other);
        File.Move(replacement, fixture.PayloadPath, overwrite: true);
        Assert.Equal(0L, stream.Position);
        Assert.Equal(fixture.Bytes, await ReadAllAsync(stream));
        Assert.Equal(other, await File.ReadAllBytesAsync(fixture.PayloadPath));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using Stream reopened = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DirectoryInPlaceOfDerivedFileIsRejected(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, poster);
        File.Delete(fixture.PayloadPath);
        Directory.CreateDirectory(fixture.PayloadPath);
        await AssertRejectedAsync(() => fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member));
        Assert.True(Directory.Exists(fixture.PayloadPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SymlinkToMatchingRegularBytesIsRejectedBeforeFollowingIt(bool poster)
    {
        // Avoid Windows privilege/developer-mode assumptions; no swallowed permission failure.
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, poster);
        string target = Path.Combine(temp.Path, "unlisted-target.bin");
        File.Move(fixture.PayloadPath, target);
        File.CreateSymbolicLink(fixture.PayloadPath, target);
        await AssertRejectedAsync(() => fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(target));
        Assert.NotNull(new FileInfo(fixture.PayloadPath).LinkTarget);
    }

    [Fact]
    public async Task UnixFifoIsRejectedWithinWatchdogWithoutBlockingOpen()
    {
        if (OperatingSystem.IsWindows()) return;
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp);
        File.Delete(fixture.PayloadPath);
        Assert.Equal(0, MkFifo(fixture.PayloadPath, 0x180u)); // owner read/write only
        // Task.Run also bounds a bad synchronous open before an async Task is returned.
        // Root must keep an outer process timeout: a failed open can leave a blocked task.
        Task attempt = Task.Run(() => AssertRejectedAsync(
            () => fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member)));
        await attempt.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("not-png")] [InlineData("crc")] [InlineData("trailing")]
    public async Task ExactDigestsDoNotAuthorizeAnInvalidVideoPoster(string corruption)
    {
        using TempDirectory temp = new();
        byte[] invalid = corruption switch
        {
            "not-png" => Preview,
            "crc" => Poster.ToArray(),
            "trailing" => [.. Poster, 0],
            _ => throw new ArgumentOutOfRangeException(nameof(corruption))
        };
        if (corruption == "crc") invalid[^1] ^= 1;
        // Hashes are measured from the malformed poster itself, not from the valid control.
        Fixture fixture = await CreateAsync(temp, poster: true, payload: invalid);
        Assert.Equal(Measure(invalid), fixture.Member.Blob!.Actual);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        });
        Assert.Equal(invalid, await File.ReadAllBytesAsync(fixture.PayloadPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreCancelledVerificationReturnsNoHandleAndDoesNotPoisonRetry(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, poster);
        byte[] manifest = await File.ReadAllBytesAsync(fixture.ManifestPath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member, cancellation.Token);
        });
        Assert.Equal(cancellation.Token, error.CancellationToken);
        using (FileStream exclusive = new(fixture.PayloadPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal((long)fixture.Bytes.Length, exclusive.Length);
        await using Stream retry = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member, CancellationToken.None);
        Assert.Equal(0L, retry.Position);
        Assert.Equal(fixture.Bytes, await ReadAllAsync(retry));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(fixture.ManifestPath));
    }

    [Fact]
    public async Task NullHistoricalSourceSha1DoesNotSkipFullyMeasuredDerivedSha1()
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, sourceWithoutSha1: true);
        Assert.Null(fixture.Member.SourceVersion.Sha1);
        Assert.NotNull(fixture.Member.Blob!.Actual.Sha1);
        await using Stream verified = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        Assert.Equal(fixture.Bytes, await ReadAllAsync(verified));
    }

    [Fact]
    public async Task MissingHistoricalSha1DoesNotMakeWrongDerivedSha1Optional()
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp, sourceWithoutSha1: true,
            alterActual: actual => actual with { Sha1 = Measure([9]).Sha1 });
        Assert.Null(fixture.Member.SourceVersion.Sha1);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using Stream stream = await fixture.Snapshot.OpenVerifiedDerivedReadAsync(fixture.Member);
        });
    }

    private static async Task AssertRejectedAsync(Func<Task<Stream>> open)
    {
        Exception? error = await Record.ExceptionAsync(async () =>
        {
            await using Stream unexpected = await open();
        });
        Assert.NotNull(error);
        // Contract specifies rejection, not a new exception taxonomy. Programming faults,
        // cancellation and test assertions are NOT accepted as an expected rejection.
        Assert.True(error is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException,
            $"Unexpected rejection type: {error.GetType().FullName}");
    }

    private static void AssertOriginalAbsent(Fixture fixture)
    {
        Assert.Equal(0, fixture.Snapshot.Count);
        Assert.Empty(fixture.Snapshot.GetPage());
        Assert.Empty(fixture.Snapshot.AttachmentBindings);
        Assert.Null(fixture.Snapshot.FindById(fixture.Member.Blob?.Actual.Sha256 ?? Hash(fixture.Bytes)));
        Assert.Null(MediaAttachmentResolver.Resolve(fixture.Snapshot, Resource, "123", fixture.Metadata));
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using MemoryStream bytes = new();
        await stream.CopyToAsync(bytes);
        return bytes.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static MediaActualIntegrity Measure(byte[] bytes) => new(bytes.LongLength,
        Convert.ToBase64String(MD5.HashData(bytes)), Hash(bytes))
        { Sha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };

    private sealed record Fixture(MediaAssetSnapshot Snapshot, MediaDerivedRepresentation Member,
        CapturedAttachment Metadata, string ManifestPath, string PayloadPath, byte[] Bytes);

    private static async Task<Fixture> CreateAsync(TempDirectory temp, bool poster = false,
        Func<MediaActualIntegrity, MediaActualIntegrity>? alterActual = null,
        MediaDerivedStatus? noncontained = null, byte[]? payload = null, bool sourceWithoutSha1 = false,
        string operation = "88a99917-1f38-4c11-bf75-ab536376e194")
    {
        byte[] bytes = payload ?? (poster ? Poster : Preview);
        MediaActualIntegrity source = Measure(Original);
        if (sourceWithoutSha1) source = source with { Sha1 = null };
        MediaProviderMetadata provider = new("456", "original", "https://stub.test/media/456.webm",
            "synthetic.webm", Original.Length, source.Md5, source.Sha256);
        string key = MediaRevisionKeys.CreateAttachmentKey(Resource, "123", 0, provider);
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url,
            provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
            { ExpectedSha256 = provider.ExpectedSha256, AttachmentRevisionKey = key };
        MediaOccurrence occurrence = new(Resource, "123", 0, key);
        MediaPassiveObservation origin = new(operation, Hash([1, 2, 3]), At);
        MediaOriginalEvidence evidence = new(occurrence, metadata, MediaObservedContentStatus.Omitted, source, origin);
        MediaActualIntegrity measured = Measure(bytes);
        MediaActualIntegrity actual = alterActual?.Invoke(measured) ?? measured;
        string fileName = actual.Sha256 + ".derived.bin";
        MediaDerivedRepresentation member = new(occurrence, source,
            poster ? MediaDerivedKind.VideoPosterPng : MediaDerivedKind.Preview,
            noncontained ?? MediaDerivedStatus.Contained,
            noncontained.HasValue ? null : new(fileName, actual), origin);
        string payloadPath = Path.Combine(temp.Path, fileName);
        await File.WriteAllBytesAsync(payloadPath, bytes);
        string manifestPath = Path.Combine(temp.Path, ".meowchan-manifest.json");
        // No constructor-extension assumption: enter via the actual persisted-reader boundary.
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new
        {
            Version = 6,
            Entries = new Dictionary<string, object>(),
            Assets = new Dictionary<string, object>(),
            AttachmentBindings = new Dictionary<string, MediaAttachmentBinding>(),
            OriginalEvidence = new[] { evidence },
            DerivedRepresentations = new[] { member }
        }));
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(6, snapshot.ManifestVersion);
        Assert.Equal(evidence, Assert.Single(snapshot.OriginalEvidence));
        Assert.Equal(member, Assert.Single(snapshot.DerivedRepresentations));
        return new(snapshot, Assert.Single(snapshot.DerivedRepresentations), metadata, manifestPath, payloadPath, bytes);
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

    [DllImport("libc", EntryPoint = "mkfifo", CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);
}
