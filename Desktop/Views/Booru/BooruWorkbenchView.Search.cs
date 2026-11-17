using System.Collections.Generic;
using System.Linq;
using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Booru;

/// <summary>The Search tab: input row, status/notice lines, pager and the render of the model's search view.</summary>
internal sealed partial class BooruWorkbenchView
{
    private readonly Button _loadSelected = new() { Name = "BooruLoadSelected" };
    private readonly Button _retryBatch = new() { Name = "BooruRetryBatch" };
    private readonly Button _cancelBatch = new() { Name = "BooruCancelBatch" };
    private readonly TextBlock _selectionInfo = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _batchInfo = new() { TextWrapping = TextWrapping.Wrap };

    private Control BuildSearchTab()
    {
        _mode.ItemsSource = new[] { new ComboBoxItem { Tag = BooruInputMode.Tags }, new ComboBoxItem { Tag = BooruInputMode.Post } };
        _mode.SelectionChanged += (_, _) =>
        {
            if (_applying || _mode.SelectedItem is not ComboBoxItem { Tag: BooruInputMode mode }) return;
            _model.Search.SetMode(mode);
        };
        _input.TextChanged += (_, _) =>
        {
            if (!_applying) _model.Search.SetInput(_input.Text);
        };
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter || e.Handled || ShortcutServiceCapturing()) return;
            e.Handled = true;
            _model.Search.SetInput(_input.Text); // the field is what is sent, even if its change event is still queued
            await _model.Search.SubmitAsync();
        };
        _submit.Classes.Add("primary");
        // I1b: the tool buttons of the page carry a catalog icon; the text stays the content (set again with every render) and the name.
        CommandBindings.EnsureIconBeforeText(_submit, "search");
        CommandBindings.EnsureIconBeforeText(_createWatcher, "plus");
        _submit.Click += async (_, _) =>
        {
            _model.Search.SetInput(_input.Text);
            await _model.Search.SubmitAsync();
        };
        _cancel.Click += (_, _) => _model.Search.Cancel();
        _retry.Click += async (_, _) => await _model.Search.RetryAsync();
        _previous.Click += async (_, _) => await _model.Search.GoToPageAsync(_model.Search.View.Page - 1);
        _next.Click += async (_, _) => await _model.Search.GoToPageAsync(_model.Search.View.Page + 1);
        _createWatcher.Click += async (_, _) => await _model.CreateWatcherAsync();

        Grid inputRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        inputRow.Children.Add(_mode);
        Grid.SetColumn(_input, 1); inputRow.Children.Add(_input);
        Grid.SetColumn(_submit, 2); inputRow.Children.Add(_submit);
        Grid.SetColumn(_cancel, 3); inputRow.Children.Add(_cancel);

        _queryLine.TextWrapping = TextWrapping.Wrap;
        StackPanel status = new() { Spacing = 8 };
        status.Children.Add(_statusText);
        status.Children.Add(_retry);
        // The repair route's actions sit under the status text, left-aligned like "Create watcher"; they never stretch across the box.
        WireRepair(_searchRepair, status);
        _statusHost.Child = status;
        AutomationProperties.SetLiveSetting(_statusText, AutomationLiveSetting.Polite);

        _pager.Children.Add(_previous);
        _pager.Children.Add(_pageInfo);
        _pager.Children.Add(_next);
        _pager.Children.Add(_pageTarget);
        _pager.Children.Add(_jumpPage);
        _pageTarget.ValueChanged += (_, _) => OnModelChanged();
        _jumpPage.Click += async (_, _) =>
        {
            if (PageJumpTarget() is { } page)
                await _model.Search.GoToPageAsync(page);
        };

        StackPanel watcherRow = new() { Spacing = 4 };
        watcherRow.Children.Add(_createWatcher);
        watcherRow.Children.Add(_createHint);

        _noticeHost.Child = _noticeText;
        AutomationProperties.SetLiveSetting(_noticeText, AutomationLiveSetting.Polite);

        StackPanel tab = new() { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        tab.Children.Add(inputRow);
        AutomationProperties.SetLiveSetting(_inputIssue, AutomationLiveSetting.Polite);
        tab.Children.Add(_inputIssue);
        tab.Children.Add(_queryLine);
        tab.Children.Add(_queryHint);
        tab.Children.Add(watcherRow);
        tab.Children.Add(_noticeHost);
        tab.Children.Add(_statusHost);
        tab.Children.Add(_pager);
        tab.Children.Add(_resultCount);
        WrapPanel selectionActions = new() { Classes = { "workspace-toolbar" } };
        selectionActions.Children.Add(_loadSelected);
        selectionActions.Children.Add(_retryBatch);
        selectionActions.Children.Add(_cancelBatch);
        _loadSelected.Click += async (_, _) => await _model.Search.LoadSelectedAsync();
        _retryBatch.Click += async (_, _) => await _model.Search.RetryBatchAsync();
        _cancelBatch.Click += (_, _) => _model.Search.CancelBatch();
        AutomationProperties.SetLiveSetting(_selectionInfo, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_batchInfo, AutomationLiveSetting.Polite);
        tab.Children.Add(_selectionInfo);
        tab.Children.Add(selectionActions);
        tab.Children.Add(_batchInfo);
        tab.Children.Add(_results);
        tab.Children.Add(_pageLimit);
        return tab;
    }

    private int? PageJumpTarget() => _pageTarget.Value is { } page
        && page >= 1 && page <= BooruSearchModel.MaximumPage && page == decimal.Truncate(page)
            ? (int)page : null;

    private void RenderSearch(BooruSearchView view, bool force)
    {
        BooruSearchModel model = _model.Search;
        bool paused = model.IsPaused;
        _paused = paused;

        // Mode selector items follow the language; the selection follows the model.
        if (_mode.ItemsSource is IEnumerable<ComboBoxItem> items)
        {
            foreach (ComboBoxItem item in items)
                item.Content = Texts.T(item.Tag is BooruInputMode.Post ? "booru.ui.mode.post" : "booru.ui.mode.tags");
            ComboBoxItem selected = items.First(item => (BooruInputMode)item.Tag! == view.Mode);
            if (!ReferenceEquals(_mode.SelectedItem, selected)) _mode.SelectedItem = selected;
        }
        AutomationProperties.SetName(_mode, Texts.T("booru.ui.mode.label"));
        if (_input.Text != view.Input) _input.Text = view.Input;
        _input.PlaceholderText = Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.input.placeholder.post" : "booru.ui.input.placeholder.tags");
        AutomationProperties.SetName(_input, Texts.T("booru.ui.input.label"));
        bool invalid = view.Parsed.Kind == BooruInputKind.Invalid;
        _input.Classes.Set("workspace-invalid", invalid);
        string issue = invalid ? BooruText.Issue(view.Parsed.Issue) : string.Empty;
        AutomationProperties.SetHelpText(_input, issue);
        _inputIssue.Text = issue;
        _inputIssue.IsVisible = invalid;

        _submit.Content = Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.lookup" : "booru.ui.search");
        _submit.IsEnabled = model.CanSubmit;
        // A disabled Search names its reason (the input issue or the provider pause) to the mouse and to assistive technology.
        string? submitWhy = model.CanSubmit ? null : invalid ? issue : paused ? BooruText.SearchStatus(view, paused) : null;
        ToolTip.SetTip(_submit, submitWhy);
        ToolTip.SetShowOnDisabled(_submit, true);
        AutomationProperties.SetHelpText(_submit, submitWhy ?? string.Empty);
        _cancel.Content = Texts.T("booru.ui.cancel");
        _cancel.IsVisible = view.IsBusy;

        // The exact query is always visible: what is sent is what the field canonicalizes to, nothing added.
        _queryLine.IsVisible = view.Parsed.Kind is BooruInputKind.Tags or BooruInputKind.Post;
        _queryLine.Text = view.Parsed.Kind switch
        {
            BooruInputKind.Tags => Texts.T("booru.ui.query.sent", view.Parsed.Canonical),
            BooruInputKind.Post => Texts.T("booru.ui.post.sent", view.Parsed.PostId),
            _ => string.Empty
        };
        _queryHint.IsVisible = view.Mode == BooruInputMode.Tags;
        _queryHint.Text = Texts.T("booru.ui.query.hint");

        _createWatcher.Content = Texts.T("booru.ui.watcher.create");
        _createWatcher.IsEnabled = model.CanCreateWatcher && !_model.Watchers.View.Creating;
        _createHint.Text = Texts.T("booru.ui.watcher.createHint");
        string createName = Texts.T("booru.ui.watcher.create");
        AutomationProperties.SetName(_createWatcher, createName);
        AutomationProperties.SetHelpText(_createWatcher, _createWatcher.IsEnabled ? _createHint.Text : Texts.T("booru.ui.watcher.createNeedsTags"));
        ToolTip.SetTip(_createWatcher, _createWatcher.IsEnabled ? _createHint.Text : Texts.T("booru.ui.watcher.createNeedsTags"));

        _loadMirror.Update(view.Rows.Select(row => (row.Post.PostId,
            row.Load == BooruRowLoad.Idle ? default((BooruRowLoad, string?)?) : (row.Load, row.LoadCode))), view.Notice);
        if (_loadMirror.RowId is { } mirrored && view.Rows.FirstOrDefault(row => row.Post.PostId == mirrored) is { } loadRow
            && BooruText.Load(loadRow) is { } loadText)
        {
            _noticeText.Text = Texts.T("booru.ui.post.title", loadRow.Post.PostId) + ": " + loadText;
            ApplyTone(_noticeHost, LoadTone(loadRow.Load));
            _noticeHost.IsVisible = true;
        }
        else
        {
            if (view.Notice is { } notice)
            {
                _noticeText.Text = BooruText.Notice(notice);
                ApplyTone(_noticeHost, notice.Kind);
            }
            _noticeHost.IsVisible = view.Notice is not null;
        }
        // The work-view gate owns presentation, while this state remains the model's notice state.
        WorkViewElement.SetStateVisible(_noticeHost, _noticeHost.IsVisible);

        // Errors interrupt (as the dialog error strips do); progress and limits wait their turn. A running build that cannot
        // bind to this SDK replaces the plain failure (and the idle prompt) with its repair route; a confirmed repair is calm.
        bool repairShown = BooruText.SearchShowsRepair(view, _repair);
        BooruNoticeKind tone = repairShown ? RepairTone() : ToneOf(view.Status);
        AutomationProperties.SetLiveSetting(_statusText, tone == BooruNoticeKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        _statusText.Text = repairShown ? BooruText.RepairStatus(_repair) : BooruText.SearchStatus(view, paused);
        AutomationProperties.SetName(_statusText, _statusText.Text);
        _statusHost.IsVisible = true;
        ApplyTone(_statusHost, tone);
        _retry.Content = Texts.T("booru.ui.status.retry");
        // Searching again before the restart fails the same way, so the route replaces "Search again".
        _retry.IsVisible = model.CanRetry && !repairShown;
        _retry.IsEnabled = !paused;
        RenderRepairControls(_searchRepair, repairShown);

        bool paging = view.Status is BooruSearchStatus.Results or BooruSearchStatus.Empty && view.ExecutedQuery is not null && !view.IsPostResult;
        // Keep recovery navigation reachable even when the failed/cancelled page has no results.
        _pager.IsVisible = paging || view.HasPreviousPage;
        // The pager is icon-only like the Archive's: the chevron, with the text as tooltip and accessible name.
        IconButtons.Apply(_previous, "chevron-left", Texts.T("booru.ui.page.previous"));
        _previous.IsEnabled = model.CanGoToPage(view.Page - 1);
        IconButtons.Apply(_next, "chevron-right", Texts.T("booru.ui.page.next"));
        _next.IsEnabled = model.CanGoToPage(view.Page + 1);
        _pageInfo.Text = Texts.T("booru.ui.page.info", view.Page, BooruSearchModel.MaximumPage, BooruSearchModel.PageSize);
        _jumpPage.Content = Texts.T("booru.ui.page.jump");
        AutomationProperties.SetName(_pageTarget, Texts.T("booru.ui.page.jump"));
        _pageTarget.IsEnabled = !view.IsBusy && !model.Batch.Running && !paused;
        _jumpPage.IsEnabled = PageJumpTarget() is { } target && model.CanGoToPage(target);
        _resultCount.IsVisible = view.ResultCountLowerBound is not null;
        _resultCount.Text = view.ResultCountLowerBound is { } count ? Texts.T("booru.ui.page.atLeast", count) : string.Empty;
        _pageLimit.IsVisible = view.PageLimitReached;
        _pageLimit.Text = Texts.T("booru.ui.page.limit", BooruSearchModel.MaximumPage, BooruSearchModel.PageSize);

        BooruBatchView batch = model.Batch;
        _selectionInfo.Text = Texts.T("booru.ui.selection.info", model.Selected.Count, BooruSearchModel.MaximumSelection);
        _selectionInfo.IsVisible = !view.Rows.IsEmpty;
        _loadSelected.Content = Texts.T("booru.ui.selection.load");
        _loadSelected.IsEnabled = model.CanLoadSelected;
        _retryBatch.Content = Texts.T("booru.ui.selection.retry");
        _retryBatch.IsVisible = model.CanRetryBatch;
        _cancelBatch.Content = Texts.T("booru.ui.selection.stop");
        _cancelBatch.IsVisible = batch.Running;
        _cancelBatch.IsEnabled = !batch.CancelRequested;
        _batchInfo.IsVisible = batch.Started;
        _batchInfo.Text = Texts.T("booru.ui.selection.summary", batch.Succeeded, batch.Failed, batch.Pending)
            + (batch.CancelRequested ? " " + Texts.T("booru.ui.selection.stopped") : string.Empty);

        string key = string.Join('\n', Texts.Language, view.Status, view.Page, view.ExecutedPostId, batch.Running, string.Join(',', model.Selected.Order()), string.Join('|',
            view.Rows.Select(row => $"{row.Post.PostId}:{row.Load}:{row.LoadCode}:{row.Preview}:{row.PreviewBytes?.Length}:{_expandedTagPosts.Contains(row.Post.PostId)}")));
        if (force || key != _resultsKey)
        {
            _resultsKey = key;
            // Unchanged hits keep their card (and the focus in it); a changed hit gets a new one, see BuildResultCards.
            RebuildPanel(_results, () => BuildResultCards(view.Rows, force), fallback: () => null, incremental: true);
            PruneBitmaps(view.Rows);
        }
    }

    private static BooruNoticeKind LoadTone(BooruRowLoad load) => load switch
    {
        BooruRowLoad.Queued or BooruRowLoad.AlreadyQueued or BooruRowLoad.AlreadyLoaded => BooruNoticeKind.Success,
        BooruRowLoad.Handing => BooruNoticeKind.Info,
        BooruRowLoad.Failed => BooruNoticeKind.Error,
        _ => BooruNoticeKind.Warning
    };

    private static BooruNoticeKind ToneOf(BooruSearchStatus status) => status switch
    {
        BooruSearchStatus.Denied or BooruSearchStatus.Failed or BooruSearchStatus.Unavailable => BooruNoticeKind.Error,
        BooruSearchStatus.RateLimited or BooruSearchStatus.Cooldown or BooruSearchStatus.Offline
            or BooruSearchStatus.NotFound or BooruSearchStatus.InvalidInput => BooruNoticeKind.Warning,
        _ => BooruNoticeKind.Info
    };

    private static void ApplyTone(Border host, BooruNoticeKind kind)
    {
        host.Classes.Set("workspace-status", kind is BooruNoticeKind.Info or BooruNoticeKind.Success);
        host.Classes.Set("workspace-status-success", kind == BooruNoticeKind.Success);
        host.Classes.Set("workspace-warning", kind == BooruNoticeKind.Warning);
        host.Classes.Set("workspace-error", kind == BooruNoticeKind.Error);
    }
}
