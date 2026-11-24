using Avalonia;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Interfaces;
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
    public void TheStarIsStyledAsAToggleSoItsOwnSurfaceIsTheHitTarget() => WithAttachment(new Wired("first"), (window, _, star) =>
    {
        Point at = star.TranslatePoint(new Point(star.Bounds.Width / 2, star.Bounds.Height / 2), window)!.Value;
        Visual hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(at));
        Assert.Contains(star, hit.GetSelfAndVisualAncestors());
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Assert.True(star.IsChecked);
    });

    [Fact]
    public void AnUnavailableStarIsVisiblyOffAndSaysWhy() => WithAttachment(new Wired("unavailable", available: false), (_, _, star) =>
    {
        Assert.True(star.IsEffectivelyVisible);
        Assert.False(star.IsEnabled);
        Assert.False(star.IsChecked);
        Assert.True(ToolTip.GetShowOnDisabled(star));
        Assert.Equal(Texts.T("archive.notSaved"), ToolTip.GetTip(star));
        Assert.Equal(Texts.T("archive.notSaved"), AutomationProperties.GetName(star));
    });

    [Fact]
    public void ARecycledCardsStarReadsTheRowItShowsNow()
    {
        Wired first = new("first"), second = new("second") { Favorite = true };
        WithAttachment(first, (_, view, star) =>
        {
            Assert.False(star.IsChecked);
            view.DataContext = second.Row;
            Assert.True(star.IsChecked);
            star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(star.IsChecked);
            Assert.Equal((0, 1), (first.Writes, second.Writes));
            Assert.False(second.Favorite);
        });
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
            ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
            ArchivePostRow firstRow = Assert.IsType<ArchivePostRow>(posts.Items[0]);
            ArchivePostRow lateRow = Assert.IsType<ArchivePostRow>(posts.Items[count - 1]);
            ArchiveFavoriteButton earlyStar = WaitForStar(window, posts, firstRow);
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
            ArchiveFavoriteButton lateStar = WaitForStar(window, posts, lateRow);
            Assert.True(lateStar.IsEnabled);
            Assert.False(lateStar.IsChecked);
            lateStar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            BooruComponentDispatcher.WaitUntil(() => lateStar.IsChecked == true, "the late star to show its saved state");
            GalleryEntry write = Assert.Single(writes);
            Assert.Equal(thread, write.Key.Thread);
            Assert.Equal(assets[1], write.Asset);
            Assert.True(lateStar.IsChecked);
            ArchiveAttachmentRow firstAttachment = Assert.Single(firstRow.Attachments);
            Assert.False(firstAttachment.IsFavorite?.Invoke());

            const string failure = "Favorite write failed";
            page.ToggleArchiveFavoriteAsync = _ => Task.FromException(new InvalidOperationException(failure));
            lateStar.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            BooruComponentDispatcher.WaitUntil(() => lateAttachment.FavoriteError?.Invoke() == failure,
                "the late attachment to report its rejected write");
            Assert.Equal(failure, lateAttachment.FavoriteError?.Invoke());
            Assert.Null(firstAttachment.FavoriteError?.Invoke());
            Assert.Contains(failure, Assert.IsType<string>(ToolTip.GetTip(lateStar)));

            // A render behind Saved media must wire the new rows even while Posts is hidden.
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            tabs.SelectedIndex = 1;
            Texts.SetLanguage("de");
            BooruComponentDispatcher.Wait(page.ReaderLocalizationTask);
            BooruComponentDispatcher.Wait(page.MediaLoadTask);
            Assert.Equal(1, tabs.SelectedIndex);
            ArchivePostRow rebuilt = Assert.IsType<ArchivePostRow>(posts.Items[count - 1]);
            Assert.NotSame(lateRow, rebuilt);
            Assert.Null(posts.ContainerFromItem(rebuilt));
            Assert.True(Assert.Single(rebuilt.Attachments).IsFavorite?.Invoke());
        }
        finally { page.EndLifetime(); window.Close(); BooruComponentDispatcher.Wait(page.MediaLoadTask); }
    });

    private static ArchiveFavoriteButton WaitForStar(Window window, ListBox posts, ArchivePostRow row)
    {
        BooruComponentDispatcher.WaitUntil(() =>
        {
            window.UpdateLayout();
            return posts.ContainerFromItem(row) is Control card
                && card.GetVisualDescendants().OfType<ArchiveFavoriteButton>().Any();
        }, $"the favorite star for post {row.Id} to be realized");
        return StarFor(posts, row);
    }

    private static ArchiveFavoriteButton StarFor(ListBox posts, ArchivePostRow row) => Assert.Single(
        Assert.IsAssignableFrom<Control>(posts.ContainerFromItem(row)).GetVisualDescendants().OfType<ArchiveFavoriteButton>());

    [Fact]
    public void TheReadersThumbnailDrawsNoStarBecauseTheFileLinesStarIsTheOneAndStaysTheToggle()
    {
        Wired favorite = new("favorite") { Favorite = true };
        WithAttachment(favorite, (_, view, star) =>
        {
            Assert.False(view.Badges.State.Favorite);
            Assert.False(view.Badges.IsHitTestVisible);
            Assert.True(star.IsEffectivelyVisible);
            Assert.Equal(Texts.T("viewer.favorite.remove"), AutomationProperties.GetName(star));
            star.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, favorite.Writes);
            Assert.Equal(Texts.T("viewer.favorite.add"), AutomationProperties.GetName(star));
            Assert.False(view.Badges.State.Favorite);
        });
    }

    private static void WithAttachment(Wired wired, Action<Window, ArchiveAttachmentView, ArchiveFavoriteButton> test) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            ArchiveAttachmentView view = new() { DataContext = wired.Row };
            Window window = new() { Content = view, Width = 500, Height = 300 };
            try
            {
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                window.UpdateLayout();
                ArchiveFavoriteButton star = Assert.Single(view.GetVisualDescendants().OfType<ArchiveFavoriteButton>());
                test(window, view, star);
            }
            finally { window.Close(); }
        });
}
