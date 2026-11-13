using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>AP6: one favourite mark everywhere, and the saved-ratings manager lives with the media instead of Diagnostics.</summary>
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
