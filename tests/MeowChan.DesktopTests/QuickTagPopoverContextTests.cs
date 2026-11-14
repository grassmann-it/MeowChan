using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.DesktopTests;

// TP3: the context of the medium a quick tag popover opened on. The ranking itself is the gallery panel's adapter (GalleryTagPanelSuggestions, TP2).
public sealed class QuickTagPopoverContextTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    private static string Hex(char value) => new(value, 64);

    // ---- context of a menu origin ----------------------------------------------------------------------------------

    [Fact]
    public void TheContextOfATileCarriesThreadBytesNameTitleAndTheNeighboursButNotBytesForASelection()
    {
        MediaLibraryEntry Make(string thread, char hash) => new(new ResourceId("s", "b", thread), "T" + thread, 1, Day, Day, "1", 1, null, 0,
            "att-v1:" + Hex(hash), "/dir/" + thread, "asset-" + thread, Hex(hash), 1, "m1", "original", "f" + thread + ".png",
            SavedMediaKind.Image, MediaLibraryPresence.Present, false, false);
        MediaLibraryEntry[] shown = { Make("a", 'a'), Make("b", 'b'), Make("c", 'c') };

        TagContext single = QuickTagPopoverContext.ContextOf(shown[1], shown, 1);
        Assert.Equal(shown[1].Thread, single.Thread);
        Assert.Equal(shown[1].Sha256, single.Sha256);
        Assert.Equal("fb.png", single.FileName);
        Assert.Contains("Tb", single.TitleTexts!);
        Assert.Equal(3, single.Neighbours.Count);
        Assert.Equal(1, single.Neighbours.Index);

        TagContext selection = QuickTagPopoverContext.ContextOf(shown[1], shown, 1, selection: true);
        Assert.Null(selection.Sha256);
        Assert.Equal(shown[1].Thread, selection.Thread);
    }

    [Fact]
    public void TheNeighbourhoodIsAFrozenWindowAroundTheIndexWithoutTheSubjectItself()
    {
        int[] list = Enumerable.Range(0, 100).ToArray();
        TagNeighbourhood around = QuickTagPopoverContext.Neighbourhood(list, 50, _ => null, radius: 3);
        Assert.Equal(7, around.Count);
        Assert.Equal(0, QuickTagPopoverContext.Neighbourhood(list, -1, _ => null).Count);
        Assert.Equal(0, QuickTagPopoverContext.Neighbourhood(list, 100, _ => null).Count);
        TagNeighbourhood edge = QuickTagPopoverContext.Neighbourhood(list, 0, _ => null, radius: 3);
        Assert.Equal(4, edge.Count);
    }
}
