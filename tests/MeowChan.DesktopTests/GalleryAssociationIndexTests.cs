using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan.DesktopTests;

public sealed class GalleryAssociationIndexTests
{
    [Fact]
    public void AssetIdentityIncludesThreadDirectoryAndAssetWhileBindingsUsePostAndPosition()
    {
        GalleryEntry first = Entry("42", "saved-a", "asset-a", "43", 0);
        GalleryEntry repeated = first with { Post = first.Post! with { Html = "duplicate snapshot" } };
        GalleryEntry second = Entry("42", "saved-a", "asset-a", "44", 0);
        GalleryEntry otherPosition = Entry("42", "saved-a", "asset-a", "43", 1);
        GalleryEntry otherThread = Entry("99", "saved-a", "asset-a", "43", 0);
        GalleryEntry otherDirectory = Entry("42", "saved-b", "asset-a", "43", 0);
        GalleryEntry otherAsset = Entry("42", "saved-a", "asset-b", "43", 0);
        GalleryEntry unassigned = first with { Post = null, Attachment = null };
        GalleryAssociationIndex index = new([first, repeated, second, otherPosition, otherThread,
            otherDirectory, otherAsset, unassigned], 17);
        Assert.Equal(17, index.Revision);
        Assert.Equal(new[] { first, second, otherPosition }, index.For(first.Key));
        Assert.Same(otherThread, Assert.Single(index.For(otherThread.Key)));
        Assert.Same(otherDirectory, Assert.Single(index.For(otherDirectory.Key)));
        Assert.Same(otherAsset, Assert.Single(index.For(otherAsset.Key)));
        Assert.Empty(index.For(first.Key with { AssetId = "missing" }));
    }

    [Fact]
    public void RevisionIsFrozenEvenWhenTheShellAppendsOrReplacesEntries()
    {
        GalleryEntry first = Entry("42", "saved", "asset", "43", 0);
        List<GalleryEntry> source = [first];
        GalleryAssociationIndex before = new(source, 1);
        GalleryEntry revised = first with { Post = first.Post! with { Html = "new revision" } };
        source[0] = revised;
        source.Add(Entry("42", "saved", "asset", "44", 0));
        GalleryAssociationIndex after = new(source, 2);
        source.Clear();
        Assert.Same(first, Assert.Single(before.For(first.Key)));
        Assert.Equal(2, after.For(first.Key).Count);
        Assert.Same(revised, after.For(first.Key)[0]);
    }

    private static GalleryEntry Entry(string thread, string directory, string asset, string post, int position) =>
        new(new(new ResourceId("fixture", "g", thread), directory, asset), null!, null!,
            new CapturedPost(post, 0, PostFieldPresence.All), new CapturedAttachment(position, asset), false, false);
}
