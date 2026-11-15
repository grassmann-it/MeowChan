using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;

namespace MeowChan;

// The empty states of the tagging page (TG1, the tool row stays visible): the queue is empty (go to the media page, how tagging works),
// everything is decided (remove the finished, reopen the skipped, go to the media page) and a state filter without a hit (show all).
public sealed partial class TaggingPage
{
    private TaggingEmptyKind _emptyKind;

    internal TaggingEmptyKind EmptyKind => _emptyKind;
    internal Button EmptyMediaButton => _emptyMedia;
    internal Button EmptyReopenButton => _emptyReopen;
    internal Button EmptyClearDoneButton => _emptyClearDone;
    internal Button EmptyShowAllButton => _emptyShowAll;
    internal Button EmptyHowButton => _emptyHow;
    internal TextBlock EmptyText => _empty;

    private void InitializeEmptyStates()
    {
        _emptyMedia.Click += (_, _) => AddRequested?.Invoke();
        _emptyHow.Click += (_, _) => _help.Popover.ShowAt(_emptyHow);
        _emptyClearDone.Click += (_, _) => _ = ClearFinishedAsync();
        _emptyReopen.Click += (_, _) => _ = ReopenSkippedAsync();
        _emptyShowAll.Click += (_, _) => { Filter = TaggingQueueFilter.All; FocusContent(); };
    }

    private void LocalizeEmptyStates()
    {
        _emptyMedia.Content = Texts.T("tagging.page.empty.media");
        _emptyHow.Content = Texts.T("tagging.page.empty.how");
        _emptyClearDone.Content = CommandCatalog.Label("tagging.clear-done");
        _emptyReopen.Content = Texts.T("tagging.page.empty.reopen");
        _emptyShowAll.Content = Texts.T("tagging.page.empty.showAll");
    }

    private void RenderEmptyState(TagQueueSnapshot queue)
    {
        _emptyKind = _queueWork.Loaded ? TaggingEmptyStates.Of(queue, Filter, _rows.Count) : TaggingEmptyKind.None;
        _empty.Text = _emptyKind switch
        {
            TaggingEmptyKind.QueueEmpty => Texts.T("tagging.page.empty.queue"),
            TaggingEmptyKind.AllDone => Texts.T("tagging.page.empty.allDone", queue.Count.ToString("N0", Texts.Culture)),
            TaggingEmptyKind.FilterNoHits => Texts.T("tagging.page.filterEmpty"),
            _ => null
        };
        _emptyMedia.IsVisible = _emptyKind is TaggingEmptyKind.QueueEmpty or TaggingEmptyKind.AllDone;
        _emptyHow.IsVisible = _emptyKind == TaggingEmptyKind.QueueEmpty;
        _emptyClearDone.IsVisible = _emptyKind == TaggingEmptyKind.AllDone && queue.Done > 0;
        _emptyReopen.IsVisible = _emptyKind == TaggingEmptyKind.AllDone && queue.Skipped > 0;
        _emptyShowAll.IsVisible = _emptyKind == TaggingEmptyKind.FilterNoHits;
        bool canChange = CanChange;
        _emptyClearDone.IsEnabled = canChange;
        _emptyReopen.IsEnabled = canChange;
        _emptyMedia.IsEnabled = AddRequested != null;
        _emptyActions.IsVisible = _emptyKind != TaggingEmptyKind.None;
    }

    /// <summary>Marks every skipped entry as open again (the "all done" state's way back to work).</summary>
    internal System.Threading.Tasks.Task ReopenSkippedAsync()
    {
        TagQueueKey[] keys = _queueWork.Queue.Items.Where(item => item.State == TagQueueState.Skipped).Select(item => item.Key).ToArray();
        return keys.Length == 0 ? System.Threading.Tasks.Task.CompletedTask : AfterAsync(_queueWork.SetStatesAsync(keys, TagQueueState.Open));
    }
}
