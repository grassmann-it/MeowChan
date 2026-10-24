using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class IdentitySuggestionTests
{
    private static IdentitySuggestions Snapshot(bool more = false) => new(new(new[] { "A", "a", "removed" }, false),
        new(new[] { new ArchiveBoardIdentity("A", "same"), new ArchiveBoardIdentity("a", "same"), new ArchiveBoardIdentity("removed", "Unicode β'") }, more));

    [Fact]
    public void MatchingPreservesQualifiedIdentityAndOnlyFiltersSourceExactly()
    {
        var catalog = Snapshot();
        var pairs = catalog.Match(true, "sa");
        Assert.Equal(2, pairs.Length);
        Assert.Equal(new[] { "A", "a" }, pairs.Select(x => x.Source));
        Assert.NotEqual(pairs[0].Label, pairs[1].Label);
        Assert.All(pairs, x => Assert.Equal("same", x.Value));
        Assert.Equal("A", Assert.Single(catalog.Match(true, "S", "A")).Source);
        Assert.Empty(catalog.Match(true, "", "unknown"));
        Assert.Equal(2, catalog.Match(false, "a").Length);
    }

    [Fact]
    public void RuleContextsRetainPreferredRemovedSourceAndBoundMaterialization()
    {
        var preferred = new[] { new ArchiveBoardIdentity("z-removed", "same") };
        var watches = Enumerable.Range(0, 10000).Select(x => new ArchiveBoardIdentity($"source{x:D5}", $"b{x:D5}"));
        var catalog = IdentitySuggestions.FromContexts(preferred, watches, new[] { "provider" });
        Assert.Equal(256, catalog.Sources.Items.Count);
        Assert.Equal(2048, catalog.Boards.Items.Count);
        Assert.True(catalog.Sources.HasMore); Assert.True(catalog.Boards.HasMore);
        Assert.Contains("z-removed", catalog.Sources.Items);
        Assert.Contains(preferred[0], catalog.Boards.Items);
        Assert.Equal(20, catalog.Match(true, "").Length);
    }

    [Fact]
    public async Task TypingUsesOneSnapshotAndOnlyOneBoundedExactScope()
    {
        int initial = 0, scopes = 0;
        using var cache = new ArchiveSuggestionCache(_ => { initial++; return Task.FromResult(Snapshot(true)); },
            (source, _) => { scopes++; return Task.FromResult(new ArchiveIdentityPage<ArchiveBoardIdentity>(new[] { new ArchiveBoardIdentity(source, "scoped") }, false)); });
        foreach (string text in new[] { "", "s", "sa", "sam" }) await cache.MatchAsync(false, text, null, default);
        Assert.Equal(1, initial); Assert.Equal(0, scopes);
        Assert.Equal("scoped", Assert.Single(await cache.MatchAsync(true, "s", "A", default)).Value);
        await cache.MatchAsync(true, "sc", "A", default);
        Assert.Equal(1, scopes);
        await cache.MatchAsync(true, "", "a", default);
        Assert.Equal(2, scopes);
    }

    [Fact]
    public async Task SupersededInputAndProfileEndCannotPublishLateSnapshot()
    {
        var completion = new TaskCompletionSource<IdentitySuggestions>(TaskCreationOptions.RunContinuationsAsynchronously);
        int loads = 0;
        var cache = new ArchiveSuggestionCache(_ => { loads++; return completion.Task; }, (_, _) => throw new Exception("no scope needed"));
        using var old = new CancellationTokenSource();
        var first = cache.MatchAsync(false, "a", null, old.Token);
        old.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var latest = cache.MatchAsync(false, "r", null, default);
        completion.SetResult(Snapshot());
        Assert.Equal("removed", Assert.Single(await latest).Value);
        Assert.Equal(1, loads);
        cache.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.MatchAsync(false, "", null, default));
    }

    [Fact]
    public async Task ProfileEndCancelsWaiterEvenIfBackendIgnoresCancellation()
    {
        var late = new TaskCompletionSource<IdentitySuggestions>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new ArchiveSuggestionCache(_ => late.Task, (_, _) => throw new Exception());
        var pending = cache.MatchAsync(false, "", null, default);
        cache.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        late.SetResult(Snapshot());
    }

    [Fact]
    public async Task CancelledScopedDebounceDoesNotQueryAndFaultDoesNotAutoRetry()
    {
        int scopes = 0;
        using var cache = new ArchiveSuggestionCache(_ => Task.FromResult(Snapshot(true)),
            (_, _) => { scopes++; throw new Exception("locked"); });
        using var typing = new CancellationTokenSource();
        var pending = cache.MatchAsync(true, "", "A", typing.Token);
        typing.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, scopes);
        int initial = 0;
        using var failed = new ArchiveSuggestionCache(_ => { initial++; return Task.FromException<IdentitySuggestions>(new IOException("locked")); }, (_, _) => throw new Exception());
        for (int i = 0; i < 2; i++) await Assert.ThrowsAsync<IOException>(() => failed.MatchAsync(false, "", null, default));
        Assert.Equal(1, initial);
    }
    [Fact]
    public async Task LateScopedSourceResponseCannotReplaceNewerExactSource()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldScope = new TaskCompletionSource<ArchiveIdentityPage<ArchiveBoardIdentity>>(TaskCreationOptions.RunContinuationsAsynchronously);
        int scopes = 0;
        using var cache = new ArchiveSuggestionCache(_ => Task.FromResult(Snapshot(true)), (source, _) =>
        {
            scopes++;
            if (source == "A") { started.SetResult(); return oldScope.Task; } // Deliberately ignores cancellation.
            return Task.FromResult(new ArchiveIdentityPage<ArchiveBoardIdentity>([new(source, "Unicode β' / raw")], false));
        });
        using var oldInput = new CancellationTokenSource();
        var old = cache.MatchAsync(true, "", "A", oldInput.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldInput.Cancel();
        var current = Assert.Single(await cache.MatchAsync(true, "Unicode", "a", default));
        Assert.Equal("a", current.Source);
        Assert.Equal("Unicode β' / raw", current.Value);
        oldScope.SetResult(new([new("A", "stale")], false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal(current, Assert.Single(await cache.MatchAsync(true, "Unicode", "a", default)));
        Assert.Equal(2, scopes); // No replacement/reload of the current source by the late response.
    }

    [Fact]
    public async Task ProfileEndDuringScopedReadCannotLeakIntoReplacementProfile()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<ArchiveIdentityPage<ArchiveBoardIdentity>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var oldProfile = new ArchiveSuggestionCache(_ => Task.FromResult(Snapshot(true)), (_, _) =>
        {
            started.SetResult(); return late.Task; // Deliberately ignores profile cancellation.
        });
        var pending = oldProfile.MatchAsync(true, "", "A", default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        oldProfile.Dispose();
        var fresh = IdentitySuggestions.FromContexts([new("new-profile", "fresh")], [], []);
        using var replacement = new ArchiveSuggestionCache(_ => Task.FromResult(fresh), (_, _) => throw new Exception("No scope read expected."));
        Assert.Equal("fresh", Assert.Single(await replacement.MatchAsync(true, "", "new-profile", default)).Value);
        late.SetResult(new([new("A", "old-profile")], false));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(await replacement.MatchAsync(true, "old", null, default));
        Assert.Equal("new-profile", Assert.Single(await replacement.MatchAsync(false, "", null, default)).Value);
    }

    [Theory]
    [InlineData(" A ", " x ")]
    [InlineData(" β/源 ", " β' / board ")]
    public void ExplicitStoredCandidatesKeepExactIdentityAndQualification(string source, string board)
    {
        var sourceCandidate = new IdentitySuggestion(source, null, source);
        var boardCandidate = new IdentitySuggestion(board, source, board + " · " + source);
        Assert.Same(sourceCandidate, IdentitySuggestions.CurrentSelection(source, sourceCandidate));
        Assert.Equal(source, IdentitySuggestions.InputIdentity(source, sourceCandidate));
        Assert.Same(boardCandidate, IdentitySuggestions.CurrentSelection(board, boardCandidate));
        Assert.Equal(board, IdentitySuggestions.InputIdentity(board, boardCandidate));
        Assert.Equal(source, IdentitySuggestions.CurrentSelection(board, boardCandidate)!.Source);
        Assert.Equal(source.Trim(), IdentitySuggestions.InputIdentity(source, null));
        Assert.Equal(board.Trim(), IdentitySuggestions.InputIdentity(board, null));
    }

    [Fact]
    public void CandidateScopeAndFreeTextRemainDistinctIncludingStaleSelection()
    {
        const string exact = " A ";
        var candidate = new IdentitySuggestion(exact, null, exact);
        var catalog = new IdentitySuggestions(new([exact, "A"], false),
            new([new(exact, "same"), new("A", "same")], false));
        Assert.Equal(exact, Assert.Single(catalog.Match(true, "same", IdentitySuggestions.InputIdentity(exact, candidate))).Source);
        Assert.Equal("A", Assert.Single(catalog.Match(true, "same", IdentitySuggestions.InputIdentity(exact, null))).Source);
        Assert.Null(IdentitySuggestions.CurrentSelection("A", candidate));
        Assert.Equal("A", IdentitySuggestions.InputIdentity("A", candidate));
        Assert.Null(IdentitySuggestions.CurrentSelection(" a ", candidate));
        Assert.Equal("a", IdentitySuggestions.InputIdentity(" a ", candidate));
    }

}
