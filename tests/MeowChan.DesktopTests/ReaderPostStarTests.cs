using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Controls.Primitives;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The star of a post's file line (Tom's review of 15.11.2026, "kazantip025.jpg (74 KiB) ☆" did nothing, in the embedded and the large reader alike).
/// The cause was the hit surface, not the wiring: ArchiveFavoriteButton had its own style key, so no "ToggleButton.…" style reached it, its fill stayed
/// unset, and the star drawing is no hit target, so a real pointer went through to the post card (a routed Click, as the older smokes raised, still
/// worked). These tests pin the surface rule and the wiring rule for recycled and late cards without a window (Avalonia controls belong to one thread
/// and share icon caches, so this class builds none); the real pointer is the reader-post-star smoke's (XTest in a private Xvfb).
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
    public void ALateCardIsWiredThroughItsRowNotThroughItsContainer()
    {
        // The page wires the rows of the shown page (data), not the realized cards: a card made later (scrolling, a page change, the large reader)
        // shows a row that is already wired, and a row wired after its card exists is picked up by the refresh that follows the wiring.
        ArchiveAttachmentRow late = new(new CapturedAttachment(0, "late", OriginalFileName: "late.jpg"));
        var star = ArchiveFavoriteButton.Callbacks(late);
        Assert.False(ArchiveFavoriteButton.ReadState(star.Available, star.Marked, star.Error).Available);
        bool favorite = false;
        late.FavoriteAvailable = () => true;
        late.IsFavorite = () => favorite;
        late.ToggleFavoriteAsync = () => { favorite = !favorite; return Task.CompletedTask; };
        Assert.True(ArchiveFavoriteButton.ReadState(star.Available, star.Marked, star.Error).Available);
        star.Toggle();
        Assert.True(favorite);
        string commands = Source("Desktop/Views/Archive/ArchivePage.MediaCommands.cs");
        Assert.Contains("if (ArchivePosts.ItemsSource is System.Collections.IEnumerable posts)", commands);
        Assert.Contains("foreach (ArchiveAttachmentRow row in post.Attachments)", commands);
        Assert.Matches(@"_favoriteErrors\.GetValueOrDefault\(FavoriteAssetKey\.From\(entry\.Key\.Thread, entry\.Asset\)\);\s*\}\s*RefreshArchiveFavoriteControls\(\);", commands);
        // Every page the reader shows is followed by the wiring: RenderThread ends in RenderMedia, which wires first.
        string page = Source("Desktop/Views/Archive/ArchivePage.axaml.cs");
        Assert.Matches(@"private void RenderMedia\(\)\s*\{\s*ConfigureArchiveMediaFavorites\(\);", page);
        // (A page that only changed behind the Media tab skips the media list's rebuild but not the wiring.)
        int render = page.IndexOf("private void RenderThread(bool refreshMedia = true)", StringComparison.Ordinal);
        Assert.True(render >= 0 && page.IndexOf("if (refreshMedia) RenderMedia(); else ConfigureArchiveMediaFavorites();", render, StringComparison.Ordinal)
            < page.IndexOf("private readonly record struct PostRowContext", StringComparison.Ordinal));
    }

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
