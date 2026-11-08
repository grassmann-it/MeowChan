using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Real synthetic package -> owned stage -> semantic plan -> inactive Journal2 candidate.
// No profile activation, SQL, network connections, personal directories, or fixture files.
public sealed class NativeV4CandidateFileBoundaryTests
{
    internal const string FifoProbeVariable = "MEOWCHAN_V4_CANDIDATE_FIFO_PROBE";
    private const string FifoRootVariable = "MEOWCHAN_V4_CANDIDATE_FIFO_ROOT";
    private const string JournalName = ".meowchan-import-candidate.json";
    private static readonly Guid Operation = Guid.Parse("32b232b2-aca2-4a42-b222-000000000002");
    private static readonly DateTimeOffset At = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);
    private const string Marker = "w32b-scalar-placeholder";
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("synthetic boundary original bytes");
    private static readonly byte[] Preview = Encoding.UTF8.GetBytes("synthetic boundary preview bytes");

    [Fact]
    public async Task MissingV2PayloadIsInvalidAndInspectionDoesNotRepair()
    {
        await using var fixture = await Fixture.CreateAsync();
        string directory = await fixture.CreateCandidateAsync();
        string payload = Assert.Single(Directory.GetFiles(directory, "*.original.bin"));
        File.Delete(payload);

        await AssertInvalidUnchangedAsync(directory);
    }

    [Fact]
    public async Task DirectoryInV2PayloadSlotIsInvalidWithoutOpeningChildren()
    {
        await using var fixture = await Fixture.CreateAsync();
        string directory = await fixture.CreateCandidateAsync();
        string payload = Assert.Single(Directory.GetFiles(directory, "*.original.bin"));
        File.Delete(payload);
        Directory.CreateDirectory(payload);
        await File.WriteAllBytesAsync(Path.Combine(payload, "sentinel.bin"), [17, 19]);

        await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Invalid);
        Assert.Equal(new byte[] { 17, 19 }, await File.ReadAllBytesAsync(Path.Combine(payload, "sentinel.bin")));
        Assert.True(Directory.Exists(payload));
        await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Invalid);
    }

    [NativeV4CandidateUnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SymlinkInV2PayloadSlotIsInvalidEvenWhenTargetBytesMatch(bool dangling)
    {
        await using var fixture = await Fixture.CreateAsync();
        string directory = await fixture.CreateCandidateAsync();
        string payload = Assert.Single(Directory.GetFiles(directory, "*.original.bin"));
        byte[] bytes = await File.ReadAllBytesAsync(payload);
        string target = Path.Combine(fixture.Root, "outside.bin");
        if (!dangling) await File.WriteAllBytesAsync(target, bytes);
        File.Delete(payload);
        File.CreateSymbolicLink(payload, target);

        var before = Files.Capture(directory, payload);
        await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Invalid);
        before.AssertUnchanged(directory, payload);
        Assert.Equal(target, new FileInfo(payload).LinkTarget);
        if (dangling) Assert.False(File.Exists(target));
        else Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
    }

    [NativeV4CandidateUnixTheory]
    [InlineData("*.original.bin")]
    [InlineData("*.derived.bin")]
    public async Task UnixSocketInV2PayloadSlotIsInvalidWithoutRepair(string pattern)
    {
        await using var fixture = await Fixture.CreateAsync();
        string directory = await fixture.CreateCandidateAsync();
        string payload = Assert.Single(Directory.GetFiles(directory, pattern));
        // A socket node fails an ordinary file open immediately, even if the type
        // guard regresses. No FIFO, Listen/Accept/Connect, or socket reads here.
        // Bind under a short name before renaming into the hash-named payload slot.
        string socketPath = Path.Combine(fixture.Root, "s");
        using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.Delete(payload);
        File.Move(socketPath, payload);
        var before = Files.Capture(directory, payload);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Invalid);
                before.AssertUnchanged(directory, payload);
                Assert.Null(new FileInfo(payload).LinkTarget);
                // This open is nonblocking on a Unix socket (never use on a FIFO).
                Assert.ThrowsAny<IOException>(() => { using var file = File.OpenRead(payload); });
            }
        }
        finally { File.Delete(payload); }
    }

    [NativeV4CandidateUnixTheory]
    [InlineData("original")]
    [InlineData("derived")]
    [InlineData("temporary")]
    public async Task NoWriterFifoRejectsInSeparateProcessWithParentWatchdog(string slot)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("w32b-fifo-");
        ProcessStartInfo start = new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(NativeV4CandidateFileBoundaryTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName=MeowChan.Tests.Transfer.NativeV4CandidateFileBoundaryTests.FifoChildProbe");
        start.Environment[FifoProbeVariable] = slot;
        start.Environment[FifoRootVariable] = root;
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
                Assert.Fail($"Journal2 FIFO child exceeded 30s; process tree killed.\n{await output.WaitAsync(reap)}\n{await errors.WaitAsync(reap)}");
            }
            Assert.True(child.ExitCode == 0, $"Journal2 FIFO probe failed.\n{await output.WaitAsync(reap)}\n{await errors.WaitAsync(reap)}");
            Assert.Equal(slot, await File.ReadAllTextAsync(Path.Combine(root, "completed")));
        }
        finally
        {
            if (!child.HasExited)
            { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(reap); }
            Directory.Delete(root, recursive: true);
        }
    }

    [NativeV4CandidateFifoProbeFact]
    public async Task FifoChildProbe()
    {
        string root = Environment.GetEnvironmentVariable(FifoRootVariable)
            ?? throw new InvalidOperationException("Missing parent-owned FIFO workspace.");
        string slot = Environment.GetEnvironmentVariable(FifoProbeVariable)
            ?? throw new InvalidOperationException("Missing FIFO slot.");
        await using var fixture = await Fixture.CreateAsync(ownedRoot: Path.Combine(root, "fixture"));
        string directory = await fixture.CreateCandidateAsync(copying: slot == "temporary");
        string pattern = slot switch
        {
            "original" => "*.original.bin", "derived" => "*.derived.bin",
            "temporary" => ".meowchan-payload-*.part", _ => throw new InvalidOperationException("Invalid FIFO slot.")
        };
        string path = Assert.Single(Directory.GetFiles(directory, pattern));
        File.Delete(path);
        Assert.True(MkFifo(path, 0x180) == 0, $"mkfifo errno={Marshal.GetLastPInvokeError()}.");
        var before = Files.Capture(directory, path);
        long[] stamp = FifoStamp(path);
        // No writer/rescue handle. If any inspected path opens this FIFO, the outer
        // parent kills the whole vstest/testhost tree, not merely an await timeout.
        await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Invalid);
        before.AssertUnchanged(directory, path);
        Assert.Equal(stamp, FifoStamp(path));
        await File.WriteAllTextAsync(Path.Combine(root, "completed"), slot);
    }

    private static long[] FifoStamp(string path)
    {
        Assert.Equal(0, LStat(path, out FileStatus status));
        Assert.Equal(0x1000, status.Mode & 0xf000);
        return [status.Dev, status.Ino, status.Mode, status.Size, status.ATime, status.ATimeNsec,
            status.MTime, status.MTimeNsec, status.CTime, status.CTimeNsec];
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
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

    [Theory]
    [InlineData("80")]
    [InlineData("C2")]
    [InlineData("E282")]
    [InlineData("F09F92")]
    public async Task MalformedUtf8InOuterJournal2CannotNormalizeToValidResource(string hex)
    {
        await using var fixture = await Fixture.CreateAsync("b\uFFFD");
        string directory = await fixture.CreateCandidateAsync();
        string path = Path.Combine(directory, JournalName);
        JsonObject journal = JsonNode.Parse(await File.ReadAllBytesAsync(path))!.AsObject();
        journal["Resource"]!["Board"] = Marker;
        string valid = ReplaceOnce(journal.ToJsonString(), JsonSerializer.Serialize(Marker), "\"b\uFFFD\"");
        await File.WriteAllTextAsync(path, valid, new UTF8Encoding(false, true));
        await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Ready);

        // All hashes and canonical selection remain valid if a decoder repairs this
        // one outer Resource scalar to U+FFFD. This is not ordinary digest tampering.
        byte[] bytes = Encoding.UTF8.GetBytes(valid);
        byte[] scalar = Encoding.UTF8.GetBytes("\uFFFD");
        int index = bytes.AsSpan().IndexOf(scalar);
        Assert.True(index >= 0);
        Assert.Equal(-1, bytes.AsSpan(index + scalar.Length).IndexOf(scalar));
        byte[] malformed = [.. bytes.AsSpan(0, index), .. Convert.FromHexString(hex),
            .. bytes.AsSpan(index + scalar.Length)];
        Assert.Equal(valid, Encoding.UTF8.GetString(malformed));
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(malformed));
        await File.WriteAllBytesAsync(path, malformed);
        await AssertInvalidUnchangedAsync(directory);
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uDBFF")]
    [InlineData("\\uDFFF")]
    public async Task EscapedLoneSurrogateInOuterJournal2CannotNormalizeToValidResource(string escape)
    {
        await using var fixture = await Fixture.CreateAsync("b\uFFFD");
        string directory = await fixture.CreateCandidateAsync();
        string path = Path.Combine(directory, JournalName);
        JsonObject journal = JsonNode.Parse(await File.ReadAllBytesAsync(path))!.AsObject();
        journal["Resource"]!["Board"] = Marker;
        string template = journal.ToJsonString();
        await File.WriteAllTextAsync(path, ReplaceOnce(template, JsonSerializer.Serialize(Marker), "\"b\\uFFFD\""));
        await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Ready);
        // Raw JSON escape injection: never serialize a lone .NET surrogate, which
        // could silently replace it before the inspector sees the wire bytes.
        await File.WriteAllTextAsync(path, ReplaceOnce(template, JsonSerializer.Serialize(Marker), "\"b" + escape + "\""));
        await AssertInvalidUnchangedAsync(directory);
    }

    [Theory]
    [InlineData("b\uFFFD", "b\\uFFFD")]
    [InlineData("b\U0001F63A", "b\\uD83D\\uDE3A")]
    public async Task ValidUnicodeScalarAndPairedSurrogateRemainReady(string board, string escaped)
    {
        await using var fixture = await Fixture.CreateAsync(board);
        string directory = await fixture.CreateCandidateAsync();
        string path = Path.Combine(directory, JournalName);
        JsonObject journal = JsonNode.Parse(await File.ReadAllBytesAsync(path))!.AsObject();
        journal["Resource"]!["Board"] = Marker;
        await File.WriteAllTextAsync(path, ReplaceOnce(journal.ToJsonString(), JsonSerializer.Serialize(Marker), "\"" + escaped + "\""));
        var before = Files.Capture(directory);
        await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Ready);
        before.AssertUnchanged(directory);
    }

    private static string ReplaceOnce(string source, string old, string replacement)
    {
        int index = source.IndexOf(old, StringComparison.Ordinal);
        Assert.True(index >= 0, "Expected wire token was not emitted.");
        Assert.Equal(-1, source.IndexOf(old, index + old.Length, StringComparison.Ordinal));
        return source[..index] + replacement + source[(index + old.Length)..];
    }

    private static async Task AssertInvalidUnchangedAsync(string directory)
    {
        var before = Files.Capture(directory);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await AssertInspectionAsync(directory, PassiveMediaCandidateStatus.Invalid);
            before.AssertUnchanged(directory);
        }
    }

    private static async Task AssertInspectionAsync(string directory, PassiveMediaCandidateStatus expected)
    {
        // This timeout only bounds an asynchronous lease wait. FIFO probes run in
        // a separate process under the parent watchdog; this is not open containment.
        PassiveMediaCandidateInspection inspection = await new MediaStore(
            File.Move, directoryLeaseTimeout: TimeSpan.FromMilliseconds(100))
            .InspectPassiveImportCandidateAsync(directory)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, inspection.Status);
        if (expected == PassiveMediaCandidateStatus.Ready)
        {
            Assert.NotNull(inspection.Candidate);
            Assert.Equal(2, inspection.Candidate.PlanVersion);
        }
        else Assert.Null(inspection.Candidate);
    }

    private sealed record Files(string[] Names, Dictionary<string, byte[]> Bytes)
    {
        internal static Files Capture(string directory, string? excluded = null)
        {
            string[] names = Directory.GetFileSystemEntries(directory).Order(StringComparer.Ordinal).ToArray();
            return new(names, names.Where(path => path != excluded)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal));
        }

        internal void AssertUnchanged(string directory, string? excluded = null)
        {
            Files actual = Capture(directory, excluded);
            Assert.Equal(Names, actual.Names);
            foreach (KeyValuePair<string, byte[]> pair in Bytes)
                Assert.Equal(pair.Value, actual.Bytes[pair.Key]);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; }
        private string Parent { get; }
        private NativeTransferImportStage Stage { get; }
        private NativeMediaMaterializationPlan Plan { get; }

        private Fixture(string root, NativeTransferImportStage stage, ResourceId resource)
        {
            Root = root;
            Parent = Directory.CreateDirectory(Path.Combine(root, "c")).FullName;
            Stage = stage;
            Plan = NativeMediaMaterializationPlan.Create(stage, resource);
        }

        internal async Task<string> CreateCandidateAsync(bool copying = false)
        {
            if (copying)
            {
                PassiveMediaCandidateWriteException error = await Assert.ThrowsAsync<PassiveMediaCandidateWriteException>(() =>
                    new MediaStore().MaterializePassiveImportCandidateAsync(Plan, Plan.FullSelection, Parent, Operation, At,
                        checkpoint: (phase, _) =>
                        {
                            if (phase == PassiveMediaCandidatePhase.PayloadFlushed)
                                throw new IOException("Synthetic stop after real partial-file flush.");
                        }));
                await AssertInspectionAsync(error.CandidateDirectory, PassiveMediaCandidateStatus.Incomplete);
                return error.CandidateDirectory;
            }
            PassiveMediaImportCandidate candidate = await new MediaStore()
                .MaterializePassiveImportCandidateAsync(Plan, Plan.FullSelection, Parent, Operation, At);
            Assert.Equal(2, candidate.PlanVersion);
            using JsonDocument journal = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(candidate.Directory, JournalName)));
            Assert.Equal(2, journal.RootElement.GetProperty("Version").GetInt32());
            await AssertInspectionAsync(candidate.Directory, PassiveMediaCandidateStatus.Ready);
            return candidate.Directory;
        }

        internal static async Task<Fixture> CreateAsync(string board = "b", string? ownedRoot = null)
        {
            string root = ownedRoot == null ? TemporaryWorkspacePaths.CreateDirectory("w32b-")
                : Directory.CreateDirectory(ownedRoot).FullName;
            NativeTransferImportStage? stage = null;
            try
            {
                ResourceId resource = new("stub", board, "404");
                TransferOriginalByteVersion version = new(Original.Length, Hash(Original), Md5(Original), null);
                MediaProviderMetadata provider = new("111", "original", "https://i.4cdn.org/b/111.webm",
                    "boundary.bin", Original.Length, Md5(Original), Hash(Original));
                string key = MediaRevisionKeys.CreateAttachmentKey(resource, "404", 0, provider);
                CapturedAttachment metadata = new(0, provider.MediaId, "original", provider.Url,
                    provider.OriginalFileName, ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
                { AttachmentRevisionKey = key, ExpectedSha256 = Hash(Original) };
                TransferAttachment attachment = new(metadata, TransferMediaRole.Original,
                    TransferContentStatus.Contained, Hash(Original), null)
                {
                    OriginalByteVersion = version,
                    DerivedRepresentations = [new(TransferDerivedRepresentationKind.Preview, key, version,
                        TransferContentStatus.Contained, Hash(Preview))]
                };
                TransferPost post = new(new CapturedPost("404", 0, PostFieldPresence.All,
                    Html: "<p>synthetic boundary fixture</p>", Attachments: [metadata]), [attachment]);
                NativeTransferCatalog catalog = new(At, [new TransferThread(resource,
                    null, "synthetic boundary", ThreadSourceState.NotFound,
                    At, null, 0, false, [post])]);
                using MemoryStream package = new();
                byte[][] payloads = [Original, Preview];
                TransferPayloadSource[] sources = payloads.Select(bytes => new TransferPayloadSource(
                    new(bytes.Length, Hash(bytes)), _ => Task.FromResult<Stream>(
                        new MemoryStream(bytes, writable: false)))).ToArray();
                TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(
                    package, catalog, sources, Operation);
                Assert.True(written.IsComplete, written.ToString());
                Assert.True(written.Package!.IsChanV4);
                package.Position = 0;
                string staging = Directory.CreateDirectory(Path.Combine(root, "stage")).FullName;
                NativeTransferStageResult staged = await NativeTransferImportStage.CreateAsync(package, staging);
                Assert.Equal(NativeTransferStageError.None, staged.Error);
                stage = Assert.IsType<NativeTransferImportStage>(staged.Stage);
                return new Fixture(root, stage, resource);
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
            try { await Stage.DisposeAsync(); }
            finally { Directory.Delete(Root, recursive: true); }
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToBase64String(MD5.HashData(bytes));
}

public sealed class NativeV4CandidateUnixTheoryAttribute : TheoryAttribute
{
    public NativeV4CandidateUnixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Synthetic Unix symlink/socket fixtures require Linux or macOS; Windows link privileges are not assumed.";
    }
}

public sealed class NativeV4CandidateFifoProbeFactAttribute : FactAttribute
{
    public NativeV4CandidateFifoProbeFactAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) Skip = "FIFO child requires Linux/macOS.";
        else if (Environment.GetEnvironmentVariable(NativeV4CandidateFileBoundaryTests.FifoProbeVariable) == null)
            Skip = "Watchdog child entry point; exercised by the three parent FIFO theories.";
    }
}
