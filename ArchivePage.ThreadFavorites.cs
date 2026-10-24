using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Favorites;

namespace MeowChan;

public partial class ArchivePage
{
    private Func<ThreadFavoriteLoadSnapshot>? _loadThreadFavorites;
    private Func<ResourceId, bool, CancellationToken, Task<ThreadFavoriteCommit>>? _setThreadFavorite;
    private ThreadFavoriteSnapshot? _threadFavoriteSnapshot;
    private HashSet<ResourceId> _threadFavoriteIds = new();
    private readonly HashSet<ResourceId> _threadFavoritePending = new();
    private readonly Dictionary<ResourceId, string> _threadFavoriteErrors = new();
    private string? _threadFavoriteLoadError;
    private ArchiveThreadRow? _detailThreadFavoriteRow;
    private CommandRouter? _threadFavoriteCommands;

    private void InitializeThreadFavoriteCommands()
    {
        foreach (Control surface in new Control[] { Control<ListBox>("ArchiveThreads"),
            Control<DataGrid>("ArchiveThreadTable"), Control<ListBox>("ArchivePosts") })
            surface.AddHandler(KeyDownEvent, OnThreadFavoriteKeyDown, RoutingStrategies.Tunnel);
        // This pane keeps ownership when it moves between the archive and large reader.
        // Route its command from toolbar focus as well as post-list focus, preserving
        // the shared router's native editor, selector and Enter/Space exclusions.
        Control pane = Control<Grid>("ArchiveReaderPane");
        pane.AttachedToVisualTree += (_, _) =>
        {
            _threadFavoriteCommands?.Dispose();
            _threadFavoriteCommands = new(pane);
            _threadFavoriteCommands.Register(new("thread.favorite-toggle", pane,
                () => pane.IsEffectivelyVisible && !IsReaderDialogOpen,
                () => _thread?.Summary.Id is ResourceId id && CanToggleThreadFavorite(id),
                () => { if (_thread?.Summary.Id is ResourceId id) _ = ToggleThreadFavoriteAsync(id); }, Priority: 100));
        };
        pane.DetachedFromVisualTree += (_, _) =>
        { _threadFavoriteCommands?.Dispose(); _threadFavoriteCommands = null; };
        pane.KeyDown += (_, e) => _threadFavoriteCommands?.Route(e);
    }

    private async void OnThreadFavoriteKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _ended || sender is not Control surface || IsReaderDialogOpen
            || !MainWindow.IsListCommandSource(e.Source, surface) || !MatchesArchiveCommand(e, "thread.favorite-toggle")) return;
        bool posts = ReferenceEquals(surface, Control<ListBox>("ArchivePosts"));
        ResourceId? id = posts ? ReaderShowsPosts ? _thread?.Summary.Id : null
            : _largeReader ? null : ArchiveLead?.Summary.Id;
        if (id == null || !CanToggleThreadFavorite(id)) return;
        e.Handled = true;
        await ToggleThreadFavoriteAsync(id);
    }

    internal void AttachThreadFavorites(DesktopSessionHost host)
    {
        _loadThreadFavorites = host.GetThreadFavoriteSnapshot;
        _setThreadFavorite = host.SetThreadFavoriteAsync;
    }

    private void ConfigureThreadFavoriteRows(IEnumerable<ArchiveThreadRow> rows)
    {
        ReloadThreadFavorites();
        foreach (ArchiveThreadRow row in rows)
        {
            ResourceId id = row.Summary.Id;
            row.ToggleThreadFavoriteAsync = () => ToggleThreadFavoriteAsync(id);
            UpdateThreadFavoriteRow(row);
        }
    }

    private void ReloadThreadFavorites()
    {
        if (_ended || _loadThreadFavorites == null) return;
        ThreadFavoriteLoadSnapshot loaded = _loadThreadFavorites();
        _threadFavoriteSnapshot = loaded.State == ThreadFavoriteLoadState.Ready ? loaded.Snapshot : null;
        _threadFavoriteIds = _threadFavoriteSnapshot?.Threads.ToHashSet() ?? new();
        _threadFavoriteLoadError = _threadFavoriteSnapshot == null ? ThreadFavoriteText.LoadError(loaded.Error ?? string.Empty) : null;
        if (_threadFavoriteLoadError != null) OperationMessage("thread-favorites-load", _threadFavoriteLoadError, true);
        else ClearOperationError("thread-favorites-load");
    }

    private bool CanToggleThreadFavorite(ResourceId id) => _available && !_ended && !_busy
        && _threadFavoriteSnapshot != null && _setThreadFavorite != null && !_threadFavoritePending.Contains(id);

    private void UpdateThreadFavoriteRow(ArchiveThreadRow row) => row.SetThreadFavorite(
        _threadFavoriteSnapshot == null ? null : _threadFavoriteIds.Contains(row.Summary.Id),
        CanToggleThreadFavorite(row.Summary.Id),
        _threadFavoriteErrors.GetValueOrDefault(row.Summary.Id) ?? _threadFavoriteLoadError);

    private void UpdateThreadFavoriteActions()
    {
        foreach (ArchiveThreadRow row in ArchiveVisibleRows) UpdateThreadFavoriteRow(row);
        ArchiveThreadFavoriteButton? button = this.FindControl<ArchiveThreadFavoriteButton>("ArchiveCurrentThreadFavorite");
        if (button == null) return;
        ResourceId? id = _thread?.Summary.Id;
        if (id == null) { _detailThreadFavoriteRow = null; button.DataContext = null; return; }
        if (_detailThreadFavoriteRow?.Summary.Id != id)
        {
            _detailThreadFavoriteRow = new ArchiveThreadRow(_thread!.Summary)
            { ToggleThreadFavoriteAsync = () => ToggleThreadFavoriteAsync(id) };
            button.DataContext = _detailThreadFavoriteRow;
        }
        UpdateThreadFavoriteRow(_detailThreadFavoriteRow);
    }

    private async Task ToggleThreadFavoriteAsync(ResourceId id)
    {
        if (!CanToggleThreadFavorite(id)) return;
        bool favorite = !_threadFavoriteIds.Contains(id);
        _threadFavoritePending.Add(id);
        _threadFavoriteErrors.Remove(id);
        ClearOperationError("thread-favorites-save");
        UpdateThreadFavoriteActions();
        try
        {
            ThreadFavoriteCommit committed = await _setThreadFavorite!(id, favorite, _lifetime.Token);
            if (_ended) return;
            // Independent thread commands may finish their dispatcher continuations out of order.
            // An older acknowledgement must not hide a newer successfully committed mark.
            if (_threadFavoriteSnapshot == null || committed.Snapshot.Revision >= _threadFavoriteSnapshot.Revision)
            {
                _threadFavoriteSnapshot = committed.Snapshot;
                _threadFavoriteIds = committed.Snapshot.Threads.ToHashSet();
            }
            _threadFavoriteLoadError = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (_ended) return;
            string message = ThreadFavoriteText.SaveError(id, error.Message);
            _threadFavoriteErrors[id] = message;
            OperationMessage("thread-favorites-save", message, true);
        }
        finally
        {
            _threadFavoritePending.Remove(id);
            if (!_ended) UpdateThreadFavoriteActions();
        }
    }
}
