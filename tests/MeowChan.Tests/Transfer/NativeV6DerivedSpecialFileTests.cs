using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// W32a synthetic manifest-reader fixtures, NOT a journalled importer or publication witness.
// All files belong to TempDirectory. FIFO regressions are contained by a child-vstest watchdog.
public sealed class NativeV6DerivedSpecialFileTests
{
    internal const string ProbeVariable = "MEOWCHAN_V6_DERIVED_FIFO_PROBE";
    private const string ProbeRootVariable = "MEOWCHAN_V6_DERIVED_FIFO_ROOT";
    internal const string DeviceOptInVariable = "MEOWCHAN_TEST_DEVICE_NODES";
    private const long FileCap = 1024L * 1024 * 1024;
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly byte[] Idat = [0x78, 0x9c, 0x63, 0x60, 0, 0, 0, 2, 0, 1];
    private static readonly byte[] Poster = Png(Chunk("IHDR", Header()), Chunk("IDAT", Idat), Chunk("IEND", []));

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ContainedDerivedReadReturnsVerifiedBytesAtZeroWithoutOriginalAssets(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, Poster, poster);
        MediaAssetSnapshot snapshot = await OpenAsync(fixture);
        await AssertBytesAsync(snapshot, fixture.Member, Poster);
        Assert.Empty(snapshot.GetPage()); Assert.Empty(snapshot.AttachmentBindings);
        Assert.Null(MediaAttachmentResolver.Resolve(snapshot, fixture.Evidence.Occurrence.Resource,
            fixture.Evidence.Occurrence.PostId, fixture.Evidence.Metadata));
        await AssertManifestUnchangedAsync(fixture);
        Assert.Equal(2, Directory.GetFileSystemEntries(temp.Path).Length);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DirectoryInContainedDerivedSlotRejectsWithoutOpeningChildren(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, Poster, poster);
        MediaAssetSnapshot snapshot = await OpenAsync(fixture);
        await AssertBytesAsync(snapshot, fixture.Member, Poster); // A valid control before type replacement.
        File.Delete(fixture.PayloadPath);
        Directory.CreateDirectory(fixture.PayloadPath);
        string sentinel = Path.Combine(fixture.PayloadPath, "unrelated.bin");
        await File.WriteAllBytesAsync(sentinel, [17, 19]);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAndDisposeAsync(snapshot, fixture.Member));
        Assert.Contains("regular file", error.Message); // Not a later digest/PNG failure.
        Assert.Equal(new byte[] { 17, 19 }, await File.ReadAllBytesAsync(sentinel));
        await AssertManifestUnchangedAsync(fixture);
    }

    [NativeV6UnixTheory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task SymlinkInContainedDerivedSlotRejectsEvenWithIdenticalTargetBytes(bool poster, bool dangling)
    {
        using TempDirectory temp = new();
        string store = Path.Combine(temp.Path, "store"); Directory.CreateDirectory(store);
        Fixture fixture = await CreateAsync(store, Poster, poster);
        MediaAssetSnapshot snapshot = await OpenAsync(fixture);
        await AssertBytesAsync(snapshot, fixture.Member, Poster);
        string target = Path.Combine(temp.Path, "outside-store.bin");
        if (!dangling) await File.WriteAllBytesAsync(target, Poster);
        File.Delete(fixture.PayloadPath);
        File.CreateSymbolicLink(fixture.PayloadPath, target);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAndDisposeAsync(snapshot, fixture.Member));
        Assert.Equal(target, new FileInfo(fixture.PayloadPath).LinkTarget);
        if (dangling) Assert.False(File.Exists(target));
        else Assert.Equal(Poster, await File.ReadAllBytesAsync(target));
        await AssertManifestUnchangedAsync(fixture);
    }

    [NativeV6UnixTheory]
    [InlineData(false)] [InlineData(true)]
    public Task NoWriterFifoInContainedDerivedSlotRejectsWithinChildWatchdog(bool poster) => RunFifoChildAsync(poster);

    // Not a symlink-to-/dev/null substitute: make and lstat an actual character node.
    // Opt-in is explicit because effective UID 0 alone does not prove CAP_MKNOD in a container.
    [NativeV6DeviceTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActualDeviceInContainedDerivedSlotRejectsAtRegularFileBoundary(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, Poster, poster);
        MediaAssetSnapshot snapshot = await OpenAsync(fixture);
        await AssertBytesAsync(snapshot, fixture.Member, Poster);
        File.Delete(fixture.PayloadPath);
        Assert.True(MkNod(fixture.PayloadPath, 0x2000u | 0x180u, 0x103UL) == 0,
            $"Opted-in runner must support Linux mknod(null,0600); errno={Marshal.GetLastPInvokeError()}.");
        FileStamp before = Stamp(fixture.PayloadPath, 0x2000);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAndDisposeAsync(snapshot, fixture.Member));
        Assert.Contains("regular file", error.Message);
        Assert.Equal(before, Stamp(fixture.PayloadPath, 0x2000));
        await AssertManifestUnchangedAsync(fixture);
    }

    [Theory]
    [InlineData("signature")] [InlineData("crc")] [InlineData("missing-end")]
    [InlineData("trailing")] [InlineData("huge-chunk")]
    [InlineData("zero-width")] [InlineData("no-data")]
    public async Task MatchingDigestCannotMakeInvalidPngAContainedPoster(string fault)
    {
        byte[] invalid = BadPng(fault);
        using TempDirectory temp = new();
        string previewDirectory = Path.Combine(temp.Path, "preview"); Directory.CreateDirectory(previewDirectory);
        string posterDirectory = Path.Combine(temp.Path, "poster"); Directory.CreateDirectory(posterDirectory);
        Fixture preview = await CreateAsync(previewDirectory, invalid, poster: false);
        Fixture poster = await CreateAsync(posterDirectory, invalid, poster: true);
        // Equal actual hashes, bytes and filenames: rejection is the PNG role, not bad integrity.
        Assert.Equal(preview.Member.Blob, poster.Member.Blob);
        await AssertBytesAsync(await OpenAsync(preview), preview.Member, invalid);
        MediaAssetSnapshot snapshot = await OpenAsync(poster);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAndDisposeAsync(snapshot, poster.Member));
        Assert.Equal(invalid, await File.ReadAllBytesAsync(poster.PayloadPath));
        await AssertManifestUnchangedAsync(preview); await AssertManifestUnchangedAsync(poster);
    }

    [Theory]
    [InlineData(false, -1)] [InlineData(false, 1)]
    [InlineData(true, -1)] [InlineData(true, 1)]
    public async Task ChangedDerivedLengthRejectsWithoutRepair(bool poster, int delta)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, Poster, poster);
        MediaAssetSnapshot snapshot = await OpenAsync(fixture);
        await AssertBytesAsync(snapshot, fixture.Member, Poster);
        byte[] changed = delta < 0 ? Poster[..^1] : [.. Poster, 0];
        await File.WriteAllBytesAsync(fixture.PayloadPath, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAndDisposeAsync(snapshot, fixture.Member));
        Assert.Equal(changed, await File.ReadAllBytesAsync(fixture.PayloadPath));
        await AssertManifestUnchangedAsync(fixture);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SameLengthDerivedCorruptionIsNotAStaleVerificationCacheHit(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, Poster, poster);
        MediaAssetSnapshot snapshot = await OpenAsync(fixture);
        await AssertBytesAsync(snapshot, fixture.Member, Poster);
        byte[] damaged = (byte[])Poster.Clone(); damaged[^1] ^= 1;
        await File.WriteAllBytesAsync(fixture.PayloadPath, damaged);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAndDisposeAsync(snapshot, fixture.Member));
        await AssertManifestUnchangedAsync(fixture);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PrecancelledReadPreservesCallerTokenBeforeMissingFileIo(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, Poster, poster);
        MediaAssetSnapshot snapshot = await OpenAsync(fixture);
        File.Delete(fixture.PayloadPath);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAndDisposeAsync(snapshot, fixture.Member, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(File.Exists(fixture.PayloadPath)); await AssertManifestUnchangedAsync(fixture);
        await File.WriteAllBytesAsync(fixture.PayloadPath, Poster);
        await AssertBytesAsync(snapshot, fixture.Member, Poster); // Cancel did not poison the view.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DerivedLengthAboveOneGiBCapRejectsMetadataWithoutLargeAllocation(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, Poster, poster);
        await OpenAsync(fixture);
        JsonNode manifest = JsonNode.Parse(fixture.ManifestBytes)!;
        manifest["DerivedRepresentations"]![0]!["Blob"]!["Actual"]!["Length"] = FileCap + 1;
        byte[] excessive = JsonSerializer.SerializeToUtf8Bytes(manifest);
        await File.WriteAllBytesAsync(fixture.ManifestPath, excessive);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(MediaManifestStatus.Corrupt, snapshot.Status);
        Assert.Empty(snapshot.DerivedRepresentations); Assert.Empty(snapshot.OriginalEvidence);
        Assert.Empty(snapshot.GetPage()); Assert.Empty(snapshot.AttachmentBindings);
        Assert.Equal(excessive, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(Poster, await File.ReadAllBytesAsync(fixture.PayloadPath));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExactlyOneGiBIsAdmittedAsMetadataButStillRequiresMatchingPhysicalLength(bool poster)
    {
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, Poster, poster);
        JsonNode manifest = JsonNode.Parse(fixture.ManifestBytes)!;
        manifest["DerivedRepresentations"]![0]!["Blob"]!["Actual"]!["Length"] = FileCap;
        byte[] boundary = JsonSerializer.SerializeToUtf8Bytes(manifest);
        await File.WriteAllBytesAsync(fixture.ManifestPath, boundary);
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(temp.Path);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        MediaDerivedRepresentation member = Assert.Single(snapshot.DerivedRepresentations);
        Assert.Equal(FileCap, member.Blob!.Actual.Length);
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAndDisposeAsync(snapshot, member));
        Assert.Contains("changed length", error.Message);
        Assert.Equal(boundary, await File.ReadAllBytesAsync(fixture.ManifestPath));
        Assert.Equal(Poster.Length, new FileInfo(fixture.PayloadPath).Length);
    }

    [Theory]
    [InlineData(1)] [InlineData(7)] [InlineData(81920)]
    public async Task LargeAncillaryChunkAndSplitPngTokensStayBoundedAndReadable(int chunkSize)
    {
        byte[] png = Png(Chunk("IHDR", Header()), Chunk("teST", new byte[90000]),
            Chunk("IDAT", Idat), Chunk("IEND", []));
        NativeTransferDerivedPayloadValidator.PngStructure parser = new(png.Length);
        for (int offset = 0; offset < png.Length; offset += chunkSize)
            parser.Append(png.AsSpan(offset, Math.Min(chunkSize, png.Length - offset)), default);
        Assert.True(parser.IsValid);
        using TempDirectory temp = new();
        Fixture fixture = await CreateAsync(temp.Path, png, poster: true);
        await AssertBytesAsync(await OpenAsync(fixture), fixture.Member, png);
        await AssertManifestUnchangedAsync(fixture);
    }

    [Fact]
    public void PngParserHonorsDeclaredLengthAndCancellationBetweenAppends()
    {
        NativeTransferDerivedPayloadValidator.PngStructure tooShort = new(Poster.Length - 1);
        tooShort.Append(Poster, default); Assert.False(tooShort.IsValid);
        NativeTransferDerivedPayloadValidator.PngStructure tooLong = new(Poster.Length + 1);
        tooLong.Append(Poster, default); Assert.False(tooLong.IsValid);
        NativeTransferDerivedPayloadValidator.PngStructure parser = new(Poster.Length);
        parser.Append(Poster.AsSpan(0, 8), default);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        OperationCanceledException error = Assert.Throws<OperationCanceledException>(
            () => parser.Append(Poster.AsSpan(8), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Assert.False(parser.IsValid);
        // No cancelled bytes consumed. This proves the parser boundary, not mid-FileStream cancellation.
        parser.Append(Poster.AsSpan(8), default); Assert.True(parser.IsValid);
    }

    [NativeV6FifoProbeFact]
    public async Task FifoChildProbe()
    {
        bool poster = Environment.GetEnvironmentVariable(ProbeVariable) switch
        { "poster" => true, "preview" => false, _ => throw new InvalidOperationException("Missing FIFO role.") };
        string root = Environment.GetEnvironmentVariable(ProbeRootVariable)
            ?? throw new InvalidOperationException("Missing parent-owned FIFO workspace.");
        Fixture fixture = await CreateAsync(root, Poster, poster);
        MediaAssetSnapshot snapshot = await OpenAsync(fixture);
        await AssertBytesAsync(snapshot, fixture.Member, Poster);
        File.Delete(fixture.PayloadPath);
        Assert.True(MkFifo(fixture.PayloadPath, 0x180) == 0, $"mkfifo errno={Marshal.GetLastPInvokeError()}.");
        FileStamp before = Stamp(fixture.PayloadPath, 0x1000);
        // No writer or rescue handle exists. Parent kills the complete child tree on a blocking open.
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => ReadAndDisposeAsync(snapshot, fixture.Member));
        Assert.Contains("regular file", error.Message);
        Assert.Equal(before, Stamp(fixture.PayloadPath, 0x1000));
        await AssertManifestUnchangedAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(root, "completed"), "derived-fifo-rejected");
    }

    private static async Task RunFifoChildAsync(bool poster)
    {
        using TempDirectory temp = new();
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("vstest"); // Existing built assembly only, never restore/build.
        start.ArgumentList.Add(typeof(NativeV6DerivedSpecialFileTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=MeowChan.Tests.Transfer.NativeV6DerivedSpecialFileTests.FifoChildProbe");
        start.Environment[ProbeVariable] = poster ? "poster" : "preview";
        start.Environment[ProbeRootVariable] = temp.Path;
        using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start FIFO probe.");
        Task<string> output = child.StandardOutput.ReadToEndAsync(), errors = child.StandardError.ReadToEndAsync();
        TimeSpan reap = TimeSpan.FromSeconds(10);
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync().WaitAsync(reap);
                Assert.Fail($"Derived FIFO probe exceeded 30s; process tree killed.\n{await output.WaitAsync(reap)}\n{await errors.WaitAsync(reap)}");
            }
            Assert.True(child.ExitCode == 0, $"Derived FIFO child failed.\n{await output.WaitAsync(reap)}\n{await errors.WaitAsync(reap)}");
            Assert.Equal("derived-fifo-rejected", await File.ReadAllTextAsync(Path.Combine(temp.Path, "completed")));
        }
        finally
        {
            if (!child.HasExited)
            { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(reap); }
        }
    }

    private sealed record Fixture(string Directory, string ManifestPath, byte[] ManifestBytes,
        string PayloadPath, MediaOriginalEvidence Evidence, MediaDerivedRepresentation Member);

    private static async Task<Fixture> CreateAsync(string directory, byte[] payload, bool poster)
    {
        ResourceId resource = new("4chan", "b", "7001");
        byte[] original = Encoding.UTF8.GetBytes("passive source-version claim; no original stored or video decoded");
        MediaActualIntegrity version = Integrity(original) with { Sha1 = null };
        MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.webm", "original.webm",
            original.Length, version.Md5, version.Sha256);
        string key = MediaRevisionKeys.CreateAttachmentKey(resource, "15", 0, provider);
        CapturedAttachment metadata = new(0, provider.MediaId, provider.Variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        { AttachmentRevisionKey = key, ExpectedSha256 = provider.ExpectedSha256 };
        MediaOccurrence occurrence = new(resource, "15", 0, key);
        MediaPassiveObservation origin = new("08a24f80-f100-43f0-a296-3ac377754304", new string('a', 64),
            new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));
        MediaOriginalEvidence evidence = new(occurrence, metadata, MediaObservedContentStatus.Omitted, version, origin);
        MediaActualIntegrity actual = Integrity(payload);
        MediaDerivedRepresentation member = new(occurrence, version, poster ? MediaDerivedKind.VideoPosterPng : MediaDerivedKind.Preview,
            MediaDerivedStatus.Contained, new(actual.Sha256 + ".derived.bin", actual), origin);
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 6, Entries = new Dictionary<string, object>(), Assets = new Dictionary<string, object>(),
            AttachmentBindings = new Dictionary<string, object>(), OriginalEvidence = new[] { evidence },
            DerivedRepresentations = new[] { member }
        });
        string manifestPath = Path.Combine(directory, ".meowchan-manifest.json");
        string payloadPath = Path.Combine(directory, member.Blob!.RelativeFileName);
        await File.WriteAllBytesAsync(manifestPath, manifest);
        await File.WriteAllBytesAsync(payloadPath, payload);
        return new(directory, manifestPath, manifest, payloadPath, evidence, member);
    }

    private static async Task<MediaAssetSnapshot> OpenAsync(Fixture fixture)
    {
        MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(fixture.Directory);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status); Assert.Equal(6, snapshot.ManifestVersion);
        Assert.Equal(fixture.Member, Assert.Single(snapshot.DerivedRepresentations));
        Assert.Equal(fixture.Evidence, Assert.Single(snapshot.OriginalEvidence));
        Assert.Equal(0, snapshot.Count);
        return snapshot;
    }

    private static async Task AssertBytesAsync(MediaAssetSnapshot snapshot, MediaDerivedRepresentation member, byte[] bytes)
    {
        await using Stream verified = await snapshot.OpenVerifiedDerivedReadAsync(member);
        Assert.Equal(0, verified.Position); Assert.Equal(bytes.LongLength, verified.Length);
        using MemoryStream copy = new(); await verified.CopyToAsync(copy); Assert.Equal(bytes, copy.ToArray());
    }

    private static async Task ReadAndDisposeAsync(MediaAssetSnapshot snapshot, MediaDerivedRepresentation member, CancellationToken ct = default)
    { await using Stream unexpected = await snapshot.OpenVerifiedDerivedReadAsync(member, ct); }

    private static async Task AssertManifestUnchangedAsync(Fixture fixture) =>
        Assert.Equal(fixture.ManifestBytes, await File.ReadAllBytesAsync(fixture.ManifestPath));

    private static MediaActualIntegrity Integrity(byte[] bytes) => new(bytes.Length, Convert.ToBase64String(MD5.HashData(bytes)),
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()) { Sha1 = Convert.ToBase64String(SHA1.HashData(bytes)) };

    private static byte[] Header(uint width = 1)
    {
        byte[] bytes = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(bytes, width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 1); bytes[8] = 8; return bytes;
    }
    private static byte[] Png(params byte[][] chunks) => [.. Signature, .. chunks.SelectMany(c => c)];
    private static byte[] Chunk(string type, byte[] data)
    {
        byte[] bytes = new byte[data.Length + 12]; BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)data.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4); data.CopyTo(bytes, 8);
        uint crc = uint.MaxValue; // Independent bitwise CRC, not the product parser's implementation.
        foreach (byte value in bytes.AsSpan(4, data.Length + 4))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u;
        }
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), ~crc); return bytes;
    }
    private static byte[] BadPng(string fault)
    {
        byte[] header = Chunk("IHDR", Header()), data = Chunk("IDAT", Idat), end = Chunk("IEND", []);
        switch (fault)
        {
            case "signature": return [0, .. Poster[1..]];
            case "crc": data[^1] ^= 1; return Png(header, data, end);
            case "missing-end": return Png(header, data);
            case "trailing": return [.. Poster, 0];
            case "huge-chunk": BinaryPrimitives.WriteUInt32BigEndian(data, uint.MaxValue); return Png(header, data, end);
            case "zero-width": return Png(Chunk("IHDR", Header(0)), data, end);
            case "no-data": return Png(header, end);
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
    }

    private static FileStamp Stamp(string path, int type)
    {
        Assert.Equal(0, LStat(path, out FileStatus status)); Assert.Equal(type, status.Mode & 0xf000);
        return new(status.Dev, status.Ino, status.Mode, status.Size, status.ATime, status.ATimeNsec,
            status.MTime, status.MTimeNsec, status.CTime, status.CTimeNsec);
    }
    private sealed record FileStamp(long Device, long Inode, int Mode, long Size, long Access, long AccessNanos,
        long Modified, long ModifiedNanos, long Changed, long ChangedNanos);
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    // Linux-only dev_t encoding for /dev/null (major1/minor3), gated by the device theory attribute.
    [DllImport("libc", EntryPoint = "mknod", SetLastError = true)]
    private static extern int MkNod([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode, ulong device);
    // Existing .NET10 normalized PAL layout, copied from NativePrivateMetadataSpecialFileTests.
    [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus status);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileStatus
    {
        internal int Flags, Mode;
        internal uint Uid, Gid;
        internal long Size, ATime, ATimeNsec, MTime, MTimeNsec, CTime, CTimeNsec;
        internal long BirthTime, BirthTimeNsec, Dev, RDev, Ino;
        internal uint UserFlags;
    }
}

public sealed class NativeV6UnixTheoryAttribute : TheoryAttribute
{
    public NativeV6UnixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "This filesystem fixture needs Linux/macOS; Windows symlink privileges are not assumed.";
    }
}
public sealed class NativeV6DeviceTheoryAttribute : TheoryAttribute
{
    public NativeV6DeviceTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable(NativeV6DerivedSpecialFileTests.DeviceOptInVariable) != "1")
            Skip = "Actual device-node test requires Linux and explicit MEOWCHAN_TEST_DEVICE_NODES=1 on a CAP_MKNOD-capable runner.";
    }
}
public sealed class NativeV6FifoProbeFactAttribute : FactAttribute
{
    public NativeV6FifoProbeFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "FIFO child needs Linux/macOS.";
        else if (Environment.GetEnvironmentVariable(NativeV6DerivedSpecialFileTests.ProbeVariable) == null)
            Skip = "Watchdog child entry point; covered by the two parent FIFO theories, not standalone.";
    }
}
