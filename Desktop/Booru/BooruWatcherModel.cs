using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop.Booru;

internal enum BooruWatcherListStatus { Loading, Ready, Empty, Failed }

/// <summary>One watcher row: the port's view plus the UI-only state of the running command.</summary>
internal sealed record BooruWatcherRowView(
    BooruWatcherView Watcher,
    bool Busy = false,
    BooruNotice? Message = null,
    bool ConfirmingRemoval = false)
{
    internal string WatcherId => Watcher.WatcherId;
}

internal sealed record BooruWatcherListView(
    BooruWatcherListStatus Status,
    ImmutableArray<BooruWatcherRowView> Rows,
    bool Creating,
    BooruNotice? Notice,
    string? Code);

/// <summary>Why a command is unavailable; the view shows it as the disabled button's explanation.</summary>
internal enum BooruWatcherBlock { None, Busy, Removed, Paused, Checking, CoolingDown, NotPaused }

/// <summary>
/// Watcher list and its five explicit commands. Nothing here polls or schedules: a paused, checking,
/// cooling or removed watcher is refused locally and the port is not called, so the UI itself can never
/// cause a hidden provider request. Removal is confirmed inline and never touches local media.
/// </summary>
internal sealed class BooruWatcherListModel : IDisposable
{
    private readonly object _gate = new();
    private readonly IBooruWatcherPort _port;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private long _refreshId;
    private BooruWatcherListView _view = new(BooruWatcherListStatus.Loading, ImmutableArray<BooruWatcherRowView>.Empty, false, null, null);
    private bool _disposed;

    internal BooruWatcherListModel(IBooruWatcherPort port, TimeProvider clock)
    {
        _port = port;
        _clock = clock;
        _port.Changed += OnPortChanged;
    }

    internal event Action? Changed;

    internal BooruWatcherListView View { get { lock (_gate) return _view; } }

    /// <summary>Watchers that still observe anything (everything but removed ones).</summary>
    internal int Count => View.Rows.Count(row => row.Watcher.Status != BooruWatcherStatus.Removed);

    /// <summary>Cooldown that is still in force at this moment, or null (a passed cooldown is simply over).</summary>
    internal DateTimeOffset? ActiveCooldown(BooruWatcherView watcher) =>
        watcher.Status == BooruWatcherStatus.Cooldown && watcher.CooldownUntil is { } until && _clock.GetUtcNow() < until
            ? until : null;

    internal BooruWatcherBlock CheckBlock(BooruWatcherRowView row) => row.Busy ? BooruWatcherBlock.Busy
        : row.Watcher.Status switch
        {
            BooruWatcherStatus.Removed => BooruWatcherBlock.Removed,
            // A paused watcher may be checked once on explicit request; it stays paused afterwards.
            BooruWatcherStatus.Checking => BooruWatcherBlock.Checking,
            BooruWatcherStatus.Cooldown when ActiveCooldown(row.Watcher) is not null => BooruWatcherBlock.CoolingDown,
            _ => BooruWatcherBlock.None
        };

    internal BooruWatcherBlock PauseBlock(BooruWatcherRowView row) => row.Busy ? BooruWatcherBlock.Busy
        : row.Watcher.Status switch
        {
            BooruWatcherStatus.Removed => BooruWatcherBlock.Removed,
            BooruWatcherStatus.Paused => BooruWatcherBlock.Paused,
            _ => BooruWatcherBlock.None
        };

    internal BooruWatcherBlock ResumeBlock(BooruWatcherRowView row) => row.Busy ? BooruWatcherBlock.Busy
        : row.Watcher.Status switch
        {
            BooruWatcherStatus.Paused => BooruWatcherBlock.None,
            BooruWatcherStatus.Removed => BooruWatcherBlock.Removed,
            _ => BooruWatcherBlock.NotPaused
        };

    internal BooruWatcherBlock RemoveBlock(BooruWatcherRowView row) => row.Busy ? BooruWatcherBlock.Busy
        : row.Watcher.Status == BooruWatcherStatus.Removed ? BooruWatcherBlock.Removed : BooruWatcherBlock.None;

    // ---- list ----------------------------------------------------------------------------

    internal async Task RefreshAsync()
    {
        long id;
        lock (_gate)
        {
            if (_disposed) return;
            id = ++_refreshId;
        }
        IReadOnlyList<BooruWatcherView> list;
        try { list = await _port.ListAsync(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            lock (_gate)
            {
                if (id != _refreshId || _disposed) return;
                _view = _view with { Status = BooruWatcherListStatus.Failed, Code = "exception" };
            }
            RaiseChanged();
            return;
        }
        lock (_gate)
        {
            if (id != _refreshId || _disposed) return;
            // Keep the UI-only state (busy, message, open confirmation) of rows that still exist.
            Dictionary<string, BooruWatcherRowView> previous = _view.Rows.ToDictionary(row => row.WatcherId, StringComparer.Ordinal);
            ImmutableArray<BooruWatcherRowView> rows = list
                .Where(watcher => watcher.Status != BooruWatcherStatus.Removed)
                .Select(watcher => previous.TryGetValue(watcher.WatcherId, out BooruWatcherRowView? old)
                    ? old with { Watcher = watcher } : new BooruWatcherRowView(watcher))
                .ToImmutableArray();
            _view = _view with
            {
                Rows = rows, Code = null,
                Status = rows.IsEmpty ? BooruWatcherListStatus.Empty : BooruWatcherListStatus.Ready
            };
        }
        RaiseChanged();
    }

    private void OnPortChanged() => _ = RefreshAsync();

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

    // ---- create (from the search tab) ----------------------------------------------------------

    /// <summary>True when a live watcher for exactly this canonical query already exists.</summary>
    internal bool HasWatcherFor(string canonicalQuery) => View.Rows.Any(row =>
        row.Watcher.Status != BooruWatcherStatus.Removed
        && string.Equals(row.Watcher.Query, canonicalQuery, StringComparison.Ordinal));

    internal async Task<BooruWatcherCommandResult> CreateAsync(string canonicalQuery)
    {
        lock (_gate)
        {
            if (_disposed) return new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, "closed");
            if (_view.Creating) return new BooruWatcherCommandResult(BooruWatcherCommandStatus.Rejected, "busy");
            _view = _view with { Creating = true };
        }
        RaiseChanged();
        BooruWatcherCommandResult result = await RunAsync(() => _port.CreateAsync(canonicalQuery, _lifetime.Token)).ConfigureAwait(false);
        lock (_gate) _view = _view with { Creating = false };
        RaiseChanged();
        if (result.Status == BooruWatcherCommandStatus.Done) await RefreshAsync().ConfigureAwait(false);
        return result;
    }

    // ---- row commands ----------------------------------------------------------------------

    internal Task<BooruWatcherCommandResult?> CheckNowAsync(string watcherId)
    {
        bool paused = View.Rows.Any(row => row.WatcherId == watcherId && row.Watcher.Status == BooruWatcherStatus.Paused);
        return CommandAsync(watcherId, CheckBlock, (id, token) => _port.CheckNowAsync(id, token),
            result => CheckMessage(result, paused), refreshAfter: true);
    }

    internal Task<BooruWatcherCommandResult?> PauseAsync(string watcherId) => CommandAsync(watcherId, PauseBlock,
        (id, token) => _port.PauseAsync(id, token), _ => new BooruNotice(BooruNoticeKind.Info, "booru.ui.watcher.msg.paused"), refreshAfter: true);

    internal Task<BooruWatcherCommandResult?> ResumeAsync(string watcherId) => CommandAsync(watcherId, ResumeBlock,
        (id, token) => _port.ResumeAsync(id, token), _ => new BooruNotice(BooruNoticeKind.Info, "booru.ui.watcher.msg.resumed"), refreshAfter: true);

    /// <summary>First step of removal: only opens the inline confirmation; nothing is removed yet.</summary>
    internal void RequestRemoval(string watcherId)
    {
        lock (_gate)
        {
            int index = _view.Rows.IndexWhere(row => row.WatcherId == watcherId);
            if (_disposed || index < 0 || RemoveBlock(_view.Rows[index]) != BooruWatcherBlock.None) return;
            // One open confirmation at a time.
            _view = _view with
            {
                Rows = _view.Rows.Select(row => row with { ConfirmingRemoval = row.WatcherId == watcherId }).ToImmutableArray()
            };
        }
        RaiseChanged();
    }

    internal void CancelRemoval(string watcherId)
    {
        lock (_gate)
        {
            int index = _view.Rows.IndexWhere(row => row.WatcherId == watcherId);
            if (index < 0 || !_view.Rows[index].ConfirmingRemoval) return;
            _view = _view with { Rows = _view.Rows.SetItem(index, _view.Rows[index] with { ConfirmingRemoval = false }) };
        }
        RaiseChanged();
    }

    /// <summary>Second step: removes the observation only. Local media, source tags and your tags stay.</summary>
    internal async Task<BooruWatcherCommandResult?> ConfirmRemovalAsync(string watcherId)
    {
        lock (_gate)
        {
            int index = _view.Rows.IndexWhere(row => row.WatcherId == watcherId);
            if (_disposed || index < 0 || !_view.Rows[index].ConfirmingRemoval) return null;
        }
        BooruWatcherCommandResult? result = await CommandAsync(watcherId, RemoveBlock,
            (id, token) => _port.RemoveAsync(id, token), _ => null, refreshAfter: false).ConfigureAwait(false);
        if (result is { Status: BooruWatcherCommandStatus.Done })
        {
            lock (_gate)
            {
                _view = _view with
                {
                    Rows = _view.Rows.Where(row => row.WatcherId != watcherId).ToImmutableArray(),
                    Notice = new BooruNotice(BooruNoticeKind.Success, "booru.ui.watcher.msg.removed")
                };
                if (_view.Rows.IsEmpty) _view = _view with { Status = BooruWatcherListStatus.Empty };
            }
            RaiseChanged();
        }
        else
        {
            lock (_gate)
            {
                int index = _view.Rows.IndexWhere(row => row.WatcherId == watcherId);
                if (index >= 0) _view = _view with { Rows = _view.Rows.SetItem(index, _view.Rows[index] with { ConfirmingRemoval = false }) };
            }
            RaiseChanged();
        }
        return result;
    }

    private async Task<BooruWatcherCommandResult?> CommandAsync(string watcherId, Func<BooruWatcherRowView, BooruWatcherBlock> block,
        Func<string, CancellationToken, Task<BooruWatcherCommandResult>> call,
        Func<BooruWatcherCommandResult, BooruNotice?> success, bool refreshAfter)
    {
        lock (_gate)
        {
            int index = _view.Rows.IndexWhere(row => row.WatcherId == watcherId);
            // The decision uses the latest known state, never a stale button: a paused, cooling,
            // checking or removed watcher never reaches the port.
            if (_disposed || index < 0 || block(_view.Rows[index]) != BooruWatcherBlock.None) return null;
            _view = _view with { Rows = _view.Rows.SetItem(index, _view.Rows[index] with { Busy = true, Message = null }) };
        }
        RaiseChanged();
        BooruWatcherCommandResult result = await RunAsync(() => call(watcherId, _lifetime.Token)).ConfigureAwait(false);
        lock (_gate)
        {
            int index = _view.Rows.IndexWhere(row => row.WatcherId == watcherId);
            if (index >= 0)
            {
                BooruNotice? message = result.Status == BooruWatcherCommandStatus.Done ? success(result) : FailureMessage(result);
                _view = _view with { Rows = _view.Rows.SetItem(index, _view.Rows[index] with { Busy = false, Message = message }) };
            }
        }
        RaiseChanged();
        if (refreshAfter) await RefreshAsync().ConfigureAwait(false);
        return result;
    }

    private static async Task<BooruWatcherCommandResult> RunAsync(Func<Task<BooruWatcherCommandResult>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (OperationCanceledException) { return new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, "canceled"); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new BooruWatcherCommandResult(BooruWatcherCommandStatus.Failed, "exception");
        }
    }

    /// <summary>A check of a paused watcher says that it was one-time and that the watcher is still paused.</summary>
    private static BooruNotice CheckMessage(BooruWatcherCommandResult result, bool wasPaused)
    {
        if (wasPaused)
            return new BooruNotice(BooruNoticeKind.Success, "booru.ui.watcher.msg.checkedPaused", new object?[] { result.NewPosts, result.Queued });
        return result.NewPosts == 0
            ? new BooruNotice(BooruNoticeKind.Success, "booru.ui.watcher.msg.checkedNone")
            : new BooruNotice(BooruNoticeKind.Success, "booru.ui.watcher.msg.checked", new object?[] { result.NewPosts, result.Queued });
    }

    private static BooruNotice FailureMessage(BooruWatcherCommandResult result) => result.Status switch
    {
        BooruWatcherCommandStatus.Rejected => new BooruNotice(BooruNoticeKind.Warning, "booru.ui.watcher.reject", new object?[] { result.Code ?? string.Empty }),
        BooruWatcherCommandStatus.RateLimited => new BooruNotice(BooruNoticeKind.Warning, "booru.ui.watcher.msg.rateLimited",
            new object?[] { result.RetryAt?.ToLocalTime() }),
        BooruWatcherCommandStatus.Cooldown => new BooruNotice(BooruNoticeKind.Warning, "booru.ui.watcher.msg.cooldown",
            new object?[] { result.RetryAt?.ToLocalTime() }),
        BooruWatcherCommandStatus.Offline => new BooruNotice(BooruNoticeKind.Warning, "booru.ui.watcher.msg.offline"),
        _ => new BooruNotice(BooruNoticeKind.Error, "booru.ui.watcher.msg.failed", new object?[] { result.Code ?? "error" })
    };

    private void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _refreshId++;
        }
        _port.Changed -= OnPortChanged;
        _lifetime.Cancel();
        Changed = null;
    }
}
