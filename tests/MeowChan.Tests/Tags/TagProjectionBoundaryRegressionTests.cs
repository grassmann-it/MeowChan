using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

public sealed class TagProjectionBoundaryRegressionTests
{
    private static readonly PrivateThreadTagId ThreadCat = new(new Guid("00000000-0000-0000-0000-000000000011"));
    private static readonly PrivateMediaTagId MediaCat = new(new Guid("00000000-0000-0000-0000-000000000012"));
    private static readonly PrivateMediaTagId MediaDog = new(new Guid("00000000-0000-0000-0000-000000000013"));
    private static readonly ResourceId Thread = new("stub", "g", "123");
    private const string Revision = "att-v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherRevision = "att-v2:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void ThreadStaleRevisionRejectsAbsentValidMembershipNoOp()
    {
        ThreadTagProjection state = DefineThread(new(), "cat");
        ResourceId absent = new("stub", "g", "124");

        Assert.True(state.TryAdd(ThreadCat, Thread, state.Revision, out ThreadTagProjection advanced, out _));
        Assert.False(advanced.TryRemove(ThreadCat, absent, state.Revision,
            out ThreadTagProjection result, out ThreadTagError error));

        Assert.Equal(ThreadTagError.StaleRevision, error);
        Assert.Same(advanced, result);
    }

    [Fact]
    public void MediaScopedSnapshotExcludesDefinitionsUsedOnlyOutsideScope()
    {
        PrivateMediaTagTarget inside = MediaTarget(position: 0);
        PrivateMediaTagTarget outside = MediaTarget(position: 1);
        MediaTagProjection state = DefineMedia(DefineMedia(new(), MediaCat, "cat"), MediaDog, "dog");
        state = AddMedia(AddMedia(state, MediaCat, inside), MediaDog, outside);

        MediaTagSnapshot snapshot = SelectMedia(state, new[] { inside });

        Assert.Equal(new[] { inside }, snapshot.Scope.ToArray());
        Assert.Equal(new[] { MediaCat }, snapshot.Definitions.Select(definition => definition.Id).ToArray());
        Assert.Equal(new[] { MediaCat }, snapshot.Assignments.Select(assignment => assignment.TagId).ToArray());
        Assert.DoesNotContain(snapshot.Definitions, definition => definition.Id == MediaDog);
    }

    [Fact]
    public void MediaSelectionOfOneSameBytesTargetDoesNotFanOutAcrossIdentityDimensions()
    {
        PrivateMediaTagTarget first = MediaTarget(position: 0);
        PrivateMediaTagTarget otherOccurrence = MediaTarget(position: 1);
        PrivateMediaTagTarget laterRevision = MediaTarget(revision: OtherRevision);
        PrivateMediaTagTarget preview = MediaTarget(role: TransferMediaRole.Preview);
        MediaTagProjection state = DefineMedia(new(), MediaCat, "cat");
        state = AddMedia(AddMedia(AddMedia(AddMedia(state, MediaCat, first), MediaCat, otherOccurrence),
            MediaCat, laterRevision), MediaCat, preview);

        MediaTagSnapshot snapshot = SelectMedia(state, new[] { first });

        Assert.Single(snapshot.Scope);
        Assert.Single(snapshot.Assignments);
        Assert.Equal(first, snapshot.Assignments[0].Target);
        Assert.All(new[] { otherOccurrence, laterRevision, preview }, other =>
            Assert.Equal(first.Sha256, other.Sha256));
    }

    [Fact]
    public void MediaStaleRevisionRejectsAbsentValidMembershipNoOp()
    {
        PrivateMediaTagTarget present = MediaTarget(position: 0);
        PrivateMediaTagTarget absent = MediaTarget(position: 1);
        MediaTagProjection state = DefineMedia(new(), MediaCat, "cat");

        Assert.True(state.TryAdd(MediaCat, present, state.Revision, out MediaTagProjection advanced, out _));
        Assert.False(advanced.TryRemove(MediaCat, absent, state.Revision,
            out MediaTagProjection result, out MediaTagError error));

        Assert.Equal(MediaTagError.StaleRevision, error);
        Assert.Same(advanced, result);
    }

    [Fact]
    public void MediaSnapshotCollectionsAreReadOnlyCopiesIncludingDefinitions()
    {
        PrivateMediaTagTarget target = MediaTarget();
        MediaTagProjection state = AddMedia(DefineMedia(new(), MediaCat, "cat"), MediaCat, target);
        PrivateMediaTagTarget[] inputScope = { target };
        Assert.True(state.TrySelectMedia(inputScope, out MediaTagSnapshot? snapshot, out MediaTagError error,
            selected: true));
        Assert.Equal(MediaTagError.None, error);

        inputScope[0] = MediaTarget(position: 1);
        Assert.Equal(target, Assert.Single(snapshot!.Scope));
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateMediaTagDefinition>)snapshot.Definitions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateMediaTagAssignment>)snapshot.Assignments).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateMediaTagTarget>)snapshot.Scope)[0] = MediaTarget(1));
        Assert.Single(snapshot.Definitions);
        Assert.Single(snapshot.Assignments);
    }

    private static PrivateMediaTagTarget MediaTarget(int position = 0, string revision = Revision,
        TransferMediaRole role = TransferMediaRole.Original)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(
            new AttachmentRevisionReference(Thread, "post-1", position, revision),
            "media-1", "original", role, Sha256,
            out PrivateMediaTagTarget? target, out MediaTagError error));
        Assert.Equal(MediaTagError.None, error);
        return target!;
    }

    private static PrivateTagName Name(string value)
    {
        Assert.True(PrivateTagName.TryCreate(value, out PrivateTagName? name, out _));
        return name!;
    }

    private static ThreadTagProjection DefineThread(ThreadTagProjection state, string name)
    {
        Assert.True(state.TryDefine(ThreadCat, Name(name), state.Revision,
            out ThreadTagProjection result, out ThreadTagError error));
        Assert.Equal(ThreadTagError.None, error);
        return result;
    }

    private static MediaTagProjection DefineMedia(MediaTagProjection state, PrivateMediaTagId id, string name)
    {
        Assert.True(state.TryDefine(id, Name(name), state.Revision,
            out MediaTagProjection result, out MediaTagError error));
        Assert.Equal(MediaTagError.None, error);
        return result;
    }

    private static MediaTagProjection AddMedia(MediaTagProjection state, PrivateMediaTagId id,
        PrivateMediaTagTarget target)
    {
        Assert.True(state.TryAdd(id, target, state.Revision,
            out MediaTagProjection result, out MediaTagError error));
        Assert.Equal(MediaTagError.None, error);
        return result;
    }

    private static MediaTagSnapshot SelectMedia(MediaTagProjection state,
        IReadOnlyCollection<PrivateMediaTagTarget> scope)
    {
        Assert.True(state.TrySelectMedia(scope, out MediaTagSnapshot? snapshot, out MediaTagError error,
            selected: true));
        Assert.Equal(MediaTagError.None, error);
        return snapshot!;
    }
}
