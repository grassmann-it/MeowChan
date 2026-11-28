using System.Security.Cryptography;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BooruOccurrenceTagTests
{
    private static readonly BooruPostKey North = new("danbooru", "north", "42");
    private static readonly BooruPostKey South = new("danbooru", "south", "42");
    private static readonly BooruPostKey Missing = new("danbooru", "missing", "42");
    private static readonly DateTimeOffset Captured = DateTimeOffset.UnixEpoch;

    [Fact]
    public void OfflineSourceTagsAndCopyKeepThePostInstanceAndCategory() => WithOccurrences(async (window, session, _) =>
    {
        (TextBlock north, Button copyNorth) = SourceRow(window, North);
        (TextBlock south, Button copySouth) = SourceRow(window, South);
        (TextBlock missing, Button copyMissing) = SourceRow(window, Missing);
        await BooruTestSupport.WaitAsync(() => north.IsEffectivelyVisible && south.IsEffectivelyVisible, "the two saved originals' source tags");
        Assert.Contains("Danbooru · north · Post #42", Peer(north).GetName());
        Assert.Contains("Artist · northern_artist", Peer(north).GetName());
        Assert.Contains("General · north_tag", Peer(north).GetName());
        Assert.Contains("General · north_tag", Peer(OccurrenceRow(window, North)).GetName());
        Assert.DoesNotContain("south_tag", north.Text);
        Assert.Contains("Danbooru · south · Post #42", Peer(south).GetName());
        Assert.Contains("Character · south_tag", Peer(south).GetName());
        Assert.DoesNotContain("north_tag", south.Text);
        Assert.False(missing.IsEffectivelyVisible);
        Assert.Null(missing.Text);
        Assert.False(copyMissing.IsEnabled);

        int reads = session.BooruCatalogReads;
        await Task.Delay(TimeSpan.FromMilliseconds(1300));
        Assert.Equal(reads, session.BooruCatalogReads);
        await CopyAsync(window, copyNorth, "northern_artist north_tag", count: 2);
        await CopyAsync(window, copySouth, "south_tag", count: 1);
        Assert.Equal(reads + 2, session.BooruCatalogReads);
    });

    [Fact]
    public void ChangedRulesAndMissingBytesRevokeTagsWhileTheWindowStaysOpen() => WithOccurrences(async (window, session, path) =>
    {
        ProfileLease lease = session.ProfileLeaseForTests;
        (TextBlock north, Button copyNorth) = SourceRow(window, North);
        (TextBlock south, Button copySouth) = SourceRow(window, South);
        StackPanel southRow = OccurrenceRow(window, South);
        Button showSouth = Assert.Single(southRow.GetVisualDescendants().OfType<Button>(), button => button.Name == "MediaOccurrenceShow");
        Button showChan = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "MediaOccurrenceShow" && button.IsEnabled);
        await BooruTestSupport.WaitAsync(() => north.IsEffectivelyVisible && south.IsEffectivelyVisible, "the initially admitted tags");
        Assert.False(showSouth.IsEnabled);

        var clipboard = window.Clipboard!;
        await clipboard.SetTextAsync("unchanged");
        Assert.True(copySouth.Focus());
        SaveRules(lease, BooruPostClaimRating.Sensitive);
        Assert.True(copySouth.IsEnabled);
        copySouth.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await BooruTestSupport.WaitAsync(() => !copySouth.IsEnabled, "the new rating ceiling to revoke copy");
        Assert.Null(south.Text);
        Assert.False(south.IsEffectivelyVisible);
        Assert.True(southRow.IsFocused);
        Assert.DoesNotContain("south_tag", Peer(southRow).GetName()!);
        Assert.Equal(Texts.T("booru.media.tags.copyFailed"), CopyStatus(window).Text);
        Assert.Equal("unchanged", await clipboard.TryGetTextAsync());
        Assert.True(north.IsEffectivelyVisible);
        Assert.True(copyNorth.Focus());
        // Exercise the same keyboard traversal as the modeless window.
        window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        window.KeyRelease(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Same(showChan, window.FocusManager?.GetFocusedElement());

        SaveRules(lease, BooruPostClaimRating.Sensitive, [new(North.SourceId, North.InstanceId, "north_tag")]);
        await BooruTestSupport.WaitAsync(() => !north.IsEffectivelyVisible, "the instance rule to remove the tags");
        Assert.Null(north.Text);
        Assert.False(copyNorth.IsEnabled);
        Assert.True(showChan.IsFocused);

        SaveRules(lease, BooruPostClaimRating.Sensitive);
        await BooruTestSupport.WaitAsync(() => north.IsEffectivelyVisible, "the allowed tags to return");
        File.Delete(path);
        Assert.True(copyNorth.IsEnabled);
        copyNorth.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await BooruTestSupport.WaitAsync(() => !north.IsEffectivelyVisible, "the deleted original's tags to disappear");
        Assert.Null(north.Text);
        Assert.False(copyNorth.IsEnabled);
        Assert.Equal("unchanged", await clipboard.TryGetTextAsync());
        Assert.True(window.IsVisible);
    });

    private static void WithOccurrences(Func<Window, DesktopSessionHost, string, Task> check) => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory temp = new();
        using DanbooruStub transport = new() { Offline = true };
        using DanbooruTestBinding binding = new(transport);
        new Config
        {
            DownloadPath = Path.Combine(temp.Path, "media"), FirstRunSetupVersion = 1,
            DownloadsPaused = true, SaveUrlsOnExit = true, AutomaticUpdateChecks = false,
            MinimizeToTray = false, CloseToTray = false, MaxParallelDownloads = 1, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0
        }.SaveConfig(temp.Path);
        Dictionary<string, MediaAssetSnapshot> manifests = new(StringComparer.Ordinal);
        byte[] bytes = DanbooruStub.Png(4, 4, 0xff5182a1, 42);
        MediaAssetReference chanAsset = SaveAsset(temp.Path, "chan", "chan-image", bytes, manifests);
        MediaAssetReference northAsset;
        string privateTagFile = Path.Combine(temp.Path, PrivateTagStore.FileName);
        using (ProfileLease lease = ProfileLease.Acquire(temp.Path))
        {
            lease.Admission.CompleteStartup();
            BooruCatalogStore catalog = new(temp.Path, lease);
            northAsset = SavePost(North, BooruPostClaimRating.General,
                [new("northern_artist", BooruPostClaimTagCategory.Artist), new("north_tag", BooruPostClaimTagCategory.General)]);
            SavePost(South, BooruPostClaimRating.Explicit, [new("south_tag", BooruPostClaimTagCategory.Character)]);
            MediaAssetReference missingAsset = SavePost(Missing, BooruPostClaimRating.General, [new("missing_tag", BooruPostClaimTagCategory.Meta)]);
            File.Delete(missingAsset.Path);
            new PrivateTagStore(temp.Path, lease).Define("private_fixture");
            SaveRules(lease, BooruPostClaimRating.Explicit);

            MediaAssetReference SavePost(BooruPostKey key, BooruPostClaimRating rating, IReadOnlyList<BooruPostClaimTag> tags)
            {
                MediaAssetReference asset = SaveAsset(temp.Path, key.InstanceId, BooruMediaKey.Original(key).StorageId, bytes, manifests);
                catalog.UpsertPost(new(key.SourceId, key.PostId, BooruPostClaimPresence.Rating | BooruPostClaimPresence.Tags,
                    Rating: rating, Tags: tags, InstanceId: key.InstanceId), Captured);
                using ProfileAdmissionToken writer = lease.Admission.EnterOrdinaryWriter();
                catalog.LinkLocalAssetAdmitted(key, asset.AssetId, asset.Path, writer);
                return asset;
            }
        }

        byte[] privateBefore = await File.ReadAllBytesAsync(privateTagFile);
        DateTime tagWriteBefore = File.GetLastWriteTimeUtc(privateTagFile);
        await using DesktopSessionHost session = await BooruCompositionHostTests.StartAsync(temp.Path, transport, () => binding.Binding, writeConfig: false);
        Texts.SetLanguage("en");
        MediaLibraryEntry chan = new(new ResourceId("4chan", "g", "123"), "A Chan thread", 1, Captured, Captured, "123", 1,
            Captured, 0, null, Path.GetDirectoryName(chanAsset.Path), chanAsset.AssetId, chanAsset.Sha256, chanAsset.Length,
            chanAsset.MediaIdentifier, chanAsset.Variant, chanAsset.OriginalFileName, SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
        (IReadOnlyList<MediaLibraryEntry> providers, _) = await BooruMediaProjection.EntriesAsync(session.ReadBooruCatalog()!, OpenManifest);
        MediaLibraryTile tile = Assert.Single(MediaLibraryGrouping.Build([chan, .. providers], [chan], MediaLibrarySort.ByThread, includeMissing: false));
        int privateWrites = 0;
        MediaPage page = new(_ => throw new InvalidOperationException("Opening occurrences must not build the archive."), OpenManifest)
        {
            ReadBooruCatalog = session.ReadBooruCatalog,
            ReadContentProfile = session.ReadLocalMediaContentProfile,
            OccurrenceProfileDirectory = session.ProfileDirectory,
            ShowInThread = _ => Task.CompletedTask,
            TagServices = new(session.GetPublishedPrivateTagSnapshot,
                (_, _, _, _) => { privateWrites++; throw new InvalidOperationException("Unexpected private tag definition."); },
                (_, _, _, _, _) => { privateWrites++; throw new InvalidOperationException("Unexpected private tag assignment."); })
        };
        try
        {
            page.ShowOccurrences(tile);
            Window window = Assert.IsType<MediaOccurrencesWindow>(page.OccurrencesWindow);
            window.UpdateLayout();
            await check(window, session, northAsset.Path);
            Assert.Empty(transport.Requests);
            Assert.Equal(0, privateWrites);
            Assert.Equal(privateBefore, await File.ReadAllBytesAsync(privateTagFile));
            Assert.Equal(tagWriteBefore, File.GetLastWriteTimeUtc(privateTagFile));
        }
        finally { page.EndLifetime(); }

        Task<MediaAssetSnapshot> OpenManifest(string directory, CancellationToken _) => Task.FromResult(manifests[directory]);
    });

    private static void SaveRules(ProfileLease lease, BooruPostClaimRating rating, IReadOnlyList<BooruContentSourceBlacklist>? sources = null)
    {
        BooruContentProfileStore store = new(lease);
        using ProfileAdmissionToken writer = lease.Admission.EnterOrdinaryWriter();
        store.SaveAdmitted(new(rating, string.Empty, sources ?? []), store.Load().Revision, writer);
    }

    private static MediaAssetReference SaveAsset(string root, string folder, string mediaId, byte[] bytes,
        Dictionary<string, MediaAssetSnapshot> manifests)
    {
        string directory = Path.Combine(root, folder);
        Directory.CreateDirectory(directory);
        const string name = "original.png";
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, bytes);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        MediaAssetReference asset = new("asset-" + folder, mediaId, "original", name, path, bytes.Length, hash,
            "https://offline.invalid/" + name, name, IsCurrent: true);
        manifests.Add(directory, new(directory, MediaManifestStatus.Available, 1, "fixture", [asset]));
        return asset;
    }

    private static (TextBlock, Button) SourceRow(Window window, BooruPostKey key)
    {
        StackPanel row = OccurrenceRow(window, key);
        TextBlock tags = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), control => AutomationProperties.GetAutomationId(control) == "MediaOccurrenceSourceTags");
        Button copy = Assert.Single(row.GetVisualDescendants().OfType<Button>(), control => AutomationProperties.GetAutomationId(control) == "MediaOccurrenceCopySourceTags");
        return (tags, copy);
    }

    private static StackPanel OccurrenceRow(Window window, BooruPostKey key) =>
        Assert.Single(window.GetVisualDescendants().OfType<StackPanel>(), row => Peer(row).GetAutomationId() == "MediaOccurrence"
            && Peer(row).GetName()?.StartsWith($"Danbooru · {key.InstanceId} · Post #{key.PostId}" + Environment.NewLine, StringComparison.Ordinal) == true);

    private static TextBlock CopyStatus(Window window) => SidePanelDesignTests.ById<TextBlock>(window, "MediaOccurrenceCopyStatus");

    private static async Task CopyAsync(Window window, Button button, string expected, int count)
    {
        Assert.True(button.IsEnabled);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var clipboard = window.Clipboard!;
        await BooruCompositionHostTests.WaitAsync(async () => await clipboard.TryGetTextAsync() == expected, "the source tag clipboard text");
        await BooruTestSupport.WaitAsync(() => CopyStatus(window).Text == Texts.Count("booru.media.tags.copied", count), "copy feedback");
    }

    private static AutomationPeer Peer(Control control) => ControlAutomationPeer.CreatePeerForElement(control);
}
