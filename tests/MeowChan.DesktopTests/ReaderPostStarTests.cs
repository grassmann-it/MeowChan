using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopTests;

/// <summary>
/// The star of a post's file line (Tom's review of 15.11.2026, "kazantip025.jpg (74 KiB) ☆" did nothing, in the embedded and the large reader alike).
/// The cause was the hit surface, not the wiring: ArchiveFavoriteButton had its own style key, so no "ToggleButton.…" style reached it, its fill stayed
/// unset, and the star drawing is no hit target, so a real pointer went through to the post card (a routed Click, as the older smokes raised, still
/// worked). The late-card test uses the real virtualized list in a headless window on an owned dispatcher.
/// The reader-post-star smoke verifies the real pointer with XTest in a private Xvfb.
/// </summary>
[Collection("Localization")]
public sealed class ReaderPostStarTests : IDisposable
{
    public ReaderPostStarTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string Source(string relative)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.sln"))) directory = Path.GetDirectoryName(directory);
        return File.ReadAllText(Path.Combine(directory ?? throw new DirectoryNotFoundException("The repository root was not found."), relative));
    }

    // The setters of the style whose selector starts with (or, exact, is) the given text.
    private static string StyleBody(string styles, string selector, bool exact = false)
    {
        Match match = Regex.Match(styles, "<Style Selector=\"" + Regex.Escape(selector) + (exact ? "" : "[^\"]*") + "\">(.*?)</Style>", RegexOptions.Singleline);
        Assert.True(match.Success, "No style for " + selector);
        return match.Groups[1].Value;
    }

    // A row as ArchivePage.ConfigureArchiveMediaFavorites wires it, with a counter of its writes.
    private sealed class Wired
    {
        internal readonly ArchiveAttachmentRow Row;
        internal bool Favorite;
        internal int Writes;
        internal Wired(string media, bool available = true)
        {
            Row = new ArchiveAttachmentRow(new CapturedAttachment(0, media, OriginalFileName: media + ".jpg"));
            Row.FavoriteAvailable = () => available;
            Row.IsFavorite = () => Favorite;
            Row.ToggleFavoriteAsync = () => { Writes++; Favorite = !Favorite; return Task.CompletedTask; };
        }
    }

    [Fact]
    public void TheStarIsStyledAsAToggleSoItsOwnSurfaceIsTheHitTarget()
    {
        // Style selectors name the exact style key. Read without running the constructor (no control, no thread).
        object star = RuntimeHelpers.GetUninitializedObject(typeof(ArchiveFavoriteButton));
        PropertyInfo key = typeof(ArchiveFavoriteButton).GetProperty("StyleKeyOverride", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(ArchiveFavoriteButton), key.GetMethod!.DeclaringType);
        Assert.Equal(typeof(ToggleButton), key.GetValue(star));
        Assert.Equal("favorite-star", ArchiveFavoriteButton.StarClass);
        Assert.Contains("Classes.Add(StarClass);", Source("Desktop/ArchiveFavoriteButton.cs"));
        // The drawing is no hit target, so the button's fill has to be one: transparent at rest (control and template), Hover under the pointer.
        Assert.Contains("IsHitTestVisible = false", Source("Desktop/Controls/FavoriteStar.cs"));
        string styles = Source("Desktop/WorkspaceStyles.axaml");
        Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\" />", StyleBody(styles, "ToggleButton.favorite-star", exact: true));
        Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\" />",
            StyleBody(styles, "ToggleButton.favorite-star /template/ ContentPresenter#PART_ContentPresenter"));
        Assert.Contains("Workspace.Hover", StyleBody(styles, "ToggleButton.favorite-star:pointerover /template/ ContentPresenter#PART_ContentPresenter"));
        // The archive tiles' corner star is the same button with tile-favorite: now it gets that chip fill (and its quiet opacity) too.
        Assert.Contains("Workspace.ChipSurface", StyleBody(styles, "ToggleButton.tile-favorite", exact: true));
    }

    [Fact]
    public void AnUnavailableStarIsVisiblyOffAndSaysWhy()
    {
        string styles = Source("Desktop/WorkspaceStyles.axaml");
        string off = StyleBody(styles, "ToggleButton.favorite-star:disabled /template/ ContentPresenter#PART_ContentPresenter");
        Assert.Contains("<Setter Property=\"Background\" Value=\"Transparent\" />", off);
        Assert.Contains("Workspace.Muted", off);
        Assert.Contains("<Setter Property=\"Opacity\" Value=\"0.6\" />", off);
        // Declared after the pointer-over fill, so an unavailable star never shows it.
        Assert.True(styles.IndexOf("ToggleButton.favorite-star:disabled", StringComparison.Ordinal)
            > styles.IndexOf("ToggleButton.favorite-star:pointerover", StringComparison.Ordinal));
        // The reason is the tooltip, shown on the disabled star too.
        string button = Source("Desktop/ArchiveFavoriteButton.cs");
        Assert.Contains("ToolTip.SetShowOnDisabled(this, true);", button);
        Assert.Contains(": IsEnabled ? label : HoldText(hold);", button);
        Assert.Equal(Texts.T("archive.notSaved"), ArchiveFavoriteButton.HoldText(FavoriteStarHold.None));
        Assert.Equal("No saved version of this attachment is available.", Texts.T("archive.notSaved"));
    }

    [Fact]
    public void ARecycledCardsStarReadsTheRowItShowsNow()
    {
        // A virtualizing list hands one card another post: the card configures its star from the new row (ArchiveAttachmentView.UpdateLabel on
        // DataContextChanged), and each callback reads that row, never the one before.
        Wired first = new("first"), second = new("second", available: false);
        var a = ArchiveFavoriteButton.Callbacks(first.Row);
        var b = ArchiveFavoriteButton.Callbacks(second.Row);
        Assert.Equal((true, false, (string?)null), ArchiveFavoriteButton.ReadState(a.Available, a.Marked, a.Error));
        Assert.False(ArchiveFavoriteButton.ReadState(b.Available, b.Marked, b.Error).Available);
        a.Toggle();
        Assert.Equal((1, 0), (first.Writes, second.Writes));
        Assert.True(ArchiveFavoriteButton.ReadState(a.Available, a.Marked, a.Error).Marked);
        Assert.False(ArchiveFavoriteButton.ReadState(b.Available, b.Marked, b.Error).Marked);
        string view = Source("Desktop/ArchiveAttachmentView.cs");
        Assert.Contains("DataContextChanged += (_, _) => { _missing = false; _expanded = false; WatchAspect(DataContext as ArchiveAttachmentRow); Reset(); UpdateLabel();", view);
        Assert.Matches(@"private void UpdateLabel\(\)\s*\{\s*ArchiveAttachmentRow\? row = DataContext as ArchiveAttachmentRow;\s*_favorite\.Configure\(row\);", view);
        Assert.Matches(@"internal void RefreshFavorite\(\)\s*\{\s*ArchiveAttachmentRow\? row = DataContext as ArchiveAttachmentRow;\s*_favorite\.Configure\(row\);", view);
    }

    [Fact]
    public void ALateCardIsWiredThroughItsRowNotThroughItsContainer() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory media = new();
        ResourceId thread = new("star-fixture", "reader", "1000");
        const string url = "https://offline.invalid/reader/1000";
        CapturedAttachment first = new(0, "first", Url: url + "/first.jpg", OriginalFileName: "first.jpg");
        CapturedAttachment late = new(0, "late", Url: url + "/late.jpg", OriginalFileName: "late.jpg");
        var assets = new[] { first, late }.Select(attachment => new MediaAssetReference(
            attachment.MediaId!, attachment.MediaId!, attachment.Variant, attachment.OriginalFileName!,
            Path.Combine(media.Path, attachment.OriginalFileName!), 3, new string('a', 64), attachment.Url!,
            attachment.OriginalFileName, IsCurrent: true)).ToArray();
        MediaAssetSnapshot manifest = new(media.Path, MediaManifestStatus.Available, 3, "fixture", assets);
        const int count = 65;
        CapturedPost[] captured = Enumerable.Range(0, count).Select(index => new CapturedPost((1000 + index).ToString(),
            index, PostFieldPresence.All, Html: "Saved post " + index,
            Attachments: index == 0 ? [first] : index == count - 1 ? [late] : [])).ToArray();
        ArchivedThreadView view = new(new ArchiveThreadSummary(thread, url, "Post stars", media.Path,
            ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, count, 1),
            captured, [media.Path], decimalPostOrder: true);
        TaskCompletionSource<MediaAssetSnapshot> inventory = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<GalleryEntry> writes = new();
        ArchivePage page = new(_ => throw new InvalidOperationException("The thread snapshot is already loaded."),
            (_, _) => Task.CompletedTask, _ => { }, (_, token) => inventory.Task.WaitAsync(token))
        {
            IsAvailable = true,
            IsArchiveFavorite = entry => writes.Any(write => write.Key == entry.Key),
            ToggleArchiveFavoriteAsync = entry => { writes.Add(entry); return Task.CompletedTask; }
        };
        Window window = new() { Content = page, Width = 1320, Height = 720 };
        try
        {
            window.Show();
            BooruComponentDispatcher.Wait(page.LoadThreadAsync(thread, preparedThread: view));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow firstRow = Assert.IsType<ArchivePostRow>(posts.Items[0]);
            ArchivePostRow lateRow = Assert.IsType<ArchivePostRow>(posts.Items[count - 1]);
            ArchiveFavoriteButton earlyStar = StarFor(posts, firstRow);
            Assert.False(earlyStar.IsEnabled);
            Assert.Equal(Texts.T("archive.favoriteLoading"), ToolTip.GetTip(earlyStar));
            Assert.Null(posts.ContainerFromItem(lateRow));

            inventory.SetResult(manifest);
            BooruComponentDispatcher.Wait(page.MediaLoadTask);
            Assert.Same(earlyStar, StarFor(posts, firstRow));
            Assert.True(earlyStar.IsEnabled);
            Assert.Null(posts.ContainerFromItem(lateRow));
            ArchiveAttachmentRow lateAttachment = Assert.Single(lateRow.Attachments);
            Assert.True(lateAttachment.FavoriteAvailable?.Invoke());

            posts.ScrollIntoView(lateRow);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            ArchiveFavoriteButton lateStar = StarFor(posts, lateRow);
            Assert.True(lateStar.IsEnabled);
            Assert.False(lateStar.IsChecked);
            lateStar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            GalleryEntry write = Assert.Single(writes);
            Assert.Equal(thread, write.Key.Thread);
            Assert.Equal(assets[1], write.Asset);
            Assert.True(lateStar.IsChecked);
            ArchiveAttachmentRow firstAttachment = Assert.Single(firstRow.Attachments);
            Assert.False(firstAttachment.IsFavorite?.Invoke());

            const string failure = "Favorite write failed";
            page.ToggleArchiveFavoriteAsync = _ => Task.FromException(new InvalidOperationException(failure));
            lateStar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(failure, lateAttachment.FavoriteError?.Invoke());
            Assert.Null(firstAttachment.FavoriteError?.Invoke());
            Assert.Contains(failure, Assert.IsType<string>(ToolTip.GetTip(lateStar)));

            // A render behind Saved media must wire the new rows even while Posts is hidden.
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            tabs.SelectedIndex = 1;
            Texts.SetLanguage("de");
            BooruComponentDispatcher.Wait(page.ReaderLocalizationTask);
            Assert.Equal(1, tabs.SelectedIndex);
            ArchivePostRow rebuilt = Assert.IsType<ArchivePostRow>(posts.Items[count - 1]);
            Assert.NotSame(lateRow, rebuilt);
            Assert.Null(posts.ContainerFromItem(rebuilt));
            Assert.True(Assert.Single(rebuilt.Attachments).IsFavorite?.Invoke());
        }
        finally { page.EndLifetime(); window.Close(); BooruComponentDispatcher.Wait(page.MediaLoadTask); }
    });

    private static ArchiveFavoriteButton StarFor(ListBox posts, ArchivePostRow row) => Assert.Single(
        Assert.IsAssignableFrom<Control>(posts.ContainerFromItem(row)).GetVisualDescendants().OfType<ArchiveFavoriteButton>());

    [Fact]
    public void TheReadersThumbnailDrawsNoStarBecauseTheFileLinesStarIsTheOneAndStaysTheToggle()
    {
        // Tom, 16.11.2026: each post with media showed two stars, the file line's toggle and a round badge over the thumbnail. The badge is gone
        // from the post attachment (inline and large reader alike); the file line's star is untouched and still the favourite's toggle.
        Wired favourite = new("fav") { Favorite = true };
        Assert.False(ArchiveMediaBadges.ForAttachment(favourite.Row).Favorite);
        ArchiveAttachmentRow large = new(new CapturedAttachment(0, "big", OriginalFileName: "big.jpg")) { LargePreview = true, IsFavorite = () => true, FavoriteAvailable = () => true };
        Assert.False(ArchiveMediaBadges.ForAttachment(large).Favorite);
        // The star on the file line is still wired to the row and still toggles it (the keys M/U/F go through the same row delegates).
        var star = ArchiveFavoriteButton.Callbacks(favourite.Row);
        Assert.Equal((true, true, (string?)null), ArchiveFavoriteButton.ReadState(star.Available, star.Marked, star.Error));
        star.Toggle();
        Assert.Equal((1, false), (favourite.Writes, favourite.Favorite));
        star.Toggle();
        Assert.Equal((2, true), (favourite.Writes, favourite.Favorite));
        // Its place (the file line, right of the name, shown in reader posts), its hit area and its accessible name are as before.
        string view = Source("Desktop/ArchiveAttachmentView.cs");
        Assert.Contains("fileLine.Children.Add(_label); fileLine.Children.Add(_favorite);", view);
        Assert.Contains("_favorite.IsVisible = !compact;", view);
        string button = Source("Desktop/ArchiveFavoriteButton.cs");
        Assert.Contains("Padding = new Avalonia.Thickness(8);", button);
        Assert.Contains("AutomationProperties.SetName(this, description);", button);
        Assert.Contains("Texts.T(marked ? \"viewer.favorite.remove\" : \"viewer.favorite.add\")", button);
        // The badges that remain on the other surfaces still draw their favourite (tiles of the Media tab and page).
        Assert.Contains("Favorite: IsFavorite(row.FavoriteAvailable, row.IsFavorite)", Source("Desktop/ArchiveMediaBadges.cs"));
    }
}
