using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>AP6: one favourite mark everywhere, and the saved-ratings manager lives with the media instead of Diagnostics.</summary>
// Non-parallel: the star test binds Stroke/Fill to the inherited foreground, and Avalonia posts a bound value to Dispatcher.UIThread when
// another xunit worker owns that slot, so the test runs on an owned UI dispatcher (OwnedUiDispatcher, process-wide slot).
[Collection("Localization")]
public sealed class CurationConsistencyTests
{
    [Fact]
    public void TheFavouriteCommandHasExactlyAnOutlineStarAndAFilledStarAndNoTickVariant()
    {
        Assert.Equal("star", CommandCatalog.IconForState("favorites.toggle", "unmarked"));
        Assert.Equal("star-filled", CommandCatalog.IconForState("favorites.toggle", "marked"));
        Assert.Equal("star", CommandCatalog.Get("favorites.toggle").Icon);
    }

    [Theory]
    [InlineData("Desktop/ArchiveFavoriteButton.cs")]
    [InlineData("Desktop/ArchiveThreadFavoriteButton.cs")]
    [InlineData("Desktop/Controls/WorkspaceChips.cs")]
    [InlineData("Desktop/GalleryViewer.cs")]
    public void EveryFavouriteControlDrawsTheSharedStarNotItsOwnGlyph(string path)
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "../..", path)));
        Assert.Contains("FavoriteStar.Create", source);
        Assert.DoesNotContain("✓", source, StringComparison.Ordinal);
        Assert.DoesNotContain("\"★\" : \"☆\"", source, StringComparison.Ordinal);
    }

    // Wave-3 smoke regression (video-contrast): the gold star on the gallery's checked favourite toggle measured 1.5:1 (Light) and
    // 1.7:1 (Dark). The toggle's checked fill carries the state, so its filled star follows the toggle's foreground like the label.
    [Fact]
    public void TheGalleryFavouriteToggleDrawsAnUntintedStarThatFollowsItsForeground()
    {
        string source = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "../..", "Desktop/GalleryViewer.cs")));
        Assert.Contains("content.Icon = FavoriteStar.Create(_favoriteSelected, tinted: false);", source);
        Assert.Contains("Border icon = favorite ? FavoriteStar.Create(_favoriteSelected, tinted: false)", source);
        OwnedUiDispatcher.Run(() =>
        {
            Border marked = FavoriteStar.Create(true, 16, tinted: false);
            Assert.True(FavoriteStar.MarkOf(marked));
            var star = Assert.IsType<Avalonia.Controls.Shapes.Path>(marked.Child);
            var foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.White);
            _ = new ContentControl { Foreground = foreground, Content = marked }; // the star inherits the host's foreground
            Assert.Same(foreground, star.Stroke);
            Assert.Same(foreground, star.Fill);
        });
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void TheSharedStarKnowsItsState()
    {
        Assert.Null(FavoriteStar.MarkOf(null));
        Assert.Null(FavoriteStar.MarkOf(new Border()));
        Assert.Null(FavoriteStar.MarkOf(new Border { Tag = "other" }));
    }

    [Fact]
    public void TheRatingsManagerBelongsToMediaArchiveAndGalleryAndNoLongerToDiagnostics()
    {
        CommandDefinition manage = CommandCatalog.Get("media.rating.manage");
        Assert.True(manage.Scope.HasFlag(CommandScope.Media) && manage.Scope.HasFlag(CommandScope.Archive) && manage.Scope.HasFlag(CommandScope.Gallery));
        Assert.False(manage.Scope.HasFlag(CommandScope.Diagnostics));
        Assert.Equal(new[] { "media", "archive", "gallery" }, manage.ExecutionContexts);
        Assert.Null(manage.Shortcut);
        Assert.DoesNotContain("diagnostics", manage.Contexts);
    }
}
