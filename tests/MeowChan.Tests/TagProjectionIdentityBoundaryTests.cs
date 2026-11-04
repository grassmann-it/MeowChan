using MeowChan.Storage;
using MeowChan.Tags;
using MeowChan.Transfer;

namespace MeowChan.Tests.Tags;

// Synthetic in-process identities only. "Current", "retained" and "missing" below
// describe caller scopes, not an archive lookup or verified bytes.
public sealed class TagProjectionIdentityBoundaryTests
{
    private static readonly ResourceId Thread = new("synthetic-source", "board", "100");
    private static readonly PrivateThreadTagId ThreadTag = new(new Guid("00000000-0000-0000-0000-000000000311"));
    private static readonly PrivateMediaTagId MediaTag = new(new Guid("00000000-0000-0000-0000-000000000312"));
    private static readonly string Key = "att-v1:" + new string('a', 64);
    private static readonly string Hash = new('c', 64);

    [Theory]
    [InlineData("source-case")]
    [InlineData("board-case")]
    [InlineData("thread-case")]
    [InlineData("thread-leading-zero")]
    public void ThreadNearMissRemovalCannotEraseReconstructedExactMembership(string dimension)
    {
        ResourceId exact = Thread with { Thread = "post-A" };
        ResourceId other = dimension switch
        {
            "source-case" => exact with { Source = "SYNTHETIC-SOURCE" },
            "board-case" => exact with { Board = "BOARD" },
            "thread-case" => exact with { Thread = "post-a" },
            "thread-leading-zero" => exact with { Thread = "0post-A" },
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        };
        Assert.True(new ThreadTagProjection().TryDefine(ThreadTag, Name("keep"), 0, out var defined, out var error));
        Assert.Equal(ThreadTagError.None, error);
        Assert.True(defined.TryAdd(ThreadTag, exact, defined.Revision, out var state, out error));
        Assert.Equal(ThreadTagError.None, error);
        Assert.True(state.TryRemove(ThreadTag, other, state.Revision, out var unchanged, out error));
        Assert.Equal(ThreadTagError.None, error);
        Assert.Same(state, unchanged);

        ResourceId reconstructed = new(exact.Source, exact.Board, exact.Thread);
        Assert.NotSame(exact, reconstructed);
        Assert.True(state.TrySelectThreads([other, reconstructed, exact], out var snapshot, out error, selected: true));
        Assert.Equal(ThreadTagError.None, error);
        Assert.Equal(new[] { other, exact }, snapshot!.Scope.ToArray());
        Assert.Equal(new PrivateThreadTagAssignment(ThreadTag, reconstructed), Assert.Single(snapshot.Assignments));
        Assert.Equal(ThreadTag, Assert.Single(snapshot.Definitions).Id);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("board")]
    [InlineData("thread")]
    [InlineData("post")]
    [InlineData("position")]
    [InlineData("revision-digest")]
    [InlineData("revision-scheme")]
    [InlineData("media")]
    [InlineData("variant")]
    [InlineData("role")]
    [InlineData("bytes")]
    public void UnassignedOneFieldNearMissIsNotAnAliasAndCannotRemoveExactMedia(string dimension)
    {
        PrivateMediaTagTarget exact = Target();
        AttachmentRevisionReference reference = exact.Reference;
        reference = dimension switch
        {
            "source" => reference with { Resource = Thread with { Source = "other-source" } },
            "board" => reference with { Resource = Thread with { Board = "other-board" } },
            "thread" => reference with { Resource = Thread with { Thread = "101" } },
            "post" => reference with { PostId = "post-2" },
            "position" => reference with { Position = 1 },
            "revision-digest" => reference with { RevisionKey = "att-v1:" + new string('b', 64) },
            "revision-scheme" => reference with { RevisionKey = "att-v2:" + new string('a', 64) },
            _ => reference
        };
        PrivateMediaTagTarget other = Target(reference,
            media: dimension == "media" ? "media-2" : "media-1",
            variant: dimension == "variant" ? "alternate" : "original",
            role: dimension == "role" ? TransferMediaRole.Preview : TransferMediaRole.Original,
            hash: dimension == "bytes" ? new string('d', 64) : Hash);
        Assert.NotEqual(exact, other);
        MediaTagProjection state = Add(DefinedMedia(), exact);
        MediaTagSnapshot missing = Select(state, other);
        Assert.Equal(other, Assert.Single(missing.Scope));
        Assert.Empty(missing.Assignments);
        Assert.Empty(missing.Definitions);
        Assert.True(missing.IsSelected);
        Assert.True(state.TryRemove(MediaTag, other, state.Revision, out var unchanged, out var error));
        Assert.Equal(MediaTagError.None, error);
        Assert.Same(state, unchanged);

        PrivateMediaTagTarget reconstructed = Target(new AttachmentRevisionReference(
            new ResourceId(Thread.Source, Thread.Board, Thread.Thread), "post-1", 0, Key));
        Assert.NotSame(exact, reconstructed);
        Assert.Equal(exact, reconstructed);
        Assert.Equal(new PrivateMediaTagAssignment(MediaTag, reconstructed),
            Assert.Single(Select(state, reconstructed).Assignments));
        Assert.Equal(MediaTag, Assert.Single(Select(state, reconstructed).Definitions).Id);
    }

    [Fact]
    public void ChangingCurrentScopeNeitherRetargetsNorRetiresTheOldRecordedVersion()
    {
        PrivateMediaTagTarget retained = Target();
        PrivateMediaTagTarget current = Target(retained.Reference with
        {
            RevisionKey = "att-v2:" + new string('b', 64)
        }, hash: new string('d', 64));
        MediaTagProjection state = Add(DefinedMedia(), retained);
        long revision = state.Revision;
        MediaTagSnapshot before = Select(state, retained);
        Assert.Empty(Select(state, current).Assignments);
        Assert.Empty(Select(state, current).Definitions);
        Assert.Empty(Select(state).Assignments); // caller has no resolved target
        Assert.Equal(revision, state.Revision);
        Assert.Equal(retained, Assert.Single(Select(state, current, retained).Assignments).Target);

        MediaTagProjection both = Add(state, current);
        Assert.True(both.TryRemove(MediaTag, current, both.Revision, out var removed, out var error));
        Assert.Equal(MediaTagError.None, error);
        Assert.Equal(retained, Assert.Single(Select(removed, current, retained).Assignments).Target);
        Assert.Equal(before.Assignments.ToArray(), Select(removed, retained).Assignments.ToArray());
        Assert.Equal(revision, before.Revision);
        Assert.Equal(2, Select(both, current, retained).Assignments.Count);
    }

    [Fact]
    public void PreviewOnlyScopeDoesNotSubstituteForMissingOriginalOrInheritItsPrivateName()
    {
        PrivateMediaTagTarget original = Target();
        PrivateMediaTagTarget preview = Target(variant: "preview", role: TransferMediaRole.Preview);
        MediaTagProjection state = Add(DefinedMedia(), original);
        MediaTagSnapshot previewOnly = Select(state, preview);
        Assert.Equal(preview, Assert.Single(previewOnly.Scope));
        Assert.Empty(previewOnly.Assignments);
        Assert.Empty(previewOnly.Definitions);
        MediaTagProjection taggedPreview = Add(state, preview);
        Assert.True(taggedPreview.TryRemove(MediaTag, original, taggedPreview.Revision, out var removed, out var error));
        Assert.Equal(MediaTagError.None, error);
        Assert.Equal(preview, Assert.Single(Select(removed, original, preview).Assignments).Target);
        Assert.Equal("source-like:cat", Assert.Single(Select(removed, preview).Definitions).Name.Display);
        Assert.Empty(Select(removed, original).Definitions);
        Assert.Equal(original, Assert.Single(Select(state, original).Assignments).Target);
    }

    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out var name, out var error));
        Assert.Equal(TagNameError.None, error);
        return name!;
    }

    private static PrivateMediaTagTarget Target(AttachmentRevisionReference? reference = null,
        string media = "media-1", string variant = "original",
        TransferMediaRole role = TransferMediaRole.Original, string? hash = null)
    {
        Assert.True(PrivateMediaTagTarget.TryCreate(reference ?? new(Thread, "post-1", 0, Key),
            media, variant, role, hash ?? Hash, out var target, out var error));
        Assert.Equal(MediaTagError.None, error);
        return target!;
    }

    private static MediaTagProjection DefinedMedia()
    {
        Assert.True(new MediaTagProjection().TryDefine(MediaTag, Name("source-like:cat"), 0,
            out var result, out var error));
        Assert.Equal(MediaTagError.None, error);
        return result;
    }

    private static MediaTagProjection Add(MediaTagProjection state, PrivateMediaTagTarget target)
    {
        Assert.True(state.TryAdd(MediaTag, target, state.Revision, out var result, out var error));
        Assert.Equal(MediaTagError.None, error);
        return result;
    }

    private static MediaTagSnapshot Select(MediaTagProjection state, params PrivateMediaTagTarget[] scope)
    {
        Assert.True(state.TrySelectMedia(scope, out var snapshot, out var error, selected: true));
        Assert.Equal(MediaTagError.None, error);
        return snapshot!;
    }
}
