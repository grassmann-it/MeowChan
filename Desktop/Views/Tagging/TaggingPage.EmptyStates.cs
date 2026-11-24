using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Tags;

namespace MeowChan;

public sealed partial class TaggingPage
{
    private TaggingEmptyKind _emptyKind;
    private ContextMenuHost? _emptyMoreHost;
    private TaggingQueueCounts _emptyScopeCounts;

    private Button? PrimaryEmptyAction => _emptyKind switch
    {
        TaggingEmptyKind.AllDone when _emptyScopeCounts.Skipped > 0 => _emptyReviewSkipped,
        TaggingEmptyKind.QueueEmpty or TaggingEmptyKind.AllDone => _emptyMedia,
        TaggingEmptyKind.FilterNoHits or TaggingEmptyKind.GroupEmpty => _emptyShowAll,
        _ => null
    };

    internal TaggingEmptyKind EmptyKind => _emptyKind;
    internal Button EmptyMediaButton => _emptyMedia;
    internal Button EmptyReviewSkippedButton => _emptyReviewSkipped;
    internal Button EmptyShowAllButton => _emptyShowAll;
    internal Button EmptyHowButton => _emptyHow;
    internal Button EmptyMoreButton => _emptyMore;
    internal TextBlock EmptyText => _empty;

    private void InitializeEmptyStates()
    {
        CommandBindings.EnsureIconBeforeText(_emptyMedia, CommandCatalog.Get("navigation.media").Icon);
        CommandBindings.EnsureIconBeforeText(_emptyHow, "help-circle");
        CommandBindings.EnsureIconBeforeText(_emptyReviewSkipped, CommandCatalog.Get("tags.queue.skip").Icon);
        CommandBindings.EnsureIconBeforeText(_emptyMore, "more-horizontal");
        _emptyMoreHost = new ContextMenuHost(_emptyMore, ComposeEmptyMoreMenu, fallbackFocus: FocusOnOpen);
        _emptyMedia.Click += (_, _) => AddRequested?.Invoke();
        _emptyHow.Click += (_, _) => _help.Popover.ShowAt(_emptyHow);
        _emptyMore.Click += (_, _) => _emptyMoreHost.Show();
        _emptyReviewSkipped.Click += (_, _) =>
        {
            Filter = TaggingQueueFilter.Skipped;
            FocusContent();
        };
        _emptyShowAll.Click += (_, _) =>
        {
            if (_emptyKind == TaggingEmptyKind.GroupEmpty)
            {
                _groupLocalizing = true;
                try { _group.SelectedItem = GroupChoices[0]; }
                finally { _groupLocalizing = false; }
                _selectedGroupId = null;
                _ungroupedSelected = false;
                _queueWork.SelectGroup(null, false);
                Render();
            }
            else { Filter = TaggingQueueFilter.All; FocusContent(); }
        };
    }

    private void LocalizeEmptyStates()
    {
        _emptyHow.Content = Texts.T("tagging.page.empty.how");
        _emptyMore.Content = Texts.T("workview.more");
        AutomationProperties.SetName(_emptyMore, Texts.T("workview.moreOptions"));
    }

    private void RenderEmptyState(TagQueueSnapshot queue)
    {
        _emptyScopeCounts = TaggingQueueView.Counts(queue, _selectedGroupId, _ungroupedSelected, Scope);
        _emptyKind = _queueWork.Loaded && _queueWork.ErrorText == null
            ? TaggingEmptyStates.Of(queue, Filter, _rows.Count, _selectedGroupId, _ungroupedSelected, _emptyScopeCounts)
            : TaggingEmptyKind.None;
        bool decided = _emptyKind == TaggingEmptyKind.AllDone;
        bool reviewSkipped = decided && _emptyScopeCounts.Skipped > 0;
        string? selectedGroupName = _selectedGroupId == null ? null : queue.Group(_selectedGroupId)?.Name;
        _empty.Text = _emptyKind switch
        {
            TaggingEmptyKind.QueueEmpty => Texts.T("tagging.page.empty.queue"),
            TaggingEmptyKind.AllDone => Texts.T("tagging.page.empty.allDone", _emptyScopeCounts.Count.ToString("N0", Texts.Culture)),
            TaggingEmptyKind.GroupEmpty => Texts.T("tagging.page.empty.group", selectedGroupName ?? Texts.T("tagging.page.group.ungrouped")),
            TaggingEmptyKind.FilterNoHits => Texts.T(Filter == TaggingQueueFilter.Untagged ? "tagging.page.filterEmpty.untagged" : "tagging.page.filterEmpty"),
            _ => null
        };
        _emptyMedia.Content = Texts.T(decided ? "tagging.page.empty.add" : "tagging.page.empty.media");
        _emptyMedia.IsVisible = _emptyKind == TaggingEmptyKind.QueueEmpty || decided && !reviewSkipped;
        _emptyHow.IsVisible = _emptyKind == TaggingEmptyKind.QueueEmpty;
        _emptyReviewSkipped.Content = reviewSkipped
            ? Texts.T("tagging.page.empty.reviewSkipped", _emptyScopeCounts.Skipped.ToString("N0", Texts.Culture)) : null;
        _emptyReviewSkipped.IsVisible = reviewSkipped;
        _emptyMore.IsVisible = decided;
        _emptyShowAll.Content = _emptyKind == TaggingEmptyKind.GroupEmpty ? Texts.T("tagging.page.empty.group.assign") : Texts.T("tagging.page.empty.showAll");
        string showAllIcon = _emptyKind == TaggingEmptyKind.GroupEmpty ? "arrow-left" : CommandCatalog.Get("media.filter.reset-all").Icon;
        if (IconButtons.KeyOf(_emptyShowAll) != showAllIcon)
            CommandBindings.ShowIconBeforeText(_emptyShowAll, showAllIcon);
        _emptyShowAll.IsVisible = _emptyKind is TaggingEmptyKind.FilterNoHits or TaggingEmptyKind.GroupEmpty;
        // Empty-state recommendations own the accent; GroupEmpty recommends returning to the queue to assign entries.
        _start.Classes.Set("primary", _emptyKind == TaggingEmptyKind.None);
        _emptyMedia.Classes.Set("primary", PrimaryEmptyAction == _emptyMedia);
        _emptyShowAll.Classes.Set("primary", PrimaryEmptyAction == _emptyShowAll);
        _emptyReviewSkipped.Classes.Set("primary", PrimaryEmptyAction == _emptyReviewSkipped);
        _emptyMedia.IsEnabled = AddRequested != null;
        _emptyActions.IsVisible = _emptyKind != TaggingEmptyKind.None;
    }

    internal ContextMenuModel? EmptyMoreMenuModel() => ComposeEmptyMoreMenu()?.ToModel();

    private ContextMenuBuilder? ComposeEmptyMoreMenu()
    {
        if (_emptyKind != TaggingEmptyKind.AllDone) return null;
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("workview.moreOptions"));
        if (_emptyScopeCounts.Skipped > 0)
        {
            menu.Item("tagging.empty.add", Texts.T("tagging.page.empty.add"), () => AddRequested?.Invoke(),
                AddRequested != null, icon: CommandCatalog.Get("navigation.media").Icon);
            menu.Item("tagging.empty.reopen", Texts.T("tagging.page.empty.reopen"),
                () => OpenActions.Start(ReopenSkippedAsync, "tagging.empty.reopen"), CanChange,
                icon: CommandCatalog.Get(TaggingCommands.MarkOpen).Icon);
        }
        if (_emptyScopeCounts.Done > 0)
            menu.Item("tagging.clear-done", CommandCatalog.Label("tagging.clear-done"),
                () => OpenActions.Start(ClearFinishedInScopeAsync, "tagging.clear-done"), CanChange,
                icon: CommandCatalog.Get("tagging.clear-done").Icon);
        return menu;
    }

    private TagQueueKey[] KeysInEmptyScope(TagQueueState state) =>
        _queueWork.Queue.Items.Where(item => item.State == state
            && TaggingQueueView.InGroup(item, _selectedGroupId, _ungroupedSelected)
            && (Scope == null || Scope(item))).Select(item => item.Key).ToArray();

    private Task ClearFinishedInScopeAsync()
    {
        return _selectedGroupId == null && !_ungroupedSelected && Scope == null
            ? ClearFinishedAsync() : AfterAsync(_queueWork.RemoveAsync(KeysInEmptyScope(TagQueueState.Done)));
    }

    internal Task ReopenSkippedAsync() => AfterAsync(_queueWork.SetStatesAsync(KeysInEmptyScope(TagQueueState.Skipped), TagQueueState.Open));
}
