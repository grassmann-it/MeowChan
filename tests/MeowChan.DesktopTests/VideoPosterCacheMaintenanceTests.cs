using System.Buffers.Binary;
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
