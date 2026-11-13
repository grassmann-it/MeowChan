using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

/// <summary>
/// The own-tag count and the private-tags.json size are write limits from Settings. Reads are bounded only by the
/// fixed ceilings, so lowering a limit never makes an existing file unreadable; refused writes write nothing.
/// </summary>
[Collection(PrivateTagStoreScaleCollection.Name)]
public sealed class PrivateTagLimitsTests
{
    private const string FileName = "private-tags.json";
    private static ResourceId Thread => new("source", "board", "42");

    private static PrivateTagStoreError Rejected(Action action) =>
        Assert.Throws<PrivateTagStoreException>(action).Error;

    private static (string Path, byte[] Primary, byte[]? Backup) Files(string directory)
    {
        string path = Path.Combine(directory, FileName);
        return (path, File.ReadAllBytes(path), File.Exists(path + ".bak") ? File.ReadAllBytes(path + ".bak") : null);
    }

    private static void Unchanged(string directory, (string Path, byte[] Primary, byte[]? Backup) before)
    {
        Assert.Equal(before.Primary, File.ReadAllBytes(before.Path));
        Assert.Equal(before.Backup, File.Exists(before.Path + ".bak") ? File.ReadAllBytes(before.Path + ".bak") : null);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void DefaultsAreTheFormerFixedLimitsAndOutOfRangeValuesAreRefused()
    {
        PrivateTagLimits limits = new();
        Assert.Equal((1024, 32L * 1024 * 1024), (limits.TagLimit, limits.DocumentLimitBytes));
        Assert.Equal((256, 20_000), (PrivateTagLimits.MinimumTagLimit, PrivateTagLimits.MaximumTagLimit));
        Assert.Equal((8L * 1024 * 1024, 128L * 1024 * 1024),
            ((long)PrivateTagLimits.MinimumDocumentLimitBytes, (long)PrivateTagLimits.MaximumDocumentLimitBytes));
        Assert.Equal(PrivateTagStore.MaximumTags, PrivateTagLimits.MaximumTagLimit);
        Assert.Equal(PrivateTagStore.MaximumBytes, PrivateTagLimits.MaximumDocumentLimitBytes);
        foreach (int invalid in new[] { 0, 255, 20_001, int.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => limits.TagLimit = invalid);
        foreach (long invalid in new[] { 0L, 8L * 1024 * 1024 - 1, 128L * 1024 * 1024 + 1 })
            Assert.Throws<ArgumentOutOfRangeException>(() => limits.DocumentLimitBytes = invalid);
        Assert.Equal((1024, 32L * 1024 * 1024), (limits.TagLimit, limits.DocumentLimitBytes));
        limits.TagLimit = 20_000;
        limits.DocumentLimitBytes = 128L * 1024 * 1024;
        Assert.Equal((20_000, 128L * 1024 * 1024), (limits.TagLimit, limits.DocumentLimitBytes));
    }

    [Fact]
    public void ARaisedTagLimitAllowsMoreTagsAndTheDefaultRefusesThemWithoutWriting()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        File.WriteAllBytes(Path.Combine(temp.Path, FileName),
            PrivateTagScaleDocument.Create(new(PrivateTagLimits.DefaultTagLimit, 1, 0, 0, 0)));
        var before = Files(temp.Path);

        Assert.Equal(PrivateTagStoreError.TagLimitReached, Rejected(() => new PrivateTagStore(temp.Path, lease).Define("one more")));
        Unchanged(temp.Path, before);

        PrivateTagLimits limits = new() { TagLimit = PrivateTagLimits.DefaultTagLimit + 2 };
        PrivateTagStore raised = new(temp.Path, lease, limits);
        Assert.True(raised.Define("one more").Changed);
        Assert.True(raised.DefineAndSetMediaSubjects("two more", Array.Empty<MediaTagSubject>()).Changed);
        Assert.Equal(PrivateTagStoreError.TagLimitReached, Rejected(() => raised.Define("three more")));
        // A name that already exists is never a new tag, so it is answered even at the limit.
        Assert.False(raised.Define("ONE MORE").Changed);
        Assert.Equal(PrivateTagLimits.DefaultTagLimit + 2, raised.ReadSnapshot().Tags.Count);

        // The setting applies to the next write of the same store instance.
        limits.TagLimit = PrivateTagLimits.DefaultTagLimit + 3;
        Assert.True(raised.Define("three more").Changed);
    }

    [Fact]
    public void ALimitBelowTheCurrentCountKeepsEveryTagUsableAndOnlyRefusesNewTags()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        const int count = 1500;
        File.WriteAllBytes(Path.Combine(temp.Path, FileName), PrivateTagScaleDocument.Create(new(count, 10, 10, 1, 1)));
        File.Copy(Path.Combine(temp.Path, FileName), Path.Combine(temp.Path, FileName + ".bak"));
        PrivateTagStore store = new(temp.Path, lease); // default limit 1024 < 1500

        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
        Assert.Equal(count, snapshot.Tags.Count);
        var before = Files(temp.Path);
        Assert.Equal(PrivateTagStoreError.TagLimitReached, Rejected(() => store.Define("new tag")));
        Assert.Equal(PrivateTagStoreError.TagLimitReached,
            Rejected(() => store.DefineAndSetMediaSubjects("new tag", Array.Empty<MediaTagSubject>())));
        Unchanged(temp.Path, before);

        TagId first = snapshot.Tags[0].Id, last = snapshot.Tags[^1].Id;
        Assert.True(store.SetThread(last, Thread, true).Changed);
        Assert.True(store.Rename(first, "renamed").Changed);
        Assert.True(store.Delete(snapshot.Tags[1].Id).Changed);
        PrivateTagStoreSnapshot after = store.ReadSnapshot();
        Assert.Equal(count - 1, after.Tags.Count);
        Assert.Equal("renamed", after.Tags[0].Name.Display);
        Assert.Contains(after.TagsOf(Thread), tag => tag.Id == last);
    }

    [Fact]
    public void AFileLargerThanTheDefaultLimitStaysReadableAndOnlyGrowthIsRefused()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        byte[] document = PrivateTagScaleDocument.Create(new(PrivateTagLimits.DefaultTagLimit - 10, 49_000, 50_000, 20_000, 20_000));
        Assert.InRange(document.Length, PrivateTagLimits.DefaultDocumentLimitBytes + 1, PrivateTagStore.MaximumBytes);
        File.WriteAllBytes(Path.Combine(temp.Path, FileName), document);
        Assert.Equal(2, PrivateTagStore.ValidateDocumentBytes(document));
        PrivateTagStore store = new(temp.Path, lease);

        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
        Assert.Equal(50_000, snapshot.MediaAssignments.Count);
        var before = Files(temp.Path);
        Assert.Equal(PrivateTagStoreError.DocumentLimitReached, Rejected(() => store.Define("grows the file")));
        Assert.Equal(PrivateTagStoreError.DocumentLimitReached,
            Rejected(() => store.SetThread(snapshot.Tags[0].Id, Thread, true)));
        Unchanged(temp.Path, before);

        // Removing data shrinks the file and is allowed over the limit, so a user can get back under it.
        Assert.True(store.Delete(snapshot.Tags[^1].Id).Changed);
        Assert.True(new FileInfo(Path.Combine(temp.Path, FileName)).Length < document.Length);
        Assert.True(store.RemoveThreads([new ResourceId("fourchan", "g", "100000000")]).Changed);

        store.Limits.DocumentLimitBytes = PrivateTagLimits.MaximumDocumentLimitBytes;
        Assert.True(store.Define("grows the file").Changed);
    }

    [Fact]
    public void ALoweredFileLimitRefusesGrowthWithAClearErrorAndWritesNothing()
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        byte[] document = PrivateTagScaleDocument.Create(new(100, 30_000, 20_000, 0, 0));
        Assert.InRange(document.Length, PrivateTagLimits.MinimumDocumentLimitBytes, PrivateTagLimits.DefaultDocumentLimitBytes);
        string path = Path.Combine(temp.Path, FileName);
        File.WriteAllBytes(path, document);
        File.WriteAllBytes(path + ".bak", document);
        // The store writes this document back byte for byte; a limit just above it leaves no room for a new entry.
        PrivateTagLimits limits = new() { DocumentLimitBytes = document.Length + 40 };
        PrivateTagStore store = new(temp.Path, lease, limits);
        PrivateTagStoreSnapshot snapshot = store.ReadSnapshot();
        var before = Files(temp.Path);

        Assert.Equal(PrivateTagStoreError.DocumentLimitReached, Rejected(() => store.Define("x")));
        Assert.Equal(PrivateTagStoreError.DocumentLimitReached, Rejected(() => store.SetThread(snapshot.Tags[0].Id, Thread, true)));
        Unchanged(temp.Path, before);

        Assert.True(store.Rename(snapshot.Tags[0].Id, "c").Changed); // shorter: allowed
        Assert.True(new FileInfo(path).Length < document.Length);
        limits.DocumentLimitBytes = document.Length + 200;
        Assert.True(store.Define("x").Changed);
        Assert.True(new FileInfo(path).Length <= limits.DocumentLimitBytes);
    }
}
