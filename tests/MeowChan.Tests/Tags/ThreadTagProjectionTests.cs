using System.Collections;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

public sealed class ThreadTagProjectionTests
{
    private static readonly PrivateThreadTagId Cat = new(new Guid("00000000-0000-0000-0000-000000000001"));
    private static readonly PrivateThreadTagId Dog = new(new Guid("00000000-0000-0000-0000-000000000002"));
    private static readonly ResourceId Thread = new("stub", "g", "123");
    private static readonly ResourceId Other = new("other", "g", "123");

    [Fact]
    public void DefaultOffDoesNotEvenInspectScopeAndDoesNotDisclosePrivateValues()
    {
        ThreadTagProjection state = Add(Define(new(), Cat, "private-cat"), Cat, Thread);
        Assert.True(state.TrySelectThreads(new UnreadableScope(), out var snapshot, out var error));
        Assert.Equal(ThreadTagError.None, error);
        Assert.NotNull(snapshot);
        Assert.False(snapshot.IsSelected);
        Assert.Empty(snapshot.Scope);
        Assert.Empty(snapshot.Definitions);
        Assert.Empty(snapshot.Assignments);
    }

    [Fact]
    public void SelectedEmptyIsDifferentFromUnselectedAndOmitsUnusedDefinitions()
    {
        var state = Define(new(), Cat, "cat");
        var selected = Select(state, Array.Empty<ResourceId>());
        Assert.True(selected.IsSelected);
        Assert.Equal(state.Revision, selected.Revision);
        Assert.Empty(selected.Definitions);
        Assert.Empty(selected.Assignments);
        Assert.True(state.TrySelectThreads(null, out var unselected, out _));
        Assert.False(unselected!.IsSelected);
    }

    [Fact]
    public void SelectionFiltersAssignmentsAndDefinitionsWithoutSourceBoardOrCaseConflation()
    {
        var board = Thread with { Board = "other" };
        var sourceCase = Thread with { Source = "STUB" };
        var number = Thread with { Thread = "124" };
        var state = Define(Define(new(), Cat, "cat"), Dog, "dog");
        state = Add(Add(Add(Add(Add(state, Cat, Thread), Dog, Other), Dog, board), Dog, sourceCase), Dog, number);
        var snapshot = Select(state, new[] { Thread, Thread });
        Assert.Equal(Thread, Assert.Single(snapshot.Scope));
        Assert.Equal(Cat, Assert.Single(snapshot.Definitions).Id);
        Assert.Equal(new PrivateThreadTagAssignment(Cat, Thread), Assert.Single(snapshot.Assignments));
        Assert.Equal(5, Select(state, new[] { Thread, Other, board, sourceCase, number }).Assignments.Count);
    }

    [Fact]
    public void RenameRetainsIdentityAssignmentsAndOldSnapshotsIncludingCaseOnlyRename()
    {
        var original = Add(Define(new(), Cat, "cat"), Cat, Thread);
        var before = Select(original, new[] { Thread });
        Assert.True(original.TryRename(Cat, Name("CAT"), original.Revision, out var renamed, out var error));
        Assert.Equal(ThreadTagError.None, error);
        Assert.Equal(original.Revision + 1, renamed.Revision);
        var after = Select(renamed, new[] { Thread });
        Assert.Equal("cat", Assert.Single(before.Definitions).Name.Display);
        Assert.Equal("CAT", Assert.Single(after.Definitions).Name.Display);
        Assert.Equal(Cat, Assert.Single(after.Definitions).Id);
        Assert.Equal(before.Assignments.ToArray(), after.Assignments.ToArray());
        Assert.True(renamed.TryRename(Cat, Name("CAT"), renamed.Revision, out var noOp, out _));
        Assert.Same(renamed, noOp);
    }

    [Fact]
    public void MembershipIsIdempotentAndRemovalNeverDeletesTheDefinition()
    {
        var state = Add(Define(new(), Cat, "cat"), Cat, Thread);
        Assert.True(state.TryAdd(Cat, Thread, state.Revision, out var duplicate, out _));
        Assert.Same(state, duplicate);
        Assert.True(state.TryRemove(Cat, Thread, state.Revision, out var removed, out _));
        Assert.Equal(state.Revision + 1, removed.Revision);
        Assert.Empty(Select(removed, new[] { Thread }).Assignments);
        Assert.True(removed.TryRemove(Cat, Thread, removed.Revision, out var absent, out _));
        Assert.Same(removed, absent);
        Assert.True(removed.TryDefine(Cat, Name("cat"), removed.Revision, out var defined, out _));
        Assert.Same(removed, defined);
        Assert.Single(Select(Add(removed, Cat, Other), new[] { Other }).Definitions);
        Assert.Single(Select(state, new[] { Thread }).Assignments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void StaleRevisionIsRejectedBeforeEveryNoOpOrInvalidInput(int operation)
    {
        var state = Add(Define(new(), Cat, "cat"), Cat, Thread);
        ThreadTagProjection result;
        ThreadTagError error;
        bool accepted = operation switch
        {
            0 => state.TryDefine(Cat, Name("cat"), 0, out result, out error),
            1 => state.TryRename(Cat, Name("cat"), 0, out result, out error),
            2 => state.TryAdd(Cat, Thread, 0, out result, out error),
            _ => state.TryRemove(default, null, 0, out result, out error)
        };
        Assert.False(accepted);
        Assert.Equal(ThreadTagError.StaleRevision, error);
        Assert.Same(state, result);
    }

    [Fact]
    public void DefinitionsNeverSilentlyMergeAndRenameCollisionLeavesStateUnchanged()
    {
        var state = Define(Define(new(), Cat, "café"), Dog, "dog");
        var unknown = new PrivateThreadTagId(new Guid("00000000-0000-0000-0000-000000000003"));
        Assert.False(state.TryDefine(unknown, Name("CAFE\u0301"), state.Revision, out var collision, out var error));
        Assert.Equal(ThreadTagError.NameCollision, error);
        Assert.Same(state, collision);
        Assert.False(state.TryRename(Dog, Name("CAFÉ"), state.Revision, out var renamed, out error));
        Assert.Equal(ThreadTagError.NameCollision, error);
        Assert.Same(state, renamed);
        Assert.False(state.TryDefine(Cat, Name("different"), state.Revision, out var redefined, out error));
        Assert.Equal(ThreadTagError.DefinitionConflict, error);
        Assert.Same(state, redefined);
    }

    [Fact]
    public void InvalidAndUnknownIdsAndNullNamesReturnConstantCodes()
    {
        var state = Define(new(), Cat, "secret");
        Assert.False(state.TryDefine(default, Name("secret"), state.Revision, out var result, out var error));
        Assert.Equal(ThreadTagError.InvalidTagId, error);
        Assert.Same(state, result);
        Assert.False(state.TryDefine(Dog, null, state.Revision, out result, out error));
        Assert.Equal(ThreadTagError.InvalidName, error);
        Assert.False(state.TryRename(Cat, null, state.Revision, out result, out error));
        Assert.Equal(ThreadTagError.InvalidName, error);
        Assert.False(state.TryRename(Dog, Name("secret"), state.Revision, out result, out error));
        Assert.Equal(ThreadTagError.UnknownTag, error);
        Assert.False(state.TryAdd(Dog, Thread, state.Revision, out result, out error));
        Assert.Equal(ThreadTagError.UnknownTag, error);
        Assert.False(state.TryRemove(Dog, Thread, state.Revision, out result, out error));
        Assert.Equal(ThreadTagError.UnknownTag, error);
        Assert.Same(state, result);
    }

    public static IEnumerable<object?[]> InvalidThreads()
    {
        yield return new object?[] { null };
        yield return new object?[] { new ResourceId("stub", "g") };
        yield return new object?[] { new ResourceId("", "g", "123") };
        yield return new object?[] { new ResourceId("stub", " ", "123") };
        yield return new object?[] { new ResourceId("stub", "g", "secret\nvalue") };
        yield return new object?[] { new ResourceId(new string('s', 257), "g", "123") };
    }

    [Theory]
    [MemberData(nameof(InvalidThreads))]
    public void OnlyValidThreadResourceIdsAreAccepted(ResourceId? thread)
    {
        var state = Define(new(), Cat, "cat");
        Assert.False(state.TryAdd(Cat, thread, state.Revision, out var result, out var error));
        Assert.Equal(ThreadTagError.InvalidThread, error);
        Assert.Same(state, result);
        Assert.False(state.TryRemove(Cat, thread, state.Revision, out result, out error));
        Assert.Equal(ThreadTagError.InvalidThread, error);
        Assert.False(state.TrySelectThreads(new[] { thread! }, out var snapshot, out error, selected: true));
        Assert.Equal(ThreadTagError.InvalidThread, error);
        Assert.Null(snapshot);
    }

    [Fact]
    public void SnapshotCollectionsAreDefensiveReadOnlyCopies()
    {
        var state = Add(Define(new(), Cat, "cat"), Cat, Thread);
        ResourceId[] scope = { Thread };
        var snapshot = Select(state, scope);
        scope[0] = Other;
        Assert.Equal(Thread, Assert.Single(snapshot.Scope));
        Assert.Throws<NotSupportedException>(() => ((IList<ResourceId>)snapshot.Scope)[0] = Other);
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateThreadTagDefinition>)snapshot.Definitions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<PrivateThreadTagAssignment>)snapshot.Assignments).Clear());
        var removed = state.TryRemove(Cat, Thread, state.Revision, out var next, out _);
        Assert.True(removed);
        Assert.Single(snapshot.Assignments);
        Assert.Empty(Select(next, new[] { Thread }).Assignments);
    }

    [Fact]
    public void RevisionOverflowRejectsRealChangesButAllowsNoOps()
    {
        var state = Add(Define(new(long.MaxValue - 2), Cat, "cat"), Cat, Thread);
        Assert.Equal(long.MaxValue, state.Revision);
        Assert.True(state.TryAdd(Cat, Thread, state.Revision, out var noOp, out _));
        Assert.Same(state, noOp);
        Assert.True(state.TryRemove(Cat, Other, state.Revision, out noOp, out _));
        Assert.Same(state, noOp);
        Assert.True(state.TryDefine(Cat, Name("cat"), state.Revision, out noOp, out _));
        Assert.Same(state, noOp);
        Assert.True(state.TryRename(Cat, Name("cat"), state.Revision, out noOp, out _));
        Assert.Same(state, noOp);
        Assert.False(state.TryDefine(Dog, Name("dog"), state.Revision, out var failed, out var error));
        Assert.Equal(ThreadTagError.RevisionOverflow, error);
        Assert.Same(state, failed);
        Assert.False(state.TryRename(Cat, Name("CAT"), state.Revision, out failed, out error));
        Assert.Equal(ThreadTagError.RevisionOverflow, error);
        Assert.False(state.TryAdd(Cat, Other, state.Revision, out failed, out error));
        Assert.Equal(ThreadTagError.RevisionOverflow, error);
        Assert.False(state.TryRemove(Cat, Thread, state.Revision, out failed, out error));
        Assert.Equal(ThreadTagError.RevisionOverflow, error);
        Assert.Same(state, failed);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThreadTagProjection(-1));
    }

    [Fact]
    public void DefinitionCapacityRejectsWithoutPartialChangeButStillAllowsIdempotentRetry()
    {
        var state = Define(new(), Cat, "cat");
        for (int index = 1; index < ThreadTagProjection.MaximumDefinitions; index++)
        {
            var id = new PrivateThreadTagId(new Guid(index + 1, 0, 0, new byte[8]));
            state = Define(state, id, "tag-" + index);
        }
        long revision = state.Revision;
        Assert.False(state.TryDefine(Dog, Name("overflow"), revision, out var failed, out var error));
        Assert.Equal(ThreadTagError.CapacityExceeded, error);
        Assert.Same(state, failed);
        Assert.True(state.TryDefine(Cat, Name("cat"), revision, out var same, out _));
        Assert.Same(state, same);
        Assert.Equal(revision, state.Revision);
    }

    [Fact]
    public void SelectedScopeValidationIsAtomicAndBounded()
    {
        var state = Add(Define(new(), Cat, "cat"), Cat, Thread);
        Assert.False(state.TrySelectThreads(null, out var snapshot, out var error, selected: true));
        Assert.Null(snapshot);
        Assert.Equal(ThreadTagError.InvalidScope, error);
        Assert.False(state.TrySelectThreads(new[] { Thread, new ResourceId("stub", "g") },
            out snapshot, out error, selected: true));
        Assert.Null(snapshot);
        Assert.Equal(ThreadTagError.InvalidThread, error);
        Assert.False(state.TrySelectThreads(new OversizedScope(), out snapshot, out error, selected: true));
        Assert.Null(snapshot);
        Assert.Equal(ThreadTagError.CapacityExceeded, error);
        Assert.Single(Select(state, new[] { Thread }).Assignments);
    }

    private static PrivateTagName Name(string text)
    {
        Assert.True(PrivateTagName.TryCreate(text, out var name, out _));
        return name!;
    }

    private static ThreadTagProjection Define(ThreadTagProjection state, PrivateThreadTagId id, string name)
    {
        Assert.True(state.TryDefine(id, Name(name), state.Revision, out var result, out var error));
        Assert.Equal(ThreadTagError.None, error);
        return result;
    }

    private static ThreadTagProjection Add(ThreadTagProjection state, PrivateThreadTagId id, ResourceId thread)
    {
        Assert.True(state.TryAdd(id, thread, state.Revision, out var result, out var error));
        Assert.Equal(ThreadTagError.None, error);
        return result;
    }

    private static ThreadTagSnapshot Select(ThreadTagProjection state, IReadOnlyCollection<ResourceId> scope)
    {
        Assert.True(state.TrySelectThreads(scope, out var snapshot, out var error, selected: true));
        Assert.Equal(ThreadTagError.None, error);
        return snapshot!;
    }

    private sealed class UnreadableScope : IReadOnlyCollection<ResourceId>
    {
        public int Count => throw new InvalidOperationException("Unselected scope must not be inspected.");
        public IEnumerator<ResourceId> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class OversizedScope : IReadOnlyCollection<ResourceId>
    {
        public int Count => ThreadTagProjection.MaximumScopeCount + 1;
        public IEnumerator<ResourceId> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void SharedCatalogRevisionIsExplicitFencedAndDoesNotChangeOldProjection()
    {
        var catalog = new PrivateTagCatalog();
        TagId id = new(Guid.NewGuid());
        Assert.True(catalog.TryDefine(id, Name("shared"), 0, out catalog, out _));
        var state = new ThreadTagProjection(catalog);
        Assert.True(catalog.TryRename(id, Name("renamed"), catalog.Revision, out var renamed, out _));
        Assert.False(state.TryUseCatalog(renamed, 1, catalog.Revision, out var failed, out var error));
        Assert.Equal(ThreadTagError.StaleRevision, error);
        Assert.Same(state, failed);
        Assert.False(state.TryUseCatalog(new PrivateTagCatalog(), 0, catalog.Revision, out failed, out error));
        Assert.Equal(ThreadTagError.DefinitionConflict, error);
        Assert.True(state.TryUseCatalog(renamed, 0, catalog.Revision, out var updated, out error));
        Assert.Same(renamed, updated.Catalog);
        Assert.Same(catalog, state.Catalog);
        Assert.Equal("shared", state.Catalog.Definitions[0].Name.Display);
        Assert.Equal("renamed", updated.Catalog.Definitions[0].Name.Display);
    }
}
