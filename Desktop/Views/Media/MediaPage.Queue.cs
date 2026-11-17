using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;
using System.IO;

namespace MeowChan;

// "Add to tagging queue" on the media page: the selection (button, menu, shortcut), the tile of a menu, and everything the filter shows
// ("Add all shown to the tagging queue…"). Above the confirmation limit (a setting) the page asks first, inline and without blocking,
// like "export all"; the tiles are frozen when the user clicks, and a pending question is dropped when the grid changes under it.
// The queue only remembers which media to walk through; nothing is tagged, favorited or rated by adding.
public sealed partial class MediaPage
{
    private readonly Button _selQueue = new() { Name = "MediaSelectionQueue" };
    private readonly TextBlock _queueAskText = new() { Name = "MediaQueueConfirmText", TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _queueAskYes = new() { Name = "MediaQueueConfirmYes" };
    private readonly Button _queueAskNo = new() { Name = "MediaQueueConfirmNo" };
    private readonly WrapPanel _queueAsk = new() { Name = "MediaQueueConfirm", ItemSpacing = 8, LineSpacing = 8, IsVisible = false };
    private IReadOnlyList<MediaLibraryEntry>? _queuePending;

    /// <summary>Adds the media index entries to the tagging queue and returns the sentence for the status line; null disables every queue action.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, Task<string>>? AddToTagQueue { get; set; }
    /// <summary>Adds media to a named tagging group; the page supplies the frozen target and group id.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, string, Task<string>>? AddToTagQueueInGroup { get; set; }
    /// <summary>Removes entries from the tagging queue (the host's "Remove from queue") and returns the sentence for the status line; null disables Delete.</summary>
    internal Func<IReadOnlyList<TagQueueKey>, Task<string>>? RemoveFromTagQueue { get; set; }
    /// <summary>The confirmation limit of the tagging queue (Settings, Storage and limits); read at every add so a change applies at once.</summary>
    internal Func<int>? TagQueueConfirmAboveSource { get; set; }
    private int? _tagQueueConfirmAbove;

    /// <summary>More media than this ask first. Tests may set it; the page never changes it.</summary>
    internal int TagQueueConfirmAbove
    {
        get => _tagQueueConfirmAbove ?? TagQueueConfirmAboveSource?.Invoke() ?? MeowChan.Config.TagQueueConfirmAboveDefault;
        set => _tagQueueConfirmAbove = value;
    }
    internal bool QueueAsking => _queueAsk.IsVisible;
    internal Button SelectionQueueButton => _selQueue;

    // ---- MP1/MP3: what the queue holds, on the tiles, in the status line, as a filter term, and the two ways to it under More ----

    /// <summary>The last committed tagging queue (the host's passive view; null before it was loaded). Throws InvalidDataException while unreadable.</summary>
    internal Func<TagQueueSnapshot?>? ReadTagQueue { get; set; }
    /// <summary>Loads the queue file once when nothing was loaded yet (the host's command; the page asks when it is shown).</summary>
    internal Func<Task>? LoadTagQueue { get; set; }
    /// <summary>"Walk through tagging queue" under More (the shell's walk; null hides the entry).</summary>
    internal Func<Task>? OpenTagQueueWalk { get; set; }
    /// <summary>"Open the Tagging page" under More (the shell's navigation; null hides the entry).</summary>
    internal Func<bool>? OpenTaggingPage { get; set; }
    private readonly Button _queueWalk = new() { Name = "MediaQueueWalk", IsVisible = false };
    private readonly Button _taggingPageOpen = new() { Name = "MediaTaggingPageOpen", IsVisible = false };
    // QW-A #10: "Add all shown to the tagging queue…" under More; the executor is the chip menu's (AddAllShownToQueueAsync), which asks above the limit.
    private readonly Button _queueAll = new() { Name = "MediaQueueAll", IsVisible = false };
    private TagQueueSnapshot? _queueSnapshot;
    // The keys of the queue (every state) for the badges and the queue term; null while the queue is unreadable.
    private IReadOnlySet<TagQueueKey>? _queueKeys = new HashSet<TagQueueKey>();
    private int _queueOpen;
    private bool _queueLoadRequested;
    // What the badges and the status line were last refreshed for: the snapshot, or this sentinel while the queue is unreadable.
    private static readonly object QueueUnreadable = new();
    private object? _queueSignature;

    /// <summary>How many queue entries are open (the status line counts them).</summary>
    internal int QueueOpenCount => _queueOpen;
    /// <summary>How many tiles had their badges refreshed by the last queue change (tests: only realized tiles are touched).</summary>
    internal int QueueBadgeRefreshes { get; private set; }
    /// <summary>How often a real queue change (another snapshot, or the queue becoming unreadable) was applied; a repeated read of the same state adds nothing (tests).</summary>
    internal int QueueChangesApplied { get; private set; }

    /// <summary>Whether the occurrence is in the tagging queue (any state). False while the queue is unknown or unreadable.</summary>
    internal bool IsInTagQueue(MediaLibraryEntry entry) => _queueKeys is { Count: > 0 } keys && TagQueueKeys.Of(entry) is { } key && keys.Contains(key);

    /// <summary>
    /// Reads the host's queue again (the shell calls it when the session changed; the page when it is shown). Nothing happens while it is
    /// the same snapshot. A change refreshes the badges of the tiles that are realized now (never the whole grid) and the status line;
    /// only a filter with the queue term presents again, since its results changed.
    /// </summary>
    internal void RefreshTagQueue(bool load = false)
    {
        if (ReadTagQueue is not { } read || _lifetime.IsCancellationRequested) return;
        TagQueueSnapshot? snapshot;
        bool unreadable = false;
        try { snapshot = read(); }
        catch (Exception error) when (error is InvalidDataException or IOException or ObjectDisposedException) { snapshot = null; unreadable = true; }
        if (snapshot == null && !unreadable && load && !_queueLoadRequested && LoadTagQueue is { } loader)
        {
            _queueLoadRequested = true;
            _ = LoadQueueQuietlyAsync(loader);
        }
        // Compared by signature: the session ticks all the time (downloads), and an unreadable queue must not refresh the badges, the status line
        // and, with the queue term, the whole grid again for every tick; the next change of state (readable again) does.
        object? signature = unreadable ? QueueUnreadable : snapshot;
        if (ReferenceEquals(signature, _queueSignature)) return;
        _queueSignature = signature;
        QueueChangesApplied++;
        _queueSnapshot = snapshot;
        _queueKeys = unreadable ? null : snapshot == null ? new HashSet<TagQueueKey>() : TagQueueKeys.Set(snapshot);
        _queueOpen = snapshot?.Open ?? 0;
        RefreshQueueBadges();
        if (HasField(MediaFilterField.Queue) && _snapshot != null) Present(keepView: true);
        else UpdateStatusText();
    }

    private async Task LoadQueueQuietlyAsync(Func<Task> loader)
    {
        try { await loader(); }
        catch (Exception error) when (error is not OutOfMemoryException) { } // An unreadable or busy queue is shown as such by the next read.
        if (!_lifetime.IsCancellationRequested) RefreshTagQueue();
    }

    // Only the realized containers (the tiles on screen and a few around them): a queue change never walks the grid's rows.
    private void RefreshQueueBadges()
    {
        int touched = 0;
        foreach (Control container in _grid.GetRealizedContainers())
            if (container is ContentControl { Presenter.Child: MediaLibraryTileView tile }) { tile.RefreshQueueBadge(); touched++; }
        QueueBadgeRefreshes = touched;
    }

    private void LocalizeQueueTools()
    {
        string walk = Texts.T("command.media.queue.open"), page = Texts.T("media.toolbar.taggingPage");
        _queueWalk.Content = walk;
        Avalonia.Automation.AutomationProperties.SetName(_queueWalk, walk);
        _taggingPageOpen.Content = page;
        string all = Texts.T("media.queue.all");
        _queueAll.Content = all;
        Avalonia.Automation.AutomationProperties.SetName(_queueAll, all);
        string folders = Texts.T("command.media.folders");
        _mediaFolders.Content = folders;
        Avalonia.Automation.AutomationProperties.SetName(_mediaFolders, folders);
        Avalonia.Automation.AutomationProperties.SetName(_taggingPageOpen, page);
    }

    private void WireQueueTools()
    {
        _queueWalk.Click += async (_, _) =>
        {
            if (_lifetime.IsCancellationRequested || OpenTagQueueWalk is not { } walk) return;
            try { await walk(); }
            catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.queue.walk")) { /* Logged and shown. */ }
        };
        _taggingPageOpen.Click += (_, _) => { if (!_lifetime.IsCancellationRequested) OpenTaggingPage?.Invoke(); };
        _queueAll.Click += async (_, _) => await AddAllShownToQueueAsync();
    }

    private Control BuildQueueAsk()
    {
        AutomationProperties.SetLiveSetting(_queueAskText, AutomationLiveSetting.Polite);
        _queueAskYes.Classes.Add("primary");
        foreach (Control control in new Control[] { _queueAskText, _queueAskYes, _queueAskNo }) _queueAsk.Children.Add(control);
        _selQueue.Click += async (_, _) => await AddSelectionToQueueAsync();
        _queueAskYes.Click += async (_, _) => await ConfirmQueueAsync();
        _queueAskNo.Click += (_, _) => DismissQueueAsk();
        return _queueAsk;
    }

    private void LocalizeQueue()
    {
        _selQueue.Content = Texts.T("media.selection.queue");
        _queueAskYes.Content = Texts.T("queue.ask.yes");
        _queueAskNo.Content = Texts.T("queue.ask.no");
        if (_queuePending != null) _queueAskText.Text = Texts.Count("queue.ask", _queuePending.Count, _queuePending.Count.ToString("N0", Texts.Culture));
    }

    private void UpdateQueueActions(bool ready, string? reason)
    {
        bool wired = AddToTagQueue != null;
        SetAction(_selQueue, ready && wired, reason, wired, "media.selection.queue.hint");
        // The page-wide entry does not depend on the selection: it needs shown tiles and an index that is not being built.
        string? allReason = _building != null ? Texts.T("media.selection.reason.building") : _tiles.Count == 0 ? Texts.T("media.queueAll.reason.none") : null;
        SetAction(_queueAll, allReason == null && wired && !_selectionBusy && !_lifetime.IsCancellationRequested, allReason, wired, "media.queueAll.hint");
    }

    private void DismissQueueAsk()
    {
        _queuePending = null;
        _queueAsk.IsVisible = false;
    }

    /// <summary>Adds the selected tiles (the tile each one shows) to the tagging queue, in page order.</summary>
    internal Task AddSelectionToQueueAsync()
    {
        if (!_selQueue.IsEnabled) return Task.CompletedTask;
        return AddTilesToQueueAsync(SelectedTiles);
    }

    /// <summary>Adds every tile the active filter shows right now. Asks first above <see cref="TagQueueConfirmAbove"/>.</summary>
    internal Task AddAllShownToQueueAsync()
    {
        if (AddToTagQueue == null || _selectionBusy || _building != null || _lifetime.IsCancellationRequested || _tiles.Count == 0) return Task.CompletedTask;
        return AddTilesToQueueAsync(_tiles); // Replaced, never changed in place: a frozen view of what is shown now.
    }

    private async Task AddTilesToQueueAsync(IReadOnlyList<MediaLibraryTile> tiles)
    {
        if (AddToTagQueue == null || _selectionBusy || _lifetime.IsCancellationRequested || tiles.Count == 0) return;
        IReadOnlyList<MediaLibraryEntry> entries = tiles.Select(tile => tile.Entry).ToArray();
        if (entries.Count > TagQueueConfirmAbove)
        {
            _queuePending = entries;
            _queueAskText.Text = Texts.Count("queue.ask", entries.Count, entries.Count.ToString("N0", Texts.Culture));
            _queueAsk.IsVisible = true;
            _queueAskYes.Focus();
            return;
        }
        await RunQueueAddAsync(entries);
    }

    private async Task ConfirmQueueAsync()
    {
        IReadOnlyList<MediaLibraryEntry>? entries = _queuePending;
        DismissQueueAsk();
        if (entries != null) await RunQueueAddAsync(entries);
    }

    /// <summary>Adds one occurrence (the tile a menu was opened on) to the tagging queue.</summary>
    internal Task AddEntryToQueueAsync(MediaLibraryEntry entry) =>
        AddToTagQueue == null || _selectionBusy || _lifetime.IsCancellationRequested ? Task.CompletedTask : RunQueueAddAsync(new[] { entry });

    internal Task AddEntriesToQueueAsync(IReadOnlyList<MediaLibraryEntry> entries) =>
        AddToTagQueue == null || _selectionBusy || _lifetime.IsCancellationRequested || entries.Count == 0 ? Task.CompletedTask : RunQueueAddAsync(entries);

    internal Task AddEntriesToQueueInGroupAsync(IReadOnlyList<MediaLibraryEntry> entries, string groupId) =>
        AddToTagQueueInGroup == null || _selectionBusy || _lifetime.IsCancellationRequested || entries.Count == 0
            ? Task.CompletedTask : RunQueueAddAsync(entries, groupId);

    /// <summary>
    /// Delete (the command <c>tagging.remove</c>, "Remove from queue"): the selected media that are in the tagging queue leave it, in one write. It is only that:
    /// with none of the selected media in the queue, while the queue is unreadable or the page is busy, Delete does nothing. A file is never deleted here.
    /// </summary>
    internal Task RemoveSelectionFromQueueAsync()
    {
        if (RemoveFromTagQueue == null || _selectionBusy || _building != null || _lifetime.IsCancellationRequested || _queueKeys is not { Count: > 0 }
            || _grid.Selection.Count == 0) return Task.CompletedTask;
        IReadOnlyList<TagQueueKey> keys = TagQueueActions.RemovableKeys(SelectedTiles.Select(tile => tile.Entry), _queueKeys);
        return keys.Count == 0 ? Task.CompletedTask : RunQueueRemoveAsync(keys);
    }

    private async Task RunQueueRemoveAsync(IReadOnlyList<TagQueueKey> keys)
    {
        if (RemoveFromTagQueue is not { } remove) return;
        _selectionBusy = true;
        UpdateSelectionBar();
        string? message = null;
        try { message = await remove(keys); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            _selectionBusy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                UpdateSelectionBar();
                ShowSelectionNote(message);
            }
        }
    }

    private async Task RunQueueAddAsync(IReadOnlyList<MediaLibraryEntry> entries, string? groupId = null)
    {
        Func<IReadOnlyList<MediaLibraryEntry>, Task<string>>? add = groupId == null ? AddToTagQueue
            : AddToTagQueueInGroup == null ? null : values => AddToTagQueueInGroup(values, groupId);
        if (add is not { } addEntries) return;
        _selectionBusy = true;
        UpdateSelectionBar();
        string? message = null;
        try { message = await addEntries(entries); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            _selectionBusy = false;
            if (!_lifetime.IsCancellationRequested)
            {
                UpdateSelectionBar(); // Clears a note while nothing is selected, so the sentence is shown after it.
                ShowSelectionNote(message);
            }
        }
    }
}
