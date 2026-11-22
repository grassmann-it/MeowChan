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
        public Task<PostFilterSnapshot> ReplaceRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token) =>
            Run(() => store.ReplaceRule(id, draft, revision, token));
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

    private static async Task<PostFilterDialogModel> Added(PostFilterSession session, ResourceId? context, params string[] patterns)
    {
        PostFilterDialogModel model = new(session, context);
        foreach (string pattern in patterns)
        {
            model.Pattern = pattern;
            Assert.True(await model.AddAsync(), model.Status);
        }
        return model;
    }

    [Fact]
    public async Task EditingARuleLoadsItIntoTheFormAndSavingReplacesItInPlace()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        (PostFilterSession session, _) = Open(lease, directory.Path);
        await session.LoadAsync();
        PostFilterDialogModel model = new(session, Thread);
        model.Pattern = "first";
        Assert.True(await model.AddAsync());
        model.Pattern = "Second One";
        model.Field = PostFilterField.Subject;
        model.Match = PostFilterMatch.Exact;
        model.Action = PostFilterAction.Hide;
        model.Replies = true;
        model.Scope = PostFilterScope.Source;
        Assert.True(await model.AddAsync());
        model.Pattern = "third";
        Assert.True(await model.AddAsync());
        Guid[] order = model.Rules.Select(row => row.Rule.Id).ToArray();
        Guid second = order[1];
        await model.SetEnabledAsync(second, false);
        Assert.False(model.IsEditing);
        Assert.False(model.CanSave);

        // Begin: the rule's fields are in the form, adding is not possible, saving is.
        model.Pattern = "left over";
        Assert.True(model.BeginEdit(second));
        Assert.True(model.IsEditing);
        Assert.Equal(second, model.EditingRule!.Id);
        Assert.Equal((PostFilterField.Subject, PostFilterMatch.Exact, "Second One", PostFilterAction.Hide, true, PostFilterScope.Source),
            (model.Field, model.Match, model.Pattern, model.Action, model.Replies, model.Scope));
        Assert.Contains("Second One", model.EditingLabel);
        Assert.False(model.CanAdd);
        Assert.True(model.CanSave);
        Assert.Null(model.Status);
        Assert.Equal(new[] { PostFilterScope.All, PostFilterScope.Source, PostFilterScope.Board }, model.Scopes);

        // Save: same ID, same place, the switched-off state is kept, the form returns to adding.
        model.Pattern = "Second Edited";
        model.Action = PostFilterAction.Highlight;
        model.Scope = PostFilterScope.Board;
        Assert.True(await model.SaveEditAsync());
        Assert.False(model.IsEditing);
        Assert.Equal(string.Empty, model.Pattern);
        Assert.Equal(Texts.T("postFilter.saved"), model.Status);
        Assert.False(model.StatusIsError);
        Assert.Equal(order, model.Rules.Select(row => row.Rule.Id).ToArray());
        PostFilterRule edited = model.Rules[1].Rule;
        Assert.Equal((PostFilterField.Subject, PostFilterMatch.Exact, "Second Edited", PostFilterAction.Highlight, false, "4chan", "g", false),
            (edited.Field, edited.Match, edited.Pattern, edited.Action, edited.Replies, edited.Source, edited.Board, edited.Enabled));
        Assert.Equal("first", model.Rules[0].Rule.Pattern);
        Assert.Equal("third", model.Rules[2].Rule.Pattern);
        Assert.True(model.CanAdd == false); // The pattern is empty again.

        // Cancel: nothing is stored, the form is empty and adds again.
        Assert.True(model.BeginEdit(order[0]));
        model.Pattern = "typed but cancelled";
        model.CancelEdit();
        Assert.False(model.IsEditing);
        Assert.Equal(string.Empty, model.Pattern);
        Assert.Equal("first", model.Rules[0].Rule.Pattern);
        Assert.Equal(3, model.Rules.Count);
        model.Pattern = "fourth";
        Assert.True(model.CanAdd);
        Assert.True(await model.AddAsync());
        Assert.Equal(4, model.Rules.Count);

        // Saving without editing and an invalid edit do nothing.
        Assert.False(await model.SaveEditAsync());
        Assert.True(model.BeginEdit(order[2]));
        model.Pattern = "   ";
        Assert.False(model.CanSave);
        Assert.False(await model.SaveEditAsync());
        Assert.True(model.StatusIsError);
        Assert.Equal("third", model.Rules[2].Rule.Pattern);
        Assert.True(model.IsEditing);
    }

    [Fact]
    public async Task EditingKeepsTheScopeOfARuleThatBelongsToAnotherThreadAndWorksWithoutAThread()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        (PostFilterSession session, _) = Open(lease, directory.Path);
        await session.LoadAsync();
        await session.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "elsewhere", PostFilterAction.Stub, false, "4chan", "v"));
        await session.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "whole source", PostFilterAction.Stub, false, "4chan"));
        PostFilterDialogModel model = new(session, Thread); // 4chan /g/
        Guid board = model.Rules[0].Rule.Id, source = model.Rules[1].Rule.Id;
        Assert.True(model.BeginEdit(board));
        Assert.Equal(PostFilterScope.Kept, model.Scope);
        Assert.Equal(new[] { PostFilterScope.All, PostFilterScope.Source, PostFilterScope.Board, PostFilterScope.Kept }, model.Scopes);
        Assert.Equal(PostFilterDialogModel.RuleScopeLabel(model.EditingRule!), model.ScopeLabel(PostFilterScope.Kept));
        model.Pattern = "elsewhere 2";
        Assert.True(await model.SaveEditAsync());
        Assert.Equal(("4chan", "v"), (model.Rules[0].Rule.Source, model.Rules[0].Rule.Board));
        Assert.Equal(new[] { PostFilterScope.All, PostFilterScope.Source, PostFilterScope.Board }, model.Scopes);

        // A source-wide rule of the open thread's source is the dialog's own "Only this source".
        Assert.True(model.BeginEdit(source));
        Assert.Equal(PostFilterScope.Source, model.Scope);
        model.CancelEdit();

        // Without an open thread only "Everywhere" can be chosen, so a scoped rule keeps its scope.
        PostFilterDialogModel detached = new(session, null);
        Assert.True(detached.BeginEdit(source));
        Assert.Equal(PostFilterScope.Kept, detached.Scope);
        detached.Pattern = "whole source 2";
        detached.Action = PostFilterAction.Hide;
        Assert.True(await detached.SaveEditAsync());
        Assert.Equal(("4chan", (string?)null, PostFilterAction.Hide), (detached.Rules[1].Rule.Source, detached.Rules[1].Rule.Board, detached.Rules[1].Rule.Action));
        Assert.Equal(new[] { PostFilterScope.All }, detached.Scopes);
    }

    [Fact]
    public async Task EditingIntoAnotherRuleIsADuplicateButOwnTextInOtherCaseIsNot()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        (PostFilterSession session, PostFilterStore store) = Open(lease, directory.Path);
        await session.LoadAsync();
        PostFilterDialogModel model = await Added(session, Thread, "alpha", "beta");
        Guid alpha = model.Rules[0].Rule.Id, beta = model.Rules[1].Rule.Id;
        long revision = session.Current!.Revision;

        Assert.True(model.BeginEdit(beta));
        model.Pattern = "ALPHA";
        Assert.False(await model.SaveEditAsync());
        Assert.Equal(Texts.T("postFilter.duplicate"), model.Status);
        Assert.True(model.StatusIsError);
        Assert.True(model.IsEditing);
        Assert.Equal("ALPHA", model.Pattern); // Kept for correcting it.
        Assert.Equal(revision, store.Load().Revision);
        Assert.Equal(new[] { "alpha", "beta" }, model.Rules.Select(row => row.Rule.Pattern).ToArray());

        // Another scope is another rule.
        model.Scope = PostFilterScope.Board;
        Assert.True(await model.SaveEditAsync());
        Assert.False(model.StatusIsError);
        Assert.Equal(("ALPHA", "g"), (model.Rules[1].Rule.Pattern, model.Rules[1].Rule.Board));

        // A rule does not collide with itself: only the case of its own text (and its action) changes.
        Assert.True(model.BeginEdit(alpha));
        model.Pattern = "ALPHA";
        model.Action = PostFilterAction.Hide;
        Assert.True(await model.SaveEditAsync());
        Assert.Equal(("ALPHA", PostFilterAction.Hide), (model.Rules[0].Rule.Pattern, model.Rules[0].Rule.Action));
    }

    [Fact]
    public async Task SavingAnEditRefusesToUndoAChangeMadeSinceTheEditBegan()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        (PostFilterSession session, PostFilterStore store) = Open(lease, directory.Path);
        await session.LoadAsync();
        PostFilterDialogModel model = new(session, Thread);
        model.Pattern = "alpha";
        Assert.True(await model.AddAsync());
        Guid id = model.Rules[0].Rule.Id;
        await model.SetEnabledAsync(id, false);
        Assert.False(model.Rules[0].Rule.Enabled);

        // The edit begins on the disabled rule; meanwhile the same rule is enabled (the list's checkbox, a second window).
        Assert.True(model.BeginEdit(id));
        Assert.True(model.IsEditingRule(id));
        Assert.False(model.IsEditingRule(Guid.NewGuid()));
        await model.SetEnabledAsync(id, true);
        model.Pattern = "alpha edited";
        Assert.False(await model.SaveEditAsync());
        Assert.Equal(Texts.T("postFilter.conflict"), model.Status);
        Assert.True(model.StatusIsError);
        Assert.True(model.IsEditing);
        Assert.Equal("alpha edited", model.Pattern);
        PostFilterRule stored = model.Rules.Single().Rule;
        Assert.Equal("alpha", stored.Pattern);
        Assert.True(stored.Enabled);

        // An explicit second save replaces the newer state and keeps its enabled state.
        Assert.True(await model.SaveEditAsync(), model.Status);
        stored = model.Rules.Single().Rule;
        Assert.Equal("alpha edited", stored.Pattern);
        Assert.True(stored.Enabled);

        // A change made behind the model's back (another session over the same file) is refused too,.
        Assert.True(model.BeginEdit(id));
        model.Pattern = "alpha again";
        PostFilterSnapshot external = store.SetRuleEnabled(id, false, session.Current!.Revision);
        Assert.False(await model.SaveEditAsync());
        Assert.Equal(Texts.T("postFilter.conflict"), model.Status);
        Assert.Equal(external.Revision, session.Current!.Revision);
        Assert.False(model.Rules.Single().Rule.Enabled);
        Assert.True(await model.SaveEditAsync(), model.Status);
        Assert.False(model.Rules.Single().Rule.Enabled);
        Assert.Equal("alpha again", model.Rules.Single().Rule.Pattern);
    }

    [Fact]
    public async Task TheSessionRefusesAReplacementBuiltOnAStaleRuleEvenWhenOnlyEnabledDiffers()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        (PostFilterSession session, _) = Open(lease, directory.Path);
        await session.LoadAsync();
        await session.AddRuleAsync(new PostFilterRuleDraft(PostFilterField.Text, PostFilterMatch.Contains, "x", PostFilterAction.Hide, false, null, null, false));
        PostFilterRule basis = session.Current!.Rules.Single();
        await session.SetRuleEnabledAsync(basis.Id, true);
        PostFilterRuleDraft draft = new(PostFilterField.Text, PostFilterMatch.Contains, "y", PostFilterAction.Hide, false, null, null, false);
        await Assert.ThrowsAsync<PostFilterRevisionException>(() => session.ReplaceRuleAsync(basis.Id, draft, default, basis));
        Assert.True(session.Current!.Rules.Single().Enabled);
        Assert.Equal("x", session.Current!.Rules.Single().Pattern);
    }

    [Fact]
    public async Task EditingAtTheLimitStillWorksAndAConflictKeepsTheDraftOrEndsTheEdit()
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
        PostFilterRule first = model.Rules[0].Rule, second = model.Rules[1].Rule, third = model.Rules[2].Rule;
        model.Pattern = "one too many";
        Assert.False(await model.AddAsync());
        Assert.Contains(Texts.T("budgets.postFilterRules"), model.Status);
        model.Pattern = string.Empty;

        // A full list does not stop editing: the count stays.
        Assert.True(model.BeginEdit(first.Id));
        model.Pattern = "rule 0 edited";
        Assert.True(await model.SaveEditAsync(), model.Status);
        Assert.Equal(ProfileStoreLimits.MinimumPostFilterRules, model.Rules.Count);

        // Another writer changed the file; the edited rule is still there: refused, newer list shown, draft kept, retry works.
        Assert.True(model.BeginEdit(second.Id));
        model.Pattern = "rule 1 edited";
        PostFilterSnapshot external = store.RemoveRule(third.Id, session.Current!.Revision);
        Assert.False(await model.SaveEditAsync());
        Assert.Equal(Texts.T("postFilter.conflict"), model.Status);
        Assert.True(model.StatusIsError);
        Assert.Equal(external.Revision, session.Current!.Revision);
        Assert.True(model.IsEditing);
        Assert.Equal("rule 1 edited", model.Pattern);
        Assert.DoesNotContain(model.Rules, row => row.Rule.Id == third.Id);
        Assert.True(await model.SaveEditAsync());
        Assert.Equal("rule 1 edited", model.Rules.Single(row => row.Rule.Id == second.Id).Rule.Pattern);

        // The edited rule itself was removed by the other writer: the edit ends, the newer list is shown.
        Assert.True(model.BeginEdit(model.Rules[2].Rule.Id));
        Guid doomed = model.EditingRule!.Id;
        model.Pattern = "doomed";
        store.RemoveRule(doomed, session.Current!.Revision);
        Assert.False(await model.SaveEditAsync());
        Assert.Equal(Texts.T("postFilter.conflict"), model.Status);
        Assert.False(model.IsEditing);
        Assert.Equal(PostFilterScope.All, model.Scope);
        Assert.DoesNotContain(model.Rules, row => row.Rule.Id == doomed);
        Assert.False(await model.SaveEditAsync());
    }

    [Fact]
    public async Task ARuleFromANewerVersionStaysReadOnlyWithAHint()
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        Guid regexId = Guid.NewGuid(), knownId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(directory.Path, PostFilterStore.FileName),
            "{\"schemaVersion\":1,\"revision\":5,\"hiddenPostsAction\":\"stub\",\"rules\":[" +
            "{\"id\":\"" + regexId + "\",\"enabled\":true,\"field\":\"text\",\"match\":\"regex\",\"pattern\":\"^a+$\",\"action\":\"hide\",\"replies\":false,\"flags\":\"i\"}," +
            "{\"id\":\"" + knownId + "\",\"enabled\":true,\"field\":\"text\",\"match\":\"contains\",\"pattern\":\"abc\",\"action\":\"hide\",\"replies\":false}],\"hiddenPosts\":[]}");
        (PostFilterSession session, _) = Open(lease, directory.Path);
        await session.LoadAsync();
        PostFilterDialogModel model = new(session, Thread);
        Assert.False(model.Rules[0].Supported);
        model.Pattern = "typed";
        Assert.False(model.BeginEdit(regexId));
        Assert.False(model.IsEditing);
        Assert.Equal(Texts.T("postFilter.editUnsupported"), model.Status);
        Assert.True(model.StatusIsError);
        Assert.Equal("typed", model.Pattern); // The form was not touched.
        Assert.False(await model.SaveEditAsync());

        // A supported rule next to it still edits, and the unsupported one is kept as it was.
        Assert.True(model.BeginEdit(knownId));
        Assert.Null(model.Status);
        model.Pattern = "abcd";
        Assert.True(await model.SaveEditAsync());
        Assert.False(model.Rules[0].Supported);
        Assert.Equal("abcd", model.Rules[1].Rule.Pattern);
        Assert.Contains("^a+$", File.ReadAllText(Path.Combine(directory.Path, PostFilterStore.FileName)));

        // Unknown rules and a store that cannot be written are not editable either.
        Assert.False(model.BeginEdit(Guid.NewGuid()));
        PostFilterDialogModel notLoaded = new(new PostFilterSession(new FailingService()), Thread);
        Assert.False(notLoaded.BeginEdit(knownId));
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

    [Fact]
    public async Task SettingsStopReportingTheLastSnapshotDuringALoadError()
    {
        PostFilterRule rule = new(Guid.NewGuid(), true, PostFilterField.Text, PostFilterMatch.Contains, "test",
            PostFilterAction.Hide, false, null, null);
        PostFilterSnapshot loaded = new(1, HiddenPostsAction.Stub, [rule], [], PostFilterStoreState.Ready);
        FailingService service = new() { Snapshot = loaded };
        PostFilterSession session = new(service);

        await session.LoadAsync();
        Assert.Same(loaded, session.CurrentForSettings);
        service.Snapshot = null;
        await session.LoadAsync();
        Assert.NotNull(session.LoadError);
        Assert.Same(loaded, session.Current);
        Assert.Null(session.CurrentForSettings);

        service.Snapshot = loaded;
        await session.LoadAsync();
        Assert.Same(loaded, session.CurrentForSettings);
    }

    private sealed class FailingService : IPostFilterService
    {
        internal PostFilterSnapshot? Snapshot { get; set; }
        private static Task<PostFilterSnapshot> Fail() => Task.FromException<PostFilterSnapshot>(new InvalidOperationException("not ready"));
        public Task<PostFilterSnapshot> LoadAsync(CancellationToken token) => Snapshot is { } snapshot ? Task.FromResult(snapshot) : Fail();
        public Task<PostFilterSnapshot> RecoverAsync(long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> AddRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> ReplaceRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> SetRuleEnabledAsync(Guid id, bool enabled, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> RemoveRuleAsync(Guid id, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> SetHiddenPostsActionAsync(HiddenPostsAction action, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> HidePostAsync(ResourceId thread, string post, bool replies, DateTimeOffset added, long revision, CancellationToken token) => Fail();
        public Task<PostFilterSnapshot> UnhidePostAsync(ResourceId thread, string post, long revision, CancellationToken token) => Fail();
    }
}
