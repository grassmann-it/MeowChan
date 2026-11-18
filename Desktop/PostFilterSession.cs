using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

/// <summary>The post filter store as the reader and the dialog use it (the session host in production, a store in tests).</summary>
internal interface IPostFilterService
{
    Task<PostFilterSnapshot> LoadAsync(CancellationToken token);
    Task<PostFilterSnapshot> RecoverAsync(long revision, CancellationToken token);
    Task<PostFilterSnapshot> AddRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token);
    Task<PostFilterSnapshot> ReplaceRuleAsync(Guid id, PostFilterRuleDraft draft, long revision, CancellationToken token);
    Task<PostFilterSnapshot> SetRuleEnabledAsync(Guid id, bool enabled, long revision, CancellationToken token);
    Task<PostFilterSnapshot> RemoveRuleAsync(Guid id, long revision, CancellationToken token);
    Task<PostFilterSnapshot> SetHiddenPostsActionAsync(HiddenPostsAction action, long revision, CancellationToken token);
    Task<PostFilterSnapshot> HidePostAsync(ResourceId thread, string post, bool replies, DateTimeOffset added, long revision, CancellationToken token);
    Task<PostFilterSnapshot> UnhidePostAsync(ResourceId thread, string post, long revision, CancellationToken token);
}

/// <summary>
/// The one current snapshot of the post filters for this window: every change goes through here, one at a time and
/// against the revision it was made on, and <see cref="Changed"/> tells the reader and an open dialog. A revision
/// conflict takes the newer snapshot over and is reported, never retried implicitly.
/// </summary>
internal sealed class PostFilterSession
{
    private readonly IPostFilterService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _clock;

    internal PostFilterSession(IPostFilterService service, Func<DateTimeOffset>? clock = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Null until the first successful load.</summary>
    internal PostFilterSnapshot? Current { get; private set; }
    internal string? LoadError { get; private set; }
    internal event EventHandler? Changed;

    /// <summary>Loads (or reloads) the store. A failure keeps the previous snapshot and is kept in <see cref="LoadError"/>.</summary>
    internal async Task<PostFilterSnapshot?> LoadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            PostFilterSnapshot loaded = await _service.LoadAsync(token);
            LoadError = null;
            Publish(loaded);
            return loaded;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            LoadError = error.Message;
            Changed?.Invoke(this, EventArgs.Empty);
            return Current;
        }
        finally { _gate.Release(); }
    }

    internal Task<PostFilterSnapshot> RecoverAsync(CancellationToken token = default) =>
        MutateAsync((revision, ct) => _service.RecoverAsync(revision, ct), token);

    internal Task<PostFilterSnapshot> AddRuleAsync(PostFilterRuleDraft draft, CancellationToken token = default)
    {
        PostFilterRuleDraft normalized = PostFilterRules.Normalize(draft);
        Guid id = Guid.NewGuid();
        return MutateAsync((revision, ct) => _service.AddRuleAsync(id, normalized, revision, ct), token);
    }

    /// <summary>
    /// Replaces the content of a rule, keeping its ID and place; the duplicate check leaves the rule itself out. With
    /// <paramref name="expected"/> the replacement only happens while the stored rule still equals that snapshot (checked under the
    /// session's gate, against the newest snapshot): an edit built on a stale rule is refused as a conflict instead of overwriting.
    /// </summary>
    internal Task<PostFilterSnapshot> ReplaceRuleAsync(Guid id, PostFilterRuleDraft draft, CancellationToken token = default, PostFilterRule? expected = null)
    {
        PostFilterRuleDraft normalized = PostFilterRules.Normalize(draft);
        return MutateAsync((revision, ct) => _service.ReplaceRuleAsync(id, normalized, revision, ct), token,
            expected == null ? null : snapshot => snapshot.Rules.Any(rule => rule.Id == id && rule == expected));
    }

    internal Task<PostFilterSnapshot> SetRuleEnabledAsync(Guid id, bool enabled, CancellationToken token = default) =>
        MutateAsync((revision, ct) => _service.SetRuleEnabledAsync(id, enabled, revision, ct), token);

    internal Task<PostFilterSnapshot> RemoveRuleAsync(Guid id, CancellationToken token = default) =>
        MutateAsync((revision, ct) => _service.RemoveRuleAsync(id, revision, ct), token);

    internal Task<PostFilterSnapshot> SetHiddenPostsActionAsync(HiddenPostsAction action, CancellationToken token = default) =>
        MutateAsync((revision, ct) => _service.SetHiddenPostsActionAsync(action, revision, ct), token);

    internal Task<PostFilterSnapshot> HidePostAsync(ResourceId thread, string post, bool replies, CancellationToken token = default)
    {
        PostFilterRules.ValidateHiddenPost(thread, post);
        DateTimeOffset added = _clock();
        return MutateAsync((revision, ct) => _service.HidePostAsync(thread, post, replies, added, revision, ct), token);
    }

    internal Task<PostFilterSnapshot> UnhidePostAsync(ResourceId thread, string post, CancellationToken token = default) =>
        MutateAsync((revision, ct) => _service.UnhidePostAsync(thread, post, revision, ct), token);

    /// <summary>The post is hidden by hand (in the current snapshot).</summary>
    internal bool IsHidden(ResourceId thread, string post) =>
        Current?.HiddenPosts.Any(entry => entry.Thread == thread && string.Equals(entry.Post, post, StringComparison.Ordinal)) == true;

    private async Task<PostFilterSnapshot> MutateAsync(Func<long, CancellationToken, Task<PostFilterSnapshot>> change, CancellationToken token,
        Func<PostFilterSnapshot, bool>? precondition = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            PostFilterSnapshot current = Current ?? await _service.LoadAsync(token);
            if (Current == null) { LoadError = null; Publish(current); }
            try
            {
                if (precondition != null && !precondition(current)) throw new PostFilterRevisionException(current);
                PostFilterSnapshot committed = await change(current.Revision, token);
                Publish(committed);
                return committed;
            }
            catch (PostFilterRevisionException conflict)
            {
                Publish(conflict.Current);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private void Publish(PostFilterSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The sentence for a failed change: duplicates, the two limits and conflicts in the user's language.</summary>
    internal static string ErrorText(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is PostFilterDuplicateException duplicate) return Texts.T(duplicate.IsRule ? "postFilter.duplicate" : "postFilter.alreadyHidden");
        if (error is PostFilterRevisionException) return Texts.T("postFilter.conflict");
        if (StoreLimitText.Describe(error) is { } limit) return limit;
        // The session host already put the localized limit sentence into the message, with the refusal inside.
        if (error is InvalidDataException && StoreLimitText.Describe(error.InnerException ?? error) != null) return error.Message;
        return Texts.T("postFilter.failed", error.Message);
    }
}
