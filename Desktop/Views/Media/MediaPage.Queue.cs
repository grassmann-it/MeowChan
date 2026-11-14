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
    private readonly WrapPanel _queueAsk = new() { Name = "MediaQueueConfirm", ItemSpacing = 8, LineSpacing = 6, IsVisible = false };
    private IReadOnlyList<MediaLibraryEntry>? _queuePending;

    /// <summary>Adds the media index entries to the tagging queue and returns the sentence for the status line; null disables every queue action.</summary>
    internal Func<IReadOnlyList<MediaLibraryEntry>, Task<string>>? AddToTagQueue { get; set; }
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
        if (_queuePending != null) _queueAskText.Text = Texts.T("queue.ask", _queuePending.Count.ToString("N0", Texts.Culture));
    }

    private void UpdateQueueActions(bool ready, string? reason)
    {
        bool wired = AddToTagQueue != null;
        SetAction(_selQueue, ready && wired, reason, wired, "media.selection.queue.hint");
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
            _queueAskText.Text = Texts.T("queue.ask", entries.Count.ToString("N0", Texts.Culture));
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

    private async Task RunQueueAddAsync(IReadOnlyList<MediaLibraryEntry> entries)
    {
        if (AddToTagQueue is not { } add) return;
        _selectionBusy = true;
        UpdateSelectionBar();
        string? message = null;
        try { message = await add(entries); }
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
