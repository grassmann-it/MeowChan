using System.Security.Cryptography;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.FolderSources;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

/// <summary>
/// A folder file that changed on disk after it was tagged: the scan gives it a new item (tags belong to the exact hashed version),
/// the hint finds the earlier tagged version, and "Copy tags from the previous version" adds what the new version lacks, only on
/// request, never removes anything from the old version and can be undone. Temp profiles and temp folders only.
/// </summary>
[Collection("Localization")]
public sealed class FolderVersionTests : IDisposable
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 3, 1, 4 };
    private static readonly byte[] PngChanged = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9, 9, 9 };
    private static readonly byte[] PngChangedAgain = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 7 };
    private readonly string _previous = Texts.Language;
    public FolderVersionTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    private sealed class Session : IAsyncDisposable
    {
        internal TempDirectory Temp = new();
        internal DesktopSessionHost Host = null!;
        internal string Media = "";
        internal FolderSourceRoot Root = null!;
        internal string File1 => Path.Combine(Media, "a.png");

        internal static async Task<Session> StartAsync()
        {
            Session session = new();
            string downloads = Path.Combine(session.Temp.Path, "downloads"), profile = Path.Combine(session.Temp.Path, "profile");
            session.Media = Path.Combine(session.Temp.Path, "media");
            Directory.CreateDirectory(session.Media);
            File.WriteAllBytes(session.File1, Png);
            new Config { DownloadPath = downloads, DownloadsPaused = true, AutomaticUpdateChecks = false, FolderSourceBackgroundHashing = false }.SaveConfig(profile);
            session.Host = new(profile, Array.Empty<IImageBoard>());
            await session.Host.StartAsync();
            session.Root = await session.Host.AddFolderSourceAsync(session.Media, "Pictures", includeSubfolders: true);
            await WaitAsync(() => session.Host.ListFolderSources().Single().Counts.Files == 1 && !session.Host.ListFolderSources().Single().Scanning);
            return session;
        }

        internal FolderSourceItem Active() => Host.ReadFolderSources()!.Items.Single(item => item.RelativePath == "a.png" && item.State != FolderSourceItemState.Missing);

        // Hashes the file as a user action and tags it, like the first write of any folder file.
        internal async Task<MediaTagSubject> TagAsync(string tag)
        {
            FolderSourceItem item = Active();
            FolderSourceHashResult hashed = await Host.HashFolderItemAsync(item.Id, FolderSourceHashPriority.UserAction);
            Assert.True(hashed.Outcome is FolderSourceHashOutcome.Hashed or FolderSourceHashOutcome.AlreadyHashed);
            MediaLibrarySnapshot index = await MediaLibraryIndexBuilder.BuildAsync(Host.OpenArchiveReaderAsync, MediaAssetReader.OpenAsync, readFolderSources: Host.ReadFolderSources);
            MediaLibraryEntry entry = index.Entries.Single(candidate => candidate.FolderItemId == FolderSourceIdentity.Format(item.Id));
            GalleryEntry gallery = (await FolderSourceMediaGallery.ResolveAsync(entry, MediaAssetReader.OpenAsync))!;
            MediaTagSubject subject = MediaTagSubjects.Of(gallery)!;
            await Host.DefineAndSetMediaSubjectTagAsync(tag, subject);
            return subject;
        }

        // The file changes on disk (new bytes and a new write time) and the folder is looked through again.
        internal async Task<FolderSourceScanResult> ChangeAsync(byte[] bytes, int secondsLater)
        {
            File.WriteAllBytes(File1, bytes);
            File.SetLastWriteTimeUtc(File1, File.GetLastWriteTimeUtc(File1).AddSeconds(secondsLater));
            return (await Host.RescanFolderSourceAsync(Root.Id))!;
        }

        internal IReadOnlyList<string> TagNames(MediaTagSubject subject) =>
            Host.GetPrivateTagSnapshot().Snapshot!.TagsOf(subject).Select(tag => tag.Name.Display).Order(StringComparer.Ordinal).ToArray();

        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); Temp.Dispose(); }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "The folder source did not reach the expected state.");
            await Task.Delay(25);
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Fact]
    public async Task AChangedTaggedFileIsFoundWithItsEarlierVersionAndTheTagsItLacks()
    {
        await using Session session = await Session.StartAsync();
        FolderSourceItem original = session.Active();
        // Nothing to say while the file is unchanged, even when it is tagged.
        await session.TagAsync("beach");
        Assert.Null(session.Host.FindFolderFileSuccession(original.Id));

        FolderSourceScanResult scan = await session.ChangeAsync(PngChanged, 5);
        Assert.Equal(1, scan.Replaced);
        FolderSourceItem current = session.Active();
        Assert.NotEqual(original.Id, current.Id);
        Assert.Equal(FolderSourceItemState.Seen, current.State);
        // The tag stayed with the old (now missing) version: the new one shows none.
        FolderFileSuccession hint = session.Host.FindFolderFileSuccession(current.Id)!;
        Assert.NotNull(hint);
        Assert.Equal((original.Id, Sha(Png)), (hint.Previous.Id, hint.Previous.Sha256));
        Assert.Equal(current.Id, hint.Current.Id);
        Assert.False(hint.CurrentHashed);
        TagId beach = session.Host.GetPrivateTagSnapshot().Snapshot!.Tags.Single(tag => tag.Name.Display == "beach").Id;
        Assert.Equal(new[] { beach }, hint.MissingOnCurrent);
        Assert.Equal(new[] { beach }, hint.PreviousTags);
        // The old version is not asked about itself, and a missing item has no earlier version to point at.
        Assert.Null(session.Host.FindFolderFileSuccession(original.Id));
        Assert.Null(session.Host.FindFolderFileSuccession(Guid.NewGuid()));
    }

    [Fact]
    public async Task AChangedFileWhoseEarlierVersionHadNoTagsHasNothingToHintAt()
    {
        await using Session session = await Session.StartAsync();
        FolderSourceItem original = session.Active();
        // Hashed but never tagged.
        await session.Host.HashFolderItemAsync(original.Id, FolderSourceHashPriority.UserAction);
        Assert.Equal(1, (await session.ChangeAsync(PngChanged, 5)).Replaced);
        Assert.Null(session.Host.FindFolderFileSuccession(session.Active().Id));
        // And one that was never hashed is simply updated in place: no new item, no earlier version.
        await using Session fresh = await Session.StartAsync();
        FolderSourceItem unhashed = fresh.Active();
        Assert.Equal(1, (await fresh.ChangeAsync(PngChanged, 5)).Changed);
        Assert.Equal(unhashed.Id, fresh.Active().Id);
        Assert.Null(fresh.Host.FindFolderFileSuccession(unhashed.Id));
    }

    [Fact]
    public async Task CopyingTheTagsReadsTheNewFileOnceAddsOnlyWhatIsMissingAndKeepsTheOldVersionsTags()
    {
        await using Session session = await Session.StartAsync();
        MediaTagSubject oldSubject = await session.TagAsync("beach");
        await session.Host.DefineAndSetMediaSubjectTagAsync("holiday", oldSubject);
        await session.ChangeAsync(PngChanged, 5);
        string[] folderBefore = Directory.GetFileSystemEntries(session.Media);
        FolderSourceItem current = session.Active();
        Assert.Null(current.Sha256);

        // The new version already has an own tag; only the two it lacks are added, and the own one stays.
        FolderTagCopyResult none = await session.Host.CopyFolderFileTagsAsync(Guid.NewGuid());
        Assert.Equal(FolderTagCopyOutcome.Unavailable, none.Outcome);
        FolderSourceHashResult hashed = await session.Host.HashFolderItemAsync(current.Id, FolderSourceHashPriority.UserAction);
        Assert.Equal(FolderSourceHashOutcome.Hashed, hashed.Outcome);
        MediaTagSubject newSubject = FolderSourceSuccessions.ObservationOf(session.Host.ReadFolderSources()!.VirtualRoot, session.Root, session.Active()) is { } observation
            ? new MediaTagSubject.Local(observation, null) : throw new InvalidOperationException();
        await session.Host.DefineAndSetMediaSubjectTagAsync("sunset", newSubject);
        Assert.Equal(new[] { "sunset" }, session.TagNames(newSubject));

        FolderTagCopyResult copy = await session.Host.CopyFolderFileTagsAsync(current.Id);
        Assert.Equal(FolderTagCopyOutcome.Copied, copy.Outcome);
        Assert.Equal(2, copy.Copy!.Added.Count);
        Assert.Equal(new[] { "beach", "holiday", "sunset" }, session.TagNames(newSubject));
        // The previous version keeps every tag it had, and nothing in the folder changed.
        Assert.Equal(new[] { "beach", "holiday" }, session.TagNames(oldSubject));
        Assert.Equal(folderBefore, Directory.GetFileSystemEntries(session.Media));
        Assert.Equal(PngChanged, File.ReadAllBytes(session.File1));
        // The new version is now hashed with its own bytes, and the hint is gone because nothing is missing any more.
        Assert.Equal(Sha(PngChanged), session.Active().Sha256);
        Assert.Null(session.Host.FindFolderFileSuccession(current.Id));
        // Asking again changes nothing.
        Assert.Equal(FolderTagCopyOutcome.NothingToCopy, (await session.Host.CopyFolderFileTagsAsync(current.Id)).Outcome);
        Assert.Equal(new[] { "beach", "holiday", "sunset" }, session.TagNames(newSubject));

        // Undo takes away exactly the two that were added; the own tag stays and the hint is back.
        Assert.True(await session.Host.UndoFolderFileTagCopyAsync(copy.Copy));
        Assert.Equal(new[] { "sunset" }, session.TagNames(newSubject));
        Assert.Equal(new[] { "beach", "holiday" }, session.TagNames(oldSubject));
        Assert.NotNull(session.Host.FindFolderFileSuccession(current.Id));
        // Undoing twice is harmless.
        Assert.True(await session.Host.UndoFolderFileTagCopyAsync(copy.Copy));
        Assert.Equal(new[] { "sunset" }, session.TagNames(newSubject));
    }

    [Fact]
    public async Task ACopyIsRefusedAndChangesNothingWhenTheFileChangedAgainOrIsGone()
    {
        await using Session session = await Session.StartAsync();
        MediaTagSubject oldSubject = await session.TagAsync("beach");
        await session.ChangeAsync(PngChanged, 5);
        FolderSourceItem current = session.Active();
        // The bytes change once more after the scan: the catalog row no longer describes them, so nothing is hashed or tagged.
        File.WriteAllBytes(session.File1, PngChangedAgain);
        File.SetLastWriteTimeUtc(session.File1, File.GetLastWriteTimeUtc(session.File1).AddSeconds(30));
        long before = session.Host.GetPrivateTagSnapshot().Snapshot!.Revision;
        FolderTagCopyResult refused = await session.Host.CopyFolderFileTagsAsync(current.Id);
        Assert.Equal((FolderTagCopyOutcome.Unavailable, (FolderTagCopy?)null), (refused.Outcome, refused.Copy));
        Assert.Equal(before, session.Host.GetPrivateTagSnapshot().Snapshot!.Revision);
        Assert.Equal(new[] { "beach" }, session.TagNames(oldSubject));
        // A rescan sees the newest bytes: the unhashed row is only updated (no new version), and the hint still points at the tagged one.
        Assert.Equal(1, (await session.Host.RescanFolderSourceAsync(session.Root.Id))!.Changed);
        Assert.Equal(current.Id, session.Active().Id);
        Assert.Equal(Sha(Png), session.Host.FindFolderFileSuccession(current.Id)!.Previous.Sha256);
        // The file goes away: copying says so and changes nothing.
        File.Delete(session.File1);
        FolderTagCopyResult gone = await session.Host.CopyFolderFileTagsAsync(current.Id);
        Assert.Equal(FolderTagCopyOutcome.Unavailable, gone.Outcome);
        Assert.Equal(before, session.Host.GetPrivateTagSnapshot().Snapshot!.Revision);
    }

    [Fact]
    public async Task TheLatestTaggedVersionIsTheOneOfferedAndTouchedFilesAreHandledToo()
    {
        await using Session session = await Session.StartAsync();
        MediaTagSubject first = await session.TagAsync("beach");
        await session.ChangeAsync(PngChanged, 5);
        MediaTagSubject second = await session.TagAsync("sunset");
        await session.ChangeAsync(PngChangedAgain, 10);
        FolderSourceItem third = session.Active();
        FolderFileSuccession hint = session.Host.FindFolderFileSuccession(third.Id)!;
        // The second version is the latest tagged one: only its tag is offered, not the first one's.
        Assert.Equal(Sha(PngChanged), hint.Previous.Sha256);
        Assert.Equal(new[] { "sunset" }, hint.MissingOnCurrent.Select(tag => session.Host.GetPrivateTagSnapshot().Snapshot!.Find(tag)!.Name.Display));
        Assert.Equal(new[] { "beach" }, session.TagNames(first));
        Assert.Equal(new[] { "sunset" }, session.TagNames(second));
        // The newest file is hashed (no tags), then touched: the same bytes with another write time are another new item, and the
        // latest version that carries tags is still the second one.
        await session.Host.HashFolderItemAsync(third.Id, FolderSourceHashPriority.UserAction);
        Assert.Equal(1, (await session.ChangeAsync(PngChangedAgain, 20)).Replaced);
        FolderSourceItem touched = session.Active();
        Assert.NotEqual(third.Id, touched.Id);
        FolderFileSuccession again = session.Host.FindFolderFileSuccession(touched.Id)!;
        Assert.Equal(Sha(PngChanged), again.Previous.Sha256);
        FolderTagCopyResult copy = await session.Host.CopyFolderFileTagsAsync(touched.Id);
        Assert.Equal(FolderTagCopyOutcome.Copied, copy.Outcome);
        Assert.Equal(Sha(PngChangedAgain), session.Active().Sha256);
        Assert.Equal(new[] { "sunset" }, session.TagNames(copy.Copy!.Subject));
    }

    [Fact]
    public void EveryNewTextExistsInEnglishAndGermanAndNeedsNoCountOrPath()
    {
        foreach (string key in new[] { "folderSource.version.note", "folderSource.version.short", "folderSource.version.copy",
            "folderSource.version.confirm", "folderSource.version.confirmYes", "folderSource.version.confirmNo", "folderSource.version.done",
            "folderSource.version.none", "folderSource.version.undo", "folderSource.version.undone", "folderSource.version.failed",
            "folderSource.version.undoFailed" })
        {
            Assert.NotEqual(key, Texts.T(key));
            Assert.DoesNotContain("{0}", Texts.T(key));
            Texts.SetLanguage("de");
            Assert.NotEqual(key, Texts.T(key));
            Assert.NotEqual(BuiltInCatalog.English[key], Texts.T(key));
            Texts.SetLanguage("en");
        }
        Assert.Contains("Copy tags from the previous version", Texts.T("folderSource.version.copy"));
    }
}
