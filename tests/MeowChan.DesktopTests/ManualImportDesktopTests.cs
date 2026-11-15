using System.Security.Cryptography;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Manual import phase 1 on the desktop side: the dialog model, drop routing data and the real host (copy + batch tags
// through the loose-file tag path, revalidated against the import catalog). Temp profiles only.
[Collection("Localization")]
public sealed class ManualImportDesktopTests : IDisposable
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7, 7 };
    private static readonly byte[] Jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 };
    private readonly string _previous = Texts.Language;
    public ManualImportDesktopTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    // FB-R10: the dedupe check builds its index on the side; it reuses the Media page's snapshot, not its manifest views, and never
    // invalidates the page's shared cache (which would make the page's thumbnails and sound check prove file identities again).
    // MediaPage needs Avalonia's asset loader, which this host does not start, so this is a source contract.
    [Fact]
    public void TheDedupeCheckKeepsItsOwnManifestCacheAndNeverInvalidatesThePages()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, "../../Desktop/Shell/MainWindow.ManualImport.cs")));
        Assert.DoesNotContain(".ManifestCache", source);
        Assert.Contains("state.Manifests ??= page?.CreateManifestCache()", source);
        Assert.Contains("previous = page?.Snapshot ?? state.Last", source);
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static ManualImportPlan Plan(params ManualImportCandidate[] items) =>
        new(items, ManualImportPlanStatus.Ready, true, items.Count(item => item.Importable), items.Sum(item => item.Length), false, null);

    private static ManualImportCandidate Candidate(string name, ManualImportDisposition disposition) =>
        new("/nowhere/" + name, name, 10, SavedMediaTypes.Classify(name), Path.GetExtension(name), disposition,
            disposition is ManualImportDisposition.New or ManualImportDisposition.AlreadyInLibrary or ManualImportDisposition.DuplicateInSelection
                ? new string('a', 64) : null, disposition == ManualImportDisposition.AlreadyInLibrary ? 2 : 0);

    [Fact]
    public async Task SessionChecksOnEverySelectionChangeAndImportsWithNameAndTags()
    {
        List<ManualImportRequest> requests = new();
        ManualImportOptions? used = null;
        ManualImportPlan plan = Plan(Candidate("a.png", ManualImportDisposition.New), Candidate("b.png", ManualImportDisposition.AlreadyInLibrary),
            Candidate("c.txt", ManualImportDisposition.Unsupported), Candidate("d.png", ManualImportDisposition.Unreadable));
        ManualImportBatch batch = new(Guid.NewGuid(), "x", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, ManualImportBatchState.Completed,
            "/dl/meowchan-import/x", null, false, false, new[] { "cat" }, true, Array.Empty<ManualImportItem>());
        ManualImportSession session = new((request, _, _) => { requests.Add(request); return Task.FromResult(plan); },
            (_, options, _, _) => { used = options; return Task.FromResult(new ManualImportOutcome(batch, true, true, null)); },
            new DateTimeOffset(2026, 11, 13, 10, 5, 0, TimeSpan.Zero));

        Assert.StartsWith("Import 2026-11-13", session.BatchName);
        Assert.False(session.CanImport);
        session.AddRoots(new[] { "/tmp/one", "/tmp/one/", "/tmp/two" });
        await session.WorkTask;
        Assert.Equal(new[] { Path.GetFullPath("/tmp/one"), Path.GetFullPath("/tmp/two") }, Assert.Single(requests).Roots);
        Assert.Equal(ManualImportStage.Ready, session.Stage);
        Assert.Equal((1, 1, 1, 1), (session.NewCount, session.LinkedCount, session.UnsupportedCount, session.UnreadableCount));
        Assert.True(session.CanImport);

        session.IncludeOtherFiles = true;
        await session.WorkTask;
        Assert.True(requests.Last().IncludeOtherFiles);
        Assert.Equal(2, requests.Count);

        Assert.False(session.AddTag("   "));
        Assert.Equal("import.tags.invalid", session.MessageKey);
        Assert.True(session.AddTag("cat"));
        Assert.True(session.AddTag("CAT"));
        Assert.Equal(new[] { "cat" }, session.Tags);
        session.BatchName = " ";
        Assert.False(session.CanImport);
        session.BatchName = "Holiday";
        await session.ImportAsync();
        Assert.Equal(ManualImportStage.Done, session.Stage);
        Assert.Equal("Holiday", used!.BatchName);
        Assert.Equal(new[] { "cat" }, used.Tags);
        // After the import the selection is frozen.
        session.AddRoots(new[] { "/tmp/three" });
        Assert.Equal(2, session.Roots.Count);
    }

    [Fact]
    public async Task RefusalsKeepTheSelectionAndExplainThemselves()
    {
        ManualImportPlan tooMany = Plan(Candidate("a.png", ManualImportDisposition.New)) with { Status = ManualImportPlanStatus.TooManyFiles, ImportableFiles = 2001 };
        ManualImportSession session = new((_, _, _) => Task.FromResult(tooMany),
            (_, _, _, _) => throw new ManualImportException(ManualImportError.NotEnoughSpace), DateTimeOffset.UnixEpoch);
        session.AddRoots(new[] { "/tmp/many" });
        await session.WorkTask;
        Assert.Equal(("import.limit.files", 2000), (session.MessageKey, (int)session.MessageArguments.Single()));
        Assert.False(session.CanImport);
        Assert.Contains("2000", ManualImportSession.MessageText(session.MessageKey!, session.MessageArguments));

        ManualImportSession full = new((_, _, _) => Task.FromResult(Plan(Candidate("a.png", ManualImportDisposition.New))),
            (_, _, _, _) => throw new ManualImportException(ManualImportError.NotEnoughSpace), DateTimeOffset.UnixEpoch);
        full.AddRoots(new[] { "/tmp/a.png" });
        await full.WorkTask;
        await full.ImportAsync();
        Assert.Equal((ManualImportStage.Ready, "import.error.space"), (full.Stage, full.MessageKey));
        Assert.Single(full.Roots);
    }

    [Fact]
    public void ALimitRefusalNamesTheSettingAndThePageInEveryLanguageAndNeverARawConfigKey()
    {
        (string Key, object[] Arguments, string Setting)[] cases =
        {
            ("import.limit.files", new object[] { 2000 }, "budgets.importFiles"),
            ("import.limit.batch", Array.Empty<object>(), "budgets.importBatchGiB"),
            ("import.error.total", Array.Empty<object>(), "budgets.importTotal"),
            ("import.error.catalogSize", Array.Empty<object>(), "budgets.importCatalogMiB"),
            ("import.error.batches", Array.Empty<object>(), "budgets.importBatches"),
            ("import.tags.tooMany", new object[] { 32 }, "budgets.importTags"),
        };
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            foreach ((string key, object[] arguments, string setting) in cases)
            {
                string text = ManualImportSession.MessageText(key, arguments);
                Assert.Contains(Texts.T(setting), text);
                Assert.Contains(Texts.T("settings.storageLimits"), text);
                Assert.DoesNotContain("ImportMax", text);
                Assert.DoesNotContain("ImportCatalogLimit", text);
                Assert.DoesNotContain("{", text);
                Assert.DoesNotContain(key, text);
            }
            Assert.Contains("2000", ManualImportSession.MessageText("import.limit.files", 2000));
            ManualImportCandidate large = Candidate("big.png", ManualImportDisposition.TooLarge);
            Assert.Contains(Texts.T("budgets.importFileMiB"), ManualImportSession.StatusText(large));
        }
        Texts.SetLanguage("de");
        Assert.DoesNotContain("setting", ManualImportSession.MessageText("import.error.total"));
        Assert.DoesNotContain("Einstellung", ManualImportSession.MessageText("import.error.total"));
    }

    [Fact]
    public async Task CancelStopsTheRunningCheck()
    {
        TaskCompletionSource started = new();
        ManualImportSession session = new(async (_, _, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return null!; },
            (_, _, _, _) => throw new InvalidOperationException(), DateTimeOffset.UnixEpoch);
        session.AddRoots(new[] { "/tmp/slow" });
        await started.Task;
        Assert.True(session.Busy);
        session.Cancel();
        await session.WorkTask;
        Assert.Equal((ManualImportStage.Selecting, "import.check.cancelled"), (session.Stage, session.MessageKey));
    }

    [Fact]
    public void DroppedFilesAndFoldersCarryTheirLocalPathsForTheImportDialog()
    {
        string folder = Path.Combine(Path.GetTempPath(), "dropped folder");
        string file = Path.Combine(folder, "image.png");
        DataTransfer transfer = new();
        transfer.Add(DataTransferItem.CreateFile(new FixtureFolder(new Uri(folder))));
        transfer.Add(DataTransferItem.CreateFile(new FixtureFolder(new Uri(file))));
        DroppedContent content = DroppedLinks.Read(transfer);
        Assert.Equal(DroppedContentKind.Files, content.Kind);
        Assert.Equal(new[] { folder, file }, content.Paths);
        Assert.Equal(new[] { file }, DroppedLinks.Classify(new Uri(file).AbsoluteUri, []).Paths);
        Assert.Empty(DroppedLinks.Classify("https://boards.4chan.org/g/thread/1", []).Paths);
        // The overlay and notices now invite the import; the format contract keeps the count placeholder in every language.
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            Assert.Contains("3", Texts.T("drop.files.title", 3));
            Assert.Contains("X", Texts.T("import.media.tile", "X"));
        }
    }

    [Fact]
    public async Task HostImportsCopiesTagsTheNewFilesAndTheMediaIndexShowsThemAsTaggedLooseFiles()
    {
        using TempDirectory temp = new();
        string downloads = Path.Combine(temp.Path, "downloads"), source = Path.Combine(temp.Path, "source");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "a.png"), Png);
        File.WriteAllBytes(Path.Combine(source, "b.jpg"), Jpg);
        File.WriteAllBytes(Path.Combine(source, "twin.png"), Png);
        string profile = Path.Combine(temp.Path, "profile");
        new Config { DownloadPath = downloads, DownloadsPaused = true }.SaveConfig(profile);

        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        host.ManualImportFreeSpaceForTests = _ => null;
        Assert.Null(host.ManualImportBlocked());
        Assert.Equal(Path.Combine(Path.GetFullPath(downloads), "meowchan-import"), host.ManualImportFolder);
        ManualImportPlan plan = await host.PlanManualImportAsync(new ManualImportRequest(new[] { source }),
            _ => Task.FromResult(ManualImportKnownMedia.Empty), null, default);
        Assert.Equal(2, plan.Count(ManualImportDisposition.New));

        ManualImportOutcome outcome = await host.RunManualImportAsync(plan, new ManualImportOptions("Holiday", new[] { "holiday" }), null, default);
        Assert.True(outcome.TagsApplied, outcome.TagError?.ToString());
        Assert.True(outcome.Batch.TagsApplied);
        Assert.True(host.ReadManualImportCatalog()!.Batches.Single().TagsApplied);

        MediaLibrarySnapshot index = await MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync,
            readBooruCatalog: host.ReadBooruCatalog, readImportCatalog: host.ReadManualImportCatalog);
        Assert.Equal(2, index.Entries.Count(entry => entry.ImportItemId != null));
        PrivateTagStoreSnapshot tags = host.GetPrivateTagSnapshot().Snapshot!;
        PrivateTagDefinition holiday = Assert.Single(tags.Tags);
        Assert.Equal("holiday", holiday.Name.Display);
        foreach (MediaLibraryEntry entry in index.Entries)
        {
            MediaTagSubject.Local local = Assert.IsType<MediaTagSubject.Local>(MediaTagSubjects.Of(entry));
            Assert.True(tags.ResolveLocal(local.Observation).IsRegistered);
        }
        // The equal-bytes twin was linked, not copied.
        Assert.Equal(2, Directory.GetFiles(outcome.Batch.Directory).Count(path => !Path.GetFileName(path).StartsWith('.')));
        Assert.True(File.Exists(Path.Combine(source, "twin.png")));

        // A second import of the same folder against the fresh index links everything and copies nothing.
        ManualImportPlan again = await host.PlanManualImportAsync(new ManualImportRequest(new[] { source }),
            _ => Task.FromResult(ManualImportKnownMedia.From(index)), null, default);
        Assert.Equal(0, again.Count(ManualImportDisposition.New));
        // Library bytes win over selection twins: all three files are already there.
        Assert.Equal(3, again.Count(ManualImportDisposition.AlreadyInLibrary));
        Assert.Equal(0, (await host.InspectLocalMediaTagOrphansAsync())!.OrphanedFiles);
    }

    private sealed class FixtureFolder(Uri path) : IStorageFolder
    {
        public string Name => System.IO.Path.GetFileName(path.LocalPath);
        public Uri Path => path;
        public bool CanBookmark => false;
        public Task<StorageItemProperties> GetBasicPropertiesAsync() => Task.FromResult(new StorageItemProperties());
        public Task<string?> SaveBookmarkAsync() => Task.FromResult<string?>(null);
        public Task<IStorageFolder?> GetParentAsync() => Task.FromResult<IStorageFolder?>(null);
        public Task DeleteAsync() => throw new NotSupportedException();
        public Task<IStorageItem?> MoveAsync(IStorageFolder destination) => throw new NotSupportedException();
        public IAsyncEnumerable<IStorageItem> GetItemsAsync() => throw new NotSupportedException();
        public Task<IStorageFile?> CreateFileAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFolder?> CreateFolderAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFolder?> GetFolderAsync(string name) => throw new NotSupportedException();
        public Task<IStorageFile?> GetFileAsync(string name) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
