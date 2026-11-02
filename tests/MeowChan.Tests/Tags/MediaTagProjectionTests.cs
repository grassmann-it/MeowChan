using System.Collections;
using MeowChan.Storage;
using MeowChan.Transfer;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

public sealed class MediaTagProjectionTests
{
    private static readonly PrivateMediaTagId Cat = new(new Guid("00000000-0000-0000-0000-000000000001"));
    private static readonly PrivateMediaTagId Dog = new(new Guid("00000000-0000-0000-0000-000000000002"));
    private static readonly ResourceId Thread = new("stub", "g", "123");
    private const string Revision = "att-v1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherRevision = "att-v2:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void TargetRequiresKeyedOccurrenceAndCanonicalRecordedByteIdentity()
    {
        PrivateMediaTagTarget target = Target();
        Assert.Equal(Thread, target.Reference.Resource);
        Assert.Equal("post-1", target.Reference.PostId);
        Assert.Equal(0, target.Reference.Position);
        Assert.Equal(Revision, target.Reference.RevisionKey);
        Assert.Equal("media-1", target.MediaId);
        Assert.Equal("original", target.Variant);
        Assert.Equal(TransferMediaRole.Original, target.Role);
        Assert.Equal(Sha256, target.Sha256);

        foreach ((AttachmentRevisionReference? reference, string? media, string? variant, string? hash) in new[]
        {
            (new AttachmentRevisionReference(Thread, "post-1", 0, null!), "media-1", "original", Sha256),
            (new AttachmentRevisionReference(Thread, "post-1", 0, "att-v1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), "media-1", "original", Sha256),
            (new AttachmentRevisionReference(Thread, "post-1", 0, Revision), "media-1", "original", Sha256.ToUpperInvariant()),
            (new AttachmentRevisionReference(Thread, "post-1", 0, Revision), "media-1", "original", new string('c', 63)),
            (new AttachmentRevisionReference(Thread, "post-1", 0, Revision), "media-1", "original", Sha256 + "c"),
            (new AttachmentRevisionReference(Thread, "post-1", -1, Revision), "media-1", "original", Sha256),
        })
        {
            Assert.False(PrivateMediaTagTarget.TryCreate(reference, media, variant, TransferMediaRole.Original, hash,
                out _, out MediaTagError error));
            Assert.Equal(MediaTagError.InvalidTarget, error);
        }
    }

    [Fact]
    public void SameBytesAtDifferentOccurrencesOrRevisionsRemainDifferentTargets()
    {
        PrivateMediaTagTarget first = Target();
        PrivateMediaTagTarget second = Target(position: 1);
        PrivateMediaTagTarget later = Target(revision: OtherRevision);
        PrivateMediaTagTarget preview = Target(role: TransferMediaRole.Preview);
        MediaTagProjection state = Define(new(), Cat, "keep");
        state = Add(state, Cat, first);
        state = Add(state, Cat, second);
        state = Add(state, Cat, later);
        state = Add(state, Cat, preview);

        MediaTagSnapshot selected = Select(state, new[] { first, second, later, preview });
        Assert.Equal(4, selected.Assignments.Count);
        Assert.Equal(new[] { first, second, later, preview }, selected.Assignments.Select(a => a.Target).ToArray());
        Assert.NotEqual(first, second);
        Assert.NotEqual(first, later);
        Assert.NotEqual(first, preview);
    }

    [Fact]
    public void DefaultOffDoesNotInspectScopeAndSelectedEmptyIsDistinct()
    {
        MediaTagProjection state = Add(Define(new(), Cat, "private-media"), Cat, Target());
        Assert.True(state.TrySelectMedia(new UnreadableScope(), out MediaTagSnapshot? unselected, out MediaTagError error));
        Assert.Equal(MediaTagError.None, error);
        Assert.False(unselected!.IsSelected);
        Assert.Empty(unselected.Scope);
        Assert.Empty(unselected.Definitions);
        Assert.Empty(unselected.Assignments);

        Assert.True(state.TrySelectMedia(Array.Empty<PrivateMediaTagTarget>(), out MediaTagSnapshot? selected,
            out error, selected: true));
        Assert.Equal(MediaTagError.None, error);
        Assert.True(selected!.IsSelected);
        Assert.Empty(selected.Scope);
        Assert.Empty(selected.Definitions);
        Assert.Empty(selected.Assignments);
    }

    [Fact]
    public void SelectionKeepsOnlyUsedDefinitionsAndDeduplicatesScopeWithoutRetargeting()
    {
        PrivateMediaTagTarget first = Target();
        PrivateMediaTagTarget second = Target(position: 1);
        MediaTagProjection state = Define(Define(new(), Cat, "cat"), Dog, "unused");
        state = Add(state, Cat, first);
        MediaTagSnapshot selected = Select(state, new[] { first, first, second });
        Assert.Equal(new[] { first, second }, selected.Scope.ToArray());
        Assert.Single(selected.Definitions);
        Assert.Equal(Cat, selected.Definitions[0].Id);
        Assert.Single(selected.Assignments);
        Assert.Equal(first, selected.Assignments[0].Target);
    }

    [Fact]
    public void RenameRetainsAssignmentsAndRemovalDoesNotDeleteDefinition()
    {
        PrivateMediaTagTarget target = Target();
        MediaTagProjection state = Add(Define(new(), Cat, "cat"), Cat, target);
        Assert.True(state.TryRename(Cat, Name("CAT"), state.Revision, out MediaTagProjection renamed, out MediaTagError error));
        Assert.Equal(MediaTagError.None, error);
        Assert.Equal(Cat, Select(renamed, new[] { target }).Definitions.Single().Id);
        Assert.Equal("CAT", Select(renamed, new[] { target }).Definitions.Single().Name.Display);
        Assert.Single(Select(renamed, new[] { target }).Assignments);

        Assert.True(renamed.TryRemove(Cat, target, renamed.Revision, out MediaTagProjection removed, out error));
        Assert.Equal(MediaTagError.None, error);
        Assert.Empty(Select(removed, new[] { target }).Assignments);
        Assert.Empty(Select(removed, new[] { target }).Definitions);
        Assert.True(removed.TryDefine(Dog, Name("dog"), removed.Revision, out MediaTagProjection defined, out error));
        Assert.Equal(MediaTagError.None, error);
        Assert.Empty(Select(defined, new[] { target }).Definitions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StaleRevisionIsRejectedBeforeNoOpOrInvalidTarget(int operation)
    {
        MediaTagProjection state = Add(Define(new(), Cat, "cat"), Cat, Target());
        MediaTagProjection result;
        MediaTagError error;
        bool accepted = operation switch
        {
            0 => state.TryDefine(Cat, Name("cat"), 0, out result, out error),
            1 => state.TryRename(Cat, Name("cat"), 0, out result, out error),
            2 => state.TryAdd(Cat, Target(), 0, out result, out error),
            _ => state.TryRemove(Cat, null, 0, out result, out error)
        };
        Assert.False(accepted);
        Assert.Equal(MediaTagError.StaleRevision, error);
        Assert.Same(state, result);
    }

    [Fact]
    public void NameCollisionsAndUnknownIdsDoNotChangeState()
    {
        MediaTagProjection state = Define(Define(new(), Cat, "café"), Dog, "dog");
        Assert.False(state.TryDefine(new(new Guid("00000000-0000-0000-0000-000000000003")), Name("CAFE\u0301"),
            state.Revision, out MediaTagProjection result, out MediaTagError error));
        Assert.Equal(MediaTagError.NameCollision, error);
        Assert.Same(state, result);
        Assert.False(state.TryAdd(new(new Guid("00000000-0000-0000-0000-000000000003")), Target(), state.Revision,
            out result, out error));
        Assert.Equal(MediaTagError.UnknownTag, error);
        Assert.Same(state, result);
    }

    [Fact]
    public void SnapshotsAreDefensiveAndRevisionOverflowRejectsOnlyRealChanges()
    {
        MediaTagProjection state = Add(Define(new(long.MaxValue - 2), Cat, "cat"), Cat, Target());
        Assert.Equal(long.MaxValue, state.Revision);
        Assert.True(state.TryAdd(Cat, Target(), state.Revision, out MediaTagProjection same, out _));
        Assert.Same(state, same);
        Assert.False(state.TryAdd(Cat, Target(position: 1), state.Revision, out MediaTagProjection failed, out MediaTagError error));
        Assert.Equal(MediaTagError.RevisionOverflow, error);
        Assert.Same(state, failed);

        MediaTagSnapshot snapshot = Select(state, new[] { Target() });
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateMediaTagAssignment>)snapshot.Assignments).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateMediaTagTarget>)snapshot.Scope).Clear());
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaTagProjection(-1));
    }

    private static PrivateMediaTagTarget Target(int position = 0, string revision = Revision,
        ResourceId? resource = null, TransferMediaRole role = TransferMediaRole.Original,
        string post = "post-1", string media = "media-1", string variant = "original", string hash = Sha256)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(
            new AttachmentRevisionReference(resource ?? Thread, post, position, revision),
            media, variant, role, hash, out PrivateMediaTagTarget? target, out MediaTagError error));
        Assert.Equal(MediaTagError.None, error);
        return target!;
    }

    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out PrivateTagName? name, out _));
        return name!;
    }

    private static MediaTagProjection Define(MediaTagProjection state, PrivateMediaTagId id, string name)
    {
        Assert.True(state.TryDefine(id, Name(name), state.Revision, out MediaTagProjection result, out MediaTagError error));
        Assert.Equal(MediaTagError.None, error);
        return result;
    }

    private static MediaTagProjection Add(MediaTagProjection state, PrivateMediaTagId id, PrivateMediaTagTarget target)
    {
        Assert.True(state.TryAdd(id, target, state.Revision, out MediaTagProjection result, out MediaTagError error));
        Assert.Equal(MediaTagError.None, error);
        return result;
    }

    private static MediaTagSnapshot Select(MediaTagProjection state, IReadOnlyCollection<PrivateMediaTagTarget> scope)
    {
        Assert.True(state.TrySelectMedia(scope, out MediaTagSnapshot? snapshot, out MediaTagError error, selected: true));
        Assert.Equal(MediaTagError.None, error);
        return snapshot!;
    }

    private sealed class UnreadableScope : IReadOnlyCollection<PrivateMediaTagTarget>
    {
        public int Count => throw new InvalidOperationException("Unselected scope must not be inspected.");
        public IEnumerator<PrivateMediaTagTarget> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
