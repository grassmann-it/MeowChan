using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

// In-memory identities and recorded hash strings only; no archive, store or byte verification.
public sealed class TagProjectionSnapshotIsolationTests
{
    private static readonly Guid SharedId = new("00000000-0000-0000-0000-000000000121");
    private static readonly PrivateThreadTagId ThreadCat = new(SharedId);
    private static readonly PrivateThreadTagId ThreadDog = new(new Guid("00000000-0000-0000-0000-000000000122"));
    private static readonly PrivateThreadTagId ThreadNew = new(new Guid("00000000-0000-0000-0000-000000000123"));
    private static readonly PrivateMediaTagId MediaCat = new(SharedId);
    private static readonly PrivateMediaTagId MediaDog = new(new Guid("00000000-0000-0000-0000-000000000122"));
    private static readonly PrivateMediaTagId MediaNew = new(new Guid("00000000-0000-0000-0000-000000000123"));
    private static readonly ResourceId Thread = new("stub", "snapshot", "123");
    private static readonly ResourceId OtherThread = new("stub", "snapshot", "124");
    private const string AttachmentKey = "att-v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RecordedHash = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void ThreadSnapshotConstructorCopiesAllThreeSourceArraysBeforeTheyAreReused()
    {
        PrivateThreadTagDefinition definition = new(ThreadCat, Name("cat"));
        PrivateThreadTagAssignment assignment = new(ThreadCat, Thread);
        ResourceId[] scope = [Thread];
        PrivateThreadTagDefinition[] definitions = [definition];
        PrivateThreadTagAssignment[] assignments = [assignment];
        ThreadTagSnapshot before = new(true, 7, scope, definitions, assignments);

        scope[0] = OtherThread;
        definitions[0] = new(ThreadDog, Name("dog"));
        assignments[0] = new(ThreadDog, OtherThread);
        ThreadTagSnapshot after = new(true, 8, scope, definitions, assignments);

        Assert.True(before.IsSelected);
        Assert.Equal(7L, before.Revision);
        Assert.Equal(Thread, Assert.Single(before.Scope));
        Assert.Same(definition, Assert.Single(before.Definitions));
        Assert.Same(assignment, Assert.Single(before.Assignments));
        Assert.Equal(8L, after.Revision);
        Assert.Equal(OtherThread, Assert.Single(after.Scope));
        Assert.Same(definitions[0], Assert.Single(after.Definitions));
        Assert.Same(assignments[0], Assert.Single(after.Assignments));
    }

    [Fact]
    public void MediaSnapshotConstructorCopiesAllThreeSourceArraysBeforeTheyAreReused()
    {
        PrivateMediaTagTarget first = Target();
        PrivateMediaTagTarget second = Target(post: "post-2");
        PrivateMediaTagDefinition definition = new(MediaCat, Name("cat"));
        PrivateMediaTagAssignment assignment = new(MediaCat, first);
        PrivateMediaTagTarget[] scope = [first];
        PrivateMediaTagDefinition[] definitions = [definition];
        PrivateMediaTagAssignment[] assignments = [assignment];
        MediaTagSnapshot before = new(true, 7, scope, definitions, assignments);

        scope[0] = second;
        definitions[0] = new(MediaDog, Name("dog"));
        assignments[0] = new(MediaDog, second);
        MediaTagSnapshot after = new(true, 8, scope, definitions, assignments);

        Assert.True(before.IsSelected);
        Assert.Equal(7L, before.Revision);
        Assert.Same(first, Assert.Single(before.Scope));
        Assert.Same(definition, Assert.Single(before.Definitions));
        Assert.Same(assignment, Assert.Single(before.Assignments));
        Assert.Equal(8L, after.Revision);
        Assert.Same(second, Assert.Single(after.Scope));
        Assert.Same(definitions[0], Assert.Single(after.Definitions));
        Assert.Same(assignments[0], Assert.Single(after.Assignments));
    }

    [Fact]
    public void SharedUuidAndNameDoNotLinkThreadMediaOrNameSetMembership()
    {
        PrivateTagName name = Name("shared");
        Assert.True(PrivateTagSet.Empty.TryAdd(name, out PrivateTagSet names, out PrivateTagSetError setError));
        Assert.Equal(PrivateTagSetError.None, setError);
        Assert.True(new ThreadTagProjection().TryDefine(ThreadCat, name, 0, out var threads, out var threadError));
        Assert.Equal(ThreadTagError.None, threadError);
        Assert.True(new MediaTagProjection().TryDefine(MediaCat, name, 0, out var media, out var mediaError));
        Assert.Equal(MediaTagError.None, mediaError);
        threads = Add(threads, ThreadCat, Thread);
        PrivateMediaTagTarget target = Target();
        ThreadTagSnapshot threadSnapshot = Select(threads, [Thread]);

        // Defining the same local UUID/name does not inherit a thread's membership onto its media.
        Assert.Empty(Select(media, [target]).Assignments);
        Assert.Empty(Select(media, [target]).Definitions);
        media = Add(media, MediaCat, target);
        MediaTagSnapshot mediaSnapshot = Select(media, [target]);
        Assert.True(names.TryRemove(name, out var emptyNames, out setError));
        Assert.Equal(PrivateTagSetError.None, setError);
        Assert.Empty(emptyNames.Items);
        Assert.Same(name, Assert.Single(names.Items));
        Assert.True(threads.TryRemove(ThreadCat, Thread, threads.Revision, out var removed, out threadError));
        Assert.Equal(ThreadTagError.None, threadError);

        Assert.Empty(Select(removed, [Thread]).Assignments);
        Assert.Equal(new PrivateThreadTagAssignment(ThreadCat, Thread), Assert.Single(threadSnapshot.Assignments));
        Assert.Equal(new PrivateMediaTagAssignment(MediaCat, target), Assert.Single(Select(media, [target]).Assignments));
        Assert.Same(name, Assert.Single(mediaSnapshot.Definitions).Name);
        Assert.Same(name, Assert.Single(threadSnapshot.Definitions).Name);
    }

    [Fact]
    public void ThreadScopedRenameChecksHiddenCollisionAndReusesReleasedNameWithoutRebindingOldId()
    {
        ThreadTagProjection original = Define(Define(new(), ThreadCat, "cat"), ThreadDog, "café");
        original = Add(Add(original, ThreadCat, Thread), ThreadDog, OtherThread);
        ThreadTagSnapshot before = Select(original, [Thread]);
        ThreadTagSnapshot outside = Select(original, [OtherThread]);
        Assert.Equal(ThreadCat, Assert.Single(before.Definitions).Id);

        Assert.False(original.TryRename(ThreadCat, Name("CAFE\u0301"), original.Revision, out var rejected, out var error));
        Assert.Equal(ThreadTagError.NameCollision, error);
        Assert.Same(original, rejected);
        Assert.True(original.TryRename(ThreadCat, Name("renamed"), original.Revision, out var renamed, out error));
        Assert.Equal(ThreadTagError.None, error);
        ThreadTagProjection reused = Add(Define(renamed, ThreadNew, "cat"), ThreadNew, OtherThread);
        Assert.True(reused.TryRemove(ThreadCat, Thread, reused.Revision, out var removed, out error));
        Assert.Equal(ThreadTagError.None, error);

        ThreadTagSnapshot insideAfter = Select(reused, [Thread]);
        ThreadTagSnapshot outsideAfter = Select(reused, [OtherThread]);
        Assert.Equal(ThreadCat, Assert.Single(insideAfter.Definitions).Id);
        Assert.Equal("renamed", Assert.Single(insideAfter.Definitions).Name.Display);
        Assert.Equal(before.Assignments.ToArray(), insideAfter.Assignments.ToArray());
        Assert.Equal(new[] { ThreadDog, ThreadNew }, outsideAfter.Definitions.Select(item => item.Id).ToArray());
        Assert.Equal(new[] { "café", "cat" }, outsideAfter.Definitions.Select(item => item.Name.Display).ToArray());
        Assert.Equal(new[] { ThreadDog, ThreadNew }, outsideAfter.Assignments.Select(item => item.TagId).ToArray());
        Assert.All(outsideAfter.Assignments, item => Assert.Equal(OtherThread, item.Thread));
        Assert.Empty(Select(removed, [Thread]).Definitions);
        Assert.Equal(outsideAfter.Assignments.ToArray(), Select(removed, [OtherThread]).Assignments.ToArray());
        Assert.Equal("cat", Assert.Single(before.Definitions).Name.Display);
        Assert.Equal("café", Assert.Single(outside.Definitions).Name.Display);
        Assert.Single(outside.Assignments);
        Assert.Equal(before.Revision + 3, reused.Revision);
        Assert.Equal(before.Revision, original.Revision);
    }

    [Fact]
    public void MediaScopedRenameChecksHiddenCollisionAndReusesReleasedNameWithoutRebindingOldId()
    {
        PrivateMediaTagTarget first = Target();
        PrivateMediaTagTarget second = Target(post: "post-2");
        MediaTagProjection original = Define(Define(new(), MediaCat, "cat"), MediaDog, "café");
        original = Add(Add(original, MediaCat, first), MediaDog, second);
        MediaTagSnapshot before = Select(original, [first]);
        MediaTagSnapshot outside = Select(original, [second]);
        Assert.Equal(MediaCat, Assert.Single(before.Definitions).Id);

        Assert.False(original.TryRename(MediaCat, Name("CAFE\u0301"), original.Revision, out var rejected, out var error));
        Assert.Equal(MediaTagError.NameCollision, error);
        Assert.Same(original, rejected);
        Assert.True(original.TryRename(MediaCat, Name("renamed"), original.Revision, out var renamed, out error));
        Assert.Equal(MediaTagError.None, error);
        MediaTagProjection reused = Add(Define(renamed, MediaNew, "cat"), MediaNew, second);
        Assert.True(reused.TryRemove(MediaCat, first, reused.Revision, out var removed, out error));
        Assert.Equal(MediaTagError.None, error);

        MediaTagSnapshot insideAfter = Select(reused, [first]);
        MediaTagSnapshot outsideAfter = Select(reused, [second]);
        Assert.Equal(MediaCat, Assert.Single(insideAfter.Definitions).Id);
        Assert.Equal("renamed", Assert.Single(insideAfter.Definitions).Name.Display);
        Assert.Equal(before.Assignments.ToArray(), insideAfter.Assignments.ToArray());
        Assert.Equal(new[] { MediaDog, MediaNew }, outsideAfter.Definitions.Select(item => item.Id).ToArray());
        Assert.Equal(new[] { "café", "cat" }, outsideAfter.Definitions.Select(item => item.Name.Display).ToArray());
        Assert.Equal(new[] { MediaDog, MediaNew }, outsideAfter.Assignments.Select(item => item.TagId).ToArray());
        Assert.All(outsideAfter.Assignments, item => Assert.Equal(second, item.Target));
        Assert.Empty(Select(removed, [first]).Definitions);
        Assert.Equal(outsideAfter.Assignments.ToArray(), Select(removed, [second]).Assignments.ToArray());
        Assert.Equal("cat", Assert.Single(before.Definitions).Name.Display);
        Assert.Equal("café", Assert.Single(outside.Definitions).Name.Display);
        Assert.Single(outside.Assignments);
        Assert.Equal(before.Revision + 3, reused.Revision);
        Assert.Equal(before.Revision, original.Revision);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("board")]
    [InlineData("thread")]
    [InlineData("post")]
    [InlineData("media")]
    [InlineData("variant")]
    public void SameRecordedHashDoesNotCoupleRemovalAcrossOtherTargetIdentityFields(string field)
    {
        PrivateMediaTagTarget first = Target();
        PrivateMediaTagTarget other = field switch
        {
            "source" => Target(resource: Thread with { Source = "other-stub" }),
            "board" => Target(resource: Thread with { Board = "other-board" }),
            "thread" => Target(resource: OtherThread),
            "post" => Target(post: "post-2"),
            "media" => Target(media: "media-2"),
            "variant" => Target(variant: "alternate"),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Equal(first.Sha256, other.Sha256);
        Assert.NotEqual(first, other);
        MediaTagProjection state = Add(Add(Define(new(), MediaCat, "cat"), MediaCat, first), MediaCat, other);
        MediaTagSnapshot before = Select(state, [first, other]);

        // Reconstructed value-equal identities must work, not just the original object reference.
        PrivateMediaTagTarget equalFirst = Target();
        Assert.NotSame(first, equalFirst);
        Assert.Equal(first, equalFirst);
        Assert.True(state.TryRemove(MediaCat, equalFirst, state.Revision, out var removed, out var error));
        Assert.Equal(MediaTagError.None, error);
        Assert.Equal(state.Revision + 1, removed.Revision);
        Assert.Empty(Select(removed, [equalFirst]).Assignments);
        Assert.Equal(new PrivateMediaTagAssignment(MediaCat, other), Assert.Single(Select(removed, [other]).Assignments));
        Assert.Equal(new[] { first, other }, before.Assignments.Select(item => item.Target).ToArray());
        Assert.Equal(new[] { first, other }, Select(state, [first, other]).Assignments.Select(item => item.Target).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ThreadNoOpRejectsSnapshotRevisionAfterOutOfScopeRoundTrip(int operation)
    {
        ThreadTagProjection state = Add(Define(new(), ThreadCat, "cat"), ThreadCat, Thread);
        ThreadTagSnapshot before = Select(state, [Thread]);
        var changed = Add(state, ThreadCat, OtherThread);
        Assert.True(changed.TryRemove(ThreadCat, OtherThread, changed.Revision, out var restored, out var removeError));
        Assert.Equal(ThreadTagError.None, removeError);
        ThreadTagSnapshot after = Select(restored, [Thread]);
        Assert.Equal(before.Scope.ToArray(), after.Scope.ToArray());
        Assert.Equal(before.Definitions.ToArray(), after.Definitions.ToArray());
        Assert.Equal(before.Assignments.ToArray(), after.Assignments.ToArray());
        Assert.Equal(before.Revision + 2, after.Revision);

        foreach (long expected in new[] { before.Revision, restored.Revision })
        {
            ThreadTagProjection result;
            ThreadTagError error;
            bool accepted = operation switch
            {
                0 => restored.TryDefine(ThreadCat, Name("cat"), expected, out result, out error),
                1 => restored.TryRename(ThreadCat, Name("cat"), expected, out result, out error),
                2 => restored.TryAdd(ThreadCat, Thread, expected, out result, out error),
                _ => restored.TryRemove(ThreadCat, OtherThread, expected, out result, out error)
            };
            bool current = expected == restored.Revision;
            Assert.Equal(current, accepted);
            Assert.Equal(current ? ThreadTagError.None : ThreadTagError.StaleRevision, error);
            Assert.Same(restored, result);
            Assert.Equal(after.Revision, result.Revision);
        }
        Assert.Equal(before.Assignments.ToArray(), Select(restored, [Thread]).Assignments.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MediaNoOpRejectsSnapshotRevisionAfterOutOfScopeRoundTrip(int operation)
    {
        PrivateMediaTagTarget first = Target();
        PrivateMediaTagTarget other = Target(post: "post-2");
        MediaTagProjection state = Add(Define(new(), MediaCat, "cat"), MediaCat, first);
        MediaTagSnapshot before = Select(state, [first]);
        var changed = Add(state, MediaCat, other);
        Assert.True(changed.TryRemove(MediaCat, other, changed.Revision, out var restored, out var removeError));
        Assert.Equal(MediaTagError.None, removeError);
        MediaTagSnapshot after = Select(restored, [first]);
        Assert.Equal(before.Scope.ToArray(), after.Scope.ToArray());
        Assert.Equal(before.Definitions.ToArray(), after.Definitions.ToArray());
        Assert.Equal(before.Assignments.ToArray(), after.Assignments.ToArray());
        Assert.Equal(before.Revision + 2, after.Revision);

        foreach (long expected in new[] { before.Revision, restored.Revision })
        {
            MediaTagProjection result;
            MediaTagError error;
            bool accepted = operation switch
            {
                0 => restored.TryDefine(MediaCat, Name("cat"), expected, out result, out error),
                1 => restored.TryRename(MediaCat, Name("cat"), expected, out result, out error),
                2 => restored.TryAdd(MediaCat, first, expected, out result, out error),
                _ => restored.TryRemove(MediaCat, other, expected, out result, out error)
            };
            bool current = expected == restored.Revision;
            Assert.Equal(current, accepted);
            Assert.Equal(current ? MediaTagError.None : MediaTagError.StaleRevision, error);
            Assert.Same(restored, result);
            Assert.Equal(after.Revision, result.Revision);
        }
        Assert.Equal(before.Assignments.ToArray(), Select(restored, [first]).Assignments.ToArray());
    }

    private static PrivateMediaTagTarget Target(ResourceId? resource = null, string post = "post-1",
        string media = "media-1", string variant = "original")
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(
            new AttachmentRevisionReference(resource ?? Thread, post, 0, AttachmentKey),
            media, variant, TransferMediaRole.Original, RecordedHash, out var target, out var error));
        Assert.Equal(MediaTagError.None, error);
        return target!;
    }

    private static PrivateTagName Name(string value)
    {
        Assert.True(PrivateTagName.TryCreate(value, out var name, out var error));
        Assert.Equal(TagNameError.None, error);
        return name!;
    }

    private static ThreadTagProjection Define(ThreadTagProjection state, PrivateThreadTagId id, string name)
    {
        Assert.True(state.TryDefine(id, Name(name), state.Revision, out var result, out var error));
        Assert.Equal(ThreadTagError.None, error);
        return result;
    }

    private static MediaTagProjection Define(MediaTagProjection state, PrivateMediaTagId id, string name)
    {
        Assert.True(state.TryDefine(id, Name(name), state.Revision, out var result, out var error));
        Assert.Equal(MediaTagError.None, error);
        return result;
    }

    private static ThreadTagProjection Add(ThreadTagProjection state, PrivateThreadTagId id, ResourceId thread)
    {
        Assert.True(state.TryAdd(id, thread, state.Revision, out var result, out var error));
        Assert.Equal(ThreadTagError.None, error);
        return result;
    }

    private static MediaTagProjection Add(MediaTagProjection state, PrivateMediaTagId id, PrivateMediaTagTarget target)
    {
        Assert.True(state.TryAdd(id, target, state.Revision, out var result, out var error));
        Assert.Equal(MediaTagError.None, error);
        return result;
    }

    private static ThreadTagSnapshot Select(ThreadTagProjection state, IReadOnlyCollection<ResourceId> scope)
    {
        Assert.True(state.TrySelectThreads(scope, out var snapshot, out var error, selected: true));
        Assert.Equal(ThreadTagError.None, error);
        return snapshot!;
    }

    private static MediaTagSnapshot Select(MediaTagProjection state, IReadOnlyCollection<PrivateMediaTagTarget> scope)
    {
        Assert.True(state.TrySelectMedia(scope, out var snapshot, out var error, selected: true));
        Assert.Equal(MediaTagError.None, error);
        return snapshot!;
    }
}
