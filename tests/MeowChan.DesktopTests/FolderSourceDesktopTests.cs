using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// PR2/PR3 on the desktop side: the real host adds, scans, hashes and removes a read-only folder source; its files reach the
// media index, take tags, favorites and ratings once hashed, and the folder stays byte- and timestamp-identical throughout.
// Temp profiles and temp folders only.
[Collection("Localization")]
public sealed class FolderSourceDesktopTests : IDisposable
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 3, 1, 4 };
    private static readonly byte[] Jpg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 5, 9 };
    private readonly string _previous = Texts.Language;
    public FolderSourceDesktopTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    private static IReadOnlyList<string> Fingerprint(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(entry => File.Exists(entry)
                ? Path.GetRelativePath(folder, entry) + " " + new FileInfo(entry).Length + " " + File.GetLastWriteTimeUtc(entry).Ticks + " "
                  + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))
                : Path.GetRelativePath(folder, entry) + "/ " + Directory.GetLastWriteTimeUtc(entry).Ticks).ToArray();

    [Fact]
    public void RemoveSourceAsksWithKeepFirstAndOnlyRemovesTheNamedSource() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        FolderSourceRoot first = Source("First");
        FolderSourceRoot second = Source("Second");
        FolderSourceRoot[] sources = [first, second];
        List<Guid> removed = new();
        FolderSourcesPanel panel = new(new FolderSourcePanelServices(
            () => sources.Select(root => new FolderSourceView(root, new(root.Id, 0, 0, 0), false)).ToArray(),
            () => new(null, null, new(0, 0, false, false), 0, 0), () => FolderSourceError.None,
            new(_ => FolderSourceError.None, (_, _, _) => Task.FromResult(first), (_, _) => Task.FromResult(first)),
            _ => Task.CompletedTask, id =>
            {
                removed.Add(id);
                sources = sources.Where(root => root.Id != id).ToArray();
                return Task.CompletedTask;
            }, () => false, _ => { }));
        Window window = new() { Width = 700, Height = 580, Content = panel };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            BooruComponentDispatcher.Wait(WaitAsync(() => panel.Rows.Children.Count == 2));
            string openName = "FolderSourceRemove_" + FolderSourceIdentity.Format(second.Id);
            foreach ((Key key, PhysicalKey physicalKey) in new[] { (Key.Escape, PhysicalKey.Escape), (Key.Enter, PhysicalKey.Enter) })
            {
                Ask();
                Press(key, physicalKey);
                Assert.Null(Find("FolderSourceRemoveConfirm"));
                Assert.Empty(removed);
            }
            Ask();
            Press(Key.Tab, PhysicalKey.Tab);
            Assert.Same(Find("FolderSourceRemoveConfirm"), window.FocusManager!.GetFocusedElement());
            Press(Key.Enter, PhysicalKey.Enter);
            Assert.Equal(second.Id, Assert.Single(removed));
            BooruComponentDispatcher.Wait(WaitAsync(() => panel.Rows.Children.Count == 1));

            void Ask()
            {
                Assert.True(Find(openName)!.Focus());
                Press(Key.Enter, PhysicalKey.Enter);
                var keep = Find("FolderSourceRemoveKeep");
                var confirm = Find("FolderSourceRemoveConfirm");
                Assert.NotNull(keep);
                Assert.NotNull(confirm);
                Assert.True(keep.TranslatePoint(default, confirm) is { X: < 0 });
                Assert.Same(keep, window.FocusManager!.GetFocusedElement());
                Assert.Contains("Second", panel.GetVisualDescendants().OfType<TextBlock>()
                    .Single(block => block.Name == "FolderSourceRemoveAsk").Text);
            }

            Button? Find(string name) => panel.GetVisualDescendants().OfType<Button>().SingleOrDefault(button => button.Name == name);
            void Press(Key key, PhysicalKey physicalKey)
            {
                window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
                window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
        }
        finally { window.Close(); }

        static FolderSourceRoot Source(string name) => new(Guid.NewGuid(), Path.GetFullPath(name), name, DateTimeOffset.UnixEpoch, false, null, null);
    });

    [Fact]
    public async Task TheHostAddsScansHashesAndRemovesAFolderWithoutEverTouchingIt()
    {
        using TempDirectory temp = new();
        string downloads = Path.Combine(temp.Path, "downloads"), media = Path.Combine(temp.Path, "media"), profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(Path.Combine(media, "sub"));
        Directory.CreateDirectory(downloads);
        File.WriteAllBytes(Path.Combine(media, "a.png"), Png);
        File.WriteAllBytes(Path.Combine(media, "sub", "b.jpg"), Jpg);
        File.WriteAllBytes(Path.Combine(media, "readme.txt"), new byte[] { 1, 2 });
        new Config { DownloadPath = downloads, DownloadsPaused = true, AutomaticUpdateChecks = false, FolderSourceBackgroundHashing = false }.SaveConfig(profile);
        IReadOnlyList<string> before = Fingerprint(media);

        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        Assert.Equal(FolderSourceError.None, host.FolderSourcesBlocked());
        Assert.Empty(host.ListFolderSources());
        Assert.False(File.Exists(Path.Combine(profile, FolderSourceCatalog.FileName)));
        Assert.Equal(FolderSourceError.FolderIsDownloads, host.CheckFolderSource(downloads));
        Assert.Equal(FolderSourceError.FolderIsProfile, host.CheckFolderSource(profile));
        Assert.Equal(FolderSourceError.None, host.CheckFolderSource(media));

        int changes = 0;
        host.FolderSourcesChanged += () => Interlocked.Increment(ref changes);
        FolderSourceRoot root = await host.AddFolderSourceAsync(media, "Holiday", includeSubfolders: true);
        await WaitAsync(() => host.ListFolderSources().Single().Counts.Files == 2 && !host.ListFolderSources().Single().Scanning);
        Assert.True(Volatile.Read(ref changes) > 0);
        Assert.Equal(FolderSourceError.FolderOverlaps, host.CheckFolderSource(Path.Combine(media, "sub")));
        FolderSourceView view = Assert.Single(host.ListFolderSources());
        Assert.Equal((2, 0, 0), (view.Counts.Files, view.Counts.Hashed, view.Counts.Missing));
        Assert.Equal(FolderSourceScanStatus.Complete, view.Root.LastScanStatus);

        MediaLibrarySnapshot index = await MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync,
            readFolderSources: host.ReadFolderSources);
        MediaLibraryEntry entry = index.Entries.Single(candidate => candidate.Name == "a.png");
        Assert.Null(entry.Sha256);
        Assert.True(MediaPage.HasThreadRules(entry) == false);

        // The first write hashes the file (a user action), then favorite, rating and tag name the hashed bytes.
        FolderSourceHashResult hashed = await host.HashFolderItemAsync(Guid.ParseExact(entry.FolderItemId!, "N"), FolderSourceHashPriority.UserAction);
        Assert.Equal(FolderSourceHashOutcome.Hashed, hashed.Outcome);
        GalleryEntry gallery = (await FolderSourceMediaGallery.ResolveAsync(entry, MediaAssetReader.OpenAsync))!;
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant(), gallery.Asset.Sha256);
        Assert.True(await host.SetFavoriteAsync(gallery, true));
        Assert.True(host.IsFavorite(gallery));
        await host.SetMediaRatingAsync(Favorites.FavoriteAssetKey.From(gallery.Key.Thread, gallery.Asset), 4);
        MediaTagSubject subject = MediaTagSubjects.Of(gallery)!;
        await host.DefineAndSetMediaSubjectTagAsync("beach", subject);
        Assert.Single(host.GetPrivateTagSnapshot().Snapshot!.LocalAssignments);
        MeowChan.Desktop.FavoriteResolutionSnapshot resolved = await host.ResolveFavoriteAssetsAsync();
        Assert.Single(resolved.Available);

        MediaLibrarySnapshot after = await MediaLibraryIndexBuilder.BuildAsync(host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync,
            readFolderSources: host.ReadFolderSources);
        MediaLibraryEntry hashedEntry = after.Entries.Single(candidate => candidate.Name == "a.png");
        Assert.NotNull(hashedEntry.Sha256);
        Assert.NotNull(MediaLibraryFilterEngine.AssetKey(hashedEntry));
        Assert.Equal(0, (await host.InspectLocalMediaTagOrphansAsync())!.OrphanedFiles);

        // Rename and rescan change nothing in the folder; removing the source keeps the tag as an orphan.
        Assert.Equal("Trip", (await host.RenameFolderSourceAsync(root.Id, "Trip")).Name);
        FolderSourceScanResult? rescan = await host.RescanFolderSourceAsync(root.Id);
        Assert.Equal(2, rescan!.Unchanged);
        await host.RemoveFolderSourceAsync(root.Id);
        Assert.Empty(host.ListFolderSources());
        Assert.Equal(1, (await host.InspectLocalMediaTagOrphansAsync())!.OrphanedFiles);
        Assert.Single(host.GetPrivateTagSnapshot().Snapshot!.LocalAssignments);
        Assert.Equal(before, Fingerprint(media));
    }

    [Fact]
    public async Task UnhashedFilesAreHashedInTheBackgroundWhenTheSettingIsOn()
    {
        using TempDirectory temp = new();
        string downloads = Path.Combine(temp.Path, "downloads"), media = Path.Combine(temp.Path, "media"), profile = Path.Combine(temp.Path, "profile");
        Directory.CreateDirectory(media);
        for (int index = 0; index < 4; index++) File.WriteAllBytes(Path.Combine(media, index + ".png"), Png.Append((byte)index).ToArray());
        new Config { DownloadPath = downloads, DownloadsPaused = true, AutomaticUpdateChecks = false }.SaveConfig(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync();
        await host.AddFolderSourceAsync(media, "Pictures", includeSubfolders: false);
        await WaitAsync(() => host.ListFolderSources().Single().Counts.Hashed == 4);
        FolderSourceActivity activity = host.FolderSourceActivity;
        Assert.Equal((4, 4), (activity.Files, activity.Hashed));
        Assert.Equal(Texts.T("folderSource.status.hashedAll"), FolderSourcesPanel.StatusLine(activity, paused: false));
    }

    [Fact]
    public void EveryErrorHasItsOwnSentenceAndNoneNamesAPath()
    {
        foreach (FolderSourceError error in Enum.GetValues<FolderSourceError>().Where(error => error != FolderSourceError.None))
        {
            string key = FolderSourceDialog.ErrorKey(error);
            Assert.NotEqual(key, Texts.T(key));
            Assert.DoesNotContain("{0}", Texts.T(key));
        }
        Assert.Equal(Enum.GetValues<FolderSourceError>().Length - 1,
            Enum.GetValues<FolderSourceError>().Where(error => error != FolderSourceError.None).Select(FolderSourceDialog.ErrorKey).Distinct().Count());
        Assert.Equal("Folder: Holiday", Texts.T("folderSource.media.board", "Holiday"));
        Assert.Equal("Reading files in the background: 1,234 of 100,000",
            FolderSourcesPanel.StatusLine(new FolderSourceActivity(null, null, new FolderSourceHashStatus(0, 0, true, false), 100_000, 1_234), false));
        Assert.EndsWith(" · paused",
            FolderSourcesPanel.StatusLine(new FolderSourceActivity(null, null, new FolderSourceHashStatus(0, 0, false, true), 10, 1), true));
    }

    [Fact]
    public void TheSuggestedNameIsTheFolderNameAndFollowsTheFolderThroughTheTextPropertyNotTheDeferredTextChanged()
    {
        string folder = Path.Combine(Path.GetTempPath(), "my photos");
        Assert.Equal("my photos", FolderSourceDialog.SuggestedName(folder));
        Assert.Equal("my photos", FolderSourceDialog.SuggestedName("  " + folder + Path.DirectorySeparatorChar + "  "));
        Assert.Equal(string.Empty, FolderSourceDialog.SuggestedName(null));
        Assert.Equal(FolderSourceCatalog.MaximumNameLength,
            FolderSourceDialog.SuggestedName(Path.Combine(Path.GetTempPath(), new string('a', FolderSourceCatalog.MaximumNameLength + 5))).Length);
        // Wave-3 smoke regression (folder-source): Avalonia posts TextChanged, so the _syncing guard was already cleared when the
        // name field reported the suggestion, which then counted as an own edit. Both fields are observed on the property itself.
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(SourceDirectory(), "../..", "Desktop/Views/Sources/FolderSourceDialog.cs")));
        Assert.DoesNotContain("_name.TextChanged", source);
        Assert.DoesNotContain("_folder.TextChanged", source);
        Assert.Contains("_name.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_syncing)", source);
        Assert.Contains("_folder.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty && !_syncing) { SuggestName();", source);
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(file)!;

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The folder source did not reach the expected state.");
            await Task.Delay(25);
        }
    }
}
