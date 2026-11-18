using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Export archive through the real host: counting, freeze/publish, progress, cancellation, the create-only refusal,
// the fixed folder ceiling, and a roundtrip read back by the import preview. Disposable temp profiles only.
[Collection("Delivered plugin hosts")]
public sealed class ArchiveTransferExportHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);
    private static readonly DateTimeOffset At = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WholeArchiveExportReportsPhasesAndTheImportPreviewReadsThePackageBack()
    {
        using Workspace files = new();
        ResourceId first = new("4chan", "b", "8101"), second = new("4chan", "b", "8102");
        await files.SeedAsync((first, 3), (second, 2));
        await using DesktopSessionHost host = await files.StartAsync();

        ArchiveTransferExportCount count = await host.CountArchiveTransferExportAsync(
            new(null, ArchiveTransferExportMedia.Everything)).WaitAsync(Deadline);
        Assert.Equal(2, count.Threads);
        Assert.Equal(5, count.Posts);
        Assert.Equal(2, count.MediaDirectories);
        Assert.True(count.WithinLimits);
        Assert.Equal(0, count.EstimatedMediaBytes); // no manifest yet: nothing to carry, not an unknown size

        List<ArchiveTransferExportProgress> reports = new();
        string target = Path.Combine(files.Output, "meowchan-archive-2026-11-15.zip");
        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(new(null, ArchiveTransferExportMedia.Everything),
            target, new Collect(reports)).WaitAsync(Deadline);

        Assert.True(result.Succeeded, result.Error.ToString());
        Assert.Equal(target, result.Destination);
        Assert.True(File.Exists(target));
        Assert.Equal(new FileInfo(target).Length, result.BytesWritten);
        Assert.False(result.StageRetained);
        Assert.Empty(Pending(files.Output));
        ArchiveTransferExportPhase[] phases = reports.Select(report => report.Phase).Distinct().ToArray();
        Assert.Equal([ArchiveTransferExportPhase.Counting, ArchiveTransferExportPhase.Reading, ArchiveTransferExportPhase.Writing], phases);

        // The import side's own reader, in its own work folder: the package is a valid chan transfer with both threads.
        await using NativeTransferPreviewSession preview = new(files.Work);
        NativeTransferPreviewSnapshot read = await preview.InspectAsync(target).WaitAsync(Deadline);
        Assert.Equal(NativeTransferPreviewState.Inventory, read.State);
        Assert.Equal(NativeTransferPreviewError.None, read.Error);
        Assert.NotNull(read.Inventory);
        Assert.Equal(new HashSet<ResourceId> { first, second }, read.Inventory!.Threads.Select(thread => thread.Id).ToHashSet());
        Assert.Equal(5, read.Inventory.Posts);
        Assert.Equal(0, read.Inventory.UniquePayloads);
    }

    [Fact]
    public async Task SelectedThreadsMetadataOnlyExportsExactlyTheSelection()
    {
        using Workspace files = new();
        ResourceId first = new("4chan", "b", "8201"), second = new("4chan", "b", "8202");
        await files.SeedAsync((first, 1), (second, 4));
        await using DesktopSessionHost host = await files.StartAsync();
        string target = Path.Combine(files.Output, "selected.zip");

        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new([second], ArchiveTransferExportMedia.MetadataOnly), target).WaitAsync(Deadline);

        Assert.True(result.Succeeded, result.Error.ToString());
        Assert.Equal(1, result.Count!.Threads);
        Assert.Equal(4, result.Count.Posts);
        await using NativeTransferPreviewSession preview = new(files.Work);
        NativeTransferPreviewSnapshot read = await preview.InspectAsync(target).WaitAsync(Deadline);
        Assert.Equal(NativeTransferPreviewState.Inventory, read.State);
        Assert.Equal(second, Assert.Single(read.Inventory!.Threads).Id);
        Assert.Equal(4, read.Inventory.Posts);
        Assert.Equal(4, read.Inventory.CurrentOriginals.Omitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExistingFileOrFolderIsRefusedAndKept(bool folder)
    {
        using Workspace files = new();
        await files.SeedAsync((new("4chan", "b", "8301"), 1));
        await using DesktopSessionHost host = await files.StartAsync();
        string target = Path.Combine(files.Output, "taken.zip");
        if (folder) Directory.CreateDirectory(target);
        else await File.WriteAllTextAsync(target, "KEEP");
        int checkpoints = 0;
        host.ArchiveTransferExportCheckpointForTests = (_, _) => { Interlocked.Increment(ref checkpoints); return Task.CompletedTask; };

        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly), target).WaitAsync(Deadline);

        Assert.Equal(ArchiveTransferExportError.DestinationExists, result.Error);
        Assert.Equal(0, checkpoints); // refused before the archive was read
        if (folder) Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        else Assert.Equal("KEEP", await File.ReadAllTextAsync(target));
        Assert.Empty(Pending(files.Output));

        ArchiveTransferExportResult relative = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly), "relative.zip").WaitAsync(Deadline);
        Assert.Equal(ArchiveTransferExportError.InvalidDestination, relative.Error);
        ArchiveTransferExportResult missingParent = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly), Path.Combine(files.Output, "absent", "x.zip")).WaitAsync(Deadline);
        Assert.Equal(ArchiveTransferExportError.InvalidDestination, missingParent.Error);
        Assert.False(Directory.Exists(Path.Combine(files.Output, "absent")));
    }

    [Theory]
    [InlineData("Reading")]
    [InlineData("Writing")]
    public async Task CancellationInEitherPhaseCreatesNoFileAndFreesTheHost(string phaseName)
    {
        ArchiveTransferExportPhase phase = Enum.Parse<ArchiveTransferExportPhase>(phaseName);
        using Workspace files = new();
        await files.SeedAsync((new("4chan", "b", "8401"), 2));
        await using DesktopSessionHost host = await files.StartAsync();
        using CancellationTokenSource cancel = new();
        host.ArchiveTransferExportCheckpointForTests = (reached, _) =>
        {
            if (reached == phase) cancel.Cancel();
            return Task.CompletedTask;
        };
        string target = Path.Combine(files.Output, "cancelled.zip");

        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.Everything), target, null, cancel.Token).WaitAsync(Deadline);

        Assert.Equal(ArchiveTransferExportError.Canceled, result.Error);
        Assert.False(File.Exists(target));
        Assert.Empty(Pending(files.Output));
        // The admission was released: the next export with a fresh token runs to completion.
        host.ArchiveTransferExportCheckpointForTests = null;
        ArchiveTransferExportResult again = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.Everything), target).WaitAsync(Deadline);
        Assert.True(again.Succeeded, again.Error.ToString());
    }

    // 1.1.0 raised the selection-wide media-folder ceiling from 32 to the thread ceiling: 40 threads, each with its own
    // folder, are one ordinary package, and the import preview reads it back.
    [Fact]
    public async Task FortyThreadsWithTheirOwnMediaFoldersFitOnePackageAndRoundTrip()
    {
        using Workspace files = new();
        ResourceId[] ids = Threads(9000, 40);
        await files.SeedAsync(ids.Select(id => (id, 2)).ToArray());
        await using DesktopSessionHost host = await files.StartAsync();

        ArchiveTransferExportCount count = await host.CountArchiveTransferExportAsync(
            new(null, ArchiveTransferExportMedia.Everything)).WaitAsync(Deadline);
        Assert.Equal(40, count.MediaDirectories);
        Assert.True(count.WithinLimits);
        Assert.Equal(1000, ArchiveTransferExportCount.MaxMediaDirectories);
        string target = Path.Combine(files.Output, "forty.zip");
        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.Everything), target).WaitAsync(Deadline);

        Assert.True(result.Succeeded, result.Error.ToString());
        Assert.Equal(target, Assert.Single(result.Parts).Path);
        await using NativeTransferPreviewSession preview = new(files.Work);
        NativeTransferPreviewSnapshot read = await preview.InspectAsync(target).WaitAsync(Deadline);
        Assert.Equal(NativeTransferPreviewState.Inventory, read.State);
        Assert.Equal(ids.ToHashSet(), read.Inventory!.Threads.Select(thread => thread.Id).ToHashSet());
        Assert.Equal(80, read.Inventory.Posts);
    }

    // Full export with a lowered packing budget (16 threads per package): numbered packages, each a complete package that
    // the import preview accepts on its own, together exactly the archive.
    [Fact]
    public async Task FullExportSplitsAtTheBudgetAndEveryPackageRoundTrips()
    {
        using Workspace files = new();
        ResourceId[] ids = Threads(9100, 40);
        await files.SeedAsync(ids.Select(id => (id, 1)).ToArray());
        await using DesktopSessionHost host = await files.StartAsync();
        host.ArchiveTransferPackageBudgetForTests = ArchiveTransferPackageBudget.Default with { Threads = 16 };
        List<ArchiveTransferExportProgress> reports = new();
        string target = Path.Combine(files.Output, "full.zip");

        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.Everything, Split: true), target, new Collect(reports)).WaitAsync(Deadline);

        Assert.True(result.Succeeded, result.Error.ToString());
        Assert.Equal([16, 16, 8], result.Parts.Select(part => part.Threads));
        Assert.Equal(new[] { 1, 2, 3 }.Select(part => Path.Combine(files.Output, $"full-part-0{part}.zip")), result.Parts.Select(part => part.Path));
        Assert.False(File.Exists(target)); // several packages: only numbered names
        Assert.Empty(result.WithoutMedia); Assert.Empty(result.Skipped);
        Assert.Contains(reports, report => report.Phase == ArchiveTransferExportPhase.Writing && report is { Package: 3, Packages: 3 });
        HashSet<ResourceId> seen = new();
        foreach (ArchiveTransferExportPart part in result.Parts)
        {
            Assert.Equal(new FileInfo(part.Path).Length, part.Bytes);
            await using NativeTransferPreviewSession preview = new(files.Work);
            NativeTransferPreviewSnapshot read = await preview.InspectAsync(part.Path).WaitAsync(Deadline);
            Assert.Equal(NativeTransferPreviewState.Inventory, read.State);
            Assert.Equal(part.Threads, read.Inventory!.Threads.Length);
            foreach (var thread in read.Inventory.Threads) Assert.True(seen.Add(thread.Id));
        }
        Assert.Equal(ids.ToHashSet(), seen);
        Assert.Empty(Pending(files.Output));
    }

    // A limit the plan cannot see in advance (here the stored-text budget, lowered so two threads exceed it): the refused
    // package is halved until each part fits. Without Full export the same selection is refused as a whole.
    [Fact]
    public async Task FullExportHalvesAPackageTheEngineRefusesUntilEveryPartFits()
    {
        using Workspace files = new();
        ResourceId[] ids = Threads(9200, 5);
        await files.SeedAsync(ids.Select(id => (id, 1)).ToArray());
        long single;
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile).WaitAsync(Deadline))
            single = await SmallestTextBudgetAsync(reader, ids[0]);
        await using DesktopSessionHost host = await files.StartAsync();
        host.ArchiveTransferMetadataLimitsForTests = new(storedTextBytes: single + single / 2);

        ArchiveTransferExportResult refused = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly), Path.Combine(files.Output, "one.zip")).WaitAsync(Deadline);
        Assert.Equal(ArchiveTransferExportError.SelectionLimit, refused.Error);
        Assert.Empty(refused.Parts);

        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly, Split: true), Path.Combine(files.Output, "halved.zip")).WaitAsync(Deadline);
        Assert.True(result.Succeeded, result.Error.ToString());
        Assert.Equal(5, result.Parts.Count);
        Assert.All(result.Parts, part => Assert.Equal(1, part.Threads));
        Assert.Equal(Enumerable.Range(1, 5).Select(part => Path.Combine(files.Output, $"halved-part-0{part}.zip")), result.Parts.Select(p => p.Path));
        Assert.Empty(Pending(files.Output));
    }

    [Fact]
    public async Task AnExistingNumberedPackageNameIsRefusedBeforeAnythingIsWritten()
    {
        using Workspace files = new();
        await files.SeedAsync(Threads(9300, 4).Select(id => (id, 1)).ToArray());
        await using DesktopSessionHost host = await files.StartAsync();
        host.ArchiveTransferPackageBudgetForTests = ArchiveTransferPackageBudget.Default with { Threads = 2 };
        await File.WriteAllTextAsync(Path.Combine(files.Output, "full-part-02.zip"), "KEEP");

        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly, Split: true), Path.Combine(files.Output, "full.zip")).WaitAsync(Deadline);

        Assert.Equal(ArchiveTransferExportError.DestinationExists, result.Error);
        Assert.Empty(result.Parts);
        Assert.False(File.Exists(Path.Combine(files.Output, "full-part-01.zip")));
        Assert.Equal("KEEP", await File.ReadAllTextAsync(Path.Combine(files.Output, "full-part-02.zip")));
    }

    // The package count is unknown until every package is frozen (halving adds some), so Full export refuses up front when any
    // numbered sibling of the chosen name exists, whatever its number; a plain export does not look at them at all.
    [Fact]
    public async Task FullExportRefusesAnyExistingNumberedSiblingBeforeWritingAndAPlainExportIgnoresThem()
    {
        using Workspace files = new();
        ResourceId[] ids = Threads(9400, 5);
        await files.SeedAsync(ids.Select(id => (id, 1)).ToArray());
        long single;
        await using (ArchiveReader reader = await ArchiveReader.OpenAsync(files.Profile).WaitAsync(Deadline))
            single = await SmallestTextBudgetAsync(reader, ids[0]);
        await using DesktopSessionHost host = await files.StartAsync();
        host.ArchiveTransferMetadataLimitsForTests = new(storedTextBytes: single + single / 2);
        string existing = Path.Combine(files.Output, "halved-part-07.zip");
        await File.WriteAllTextAsync(existing, "KEEP");
        File.WriteAllText(Path.Combine(files.Output, "halved-part-x.zip"), "OTHER"); // not a numbered package name
        File.WriteAllText(Path.Combine(files.Output, "other-part-01.zip"), "OTHER");

        // A one-package plan that must be halved: the number of parts is unknown at the start, so the sibling decides.
        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly, Split: true), Path.Combine(files.Output, "halved.zip")).WaitAsync(Deadline);

        Assert.Equal(ArchiveTransferExportError.DestinationExists, result.Error);
        Assert.Equal(existing, result.Destination);
        Assert.Empty(result.Parts);
        Assert.Equal(["halved-part-07.zip", "halved-part-x.zip", "other-part-01.zip"],
            Directory.EnumerateFiles(files.Output).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("KEEP", await File.ReadAllTextAsync(existing));
        Assert.Empty(Pending(files.Output));

        ArchiveTransferExportResult plain = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly), Path.Combine(files.Output, "plain.zip")).WaitAsync(Deadline);
        // Past the sibling check: refused by the lowered text budget, which a plain export does not halve around.
        Assert.Equal(ArchiveTransferExportError.SelectionLimit, plain.Error);
        Assert.Equal("KEEP", await File.ReadAllTextAsync(existing));
    }

    // A numbered name that appears after the up-front check is never overwritten: the publisher refuses it, and the package
    // already written stays listed.
    [Fact]
    public async Task ANumberedNameCreatedDuringAFullExportIsKeptAndTheWrittenPartsAreListed()
    {
        using Workspace files = new();
        await files.SeedAsync(Threads(9500, 4).Select(id => (id, 1)).ToArray());
        await using DesktopSessionHost host = await files.StartAsync();
        host.ArchiveTransferPackageBudgetForTests = ArchiveTransferPackageBudget.Default with { Threads = 2 };
        string raced = Path.Combine(files.Output, "full-part-02.zip");
        int writes = 0;
        host.ArchiveTransferExportCheckpointForTests = (phase, _) =>
        {
            if (phase == ArchiveTransferExportPhase.Writing && ++writes == 2) File.WriteAllText(raced, "KEEP");
            return Task.CompletedTask;
        };

        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly, Split: true), Path.Combine(files.Output, "full.zip")).WaitAsync(Deadline);

        Assert.Equal(ArchiveTransferExportError.DestinationExists, result.Error);
        Assert.Equal(Path.Combine(files.Output, "full-part-01.zip"), Assert.Single(result.Parts).Path);
        Assert.Equal("KEEP", await File.ReadAllTextAsync(raced));
        Assert.Empty(Pending(files.Output));
    }

    // Errors keep their meaning: no archive catalog is an empty selection; a read error midway (even a missing file) or a
    // closed connection is a read failure while the session is ready; only a session that is not ready is "profile unavailable".
    [Fact]
    public async Task ExportErrorsDistinguishNoCatalogReadFailuresAndAnUnavailableProfile()
    {
        using Workspace empty = new();
        await using (DesktopSessionHost noCatalog = await empty.StartAsync())
        {
            ArchiveTransferExportResult none = await noCatalog.ExportArchiveTransferAsync(
                new(null, ArchiveTransferExportMedia.MetadataOnly), Path.Combine(empty.Output, "none.zip")).WaitAsync(Deadline);
            Assert.Equal(ArchiveTransferExportError.EmptySelection, none.Error);
        }

        using Workspace files = new();
        await files.SeedAsync((new ResourceId("4chan", "b", "9501"), 1));
        await using DesktopSessionHost host = await files.StartAsync();
        string target = Path.Combine(files.Output, "failing.zip");
        foreach (Exception thrown in new Exception[] { new FileNotFoundException("vanished"), new InvalidOperationException("closed") })
        {
            host.ArchiveTransferExportCheckpointForTests = (_, _) => throw thrown;
            ArchiveTransferExportResult failed = await host.ExportArchiveTransferAsync(
                new(null, ArchiveTransferExportMedia.MetadataOnly), target).WaitAsync(Deadline);
            Assert.Equal(ArchiveTransferExportError.ReadFailure, failed.Error);
            Assert.False(File.Exists(target));
        }
        host.ArchiveTransferExportCheckpointForTests = null;

        await host.StopAsync().WaitAsync(Deadline);
        ArchiveTransferExportResult stopped = await host.ExportArchiveTransferAsync(
            new(null, ArchiveTransferExportMedia.MetadataOnly), target).WaitAsync(Deadline);
        Assert.Equal(ArchiveTransferExportError.ProfileUnavailable, stopped.Error);
    }

    private static ResourceId[] Threads(int first, int count) =>
        Enumerable.Range(first, count).Select(i => new ResourceId("4chan", "b", i.ToString())).ToArray();

    // The smallest stored-text budget one thread passes, found with the exporter's own read-only preflight.
    private static async Task<long> SmallestTextBudgetAsync(ArchiveReader reader, ResourceId id)
    {
        long low = 0, high = MeowChan.Transfer.NativeTransferArchiveMetadataLimits.MaxStoredTextBytes;
        while (low < high)
        {
            long middle = low + (high - low) / 2;
            if (await MeowChan.Transfer.NativeTransferArchiveExporter.PreflightMetadataAsync(reader, [id], new(storedTextBytes: middle), CancellationToken.None))
                high = middle;
            else low = middle + 1;
        }
        return low;
    }

    [Fact]
    public async Task ASelectedThreadThatIsNoLongerArchivedIsCountedAsMissingAndNotExported()
    {
        using Workspace files = new();
        ResourceId kept = new("4chan", "b", "8601");
        await files.SeedAsync((kept, 1));
        await using DesktopSessionHost host = await files.StartAsync();
        ResourceId gone = new("4chan", "b", "8699");

        ArchiveTransferExportCount count = await host.CountArchiveTransferExportAsync(
            new([kept, gone], ArchiveTransferExportMedia.MetadataOnly)).WaitAsync(Deadline);
        Assert.Equal(1, count.Threads);
        Assert.Equal(1, count.MissingThreads);
        string target = Path.Combine(files.Output, "partial.zip");
        ArchiveTransferExportResult result = await host.ExportArchiveTransferAsync(
            new([kept, gone], ArchiveTransferExportMedia.MetadataOnly), target).WaitAsync(Deadline);
        Assert.Equal(ArchiveTransferExportError.StaleSnapshot, result.Error);
        Assert.False(File.Exists(target));
    }

    private static string[] Pending(string directory) => Directory.EnumerateFiles(directory, ".meowchan-transfer-*.pending").ToArray();

    private sealed class Collect(List<ArchiveTransferExportProgress> reports) : IProgress<ArchiveTransferExportProgress>
    {
        public void Report(ArchiveTransferExportProgress value) { lock (reports) reports.Add(value); }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly TempDirectory _directory = new();
        internal string Profile { get; }
        internal string Work { get; }
        internal string Output { get; }
        private readonly string _media;

        internal Workspace()
        {
            Profile = Directory.CreateDirectory(Path.Combine(_directory.Path, "profile")).FullName;
            Work = Directory.CreateDirectory(Path.Combine(_directory.Path, "preview-work")).FullName;
            Output = Directory.CreateDirectory(Path.Combine(_directory.Path, "output")).FullName;
            _media = Path.Combine(Profile, "absent-media");
            new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadsPaused = true, DownloadPath = _media }.SaveConfig(Profile);
        }

        // Real catalog captures before the host takes the profile lease; one media folder per thread, never created.
        internal async Task SeedAsync(params (ResourceId Id, int Posts)[] threads)
        {
            using ProfileLease lease = ProfileLease.Acquire(Profile);
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(Profile, lease);
            foreach ((ResourceId id, int count) in threads)
            {
                CapturedPost[] posts = Enumerable.Range(0, count).Select(i => new CapturedPost((long.Parse(id.Thread!) * 10 + i).ToString(),
                    i, PostFieldPresence.Html | PostFieldPresence.Attachments, At.AddSeconds(i), Html: "<p>offline " + i + "</p>",
                    Attachments: [new(0, "m" + i, Url: $"https://i.4cdn.org/b/{id.Thread}{i}.jpg", OriginalFileName: "synthetic.jpg")])).ToArray();
                await catalog.CaptureAsync(new(id, $"https://boards.4chan.org/b/thread/{id.Thread}", "Synthetic " + id.Thread,
                    ThreadSourceState.NotFound, At, Path.Combine(_media, id.Thread!), posts));
            }
        }

        internal async Task<DesktopSessionHost> StartAsync()
        {
            DesktopSessionHost host = new(Profile, Array.Empty<IImageBoard>());
            try
            {
                await host.StartAsync().WaitAsync(Deadline);
                Assert.Equal(DesktopSessionState.Ready, host.State);
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }

        public void Dispose() => _directory.Dispose();
    }
}
