using MeowChan.Tags;
using MeowChan.Storage;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

public sealed class PrivateTagCatalogTests
{
    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out var name, out _));
        return name!;
    }

    [Fact]
    public void NormalizedCollisionConflictStaleAndNoOpPreserveInstance()
    {
        TagId first = new(Guid.NewGuid());
        TagId second = new(Guid.NewGuid());
        var empty = new PrivateTagCatalog();
        Assert.True(empty.TryDefine(first, Name(" café "), 0, out var catalog, out _));
        Assert.Empty(empty.Definitions);
        Assert.True(catalog.TryDefine(first, Name("café"), 1, out var same, out _));
        Assert.Same(catalog, same);
        Assert.False(catalog.TryDefine(second, Name("CAFE\u0301"), 1, out same, out var error));
        Assert.Equal(PrivateTagCatalogError.NameCollision, error);
        Assert.Same(catalog, same);
        Assert.False(catalog.TryDefine(first, Name("other"), 1, out same, out error));
        Assert.Equal(PrivateTagCatalogError.DefinitionConflict, error);
        Assert.False(catalog.TryRename(first, Name("café"), 0, out same, out error));
        Assert.Equal(PrivateTagCatalogError.StaleRevision, error);
        Assert.True(catalog.TryRename(first, Name("CAFÉ"), 1, out var renamed, out _));
        Assert.Equal(first, renamed.Definitions[0].Id);
        Assert.Equal("café", catalog.Definitions[0].Name.Display);
    }

    [Fact]
    public void InvalidUnknownOverflowAndRenameCollisionAreNonMutating()
    {
        TagId id = new(Guid.NewGuid());
        var empty = new PrivateTagCatalog();
        Assert.False(empty.TryDefine(default, Name("a"), 0, out _, out var error));
        Assert.Equal(PrivateTagCatalogError.InvalidTagId, error);
        Assert.False(empty.TryDefine(id, null, 0, out _, out error));
        Assert.Equal(PrivateTagCatalogError.InvalidName, error);
        Assert.False(empty.TryRename(id, Name("a"), 0, out _, out error));
        Assert.Equal(PrivateTagCatalogError.UnknownTag, error);
        var fullRevision = new PrivateTagCatalog(long.MaxValue);
        Assert.False(fullRevision.TryDefine(id, Name("a"), long.MaxValue, out var same, out error));
        Assert.Equal(PrivateTagCatalogError.RevisionOverflow, error);
        Assert.Same(fullRevision, same);
        Assert.True(empty.TryDefine(id, Name("a"), 0, out var catalog, out _));
        Assert.True(catalog.TryDefine(new(Guid.NewGuid()), Name("b"), 1, out catalog, out _));
        Assert.False(catalog.TryRename(id, Name("B"), 2, out same, out error));
        Assert.Equal(PrivateTagCatalogError.NameCollision, error);
        Assert.Same(catalog, same);
    }

    [Fact]
    public void OneRenameFeedsBothTypedAssignmentsAndUnassignmentKeepsDefinitions()
    {
        TagId id = new(Guid.NewGuid());
        var catalog = new PrivateTagCatalog();
        Assert.True(catalog.TryDefine(id, Name("shared"), 0, out catalog, out _));
        ResourceId thread = new("stub", "g", "123");
        Assert.True(PrivateMediaTagTarget.TryCreate(
            new AttachmentRevisionReference(thread, "post", 0, "att-v1:" + new string('a', 64)),
            "media", "original", TransferMediaRole.Original, new string('b', 64), out var target, out _));
        var threads = new ThreadTagProjection(catalog);
        var media = new MediaTagProjection(catalog);
        Assert.True(threads.TryAdd(id, thread, 0, out threads, out _));
        Assert.True(media.TryAdd(id, target, 0, out media, out _));
        Assert.True(catalog.TryRename(id, Name("renamed"), 1, out var renamed, out _));
        Assert.True(threads.TryUseCatalog(renamed, 1, 1, out threads, out _));
        Assert.True(media.TryUseCatalog(renamed, 1, 1, out media, out _));
        Assert.True(threads.TrySelectThreads(new[] { thread }, out var ts, out _, selected: true));
        Assert.True(media.TrySelectMedia(new[] { target! }, out var ms, out _, selected: true));
        Assert.Equal("renamed", Assert.Single(ts!.Definitions).Name.Display);
        Assert.Equal("renamed", Assert.Single(ms!.Definitions).Name.Display);
        Assert.True(threads.TryRemove(id, thread, 2, out threads, out _));
        Assert.Same(renamed, threads.Catalog);
        Assert.Same(renamed, media.Catalog);
        Assert.Single(renamed.Definitions);
        Assert.Single(ms.Assignments);
        // An alternative rename from the old revision is not a continuation of renamed.
        Assert.True(catalog.TryRename(id, Name("branch"), 1, out var branch, out _));
        Assert.False(media.TryUseCatalog(branch, 2, 2, out var unchanged, out _));
        Assert.Same(media, unchanged);
    }
}
