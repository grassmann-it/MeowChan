using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.DesktopTests;

/// <summary>
/// Poster cache maintenance: warm-hit validation without a second full decode, corrupt cache files, bounded request admission and
/// eviction outside the native decode gate. Fakes only; no native decoder and no network.
/// </summary>
public sealed class VideoPosterCacheMaintenanceTests
{
    [Fact]
    public async Task AWarmHitPerformsNoValidationDecodeAndAColdPosterExactlyOne()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(Path.Combine(temp.Path, "media"), [1, 2, 3, 4]);
        string cache = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60, SKColors.Coral);
        int fullDecodes = 0, decoderCalls = 0;
        VideoPosterCacheTestSeam cold = new()
        {
            FullDecodeValidation = () => Interlocked.Increment(ref fullDecodes),
            Decoder = (verified, _) => { verified.Dispose(); Interlocked.Increment(ref decoderCalls); return Task.FromResult(png); },
        };

        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None, testSeam: cold));
        Assert.Equal(1, decoderCalls);
        Assert.Equal(1, fullDecodes);

        VideoPosterCacheTestSeam warm = new()
        {
            FullDecodeValidation = () => Interlocked.Increment(ref fullDecodes),
            Decoder = (_, _) => throw new InvalidOperationException("A warm hit must not enter the decoder."),
        };
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None, testSeam: warm));
        // The display decode of the caller is the only full decode of a warm hit.
        Assert.Equal(1, fullDecodes);
        Assert.Equal(1, decoderCalls);
    }

    public static TheoryData<string> Corruptions => new() { "drop-iend", "truncate-middle", "flip-idat", "append", "zero-length" };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task ACorruptCachedPosterIsNeverOfferedAndIsRegenerated(string corruption)
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(Path.Combine(temp.Path, "media"), [1, 2, 3, 4]);
        string cache = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(cache);
        byte[] fresh = Png(80, 60, SKColors.Coral);
        byte[] corrupt = Corrupt(Png(80, 60, SKColors.Teal), corruption);
        string path = Path.Combine(cache, asset.Sha256.ToLowerInvariant() + ".png");
        await File.WriteAllBytesAsync(path, corrupt);
        int decoderCalls = 0;
        VideoPosterCacheTestSeam seam = new()
        {
            Decoder = (verified, _) => { verified.Dispose(); Interlocked.Increment(ref decoderCalls); return Task.FromResult(fresh); },
        };

        Assert.Equal(fresh, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None, testSeam: seam));
        Assert.Equal(1, decoderCalls);
        Assert.Equal(fresh, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ABurstBeyondTheWorkingSlotsQueuesAndOnlyAFullQueueRefuses()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(Path.Combine(temp.Path, "media"), [1, 2, 3, 4]);
        string cache = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60, SKColors.Coral);
        VideoPosterAdmission admission = new(activeLimit: 1, queueLimit: 2);
        TaskCompletionSource decoding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<byte[]> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int decoderCalls = 0;
        VideoPosterCacheTestSeam first = new()
        {
            Admission = admission,
            Decoder = async (verified, token) =>
            {
                await using (verified)
                {
                    Interlocked.Increment(ref decoderCalls);
                    decoding.TrySetResult();
                    return await release.Task.WaitAsync(token);
                }
            },
        };
        // Queued requests carry a short watchdog: waiting for admission must not count against it.
        VideoPosterCacheTestSeam queued = new()
        {
            Admission = admission,
            BoundedTimeout = TimeSpan.FromSeconds(1),
            Decoder = (_, _) => throw new InvalidOperationException("Queued requests are served from the published poster."),
        };

        Task<byte[]?> working = VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None, testSeam: first);
        await decoding.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<byte[]?> second = VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None, testSeam: queued);
        Task<byte[]?> third = VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None, testSeam: queued);
        await WaitUntilAsync(() => admission.Waiting == 2);

        // The bounded queue is full: this request is refused at once instead of waiting without bound.
        Assert.Null(await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None, testSeam: queued)
            .WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        release.SetResult(png);
        Assert.Equal(png, await working.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(png, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(png, await third.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, decoderCalls);
        Assert.Equal(0, admission.Waiting);
        Assert.Equal(1, admission.Available);
    }

    [Fact]
    public async Task CallerCancellationWhileQueuedIsObservableAndHoldsNoSlot()
    {
        using TempDirectory temp = new();
        var (manifest, asset) = await ManifestAsync(Path.Combine(temp.Path, "media"), [1, 2, 3, 4]);
        string cache = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(cache);
        byte[] png = Png(80, 60, SKColors.Coral);
        VideoPosterAdmission admission = new(activeLimit: 1, queueLimit: 1);
        TaskCompletionSource decoding = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<byte[]> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoPosterCacheTestSeam first = new()
        {
            Admission = admission,
            Decoder = async (verified, token) =>
            {
                await using (verified)
                {
                    decoding.TrySetResult();
                    return await release.Task.WaitAsync(token);
                }
            },
        };

        Task<byte[]?> working = VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None, testSeam: first);
        await decoding.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource caller = new();
        Task<byte[]?> waiting = VideoPosterCache.GetPngAsync(manifest, asset, cache, caller.Token,
            testSeam: new VideoPosterCacheTestSeam { Admission = admission });
        await WaitUntilAsync(() => admission.Waiting == 1);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, admission.Waiting);

        release.SetResult(png);
        Assert.Equal(png, await working.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, admission.Available);
        // The canceled request left no negative entry: the same poster is served right away.
        Assert.Equal(png, await VideoPosterCache.GetPngAsync(manifest, asset, cache, CancellationToken.None,
            testSeam: new VideoPosterCacheTestSeam { Admission = admission }));
    }

    [Fact]
    public async Task AdmissionEntersRefusesAndReleasesWithinItsBounds()
    {
        VideoPosterAdmission admission = new(activeLimit: 1, queueLimit: 1);
        Assert.True(await admission.TryEnterAsync(CancellationToken.None));
        ValueTask<bool> queued = admission.TryEnterAsync(CancellationToken.None);
        Assert.False(queued.IsCompleted);
        Assert.False(await admission.TryEnterAsync(CancellationToken.None));
        admission.Exit();
        Assert.True(await queued.AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        admission.Exit();
        Assert.Equal(1, admission.Available);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await admission.TryEnterAsync(canceled.Token));
        Assert.Equal(1, admission.Available);
    }

    [Fact]
    public void ALockedEvictionCandidateIsKeptAndTheNextOneIsEvicted()
    {
        using TempDirectory temp = new();
        for (int index = 0; index < 10; index++)
        {
            string path = Path.Combine(temp.Path, index.ToString("D4") + ".png");
            using (FileStream stream = File.Create(path)) stream.SetLength(600_000);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }
        string locked = Path.Combine(temp.Path, "0000.png");

        VideoPosterCache.TrimResult result = VideoPosterCache.Trim(temp.Path, 0, 3_000_000, 100, path =>
        {
            if (path == locked) throw new IOException("The file is in use.");
            File.Delete(path);
        });

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(Path.Combine(temp.Path, "0001.png")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "0009.png")));
        Assert.Equal(5, result.Files);
        Assert.Equal(5, Directory.GetFiles(temp.Path, "*.png").Length);
        Assert.Equal(3_000_000, result.Bytes);
    }

    [Fact]
    public void AVanishedEvictionCandidateCountsAsEvicted()
    {
        using TempDirectory temp = new();
        for (int index = 0; index < 4; index++)
        {
            string path = Path.Combine(temp.Path, index.ToString("D4") + ".png");
            using (FileStream stream = File.Create(path)) stream.SetLength(1_000);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 100));
        }

        VideoPosterCache.TrimResult result = VideoPosterCache.Trim(temp.Path, 0, 1_000_000, 3, path =>
        {
            File.Delete(path);
            throw new FileNotFoundException("Removed by someone else.", path);
        });

        Assert.Equal(2, result.Files);
        Assert.Equal(2, Directory.GetFiles(temp.Path, "*.png").Length);
    }

    [Fact]
    public async Task EvictionRunsAfterTheNativeGateAndLockedCandidatesDoNotPoisonTheNewPoster()
    {
        using TempDirectory temp = new();
        string cache = Path.Combine(temp.Path, "cache");
        Directory.CreateDirectory(cache);
        // More files than the default file limit: the first save in this directory scans and must evict.
        int seeded = VideoPosterCache.DefaultCacheFiles + 1;
        for (int index = 0; index < seeded; index++)
        {
            string path = Path.Combine(cache, "old-" + index.ToString("D4") + ".png");
            File.WriteAllBytes(path, [0]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 10_000));
        }
        var (firstManifest, firstAsset) = await ManifestAsync(Path.Combine(temp.Path, "one"), [1, 2, 3, 4]);
        var (secondManifest, secondAsset) = await ManifestAsync(Path.Combine(temp.Path, "two"), [5, 6, 7, 8]);
        byte[] firstPng = Png(80, 60, SKColors.Coral);
        byte[] secondPng = Png(80, 60, SKColors.Teal);
        TaskCompletionSource evicting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseEviction = new(false);
        int deleteAttempts = 0;
        VideoPosterCacheTestSeam lockedEviction = new()
        {
            Decoder = (verified, _) => { verified.Dispose(); return Task.FromResult(firstPng); },
            DeleteCacheFile = _ =>
            {
                Interlocked.Increment(ref deleteAttempts);
                evicting.TrySetResult();
                releaseEviction.Wait(TimeSpan.FromSeconds(10));
                throw new IOException("The file is in use.");
            },
        };

        Task<byte[]?> first = VideoPosterCache.GetPngAsync(firstManifest, firstAsset, cache, CancellationToken.None,
            testSeam: lockedEviction);
        await evicting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While the first save is stuck in eviction, another cold poster in the same directory still gets the native gate,
        // publishes, and does not wait behind the running scan.
        Assert.Equal(secondPng, await VideoPosterCache.GetPngAsync(secondManifest, secondAsset, cache, CancellationToken.None,
            testSeam: new VideoPosterCacheTestSeam
            {
                Decoder = (verified, _) => { verified.Dispose(); return Task.FromResult(secondPng); },
            }).WaitAsync(TimeSpan.FromSeconds(5)));

        releaseEviction.Set();
        Assert.Equal(firstPng, await first.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(deleteAttempts > 1);
        string firstPath = Path.Combine(cache, firstAsset.Sha256.ToLowerInvariant() + ".png");
        Assert.Equal(firstPng, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(seeded + 2, Directory.GetFiles(cache, "*.png").Length);

        // No negative cache entry was recorded: once the poster is gone, the same saved file generates it again at once.
        File.Delete(firstPath);
        Assert.Equal(firstPng, await VideoPosterCache.GetPngAsync(firstManifest, firstAsset, cache, CancellationToken.None,
            testSeam: new VideoPosterCacheTestSeam
            {
                Decoder = (verified, _) => { verified.Dispose(); return Task.FromResult(firstPng); },
            }));
        Assert.True(File.Exists(firstPath));
        Assert.True(Directory.GetFiles(cache, "*.png").Length <= VideoPosterCache.DefaultCacheFiles);
    }

    [Fact]
    public void StructureCheckRejectsEveryTruncationAndEverySingleByteFlip()
    {
        byte[] png = Png(12, 8, SKColors.Coral);
        Assert.True(PreviewPngIntegrity.TryReadStructure(png, png.Length, 960, 640, out int width, out int height));
        Assert.Equal((12, 8), (width, height));
        Assert.True(VideoPosterCache.ValidStored(png));
        Assert.True(VideoPosterCache.Valid(png));

        for (int length = 0; length < png.Length; length++)
            Assert.False(PreviewPngIntegrity.TryReadStructure(png.AsSpan(0, length), png.Length, 960, 640, out _, out _));
        for (int index = 0; index < png.Length; index++)
        {
            byte[] flipped = (byte[])png.Clone();
            flipped[index] ^= 0x01;
            Assert.False(PreviewPngIntegrity.TryReadStructure(flipped, png.Length, 960, 640, out _, out _));
        }
        byte[] appended = [.. png, 0];
        Assert.False(VideoPosterCache.ValidStored(appended));
        Assert.False(VideoPosterCache.Valid(appended));
    }

    [Fact]
    public void BothChecksKeepTheDimensionAndSizeBounds()
    {
        byte[] tooWide = Png(961, 2, SKColors.Coral);
        Assert.False(VideoPosterCache.ValidStored(tooWide));
        Assert.False(VideoPosterCache.Valid(tooWide));
        byte[] scrubTooTall = Png(16, VideoScrubPreviewOptions.ThumbnailMaximumHeight + 1, SKColors.Coral);
        Assert.False(VideoScrubPreviewCache.ValidStored(scrubTooTall));
        Assert.False(VideoScrubPreviewCache.Valid(scrubTooTall));
        Assert.False(VideoPosterCache.ValidStored(new byte[VideoPosterCache.MaximumPngBytes + 1]));
        Assert.False(VideoPosterCache.ValidStored(Encoding.ASCII.GetBytes("not a png at all")));
    }

    [Fact]
    public void ChunkReservedBitUsesTheThirdCharacterAndAllowsSafeToCopyAncillaryChunks()
    {
        byte[] png = Png(12, 8, SKColors.Coral);
        byte[] withMetadata = InsertBeforeIdat(png,
            Chunk("pHYs", [0, 0, 0, 1, 0, 0, 0, 1, 1]),
            Chunk("tEXt", Encoding.ASCII.GetBytes("Comment\0offline")),
            Chunk("iTXt", Encoding.UTF8.GetBytes("Comment\0\0\0\0\0offline")));

        Assert.True(PreviewPngIntegrity.TryReadStructure(withMetadata, withMetadata.Length, 960, 640, out _, out _));

        // The third type byte is the PNG reserved bit. A lowercase fourth byte is valid Safe-to-Copy metadata.
        byte[] invalidReserved = InsertBeforeIdat(png, Chunk("pHys", []));
        Assert.False(PreviewPngIntegrity.TryReadStructure(invalidReserved, invalidReserved.Length, 960, 640, out _, out _));
    }

    [Fact]
    public void StoredCheckAcceptsStoredFixedDynamicAndMultiBlockMembersButNotFalseCompleteMembers()
    {
        byte[] raw = [0, 1, 2, 3, 4];
        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(4, 1, StoredZlib(raw))));
        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(4, 1, FixedZlib(raw))));
        byte[] dynamicMember = DynamicZlib(raw);
        // Prove this hand-built positive fixture with the independent BCL inflater, not just our boundary parser.
        using (MemoryStream compressed = new(dynamicMember))
        using (ZLibStream inflater = new(compressed, CompressionMode.Decompress))
        using (MemoryStream inflated = new())
        {
            inflater.CopyTo(inflated);
            Assert.Equal(raw, inflated.ToArray());
        }
        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(4, 1, dynamicMember)));
        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(4, 1, MultiStoredZlib(raw))));
        Assert.True(VideoPosterCache.ValidStored(SyntheticPng(1, 1, StoredZlib([0, 1]), interlaceMethod: 1)));

        byte[] stored = StoredZlib(raw);
        byte[] trailingMemberBytes = [.. stored[..^4], 0x42, 0x42, .. stored[^4..]];
        Assert.False(VideoPosterCache.ValidStored(SyntheticPng(4, 1, trailingMemberBytes)));

        byte[] nonFinalBlock = NonFinalStoredZlib(raw);
        Assert.False(VideoPosterCache.ValidStored(SyntheticPng(4, 1, nonFinalBlock)));

        // The member boundary pass rejects output beyond the exact IHDR-derived scanline budget before ZLibStream sees it.
        Assert.False(VideoPosterCache.ValidStored(SyntheticPng(1, 1, StoredZlib([0, 1, 2]))));
        Assert.False(VideoPosterCache.ValidStored(SyntheticPng(4, 1, stored[..^1])));
    }

    [Fact]
    public void StoredCheckRequiresTheZlibAdlerTrailer()
    {
        byte[] withoutAdler = WithoutIdatAdlerTrailer(Png(12, 8, SKColors.Coral));

        // Chunk framing and CRC remain valid; only the mandatory zlib trailer is missing.
        Assert.True(PreviewPngIntegrity.TryReadStructure(withoutAdler, withoutAdler.Length, 960, 640, out _, out _));
        Assert.False(VideoPosterCache.ValidStored(withoutAdler));
    }

    [Fact]
    public void StoredCheckRejectsMalformedZlibEvenWhenTheChunkCrcIsValid()
    {
        byte[] malformed = Png(12, 8, SKColors.Coral);
        int idat = IndexOf(malformed, "IDAT"u8);
        Assert.True(idat >= 4);
        int payloadLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(malformed.AsSpan(idat - 4, 4)));
        Assert.True(payloadLength >= 3);

        // Keep the PNG chunk structurally valid but make the first DEFLATE block use the reserved BTYPE=3.
        malformed[idat + 6] = 0x07;
        uint crc = PreviewPngIntegrity.Crc32(malformed.AsSpan(idat, 4 + payloadLength));
        BinaryPrimitives.WriteUInt32BigEndian(malformed.AsSpan(idat + 4 + payloadLength, 4), crc);

        Assert.True(PreviewPngIntegrity.TryReadStructure(malformed, malformed.Length, 960, 640, out _, out _));
        Assert.False(VideoPosterCache.ValidStored(malformed));
    }

    [Fact]
    public void ParserRejectsNonPositiveBoundsAndUnknownCriticalChunks()
    {
        byte[] png = Png(12, 8, SKColors.Coral);
        Assert.False(PreviewPngIntegrity.TryReadStructure(png, 0, 960, 640, out _, out _));
        Assert.False(PreviewPngIntegrity.TryReadStructure(png, png.Length, 0, 640, out _, out _));
        Assert.False(PreviewPngIntegrity.TryReadStructure(png, png.Length, 960, 0, out _, out _));

        int idat = IndexOf(png, "IDAT"u8);
        Assert.True(idat >= 4);
        int chunkStart = idat - 4;
        byte[] unknownCritical = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(unknownCritical, 0);
        "ABCD"u8.CopyTo(unknownCritical.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(unknownCritical.AsSpan(8), PreviewPngIntegrity.Crc32(unknownCritical.AsSpan(4, 4)));
        byte[] withUnknownCritical = [.. png[..chunkStart], .. unknownCritical, .. png[chunkStart..]];

        Assert.False(PreviewPngIntegrity.TryReadStructure(withUnknownCritical, withUnknownCritical.Length, 960, 640, out _, out _));
    }

    internal static byte[] Corrupt(byte[] png, string corruption)
    {
        switch (corruption)
        {
            case "drop-iend": return png[..^12];
            case "truncate-middle": return png[..(png.Length / 2)];
            case "append": return [.. png, 0, 0, 0, 0];
            case "zero-length": return [];
            case "flip-idat":
            {
                byte[] copy = (byte[])png.Clone();
                int idat = IndexOf(copy, "IDAT"u8);
                Assert.True(idat > 0);
                copy[idat + 6] ^= 0x40;
                return copy;
            }
            default: throw new ArgumentOutOfRangeException(nameof(corruption), corruption, null);
        }
    }

    private static int IndexOf(byte[] bytes, ReadOnlySpan<byte> value) => bytes.AsSpan().IndexOf(value);

    private static byte[] SyntheticPng(int width, int height, byte[] zlib, byte interlaceMethod = 0)
    {
        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 0; // 8-bit grayscale, so raw scanlines are one filter byte plus one byte per pixel.
        ihdr[12] = interlaceMethod;
        return [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            .. Chunk("IHDR", ihdr), .. Chunk("IDAT", zlib), .. Chunk("IEND", [])];
    }

    private static byte[] StoredZlib(byte[] raw) => Zlib(StoredDeflate(raw), raw);

    private static byte[] NonFinalStoredZlib(byte[] raw) => Zlib(StoredDeflate(raw, final: false), raw);

    private static byte[] MultiStoredZlib(byte[] raw)
    {
        DeflateBitWriter writer = new();
        WriteStoredBlock(writer, raw[..2], final: false);
        WriteStoredBlock(writer, raw[2..], final: true);
        return Zlib(writer.ToArray(), raw);
    }

    private static byte[] FixedZlib(byte[] raw)
    {
        DeflateBitWriter writer = new();
        writer.Bits(1, 1); // BFINAL
        writer.Bits(1, 2); // BTYPE=01, fixed Huffman
        foreach (byte value in raw) WriteFixedSymbol(writer, value);
        WriteFixedSymbol(writer, 256); // end-of-block
        return Zlib(writer.ToArray(), raw);
    }

    private static byte[] DynamicZlib(byte[] raw)
    {
        DeflateBitWriter writer = new();
        writer.Bits(1, 1); // BFINAL
        writer.Bits(2, 2); // BTYPE=10, dynamic Huffman
        writer.Bits(0, 5); // HLIT=257: 255 eight-bit codes and two nine-bit codes form a COMPLETE tree.
        writer.Bits(0, 5); // HDIST=1
        writer.Bits(15, 4); // HCLEN=19
        foreach (int length in new[] { 2, 0, 0, 0, 2, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0 }) writer.Bits(length, 3);

        WriteDynamicCodeLengthSymbol(writer, 8);
        for (int repeat = 0; repeat < 42; repeat++) WriteDynamicRepeat(writer, 3); // six eights each: 1 + 252
        for (int repeat = 0; repeat < 2; repeat++) WriteDynamicCodeLengthSymbol(writer, 8); // 255 eights total
        WriteDynamicCodeLengthSymbol(writer, 9); // literal 255
        WriteDynamicCodeLengthSymbol(writer, 9); // EOB 256
        WriteDynamicCodeLengthSymbol(writer, 1); // one unused one-bit distance code

        foreach (byte value in raw) WriteDeflateCode(writer, value == 255 ? 510 : value, value == 255 ? 9 : 8);
        WriteDeflateCode(writer, 511, 9); // EOB, the final canonical code in the complete tree
        return Zlib(writer.ToArray(), raw);
    }

    private static byte[] StoredDeflate(byte[] raw, bool final = true)
    {
        DeflateBitWriter writer = new();
        WriteStoredBlock(writer, raw, final);
        return writer.ToArray();
    }

    private static void WriteStoredBlock(DeflateBitWriter writer, byte[] raw, bool final)
    {
        writer.Bits(final ? 1 : 0, 1);
        writer.Bits(0, 2); // BTYPE=00, stored
        writer.Align();
        writer.Bits(raw.Length, 16);
        writer.Bits(raw.Length ^ 0xFFFF, 16);
        foreach (byte value in raw) writer.Bits(value, 8);
    }

    private static void WriteFixedSymbol(DeflateBitWriter writer, int symbol)
    {
        (int code, int length) = symbol switch
        {
            <= 143 => (0x30 + symbol, 8),
            <= 255 => (0x190 + symbol - 144, 9),
            <= 279 => (symbol - 256, 7),
            <= 287 => (0xC0 + symbol - 280, 8),
            _ => throw new ArgumentOutOfRangeException(nameof(symbol)),
        };
        WriteDeflateCode(writer, code, length);
    }

    private static void WriteDeflateCode(DeflateBitWriter writer, int code, int length)
    {
        writer.Bits(ReverseBits(code, length), length);
    }

    private static int ReverseBits(int value, int length)
    {
        int reversed = 0;
        for (int index = 0; index < length; index++)
        {
            reversed = (reversed << 1) | (value & 1);
            value >>= 1;
        }
        return reversed;
    }

    private static void WriteDynamicCodeLengthSymbol(DeflateBitWriter writer, int symbol)
    {
        switch (symbol)
        {
            case 1: WriteDeflateCode(writer, 0, 2); break; // canonical code 00, length 2
            case 8: WriteDeflateCode(writer, 1, 2); break; // canonical code 01, length 2
            case 9: WriteDeflateCode(writer, 2, 2); break; // canonical code 10, length 2
            case 16: WriteDeflateCode(writer, 3, 2); break; // canonical code 11, length 2
            default: throw new ArgumentOutOfRangeException(nameof(symbol));
        }
    }

    private static void WriteDynamicRepeat(DeflateBitWriter writer, int extra)
    {
        WriteDynamicCodeLengthSymbol(writer, 16);
        writer.Bits(extra, 2);
    }

    private static byte[] Zlib(byte[] deflate, byte[] raw)
    {
        byte[] result = new byte[2 + deflate.Length + 4];
        result[0] = 0x78;
        result[1] = 0x9C;
        Buffer.BlockCopy(deflate, 0, result, 2, deflate.Length);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), Adler32(raw));
        return result;
    }

    private static uint Adler32(byte[] bytes)
    {
        const uint Modulus = 65_521;
        uint first = 1, second = 0;
        foreach (byte value in bytes)
        {
            first += value;
            if (first >= Modulus) first -= Modulus;
            second += first;
            if (second >= Modulus) second %= Modulus;
        }
        return (second << 16) | first;
    }

    private sealed class DeflateBitWriter
    {
        private readonly List<byte> _bytes = [0];
        private int _bit;

        internal void Bits(int value, int count)
        {
            for (int index = 0; index < count; index++)
            {
                if (((value >> index) & 1) != 0) _bytes[^1] |= (byte)(1 << _bit);
                if (++_bit == 8)
                {
                    _bit = 0;
                    _bytes.Add(0);
                }
            }
        }

        internal void Align()
        {
            if (_bit == 0) return;
            _bit = 0;
            _bytes.Add(0);
        }

        internal byte[] ToArray()
        {
            if (_bit == 0) _bytes.RemoveAt(_bytes.Count - 1);
            return _bytes.ToArray();
        }
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        Assert.Equal(4, Encoding.ASCII.GetByteCount(type));
        byte[] chunk = new byte[data.Length + 12];
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(0, 4), (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(chunk.AsSpan(4, 4));
        data.CopyTo(chunk.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length, 4),
            PreviewPngIntegrity.Crc32(chunk.AsSpan(4, 4 + data.Length)));
        return chunk;
    }

    private static byte[] InsertBeforeIdat(byte[] png, params byte[][] chunks)
    {
        int idat = IndexOf(png, "IDAT"u8);
        Assert.True(idat >= 4);
        int insertion = idat - 4;
        int extra = chunks.Sum(chunk => chunk.Length);
        byte[] result = new byte[png.Length + extra];
        Buffer.BlockCopy(png, 0, result, 0, insertion);
        int offset = insertion;
        foreach (byte[] chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }
        Buffer.BlockCopy(png, insertion, result, offset, png.Length - insertion);
        return result;
    }

    private static byte[] WithoutIdatAdlerTrailer(byte[] png)
    {
        int idat = IndexOf(png, "IDAT"u8);
        Assert.True(idat >= 4);
        int chunkStart = idat - 4;
        int payloadLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(chunkStart, 4)));
        Assert.True(payloadLength >= 6);

        // The small test PNG has one IDAT chunk. Remove exactly its four-byte Adler trailer and rebuild its length/CRC.
        byte[] result = new byte[png.Length - 4];
        Buffer.BlockCopy(png, 0, result, 0, chunkStart);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(chunkStart, 4), (uint)(payloadLength - 4));
        Buffer.BlockCopy(png, idat, result, chunkStart + 4, payloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(chunkStart + 4 + payloadLength, 4),
            PreviewPngIntegrity.Crc32(result.AsSpan(chunkStart + 4, payloadLength)));

        int originalEnd = chunkStart + 12 + payloadLength;
        int newEnd = chunkStart + 8 + payloadLength;
        Buffer.BlockCopy(png, originalEnd, result, newEnd, png.Length - originalEnd);
        return result;
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    internal static byte[] Png(int width, int height, SKColor color)
    {
        using SKBitmap bitmap = new(width, height);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(color);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    private static async Task<(MediaAssetSnapshot Manifest, MediaAssetReference Asset)> ManifestAsync(string directory, byte[] bytes)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "clip.webm"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new
        {
            Version = 1, Entries = new Dictionary<string, object> { ["clip.webm"] = new {
                MediaIdentifier = "video", SourceUrl = "https://offline.invalid/never-request.webm", FileName = "clip.webm",
                Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) } }
        }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        return (manifest, manifest.GetPage().Single());
    }
}
