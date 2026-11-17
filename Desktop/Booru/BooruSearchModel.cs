using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginHost.Http;

namespace MeowChan.Desktop.Booru;

internal enum BooruSearchStatus
{
    Idle, Searching, Results, Empty, Offline, RateLimited, Cooldown, Denied, NotFound, Unavailable, Failed, InvalidInput
}

/// <summary>Immutable snapshot of the search tab. Views render it; tests assert on it.</summary>
internal sealed record BooruSearchView(
    string ProviderId,
    string ProviderName,
    BooruInputMode Mode,
    string Input,
    BooruParsedInput Parsed,
    BooruSearchStatus Status,
    string? ExecutedQuery,
    string? ExecutedPostId,
    int Page,
    bool IsEnd,
    ImmutableArray<BooruResultRowView> Rows,
    DateTimeOffset? RetryAt,
    string? Code,
    BooruNotice? Notice)
{
    internal bool IsPostResult => ExecutedPostId is not null;
    internal bool IsBusy => Status == BooruSearchStatus.Searching;
    /// <summary>Bounded paging: never beyond page 5, never after the provider reported the end.</summary>
    internal bool HasNextPage => Status == BooruSearchStatus.Results && ExecutedQuery is not null && !IsPostResult
        && !IsEnd && Page < BooruSearchModel.MaximumPage;
    /// <summary>Also offered after an offline, failed or cancelled page, so page N&gt;1 is never a dead end.</summary>
    internal bool HasPreviousPage => Status is BooruSearchStatus.Results or BooruSearchStatus.Empty
        or BooruSearchStatus.Offline or BooruSearchStatus.Failed or BooruSearchStatus.Idle
        && ExecutedQuery is not null && !IsPostResult && Page > 1;
    /// <summary>Explicit repeat of the shown search or lookup after an offline, failed or cancelled request; never for the initial Idle.</summary>
    internal bool CanRetry => Status is BooruSearchStatus.Offline or BooruSearchStatus.Failed or BooruSearchStatus.Idle
        && (ExecutedQuery is not null || ExecutedPostId is not null);
    /// <summary>True when the five-page bound, not the provider, is what stops the paging.</summary>
    internal bool PageLimitReached => Status == BooruSearchStatus.Results && ExecutedQuery is not null && !IsPostResult
        && !IsEnd && Page >= BooruSearchModel.MaximumPage;
}

/// <summary>
/// Search and one-shot logic of the Danbooru window. A request happens only on an explicit
/// <see cref="SubmitAsync"/>, <see cref="GoToPageAsync"/> or <see cref="LoadOriginalAsync"/>; editing the
/// field, switching the mode, language changes and cooldown expiry never send anything.
/// </summary>
internal sealed class BooruSearchModel : IDisposable
{
    internal const int MaximumPage = BooruHostHttp.MaximumSearchPage;
    internal const int PageSize = BooruHostHttp.MaximumSearchLimit;
    private static readonly TimeSpan DefaultPause = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly IBooruSearchPort _search;
    private readonly IBooruQueuePort _queue;
    private readonly IBooruPreviewPort _previews;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _previewSlots = new(2);
    private CancellationTokenSource? _request;
    // Prevents a double click from issuing two lifecycle mutations while the row is rebuilt.
    private readonly HashSet<string> _originalActions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _originalActionVersions = new(StringComparer.Ordinal);
    private long _nextOriginalAction;
    private long _requestId;
    private BooruSearchView _view;
    private bool _disposed;

    internal BooruSearchModel(IBooruSearchPort search, IBooruQueuePort queue, IBooruPreviewPort previews, TimeProvider clock)
    {
        _search = search;
        _queue = queue;
        _previews = previews;
        _clock = clock;
        _view = new BooruSearchView(search.ProviderId, search.ProviderName, BooruInputMode.Tags, string.Empty,
            new BooruParsedInput(BooruInputKind.Empty), BooruSearchStatus.Idle, null, null, 1, true,
            ImmutableArray<BooruResultRowView>.Empty, null, null, null);
        _queue.LocalStatesChanged += OnLocalStatesChanged;
    }

    /// <summary>Raised after every state change, from whatever thread caused it.</summary>
    internal event Action? Changed;

    internal BooruSearchView View { get { lock (_gate) return _view; } }

    /// <summary>True while a provider pause is in force; the search is then refused locally, without a request.</summary>
    internal bool IsPaused => PauseEnd(View) is not null;

    internal DateTimeOffset? PauseEnd(BooruSearchView view) =>
        view.Status is BooruSearchStatus.RateLimited or BooruSearchStatus.Cooldown
        && view.RetryAt is { } until && _clock.GetUtcNow() < until ? until : null;

    internal bool CanSubmit
    {
        get
        {
            BooruSearchView view = View;
            return !_disposed && view.Parsed.IsValid && !view.IsBusy && PauseEnd(view) is null;
        }
    }

    internal bool CanCreateWatcher
    {
        get
        {
            BooruSearchView view = View;
            return !_disposed && view.Mode == BooruInputMode.Tags && view.Parsed.Kind == BooruInputKind.Tags;
        }
    }

    // ---- input (never requests) ----------------------------------------------------------

    internal void SetMode(BooruInputMode mode)
    {
        lock (_gate)
        {
            if (_disposed || _view.Mode == mode) return;
            _view = _view with { Mode = mode, Parsed = BooruInput.Parse(mode, _view.Input) };
        }
        RaiseChanged();
    }

    internal void SetInput(string? text)
    {
        lock (_gate)
        {
            string value = text ?? string.Empty;
            if (_disposed || _view.Input == value) return;
            _view = _view with { Input = value, Parsed = BooruInput.Parse(_view.Mode, value) };
        }
        RaiseChanged();
    }

    /// <summary>A cooldown that has run out only re-enables the commands; it starts nothing.</summary>
    internal void Tick() => RaiseChanged();

    internal void DismissNotice()
    {
        lock (_gate)
        {
            if (_view.Notice is null) return;
            _view = _view with { Notice = null };
        }
        RaiseChanged();
    }

    internal void SetNotice(BooruNotice notice)
    {
        lock (_gate) _view = _view with { Notice = notice };
        RaiseChanged();
    }

    // ---- search / one-shot ---------------------------------------------------------------

    /// <summary>The Search button and the Enter key: a tag search, or a one-shot post lookup.</summary>
    internal async Task SubmitAsync()
    {
        BooruParsedInput parsed;
        bool refused = false;
        lock (_gate)
        {
            if (_disposed) return;
            parsed = _view.Parsed;
            if (!parsed.IsValid)
            {
                // Invalid input is explained locally; empty input only stays idle. No request.
                if (parsed.Kind == BooruInputKind.Invalid)
                    _view = _view with { Status = BooruSearchStatus.InvalidInput, Rows = ImmutableArray<BooruResultRowView>.Empty };
                refused = true;
            }
            else if (PauseEnd(_view) is not null || _view.IsBusy) refused = true;
            // A pasted post link switches the field to the one-shot mode visibly instead of silently.
            else if (parsed.Kind == BooruInputKind.Post && _view.Mode != BooruInputMode.Post)
                _view = _view with { Mode = BooruInputMode.Post };
        }
        if (refused) { RaiseChanged(); return; }
        if (parsed.Kind == BooruInputKind.Post) await RunPostAsync(parsed.PostId!).ConfigureAwait(false);
        else await RunSearchAsync(parsed.Canonical!, 1).ConfigureAwait(false);
    }

    /// <summary>Previous/next page of the search that is displayed (not of unsent edits in the field).</summary>
    internal Task GoToPageAsync(int page)
    {
        string query;
        lock (_gate)
        {
            if (_disposed || _view.ExecutedQuery is not { } executed || _view.IsPostResult || _view.IsBusy
                || page < 1 || page > MaximumPage || page == _view.Page
                || PauseEnd(_view) is not null
                || (page > _view.Page && (_view.IsEnd || _view.Status != BooruSearchStatus.Results)))
                return Task.CompletedTask;
            query = executed;
        }
        return RunSearchAsync(query, page);
    }

    /// <summary>Explicit "Search again" after an offline, failed or cancelled request: repeats exactly what was shown.</summary>
    internal Task RetryAsync()
    {
        string? query, postId;
        int page;
        lock (_gate)
        {
            if (_disposed || _view.IsBusy || PauseEnd(_view) is not null || !_view.CanRetry)
                return Task.CompletedTask;
            (query, postId, page) = (_view.ExecutedQuery, _view.ExecutedPostId, _view.Page);
        }
        if (postId is not null) return RunPostAsync(postId);
        return query is null ? Task.CompletedTask : RunSearchAsync(query, page);
    }

    internal void Cancel()
    {
        lock (_gate)
        {
            if (!_view.IsBusy) return;
            _request?.Cancel();
            _requestId++;
            _view = _view with { Status = BooruSearchStatus.Idle };
        }
        RaiseChanged();
    }

    private async Task RunSearchAsync(string query, int page)
    {
        (long id, CancellationToken token) = Begin(BooruSearchStatus.Searching, query, null, page);
        if (id == 0) return;
        RaiseChanged();
        BooruCallResult<BooruResultPage> result;
        try
        {
            result = await _search.SearchAsync(query, page, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = BooruCallResult<BooruResultPage>.Of(BooruCallStatus.Failed, "exception");
        }

        bool follow = false;
        lock (_gate)
        {
            if (id != _requestId || _disposed) return;
            if (result.Status == BooruCallStatus.Ok && result.Value is { } value)
            {
                if (value.Page != page || !string.Equals(value.Query, query, StringComparison.Ordinal)
                    || value.Posts.Length > PageSize)
                {
                    _view = _view with { Status = BooruSearchStatus.Failed, Code = "page_binding" };
                }
                else
                {
                    ImmutableArray<BooruResultRowView> rows = value.Posts
                        .Select(post => new BooruResultRowView(post,
                            Preview: post.PreviewUrl is null ? BooruPreviewState.None : BooruPreviewState.Loading))
                        .ToImmutableArray();
                    _view = _view with
                    {
                        Status = rows.IsEmpty ? BooruSearchStatus.Empty : BooruSearchStatus.Results,
                        Rows = rows, IsEnd = value.IsEnd || rows.IsEmpty,
                        Code = null, RetryAt = null
                    };
                    follow = !rows.IsEmpty;
                }
            }
            else _view = ApplyFailure(_view, result);
        }
        RaiseChanged();
        if (follow) StartFollowUps(id, token);
    }

    private async Task RunPostAsync(string postId)
    {
        (long id, CancellationToken token) = Begin(BooruSearchStatus.Searching, null, postId, 1);
        if (id == 0) return;
        RaiseChanged();
        BooruCallResult<BooruPostView> result;
        try
        {
            result = await _search.ReadPostAsync(postId, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            result = BooruCallResult<BooruPostView>.Of(BooruCallStatus.Failed, "exception");
        }

        bool follow = false;
        lock (_gate)
        {
            if (id != _requestId || _disposed) return;
            if (result.Status == BooruCallStatus.Ok && result.Value is { } post
                && string.Equals(post.PostId, postId, StringComparison.Ordinal))
            {
                _view = _view with
                {
                    Status = BooruSearchStatus.Results, IsEnd = true, Code = null, RetryAt = null,
                    Rows = ImmutableArray.Create(new BooruResultRowView(post,
                        Preview: post.PreviewUrl is null ? BooruPreviewState.None : BooruPreviewState.Loading))
                };
                follow = true;
            }
            else if (result.Status == BooruCallStatus.Ok)
                _view = _view with { Status = BooruSearchStatus.Failed, Code = "wrong_post" };
            else _view = ApplyFailure(_view, result);
        }
        RaiseChanged();
        if (follow) StartFollowUps(id, token);
    }

    private (long Id, CancellationToken Token) Begin(BooruSearchStatus status, string? query, string? postId, int page)
    {
        lock (_gate)
        {
            if (_disposed) return (0, default);
            _request?.Cancel();
            _request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _view = _view with
            {
                Status = status, ExecutedQuery = query, ExecutedPostId = postId, Page = page,
                Rows = ImmutableArray<BooruResultRowView>.Empty, Code = null, RetryAt = null, IsEnd = false, Notice = null
            };
            long id = ++_requestId;
            return (id, _request.Token);
        }
    }

    private BooruSearchView ApplyFailure<T>(BooruSearchView view, BooruCallResult<T> result)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        BooruSearchStatus status = result.Status switch
        {
            BooruCallStatus.Offline => BooruSearchStatus.Offline,
            BooruCallStatus.RateLimited => BooruSearchStatus.RateLimited,
            BooruCallStatus.Cooldown => BooruSearchStatus.Cooldown,
            BooruCallStatus.Denied => BooruSearchStatus.Denied,
            BooruCallStatus.NotFound => BooruSearchStatus.NotFound,
            BooruCallStatus.InvalidInput => BooruSearchStatus.InvalidInput,
            BooruCallStatus.Unavailable => BooruSearchStatus.Unavailable,
            _ => BooruSearchStatus.Failed
        };
        DateTimeOffset? retryAt = status is BooruSearchStatus.RateLimited or BooruSearchStatus.Cooldown
            ? (result.RetryAt is { } at && at > now ? at : now + DefaultPause) : null;
        return view with { Status = status, Code = result.Code, RetryAt = retryAt, Rows = ImmutableArray<BooruResultRowView>.Empty };
    }

    // ---- previews and local state (follow-ups of a displayed page only) ------------------------

    private void StartFollowUps(long id, CancellationToken token)
    {
        ImmutableArray<BooruResultRowView> rows = View.Rows;
        foreach (BooruResultRowView row in rows.Where(row => row.Preview == BooruPreviewState.Loading))
        {
            BooruPostView post = row.Post;
            _ = Task.Run(() => LoadPreviewAsync(id, post, token), CancellationToken.None);
        }
        _ = Task.Run(() => RefreshLocalStatesAsync(id, token), CancellationToken.None);
    }

    private async Task LoadPreviewAsync(long id, BooruPostView post, CancellationToken token)
    {
        bool entered = false;
        byte[]? bytes = null;
        try
        {
            await _previewSlots.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            bytes = await _previews.LoadPreviewAsync(post, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { bytes = null; }
        finally
        {
            if (entered) _previewSlots.Release();
        }
        UpdateRow(id, post.PostId, row => row with
        {
            Preview = bytes is { Length: > 0 } ? BooruPreviewState.Ready : BooruPreviewState.Unavailable,
            PreviewBytes = bytes is { Length: > 0 } ? bytes : null
        });
    }

    private void OnLocalStatesChanged()
    {
        long id;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _request is null || _view.Rows.IsEmpty) return;
            id = _requestId;
            token = _request.Token;
        }
        _ = Task.Run(() => RefreshLocalStatesAsync(id, token), CancellationToken.None);
    }

    private async Task RefreshLocalStatesAsync(long id, CancellationToken token)
    {
        string[] ids;
        lock (_gate)
        {
            if (id != _requestId) return;
            ids = _view.Rows.Select(row => row.Post.PostId).ToArray();
        }
        if (ids.Length == 0) return;
        IReadOnlyDictionary<string, BooruLocalState> states;
        try { states = await _queue.GetLocalStatesAsync(ids, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return; }
        bool changed = false;
        lock (_gate)
        {
            if (id != _requestId || _disposed) return;
            ImmutableArray<BooruResultRowView>.Builder rows = _view.Rows.ToBuilder();
            for (int index = 0; index < rows.Count; index++)
            {
                BooruResultRowView row = rows[index];
                if (!states.TryGetValue(row.Post.PostId, out BooruLocalState state)) continue;
                // A failed row only ever follows a committed file (the original arrived by another path); every other
                // change of state belongs to the explicit retry.
                if (row.Load == BooruRowLoad.Failed ? state != BooruLocalState.Loaded
                    : row.Load is not (BooruRowLoad.Idle or BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued or BooruRowLoad.AlreadyLoaded))
                    continue;
                BooruRowLoad load = state switch
                {
                    BooruLocalState.Loaded => BooruRowLoad.AlreadyLoaded,
                    BooruLocalState.Queued => row.Load == BooruRowLoad.Queued ? BooruRowLoad.Queued : BooruRowLoad.AlreadyQueued,
                    // A download, link or admission that failed in the queue: shown as a failed row with the explicit retry.
                    BooruLocalState.Failed => row.Post.OriginalAccess == BooruOriginalAccess.Available ? BooruRowLoad.Failed : row.Load,
                    _ => row.Load is BooruRowLoad.AlreadyLoaded or BooruRowLoad.AlreadyQueued ? BooruRowLoad.Idle : row.Load
                };
                if (load == row.Load) continue;
                rows[index] = load == BooruRowLoad.Failed ? row with { Load = load, LoadCode = BooruCodes.NotAdmitted }
                    : row with { Load = load };
                changed = true;
            }
            if (changed) _view = _view with { Rows = rows.ToImmutable() };
        }
        if (changed) RaiseChanged();
    }

    // ---- load original (the explicit action) ---------------------------------------------------

    /// <summary>
    /// Hands one post's original to the queue port. A post without a usable original never reaches
    /// the port; a row that is already handing over, queued or loaded is not queued a second time.
    /// </summary>
    internal async Task LoadOriginalAsync(string postId)
    {
        BooruPostView post;
        BooruLoadOrigin origin;
        string? query = null;
        long actionVersion = 0;
        bool blocked = false;
        lock (_gate)
        {
            if (_disposed) return;
            int index = _view.Rows.IndexWhere(row => row.Post.PostId == postId);
            if (index < 0) return;
            BooruResultRowView row = _view.Rows[index];
            post = row.Post;
            origin = _view.IsPostResult ? BooruLoadOrigin.OneShot : BooruLoadOrigin.Search;
            // The search that found the post is the provenance of a search finding; a post link has none.
            if (origin == BooruLoadOrigin.Search) query = _view.ExecutedQuery;
            if (row.Post.OriginalAccess != BooruOriginalAccess.Available)
            {
                BooruRowLoad reason = row.Post.OriginalAccess == BooruOriginalAccess.Restricted ? BooruRowLoad.Restricted : BooruRowLoad.NoOriginal;
                SetRow(index, row with { Load = reason });
                blocked = true;
            }
            else if (!row.CanLoad) return;
            else
            {
                actionVersion = BeginOriginalAction(postId);
                SetRow(index, row with { Load = BooruRowLoad.Handing, LoadCode = null });
            }
        }
        RaiseChanged();
        if (blocked) return;
        BooruLoadResult result;
        try { result = await _queue.LoadOriginalAsync(post, origin, query, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { result = new BooruLoadResult(BooruLoadStatus.Failed, "canceled"); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { result = new BooruLoadResult(BooruLoadStatus.Failed, "exception"); }
        BooruRowLoad load = MapLoad(result);
        lock (_gate)
        {
            if (_disposed) return;
            int index = _view.Rows.IndexWhere(row => row.Post.PostId == post.PostId);
            // A concurrent cancel/retry owns the newer version; never let this admission resurrect a cancelled row.
            if (index >= 0 && IsCurrentOriginalAction(post.PostId, actionVersion))
                SetRow(index, _view.Rows[index] with { Load = load, LoadCode = result.Code });
        }
        RaiseChanged();
    }

    /// <summary>Durably cancels one queued or in-flight original through the queue port.</summary>
    internal async Task CancelOriginalAsync(string postId)
    {
        bool started = false;
        long actionVersion = 0;
        lock (_gate)
        {
            if (_disposed) return;
            int index = _view.Rows.IndexWhere(row => row.Post.PostId == postId);
            if (index < 0) return;
            BooruResultRowView row = _view.Rows[index];
            if (!row.CanCancel || !_originalActions.Add(postId)) return;
            actionVersion = BeginOriginalAction(postId);
            SetRow(index, row with { Load = BooruRowLoad.Handing, LoadCode = null });
            started = true;
        }
        if (!started) return;
        RaiseChanged();
        bool cancelled;
        string code;
        try
        {
            cancelled = await _queue.CancelOriginalAsync(postId, _lifetime.Token).ConfigureAwait(false);
            code = cancelled ? BooruCodes.Cancelled : BooruCodes.CancelNotPending;
        }
        catch (OperationCanceledException) { cancelled = false; code = BooruCodes.Canceled; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            cancelled = false;
            code = BooruCodes.CancelFailed;
        }
        lock (_gate)
        {
            _originalActions.Remove(postId);
            if (_disposed) return;
            int index = _view.Rows.IndexWhere(row => row.Post.PostId == postId);
            if (index >= 0 && IsCurrentOriginalAction(postId, actionVersion))
                SetRow(index, _view.Rows[index] with { Load = BooruRowLoad.Failed, LoadCode = code });
        }
        RaiseChanged();
    }

    /// <summary>Explicitly retries a stored claim after a cancellation or failed admission.</summary>
    internal async Task RetryOriginalAsync(string postId)
    {
        BooruPostView post;
        long actionVersion = 0;
        lock (_gate)
        {
            if (_disposed) return;
            int index = _view.Rows.IndexWhere(row => row.Post.PostId == postId);
            if (index < 0) return;
            BooruResultRowView row = _view.Rows[index];
            if (!row.CanRetry || !_originalActions.Add(postId)) return;
            actionVersion = BeginOriginalAction(postId);
            post = row.Post;
            SetRow(index, row with { Load = BooruRowLoad.Handing, LoadCode = null });
        }
        RaiseChanged();
        BooruLoadResult result;
        try { result = await _queue.RetryOriginalAsync(post.PostId, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { result = new BooruLoadResult(BooruLoadStatus.Failed, BooruCodes.Canceled); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { result = new BooruLoadResult(BooruLoadStatus.Failed, "exception"); }
        lock (_gate)
        {
            _originalActions.Remove(postId);
            if (_disposed) return;
            int index = _view.Rows.IndexWhere(row => row.Post.PostId == postId);
            if (index >= 0 && IsCurrentOriginalAction(postId, actionVersion))
                SetRow(index, _view.Rows[index] with { Load = MapLoad(result), LoadCode = result.Code });
        }
        RaiseChanged();
    }

    private long BeginOriginalAction(string postId)
    {
        long version = ++_nextOriginalAction;
        _originalActionVersions[postId] = version;
        return version;
    }

    private bool IsCurrentOriginalAction(string postId, long version) =>
        _originalActionVersions.TryGetValue(postId, out long current) && current == version;

    private static BooruRowLoad MapLoad(BooruLoadResult result) => result.Status switch
    {
        BooruLoadStatus.Queued => BooruRowLoad.Queued,
        BooruLoadStatus.AlreadyQueued => BooruRowLoad.AlreadyQueued,
        BooruLoadStatus.AlreadyLoaded => BooruRowLoad.AlreadyLoaded,
        BooruLoadStatus.NoOriginal => BooruRowLoad.NoOriginal,
        BooruLoadStatus.Restricted => BooruRowLoad.Restricted,
        BooruLoadStatus.Offline => BooruRowLoad.Offline,
        _ => BooruRowLoad.Failed
    };

    private void UpdateRow(long id, string postId, Func<BooruResultRowView, BooruResultRowView> update)
    {
        lock (_gate)
        {
            if (id != _requestId || _disposed) return;
            int index = _view.Rows.IndexWhere(row => row.Post.PostId == postId);
            if (index < 0) return;
            SetRow(index, update(_view.Rows[index]));
        }
        RaiseChanged();
    }

    private void SetRow(int index, BooruResultRowView row) =>
        _view = _view with { Rows = _view.Rows.SetItem(index, row) };

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _requestId++;
            _request?.Cancel();
            _originalActions.Clear();
            _originalActionVersions.Clear();
        }
        _queue.LocalStatesChanged -= OnLocalStatesChanged;
        _lifetime.Cancel();
        Changed = null;
    }
}

internal static class ImmutableArrayExtensions
{
    internal static int IndexWhere<T>(this ImmutableArray<T> array, Func<T, bool> predicate)
    {
        for (int index = 0; index < array.Length; index++)
            if (predicate(array[index])) return index;
        return -1;
    }
}
