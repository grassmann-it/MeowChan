using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

// Post filters (plan P2): post-filters.json through the host's command queue and profile admission, like the saved
// searches. The store is created and loaded lazily, so a damaged or newer file stays confined to this feature.
internal sealed partial class DesktopSessionHost
{
    private PostFilterStore? _postFilterStore;
    internal Action<ProfileAdmissionToken>? BeforePostFilterAdmittedMutationForTests { get; set; }
    internal Action<ProfileAdmissionToken>? AfterPostFilterAdmittedMutationForTests { get; set; }
    private PostFilterStore PostFilters => _postFilterStore ??= new PostFilterStore(ProfileDirectory, _profileLease);

    internal Task<PostFilterSnapshot> LoadPostFiltersAsync(CancellationToken token = default)
        => RunInteractiveAsync(nameof(LoadPostFiltersAsync), "profile", () => PostFilterCommandAsync(_ => PostFilters.Load(), token, null), captureAfter: false);

    internal Task<PostFilterSnapshot> RecoverPostFiltersAsync(long revision, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(RecoverPostFiltersAsync), "profile", admission =>
            PostFilterCommandAsync(store => store.RecoverAdmitted(revision, admission, token), token, admission));

    internal Task<PostFilterSnapshot> AddPostFilterRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token = default)
    {
        PostFilterRuleDraft frozen = PostFilterRules.Normalize(draft);
        return RunInteractiveWithAdmissionAsync(nameof(AddPostFilterRuleAsync), id.ToString(), admission =>
            PostFilterCommandAsync(store => store.AddRuleAdmitted(id, frozen, revision, admission, token), token, admission));
    }

    internal Task<PostFilterSnapshot> SetPostFilterRuleEnabledAsync(Guid id, bool enabled, long revision, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(SetPostFilterRuleEnabledAsync), id.ToString(), admission =>
            PostFilterCommandAsync(store => store.SetRuleEnabledAdmitted(id, enabled, revision, admission, token), token, admission));

    internal Task<PostFilterSnapshot> RemovePostFilterRuleAsync(Guid id, long revision, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(RemovePostFilterRuleAsync), id.ToString(), admission =>
            PostFilterCommandAsync(store => store.RemoveRuleAdmitted(id, revision, admission, token), token, admission));

    internal Task<PostFilterSnapshot> SetHiddenPostsActionAsync(HiddenPostsAction action, long revision, CancellationToken token = default)
        => RunInteractiveWithAdmissionAsync(nameof(SetHiddenPostsActionAsync), "profile", admission =>
            PostFilterCommandAsync(store => store.SetHiddenPostsActionAdmitted(action, revision, admission, token), token, admission));

    internal Task<PostFilterSnapshot> HidePostAsync(ResourceId thread, string post, bool replies, DateTimeOffset added, long revision, CancellationToken token = default)
    {
        PostFilterRules.ValidateHiddenPost(thread, post);
        return RunInteractiveWithAdmissionAsync(nameof(HidePostAsync), thread + "#" + post, admission =>
            PostFilterCommandAsync(store => store.HidePostAdmitted(thread, post, replies, added, revision, admission, token), token, admission));
    }

    internal Task<PostFilterSnapshot> UnhidePostAsync(ResourceId thread, string post, long revision, CancellationToken token = default)
    {
        PostFilterRules.ValidateHiddenPost(thread, post);
        return RunInteractiveWithAdmissionAsync(nameof(UnhidePostAsync), thread + "#" + post, admission =>
            PostFilterCommandAsync(store => store.UnhidePostAdmitted(thread, post, revision, admission, token), token, admission));
    }

    private async Task<PostFilterSnapshot> PostFilterCommandAsync(Func<PostFilterStore, PostFilterSnapshot> action, CancellationToken token,
        ProfileAdmissionToken? admission)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureReady();
            PostFilterStore store = PostFilters;
            if (admission != null)
            {
                store.BeforeAdmittedMutationForTests = BeforePostFilterAdmittedMutationForTests;
                store.AfterAdmittedMutationForTests = AfterPostFilterAdmittedMutationForTests;
            }
            PostFilterSnapshot result = await Task.Run(() => action(store), token).ConfigureAwait(false);
            if (admission != null) SignalChanged();
            return result;
        }
        finally { _commands.Release(); }
    }

    /// <summary>The host as the reader's post filter service.</summary>
    internal IPostFilterService PostFilterService => _postFilterService ??= new HostPostFilterService(this);
    private IPostFilterService? _postFilterService;

    private sealed class HostPostFilterService(DesktopSessionHost host) : IPostFilterService
    {
        public Task<PostFilterSnapshot> LoadAsync(CancellationToken token) => host.LoadPostFiltersAsync(token);
        public Task<PostFilterSnapshot> RecoverAsync(long revision, CancellationToken token) => host.RecoverPostFiltersAsync(revision, token);
        public Task<PostFilterSnapshot> AddRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token) =>
            host.AddPostFilterRuleAsync(id, draft, revision, token);
        public Task<PostFilterSnapshot> SetRuleEnabledAsync(Guid id, bool enabled, long revision, CancellationToken token) =>
            host.SetPostFilterRuleEnabledAsync(id, enabled, revision, token);
        public Task<PostFilterSnapshot> RemoveRuleAsync(Guid id, long revision, CancellationToken token) => host.RemovePostFilterRuleAsync(id, revision, token);
        public Task<PostFilterSnapshot> SetHiddenPostsActionAsync(HiddenPostsAction action, long revision, CancellationToken token) =>
            host.SetHiddenPostsActionAsync(action, revision, token);
        public Task<PostFilterSnapshot> HidePostAsync(ResourceId thread, string post, bool replies, DateTimeOffset added, long revision, CancellationToken token) =>
            host.HidePostAsync(thread, post, replies, added, revision, token);
        public Task<PostFilterSnapshot> UnhidePostAsync(ResourceId thread, string post, long revision, CancellationToken token) =>
            host.UnhidePostAsync(thread, post, revision, token);
    }
}
