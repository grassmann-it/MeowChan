using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Post filters (plan P2 part B): the dialog's model over the real store, and hiding/showing posts surviving a restart.
[Collection("Localization")]
public sealed class PostFilterDialogModelTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "100");

    // The store directly, as the session host would run it (without the host's queue).
    private sealed class StoreService(PostFilterStore store) : IPostFilterService
    {
        public Task<PostFilterSnapshot> LoadAsync(CancellationToken token) => Run(() => store.Load());
        public Task<PostFilterSnapshot> RecoverAsync(long revision, CancellationToken token) => Run(() => store.Recover(revision, token));
        public Task<PostFilterSnapshot> AddRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token) =>
            Run(() => store.AddRule(id, draft, revision, token));
        public Task<PostFilterSnapshot> SetRuleEnabledAsync(Guid id, bool enabled, long revision, CancellationToken token) =>
            Run(() => store.SetRuleEnabled(id, enabled, revision, token));
        public Task<PostFilterSnapshot> RemoveRuleAsync(Guid id, long revision, CancellationToken token) => Run(() => store.RemoveRule(id, revision, token));
        public Task<PostFilterSnapshot> SetHiddenPostsActionAsync(HiddenPostsAction action, long revision, CancellationToken token) =>
            Run(() => store.SetHiddenPostsAction(action, revision, token));
        public Task<PostFilterSnapshot> HidePostAsync(ResourceId thread, string post, bool replies, DateTimeOffset added, long revision, CancellationToken token) =>
            Run(() => store.HidePost(thread, post, replies, added, revision, token));
        public Task<PostFilterSnapshot> UnhidePostAsync(ResourceId thread, string post, long revision, CancellationToken token) =>
            Run(() => store.UnhidePost(thread, post, revision, token));

        private static Task<PostFilterSnapshot> Run(Func<PostFilterSnapshot> action)
        {
            try { return Task.FromResult(action()); }
            catch (Exception error) { return Task.FromException<PostFilterSnapshot>(error); }
        }
    }

    private static (PostFilterSession Session, PostFilterStore Store) Open(ProfileLease lease, string directory)
    {
        PostFilterStore store = new(directory, lease);
        return (new PostFilterSession(new StoreService(store), () => new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)), store);
    }

    [Fact]
    public async Task AddingARuleStoresItClearsTheDraftAndRefusesADuplicate()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        (PostFilterSession session, _) = Open(lease, directory.Path);
        await session.LoadAsync();
        PostFilterDialogModel model = new(session, Thread);
        int changes = 0;
        model.Changed += (_, _) => changes++;
        Assert.False(model.CanAdd); // Empty draft.
        model.Pattern = "Spam Link";
        model.Action = PostFilterAction.Hide;
        model.Replies = true;
        model.Scope = PostFilterScope.Board;
        Assert.True(model.CanAdd);
        Assert.True(await model.AddAsync());
        Assert.Equal(string.Empty, model.Pattern);
        Assert.Equal(Texts.T("postFilter.saved"), model.Status);
        Assert.False(model.StatusIsError);
        Assert.True(changes > 0);
        PostFilterDialogModel.RuleRow row = Assert.Single(model.Rules);
        Assert.Equal((PostFilterField.Text, PostFilterMatch.Contains, "Spam Link", PostFilterAction.Hide, true, "4chan", "g"),
            (row.Rule.Field, row.Rule.Match, row.Rule.Pattern, row.Rule.Action, row.Rule.Replies, row.Rule.Source, row.Rule.Board));
        Assert.Contains("Spam Link", row.Label);
        Assert.Contains(Texts.T("postFilter.withReplies"), row.Details);
        Assert.Contains(Texts.T("postFilter.scope.board", "4chan /g/"), row.Details);

        // Same field, comparison, scope and text in other case: refused with the dialog's own sentence.
        model.Pattern = "spam link";
        model.Scope = PostFilterScope.Board;
        Assert.False(await model.AddAsync());
        Assert.Equal(Texts.T("postFilter.duplicate"), model.Status);
        Assert.True(model.StatusIsError);
        Assert.Equal("spam link", model.Pattern); // Kept for correcting it.
        Assert.Single(model.Rules);

        // Switching off and removing.
        await model.SetEnabledAsync(row.Rule.Id, false);
        Assert.False(Assert.Single(session.Current!.Rules).Enabled);
        await model.RemoveAsync(row.Rule.Id);
        Assert.Empty(model.Rules);
        Assert.Empty(session.Current!.Rules);
    }

    [Fact]
    public void TheDraftValidatesItsTextMd5ScopeAndReplies()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        (PostFilterSession session, _) = Open(lease, directory.Path);
        PostFilterDialogModel model = new(session, Thread);
        Assert.Equal(new[] { PostFilterScope.All, PostFilterScope.Source, PostFilterScope.Board }, model.Scopes);
        Assert.Equal(new[] { PostFilterScope.All }, new PostFilterDialogModel(session, null).Scopes);
        model.Pattern = "   ";
        Assert.Equal(Texts.T("postFilter.invalidPattern"), model.DraftError);
        model.Pattern = new string('a', PostFilterRules.MaximumPatternCodepoints + 1);
        Assert.Equal(Texts.T("postFilter.invalidPattern"), model.DraftError);
        model.Field = PostFilterField.Md5;
        model.Pattern = "abc";
        Assert.False(model.MatchSelectable);
        Assert.Equal(Texts.T("postFilter.invalidMd5"), model.DraftError);
        model.Pattern = "00112233445566778899AABBCCDDEEFF";
        Assert.Null(model.DraftError);
        model.Match = PostFilterMatch.Contains;
        Assert.Equal(PostFilterMatch.Exact, model.Draft().Match);
        // Not loaded yet: nothing may be written.
        Assert.False(model.CanAdd);
        model.Action = PostFilterAction.Highlight;
        model.Replies = true;
        Assert.False(model.RepliesSelectable);
        Assert.False(model.Draft().Replies);
        model.Action = PostFilterAction.Stub;
        Assert.True(model.Draft().Replies);
        model.Scope = PostFilterScope.Source;
        Assert.Equal(("4chan", (string?)null), (model.Draft().Source, model.Draft().Board));
        model.Scope = PostFilterScope.All;
        Assert.Equal(((string?)null, (string?)null), (model.Draft().Source, model.Draft().Board));
    }

    [Fact]
    public async Task AFullListNamesTheSettingAndAConflictShowsTheNewerList()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        lease.StoreLimits.PostFilterRules = ProfileStoreLimits.MinimumPostFilterRules;
        (PostFilterSession session, PostFilterStore store) = Open(lease, directory.Path);
        await session.LoadAsync();
        PostFilterDialogModel model = new(session, Thread);
        for (int index = 0; index < ProfileStoreLimits.MinimumPostFilterRules; index++)
        {
            model.Pattern = "rule " + index;
            Assert.True(await model.AddAsync(), model.Status);
        }
        model.Pattern = "one too many";
        Assert.False(await model.AddAsync());
        Assert.True(model.StatusIsError);
        Assert.Contains(Texts.T("budgets.postFilterRules"), model.Status);
        Assert.Contains(Texts.T("settings.storageLimits"), model.Status);
        Assert.Equal(ProfileStoreLimits.MinimumPostFilterRules, model.Rules.Count);

        // Another writer changed the file: the change is refused, and the dialog shows the newer list.
        PostFilterRule first = model.Rules[0].Rule;
        PostFilterSnapshot external = store.RemoveRule(first.Id, session.Current!.Revision);
        PostFilterRule second = model.Rules[1].Rule;
        await model.RemoveAsync(second.Id);
        Assert.Equal(Texts.T("postFilter.conflict"), model.Status);
        Assert.True(model.StatusIsError);
        Assert.Equal(external.Revision, session.Current!.Revision);
        Assert.DoesNotContain(model.Rules, row => row.Rule.Id == first.Id);
        Assert.Contains(model.Rules, row => row.Rule.Id == second.Id);
        // Explicitly retried on the newer list, it works.
        await model.RemoveAsync(second.Id);
        Assert.False(model.StatusIsError);
        Assert.DoesNotContain(model.Rules, row => row.Rule.Id == second.Id);
    }

    [Fact]
    public async Task HiddenPostsSurviveARestartAndShowAgainFromTheDialog()
    {
        using TempDirectory directory = new();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            (PostFilterSession session, _) = Open(lease, directory.Path);
            await session.LoadAsync();
            await session.HidePostAsync(Thread, "101", replies: false);
            await session.HidePostAsync(Thread, "105", replies: true);
            Assert.True(session.IsHidden(Thread, "101"));
            Assert.False(session.IsHidden(new ResourceId("4chan", "v", "100"), "101"));
            await Assert.ThrowsAsync<PostFilterDuplicateException>(() => session.HidePostAsync(Thread, "101", replies: true));
            Assert.Equal(Texts.T("postFilter.alreadyHidden"), PostFilterSession.ErrorText(new PostFilterDuplicateException(rule: false)));
            await new PostFilterDialogModel(session, Thread).SetHiddenActionAsync(HiddenPostsAction.Hide);
        }
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            (PostFilterSession session, _) = Open(lease, directory.Path);
            Assert.Null(session.Current);
            await session.LoadAsync();
            Assert.True(session.IsHidden(Thread, "101"));
            Assert.True(session.IsHidden(Thread, "105"));
            PostFilterDialogModel model = new(session, Thread);
            Assert.Equal(HiddenPostsAction.Hide, model.HiddenAction);
            Assert.Equal(2, model.HiddenPosts.Count);
            PostFilterDialogModel.HiddenRow row = model.HiddenPosts.Single(candidate => candidate.Post.Post == "105");
            Assert.True(row.Post.Replies);
            Assert.Contains(Texts.T("postFilter.postRow", "105", "4chan /g/ 100"), row.Label);
            Assert.Contains(Texts.T("postFilter.withReplies"), row.Label);
            await model.UnhideAsync(row.Post);
            Assert.Equal(Texts.T("postFilter.unhideDone", "105"), model.Status);
            Assert.False(session.IsHidden(Thread, "105"));
            // The reader's matcher follows the session.
            PostFilterMatcher matcher = new();
            session.Changed += (_, _) => matcher.Update(session.Current);
            await session.UnhidePostAsync(Thread, "101");
            Assert.False(matcher.IsActive(Thread));
        }
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            PostFilterSnapshot reloaded = new PostFilterStore(directory.Path, lease).Load();
            Assert.Empty(reloaded.HiddenPosts);
            Assert.Equal(HiddenPostsAction.Hide, reloaded.HiddenPostsAction);
        }
    }

    [Fact]
    public async Task ADamagedFileFiltersNothingUntilItsBackupIsRestored()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        (PostFilterSession session, _) = Open(lease, directory.Path);
        await session.LoadAsync();
        PostFilterDialogModel model = new(session, Thread);
        model.Pattern = "first";
        Assert.True(await model.AddAsync());
        model.Pattern = "second";
        Assert.True(await model.AddAsync());
        File.WriteAllText(Path.Combine(directory.Path, PostFilterStore.FileName), "{ damaged");

        (PostFilterSession reopened, _) = Open(lease, directory.Path);
        await reopened.LoadAsync();
        PostFilterMatcher matcher = new();
        matcher.Update(reopened.Current);
        Assert.False(matcher.IsActive(Thread));
        PostFilterDialogModel recovery = new(reopened, Thread);
        Assert.True(recovery.CanRecover);
        Assert.False(recovery.CanWrite);
        Assert.Equal(Texts.T("postFilter.recovery.available", 1, 0), recovery.StoreNotice);
        recovery.Pattern = "third";
        Assert.False(await recovery.AddAsync());
        await recovery.RecoverAsync();
        Assert.Equal(Texts.T("postFilter.recovery.done"), recovery.Status);
        Assert.Null(recovery.StoreNotice);
        Assert.True(recovery.CanWrite);
        matcher.Update(reopened.Current);
        Assert.True(matcher.IsActive(Thread));
    }

    [Fact]
    public async Task ALoadFailureIsKeptAndShown()
    {
        PostFilterSession session = new(new FailingService());
        Assert.Null(await session.LoadAsync());
        Assert.Equal("not ready", session.LoadError);
        PostFilterDialogModel model = new(session, null);
        Assert.Equal(Texts.T("postFilter.loadFailed", "not ready"), model.StoreNotice);
        Assert.False(model.CanWrite);
        Assert.Equal(Texts.T("postFilter.failed", "boom"), PostFilterSession.ErrorText(new InvalidOperationException("boom")));
    }

    private sealed class FailingService : IPostFilterService
    {
        private static Task<PostFilterSnapshot> Fail() => Task.FromException<PostFilterSnapshot>(new InvalidOperationException("not ready"));
        public Task<PostFilterSnapshot> LoadAsync(CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> RecoverAsync(long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> AddRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> SetRuleEnabledAsync(Guid id, bool enabled, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> RemoveRuleAsync(Guid id, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> SetHiddenPostsActionAsync(HiddenPostsAction action, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> HidePostAsync(ResourceId thread, string post, bool replies, DateTimeOffset added, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> UnhidePostAsync(ResourceId thread, string post, long revision, CancellationToken token) => Fail();
    }
}
