using System.Text;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;
using MeowChan.Storage;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

// Disposable synthetic archives only. No provider, profile fixtures or network.
public sealed class NativeTransferMultiThreadExportTests
{
    private static readonly ResourceId First = new("4chan", "b", "7001");
    private static readonly ResourceId Second = new("4chan", "b", "7002");
    private static readonly DateTimeOffset At = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Package = Guid.Parse("08132d5c-a474-47a4-8db9-73619aa3fa20");

    private static CapturedThreadSnapshot Capture(ResourceId id, string root, string title = "synthetic") =>
        new(id, $"https://boards.4chan.org/b/thread/{id.Thread}", title, ThreadSourceState.NotFound, At, root,
            [new(id.Thread!, 0, PostFieldPresence.Html | PostFieldPresence.Attachments, Html: "<p>offline</p>",
                Attachments: [new(0, "123", Url: "https://i.4cdn.org/b/123.jpg", OriginalFileName: "original.jpg")])]);

    [Fact]
    public void SelectionCopiesInputPreservesOrderAndRejectsEmptyDuplicateNullAndOverflow()
    {
        List<ResourceId> input = [Second, First];
        NativeTransferArchiveSelection selection = new(input);
        input.Clear();
        Assert.Equal(new[] { Second, First }, selection.Threads.ToArray());
        Assert.Throws<ArgumentException>(() => new NativeTransferArchiveSelection([]));
        Assert.Throws<ArgumentException>(() => new NativeTransferArchiveSelection([First, First]));
        Assert.Throws<ArgumentException>(() => new NativeTransferArchiveSelection([null!]));
        Assert.Equal(1000, new NativeTransferArchiveSelection(Enumerable.Range(0, 1000)
            .Select(i => new ResourceId("4chan", "b", i.ToString()))).Threads.Length);
        Assert.Throws<ArgumentException>(() => new NativeTransferArchiveSelection(Enumerable.Range(0, 1001)
            .Select(i => new ResourceId("4chan", "b", i.ToString()))));
    }

    [Fact]
    public async Task ExplicitOrderWritesOneValidatedMetadataPackageWithoutOpeningMedia()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        string root = Path.Combine(profile.Path, "corrupt-media"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"), "must not read");
        await catalog.CaptureAsync(Capture(First, root));
        await catalog.CaptureAsync(Capture(Second, root));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        var result = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([Second, First]), At, Package, false);
        Assert.True(result.IsFrozen, result.Error.ToString());
        Assert.Equal(2, result.Snapshot!.Views.Length);
        Assert.Empty(result.Snapshot.Manifests); Assert.Empty(result.Snapshot.Payloads);
        using MemoryStream stage = new();
        var written = await NativeTransferArchiveExporter.WriteAsync(result.Snapshot, stage);
        Assert.True(written.IsComplete, written.Error.ToString());
        Assert.Equal(new[] { Second, First }, written.Package!.Catalog.Threads.Select(t => t.Id).ToArray());
        Assert.All(written.Package.Catalog.Threads, t =>
            Assert.Equal(TransferContentStatus.Omitted, t.Posts[0].Attachments[0].Status));
        Assert.True((await NativeTransferPackageValidator.InspectAsync(stage)).IsPackageValid);
        Assert.DoesNotContain(profile.Path, result.Snapshot.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnySelectedThreadMutationRejectsBeforeTouchingStage(bool changeFirst)
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        var first = Capture(First, Path.Combine(profile.Path, "a"));
        var second = Capture(Second, Path.Combine(profile.Path, "b"));
        await catalog.CaptureAsync(first); await catalog.CaptureAsync(second);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        var frozen = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([First, Second]), At, Package, false);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        await catalog.CaptureAsync((changeFirst ? first : second) with { Title = "changed", CapturedAt = At.AddMinutes(1) });
        using MemoryStream stage = new();
        var result = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error);
        Assert.Null(result.Package); Assert.Equal(0, stage.Length);
    }

    [Fact]
    public async Task MissingLaterSelectionRejectsEntireFreezeBeforeCorruptMediaAccess()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        string root = Path.Combine(profile.Path, "media"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"), "corrupt");
        await catalog.CaptureAsync(Capture(First, root));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        var result = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([First, Second]), At, Package);
        Assert.Equal(TransferArchiveError.MissingThread, result.Error); Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task SingleThreadEntryPointProducesSameCatalogAndPackageAsExplicitSelection()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await catalog.CaptureAsync(Capture(First, Path.Combine(profile.Path, "media")));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        var single = await NativeTransferArchiveExporter.FreezeAsync(reader, First, At, Package, false);
        var explicitOne = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([First]), At, Package, false);
        Assert.True(single.IsFrozen); Assert.True(explicitOne.IsFrozen);
        using MemoryStream a = new(); using MemoryStream b = new();
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(single.Snapshot!, a)).IsComplete);
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(explicitOne.Snapshot!, b)).IsComplete);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact]
    public async Task SharedRootOriginalsAreRepresentedAcrossThreadsAndPayloadsDeduplicate()
    {
        await using MeowChan.Tests.GoalFixtures.NativeTransferArchiveFixture fixture =
            await MeowChan.Tests.GoalFixtures.NativeTransferArchiveFixture.CreateAsync();
        ResourceId other = new("4chan", "b", "8001");
        using (ProfileLease lease = ProfileLease.Acquire(fixture.ProfilePath))
        {
            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(fixture.ProfilePath, lease);
            await catalog.CapturePreservingRevisionsAsync(Capture(other, fixture.MediaDirectory) with
            {
                Posts = [new("8001", 0, PostFieldPresence.Attachments,
                    Attachments: [new(0, "112", Url: "https://i.4cdn.org/b/112.jpg", OriginalFileName: "legacy-synthetic.jpg")])]
            });
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        var frozen = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([fixture.ThreadId, other]), At, Package);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        Assert.Single(frozen.Snapshot!.Manifests); Assert.Equal(2, frozen.Snapshot.Payloads.Length);
        Assert.Equal(TransferContentStatus.Contained, frozen.Snapshot.Catalog.Threads[1].Posts[0].Attachments[0].Status);
        using MemoryStream stage = new();
        var written = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot, stage);
        Assert.True(written.IsComplete, written.Error + " / " + written.WriteError);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(stage)).IsPackageValid);
        Assert.Equal(0, fixture.UnexpectedProviderCalls);
    }

    [Fact]
    public async Task AggregateDirectoryLimitRejectsBeforeOpeningAnyManifest()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        List<ResourceId> ids = [];
        for (int i = 0; i < 33; i++)
        {
            ResourceId id = new("4chan", "b", (8000 + i).ToString()); ids.Add(id);
            string root = Path.Combine(profile.Path, i.ToString()); Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"), "corrupt");
            await catalog.CaptureAsync(Capture(id, root));
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        var result = await NativeTransferArchiveExporter.FreezeAsync(reader, new NativeTransferArchiveSelection(ids), At, Package);
        Assert.Equal(TransferArchiveError.SelectionLimit, result.Error); Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task LaterThreadChangedDuringWriteRejectsOtherwiseValidStage()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        var second = Capture(Second, Path.Combine(profile.Path, "b"));
        await catalog.CaptureAsync(Capture(First, Path.Combine(profile.Path, "a")));
        await catalog.CaptureAsync(second);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        var frozen = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([First, Second]), At, Package, false);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        using MutationStream stage = new(() => catalog.CaptureAsync(second with
            { Title = "concurrent change", CapturedAt = At.AddMinutes(1) }).GetAwaiter().GetResult());
        var result = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error);
        Assert.Null(result.Package); Assert.Null(result.BytesWritten); Assert.True(stage.Length > 0);
        Assert.True((await NativeTransferPackageValidator.InspectAsync(stage)).IsPackageValid);
    }

    [Fact]
    public async Task ManyThreadTextPreflightStopsBeforeViewsLaterIdentitiesAndCorruptMedia()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        string root = Path.Combine(profile.Path, "corrupt-media"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".meowchan-manifest.json"), "must not read");
        ResourceId[] ids = Enumerable.Range(7001, 64).Select(i => new ResourceId("4chan", "b", i.ToString())).ToArray();
        foreach (ResourceId id in ids)
            await catalog.CaptureAsync(Capture(id, root) with
            {
                Posts = [new(id.Thread!, 0, PostFieldPresence.Html, Html: new string('x', 1024))]
            });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        NativeTransferArchiveMetadataLimits limits = new(storedTextBytes: 4096);
        // Each thread passes both the real reader and the small selection preflight alone.
        Assert.NotNull(await reader.ReadNativeTransferAsync(First, default));
        Assert.NotNull(await reader.ReadNativeTransferAsync(Second, default));
        Assert.True(await NativeTransferArchiveExporter.PreflightMetadataAsync(reader, [First], limits, default));
        Assert.True(await NativeTransferArchiveExporter.PreflightMetadataAsync(reader, [Second], limits, default));
        // A materialization of even the FIRST thread would now throw FormatException.
        await SetThreadDateAsync(profile.Path, First, "invalid-date");
        await Assert.ThrowsAsync<FormatException>(() => reader.ReadNativeTransferAsync(First, default));
        // The final identity is intentionally invalid: even reaching its validation is a bug.
        ResourceId neverRead = new("4chan", "b");
        var result = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([.. ids, neverRead]), At, Package, metadataLimits: limits);
        Assert.Equal(TransferArchiveError.SelectionLimit, result.Error); Assert.Null(result.Snapshot);
        Assert.Equal("must not read", await File.ReadAllTextAsync(Path.Combine(root, ".meowchan-manifest.json")));
    }

    [Fact]
    public async Task StoredTextPreflightCountsWholeUtf8IncludingNulAndNonportablePathsAtExactBoundary()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        string root = Path.Combine(profile.Path, "media");
        await catalog.CaptureAsync(Capture(First, root)); await catalog.CaptureAsync(Capture(Second, root));
        // SQL-only synthetic corruption avoids manufacturing a valid OS path. The size pass
        // must count ALL bytes, not SQLite length(TEXT)'s prefix before NUL or UTF-16 chars.
        string local = root + "\0" + string.Concat(Enumerable.Repeat("é雪😀", 200));
        await SetPlannedPathAsync(profile.Path, Second, local);
        long bytes = await StoredTextBytesAsync(profile.Path);
        Assert.True(bytes > Encoding.UTF8.GetByteCount(local));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        Assert.True(await NativeTransferArchiveExporter.PreflightMetadataAsync(reader, [First, Second], new(bytes), default));
        Assert.False(await NativeTransferArchiveExporter.PreflightMetadataAsync(reader, [First, Second], new(bytes - 1), default));
        // Nothing in the portable catalog depends on PlannedPath, but its retained view does.
        var result = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([First, Second]), At, Package, false, metadataLimits: new(bytes - 1));
        Assert.Equal(TransferArchiveError.SelectionLimit, result.Error); Assert.Null(result.Snapshot);
        var inside = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([First, Second]), At, Package, false, metadataLimits: new(bytes));
        Assert.True(inside.IsFrozen, inside.Error.ToString());
        Assert.DoesNotContain(local, Encoding.UTF8.GetString(inside.Snapshot!.Metadata.AsSpan()));
    }

    [Fact]
    public void ActualViewReservationCountsEveryStringAndPrivateFieldRatherThanPostCountsOrCharacters()
    {
        string local = "/private/\0" + new string('雪', 40);
        CapturedAttachment attachment = new(0, "media", "original", "https://i.4cdn.org/b/123.jpg",
            "name-é.jpg", local, ExpectedMd5: "md5")
        {
            ExpectedSha1 = "sha1", ExpectedSha256 = "sha256", AttachmentRevisionKey = "revision",
            ProviderSizeHint = new("123", "KiB", MediaSizePrecision.Rounded)
        };
        ArchiveThreadSummary summary = new(First, "https://boards.4chan.org/b/thread/7001", "title-😀", local,
            ThreadSourceState.NotFound, false, At, At, 1, 1);
        CapturedPost post = new("7001", 0, PostFieldPresence.All, At, "subject-雪", "author-é", "\0html-😀",
            Attachments: [attachment]);
        ArchivedThreadView view = new(summary, [post], [local, "/other/root"]);
        // Independent literal inventory includes each property and repeated stored-root slot.
        string[] values = [First.Source, First.Board, First.Thread!, summary.Url, summary.Title, local, local, "/other/root",
            post.PostId, post.Subject!, post.Author!, post.Html!, "media", "original", attachment.Url!, "name-é.jpg", local,
            "md5", "sha1", "sha256", "revision", "123", "KiB"];
        long bytes = values.Sum(value => (long)Encoding.UTF8.GetByteCount(value));
        Assert.True(bytes > values.Sum(value => (long)value.Length));
        var exact = new NativeTransferArchiveExporter.MetadataBudget(new(bytes), At, default);
        Assert.True(exact.TryReserveView(view, default));
        Assert.False(exact.TryReserveView(view, default)); // repeated view consumes the remaining selection budget
        Assert.False(new NativeTransferArchiveExporter.MetadataBudget(new(bytes - 1), At, default).TryReserveView(view, default));
    }

    [Fact]
    public async Task ActualViewReservationCatchesGrowthAfterAdvisoryPreflight()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        string root = Path.Combine(profile.Path, "media");
        await catalog.CaptureAsync(Capture(First, root)); await catalog.CaptureAsync(Capture(Second, root));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        long bytes = await StoredTextBytesAsync(profile.Path);
        NativeTransferArchiveMetadataLimits limits = new(bytes);
        Assert.True(await NativeTransferArchiveExporter.PreflightMetadataAsync(reader, [First, Second], limits, default));
        var budget = new NativeTransferArchiveExporter.MetadataBudget(limits, At, default);
        Assert.True(budget.TryReserveView((await reader.ReadNativeTransferAsync(First, default))!, default));
        await SetPlannedPathAsync(profile.Path, Second, root + new string('x', checked((int)bytes)));
        // No shared SQL snapshot is assumed. The fresh bounded read has real larger strings,
        // even though the post/attachment counts, summary and portable metadata are unchanged.
        Assert.False(budget.TryReserveView((await reader.ReadNativeTransferAsync(Second, default))!, default));
    }

    [Fact]
    public async Task PortableBytesCountJsonEscapingAndCommasAtBoundaryAndStopBeforeLaterRead()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        string root = Path.Combine(profile.Path, "media");
        string text = string.Concat(Enumerable.Repeat("雪 & 😀", 100));
        foreach (ResourceId id in new[] { First, Second })
            await catalog.CaptureAsync(Capture(id, root) with
            { Posts = [new(id.Thread!, 0, PostFieldPresence.Subject, Subject: text)] });
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferThread first = NativeTransferArchiveProjection.From((await reader.ReadNativeTransferAsync(First, default))!);
        TransferThread second = NativeTransferArchiveProjection.From((await reader.ReadNativeTransferAsync(Second, default))!);
        int bytes = NativeTransferCatalogSerializer.Serialize(new(At, [first, second])).Length;
        Assert.True(bytes > 2 * Encoding.UTF8.GetByteCount(text));
        var inside = await NativeTransferArchiveExporter.FreezeAsync(reader, new NativeTransferArchiveSelection([First, Second]),
            At, Package, false, metadataLimits: new(catalogBytes: bytes));
        Assert.True(inside.IsFrozen, inside.Error.ToString());
        Assert.Equal(bytes, inside.Snapshot!.Metadata.Length);
        Assert.True(await NativeTransferArchiveExporter.IsCurrentAsync(inside.Snapshot, default));
        ResourceId third = new("4chan", "b", "7003");
        await catalog.CaptureAsync(Capture(third, root)); await SetThreadDateAsync(profile.Path, third, "bad-date");
        await Assert.ThrowsAsync<FormatException>(() => reader.ReadNativeTransferAsync(third, default));
        var rejected = await NativeTransferArchiveExporter.FreezeAsync(reader, new NativeTransferArchiveSelection([First, Second, third]),
            At, Package, metadataLimits: new(catalogBytes: bytes - 1));
        Assert.Equal(TransferArchiveError.SelectionLimit, rejected.Error); Assert.Null(rejected.Snapshot);
    }

    [Fact]
    public async Task RecheckPreflightRejectsGrownPrivateTextBeforeReadingOrWritingStage()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        string root = Path.Combine(profile.Path, "media");
        await catalog.CaptureAsync(Capture(First, root)); await catalog.CaptureAsync(Capture(Second, root));
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        long bytes = await StoredTextBytesAsync(profile.Path);
        var frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, new NativeTransferArchiveSelection([First, Second]),
            At, Package, false, metadataLimits: new(bytes));
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        await SetPlannedPathAsync(profile.Path, Second, root + new string('x', checked((int)bytes)));
        await SetThreadDateAsync(profile.Path, First, "bad-date"); // proves recheck never materializes even the first view
        Assert.False(await NativeTransferArchiveExporter.IsCurrentAsync(frozen.Snapshot!, default));
        using MemoryStream stage = new();
        var result = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, stage);
        Assert.Equal(TransferArchiveError.StaleSnapshot, result.Error); Assert.Null(result.Package);
        Assert.Equal(0, stage.Length);
    }

    [Fact]
    public async Task AllThousandSmallThreadsRemainAdmissible()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        List<ResourceId> ids = [];
        string root = Path.Combine(profile.Path, "media");
        for (int i = 0; i < NativeTransferArchiveSelection.MaxThreads; i++)
        {
            ResourceId id = new("4chan", "b", (10000 + i).ToString()); ids.Add(id);
            await catalog.CaptureAsync(Capture(id, root) with { Posts = [] });
        }
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        var frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, new NativeTransferArchiveSelection(ids), At, Package, false);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        Assert.Equal(1000, frozen.Snapshot!.Views.Length); Assert.Equal(1000, frozen.Snapshot.Catalog.Threads.Length);
    }

    [Fact]
    public async Task SingleThreadKeepsExistingLimitsEvenWithZeroMultiSelectionBudgets()
    {
        await using MeowChan.Tests.GoalFixtures.NativeTransferArchiveFixture fixture =
            await MeowChan.Tests.GoalFixtures.NativeTransferArchiveFixture.CreateAsync();
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(fixture.ProfilePath);
        var original = await NativeTransferArchiveExporter.FreezeAsync(reader, fixture.ThreadId, At, Package);
        var explicitOne = await NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([fixture.ThreadId]), At, Package, metadataLimits: new(0, 0));
        Assert.True(original.IsFrozen, original.Error.ToString()); Assert.True(explicitOne.IsFrozen, explicitOne.Error.ToString());
        using MemoryStream a = new(); using MemoryStream b = new();
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(original.Snapshot!, a)).IsComplete);
        Assert.True((await NativeTransferArchiveExporter.WriteAsync(explicitOne.Snapshot!, b)).IsComplete);
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact]
    public void MetadataSeamCannotRaiseProductionCeilings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeTransferArchiveMetadataLimits(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeTransferArchiveMetadataLimits(catalogBytes: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeTransferArchiveMetadataLimits(NativeTransferArchiveMetadataLimits.MaxStoredTextBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NativeTransferArchiveMetadataLimits(catalogBytes: NativeTransferArchiveMetadataLimits.MaxCatalogBytes + 1));
    }

    private static async Task SetThreadDateAsync(string profile, ResourceId id, string value)
    {
        await using SqliteConnection connection = new("Data Source=" + Path.Combine(profile, "archive.db") + ";Pooling=False");
        await connection.OpenAsync(); using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE archive_threads SET first_seen_at=$value WHERE source=$source AND board=$board AND thread=$thread";
        command.Parameters.AddWithValue("$value", value); command.Parameters.AddWithValue("$source", id.Source);
        command.Parameters.AddWithValue("$board", id.Board); command.Parameters.AddWithValue("$thread", id.Thread);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task SetPlannedPathAsync(string profile, ResourceId id, string value)
    {
        await using SqliteConnection connection = new("Data Source=" + Path.Combine(profile, "archive.db") + ";Pooling=False");
        await connection.OpenAsync(); using SqliteCommand command = connection.CreateCommand();
        // Preserve the synthetic NUL suffix across the SQLite provider's string binder.
        // Store TEXT, not a blob column: the production preflight must count the whole value.
        command.CommandText = "UPDATE archive_attachments SET planned_path=CAST($value AS TEXT) WHERE source=$source AND board=$board AND thread=$thread";
        command.Parameters.AddWithValue("$value", Encoding.UTF8.GetBytes(value)); command.Parameters.AddWithValue("$source", id.Source);
        command.Parameters.AddWithValue("$board", id.Board); command.Parameters.AddWithValue("$thread", id.Thread);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        command.CommandText = "SELECT CAST(planned_path AS BLOB) FROM archive_attachments WHERE source=$source AND board=$board AND thread=$thread";
        command.Parameters.RemoveAt("$value");
        Assert.Equal(Encoding.UTF8.GetBytes(value), Assert.IsType<byte[]>(await command.ExecuteScalarAsync()));
    }

    // Small synthetic fixtures only: independently count the stored UTF-8 bytes, not
    // the production SQL length/SUM expression. GetString may truncate at embedded NUL;
    // reading each value as a blob keeps the nonportable suffix in this exact-boundary oracle.
    // Current fixture schema has all optional fields.
    private static async Task<long> StoredTextBytesAsync(string profile)
    {
        await using SqliteConnection connection = new("Data Source=" + Path.Combine(profile, "archive.db") + ";Pooling=False");
        await connection.OpenAsync(); long bytes = 0;
        foreach ((string table, string columns) in new (string, string)[]
        {
            ("archive_threads", "source,board,thread,url,title,media_directory,first_seen_at,last_captured_at"),
            ("archive_posts", "post,posted_at,subject,author,html"),
            ("archive_attachments", "post,media_id,variant,url,original_name,planned_path,expected_md5,expected_sha256,attachment_revision_key,expected_sha1,provider_size_hint_json"),
            ("archive_media_directories", "directory")
        })
        {
            string byteColumns = string.Join(",", columns.Split(',').Select(column => $"CAST({column} AS BLOB)"));
            using SqliteCommand command = connection.CreateCommand(); command.CommandText = $"SELECT {byteColumns} FROM {table}";
            using SqliteDataReader row = command.ExecuteReader();
            while (row.Read())
                for (int i = 0; i < row.FieldCount; i++)
                    if (!row.IsDBNull(i)) bytes += row.GetFieldValue<byte[]>(i).LongLength;
        }
        return bytes;
    }

    private sealed class MutationStream(Action once) : MemoryStream
    {
        private Action? pending = once;
        private void Before() => Interlocked.Exchange(ref pending, null)?.Invoke();
        public override void Write(byte[] buffer, int offset, int count) { Before(); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Before(); base.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Before(); return base.WriteAsync(buffer, cancellationToken); }
    }

    [Fact]
    public async Task MultiSelectionPrecancellationPropagates()
    {
        using TempDirectory profile = new(); using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile.Path, lease);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeTransferArchiveExporter.FreezeAsync(reader,
            new NativeTransferArchiveSelection([First, Second]), At, Package, cancellationToken: cancelled.Token));
    }
}
