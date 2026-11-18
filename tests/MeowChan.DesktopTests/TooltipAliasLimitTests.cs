using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// Tooltips name all active gestures (UX-LEDGER-19 supersedes the former two-gesture cap); the shortcuts window (F1), the menus and the shortcut editor
/// keep listing every gesture. A control that knows its execution context passes it, so a default that gave way to a saved binding there is not named.
/// Plain objects and source scans; no window.
/// </summary>
[Collection("Localization")]
public sealed class TooltipAliasLimitTests : IDisposable
{
    public TooltipAliasLimitTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    public static IEnumerable<object[]> Platforms() => Enum.GetValues<ShortcutPlatform>().Select(platform => new object[] { platform });

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));

    [Theory, MemberData(nameof(Platforms))]
    public void EveryTooltipNamesAllEffectiveGesturesAndKeepsTheOrder(ShortcutPlatform platform)
    {
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
        bool mac = platform == ShortcutPlatform.MacOS;
        ShortcutPlatform shown = mac ? ShortcutPlatform.MacOS : ShortcutPlatform.Windows;
        int withAliases = 0;
        foreach (CommandDefinition command in CommandCatalog.All)
        {
            IReadOnlyList<string> tooltip = CommandCatalog.TooltipGestures(command.Id, mac, null, bindings);
            string[] all = bindings.For(command.Id).Select(gesture => gesture.Display(shown)).Distinct(StringComparer.Ordinal).ToArray();
            Assert.Equal(all.Length, tooltip.Count);
            // The first gestures, in the order of the bindings (the first key stays first).
            Assert.Equal(all, tooltip.ToArray());
            string? text = CommandCatalog.ShortcutTooltipDisplay(command.Id, mac, null, bindings);
            Assert.Equal(tooltip.Count == 0 ? null : string.Join(" / ", tooltip), text);
            if (all.Length > 2) withAliases++;
        }
        // The guard is meaningful: the table still has commands with more than two gestures (zoom in, the tile sizes, find next, the context menu).
        Assert.True(withAliases > 0);
    }

    [Fact]
    public void AMenuAndTheShortcutsWindowStillListEveryGesture()
    {
        // Zoom in has more than two default gestures; all presentation paths must keep them.
        string[] all = CommandCatalog.ShortcutDisplay("gallery.zoom-in")!.Split(" / ");
        Assert.True(all.Length >= 3, string.Join(" / ", all));
        string tip = CommandBindings.Tooltip("gallery.zoom-in", null, null, null);
        string headline = tip.Split('\n')[0];
        string[] shown = headline[(CommandCatalog.Label("gallery.zoom-in") + " · ").Length..].Split(" / ");
        Assert.Equal(all.Length, shown.Length);
        Assert.Equal(all, shown);
        Assert.Equal(CommandCatalog.ShortcutTooltipDisplay("gallery.zoom-in"), headline[(CommandCatalog.Label("gallery.zoom-in") + " · ").Length..]);
        // The editor / help line uses ShortcutDisplay, not the tooltip text.
        Assert.Contains("ShortcutDisplay(id)", Source("Desktop/Views/Shortcuts/ShortcutBindingsEditor.cs"));
    }

    [Fact]
    public void TheFirstLineOfEveryCommandTooltipIsTheLabelAndAllEffectiveKeys()
    {
        foreach (CommandDefinition command in CommandCatalog.All.Where(command => command.IsImplemented))
        {
            string label = CommandCatalog.Label(command.Id);
            string? keys = CommandCatalog.ShortcutTooltipDisplay(command.Id);
            string headline = CommandBindings.TooltipHeadline(command.Id);
            Assert.Equal(keys == null ? label : label + " · " + keys, headline);
        }
    }

    [Theory, MemberData(nameof(Platforms))]
    public void ATooltipAskedWithItsContextDoesNotNameAKeyThatGaveWayThere(ShortcutPlatform platform)
    {
        // A saved archive command on M takes M from "Add favorite" in the archive only: it keeps the key in the gallery and on the Media page.
        EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(
            new ShortcutBindingsSection().Set("archive.refresh", [ShortcutGesture.Parse("M")]), platform);
        bool mac = platform == ShortcutPlatform.MacOS;
        Assert.Empty(CommandCatalog.TooltipGestures("favorites.add", mac, "archive", bindings));
        Assert.Equal(new[] { "M" }, CommandCatalog.TooltipGestures("favorites.add", mac, "gallery", bindings));
        Assert.Equal(new[] { "M" }, CommandCatalog.TooltipGestures("favorites.add", mac, "media", bindings));
        // Without a context nothing is hidden (the catalog's display of the bound gestures).
        Assert.Equal(new[] { "M" }, CommandCatalog.TooltipGestures("favorites.add", mac, null, bindings));
        // The star's tooltip: the toggle first, then M where M still adds.
        Assert.Equal(Texts.T("viewer.favorite.add") + " · F", FavoriteKeys.Tooltip(false, "archive", bindings));
        Assert.Equal(Texts.T("viewer.favorite.add") + " · F / M", FavoriteKeys.Tooltip(false, "gallery", bindings));
        Assert.Equal(Texts.T("viewer.favorite.add") + " · F / M", FavoriteKeys.Tooltip(false, "media", bindings));
    }

    [Fact]
    public void EverySurfaceThatKnowsItsContextPassesItToItsTooltips()
    {
        // The Media tile's star, the gallery's two favourite surfaces and the reader's stars (P3-1).
        Assert.Contains("FavoriteKeys.Tooltip(marked, \"media\")", Source("Desktop/MediaLibraryTileView.cs"));
        Assert.Contains("FavoriteKeys.Tooltip(_favoriteSelected, \"gallery\")", Source("Desktop/GalleryViewer.cs"));
        Assert.Contains("FavoriteKeys.Tooltip(_favoriteSelected, \"gallery\")", Source("Desktop/GalleryViewer.Favorites.cs"));
        Assert.Contains("FavoriteKeys.Tooltip(marked, KeyContext)", Source("Desktop/ArchiveFavoriteButton.cs"));
        // The decision row (stars and remove button) of the gallery's panels and of the quick-tag popover, per host.
        string row = Source("Desktop/Controls/GalleryDecisionRow.cs");
        Assert.Contains("ShortcutTooltipDisplay(id, _keyContext)", row);
        Assert.Contains("ShortcutTooltipDisplay(\"media.rating.clear\", _keyContext)", row);
        Assert.Contains("KeyContext = \"gallery\"", Source("Desktop/GalleryViewer.TagPanel.cs"));
        Assert.Contains("KeyContext = \"gallery\"", Source("Desktop/GalleryViewer.PostPanel.cs"));
        Assert.Contains("KeyContext = options.TooltipKeyContext", Source("Desktop/Controls/QuickTagPopover.cs"));
        Assert.Contains("TooltipKeyContext = \"media\"", Source("Desktop/Views/Media/MediaPage.Selection.cs"));
        Assert.Contains("TooltipKeyContext = \"reader\"", Source("Desktop/Views/Archive/ArchivePage.MediaSelection.cs"));
        string window = Source("Desktop/Shell/MainWindow.QuickTags.cs");
        Assert.Contains("TooltipKeyContext = \"media\"", window);
        Assert.Contains("TooltipKeyContext = \"reader\"", window);
    }
}
