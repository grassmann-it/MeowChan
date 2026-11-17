using System.Linq;
using System.Threading.Tasks;
using System;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Booru;

/// <summary>The Watchers tab: list, notices, catalog usage and the watcher cards.</summary>
internal sealed partial class BooruWorkbenchView
{
    internal const string CatalogStorageName = "BooruCatalogStorage";
    // Shown from 75 % of the configured catalog limit on (S2); the text names the setting that raises it.
    private readonly Border _storageHost = new() { Name = CatalogStorageName, IsVisible = false };
    private readonly TextBlock _storageText = new() { TextWrapping = TextWrapping.Wrap };

    private Control BuildWatchersTab()
    {
        _storageHost.Child = _storageText;
        AutomationProperties.SetLiveSetting(_storageText, AutomationLiveSetting.Polite);
        _removeNoteHost.Child = _removeNote;
        _watcherNoticeHost.Child = _watcherNoticeText;
        AutomationProperties.SetLiveSetting(_watcherNoticeText, AutomationLiveSetting.Polite);
        _watchersReload.Click += async (_, _) => await _model.Watchers.RefreshAsync();
        _resetCooldown.Click += async (_, _) => await _model.Watchers.ResetProviderCooldownAsync();
        StackPanel tab = new() { Spacing = 12, Margin = new Thickness(0, 12, 0, 0) };
        tab.Children.Add(_watchersIntro);
        // The same repair route as on the Search tab: watcher checks fail the same way while the build mismatches.
        tab.Children.Add(BuildWatcherRepair());
        tab.Children.Add(_removeNoteHost);
        tab.Children.Add(_watcherNoticeHost);
        tab.Children.Add(_storageHost);
        tab.Children.Add(_resetCooldown);
        tab.Children.Add(_watchersState);
        tab.Children.Add(_watchersReload);
        tab.Children.Add(_watchers);
        return tab;
    }

    private void RenderWatchers(BooruWatcherListView view, bool force)
    {
        BooruWatcherListModel model = _model.Watchers;
        _watchersIntro.Text = Texts.T("booru.ui.watchers.intro");
        RenderWatcherRepair();
        _removeNote.Text = Texts.T("booru.ui.watchers.removeNote");
        _watcherMirror.Update(view.Rows.Select(row => (row.WatcherId, row.Message)), view.Notice);
        if (_watcherMirror.RowId is { } mirrored && view.Rows.FirstOrDefault(row => row.WatcherId == mirrored) is { } messageRow)
        {
            // While the row's next command runs it has no message; the line keeps the previous one instead of flashing.
            if (messageRow.Message is { } message)
            {
                _watcherNoticeText.Text = messageRow.Watcher.Query + ": " + BooruText.Notice(message);
                ApplyTone(_watcherNoticeHost, message.Kind);
            }
            _watcherNoticeHost.IsVisible = true;
        }
        else
        {
            if (view.Notice is { } notice)
            {
                _watcherNoticeText.Text = BooruText.Notice(notice);
                ApplyTone(_watcherNoticeHost, notice.Kind);
            }
            _watcherNoticeHost.IsVisible = view.Notice is not null;
        }
        if (view.Storage is { NearLimit: true } storage)
        {
            _storageText.Text = Texts.T("booru.ui.watchers.catalogSize", Size(storage.DocumentBytes), Size(storage.LimitBytes),
                storage.Percent, storage.PostCount, Texts.T("settings.storageLimits"), Texts.T("budgets.booruCatalog"));
            ApplyTone(_storageHost, BooruNoticeKind.Warning);
        }
        bool storageVisible = view.Storage is { NearLimit: true };
        WorkViewGate.Apply(_storageHost, _workView, storageVisible, _catalogUsageRevealed);
        _watchersState.IsVisible = view.Status != BooruWatcherListStatus.Ready;
        _watchersState.Text = view.Status switch
        {
            BooruWatcherListStatus.Loading => Texts.T("booru.ui.watchers.loading"),
            BooruWatcherListStatus.Empty => Texts.T("booru.ui.watchers.empty"),
            BooruWatcherListStatus.Failed => Texts.T("booru.ui.watchers.loadFailed", BooruText.CodeText(view.Code)),
            _ => string.Empty
        };
        _watchersState.Classes.Set("workspace-error", view.Status == BooruWatcherListStatus.Failed);
        _resetCooldown.Content = Texts.T("booru.ui.watchers.resetCooldown");
        _resetCooldown.IsEnabled = !view.Resetting && (view.Status is BooruWatcherListStatus.Ready or BooruWatcherListStatus.Empty);
        AutomationProperties.SetName(_resetCooldown, Texts.T("booru.ui.watchers.resetCooldown"));
        AutomationProperties.SetHelpText(_resetCooldown, Texts.T("booru.ui.watchers.resetCooldownHint"));
        ToolTip.SetTip(_resetCooldown, Texts.T("booru.ui.watchers.resetCooldownHint"));
        _watchersReload.Content = Texts.T("booru.ui.watchers.retry");
        _watchersReload.IsVisible = view.Status == BooruWatcherListStatus.Failed;

        _cooling = string.Join('|', view.Rows.Where(row => model.ActiveCooldown(row.Watcher) is not null).Select(row => row.WatcherId));
        string key = string.Join('\n', Texts.Language, _cooling, string.Join('|', view.Rows.Select(row => string.Join(',',
            row.WatcherId, row.Watcher, row.Busy, row.ConfirmingRemoval, row.Message?.Key, row.Message is { } message
                ? string.Join(',', message.Args.Select(arg => arg?.ToString())) : string.Empty))));
        // Opening the removal question moves focus to its safe answer; closing it returns focus to "Remove".
        string? confirming = view.Rows.FirstOrDefault(row => row.ConfirmingRemoval)?.WatcherId;
        string? moveTo = null;
        if (!string.Equals(confirming, _confirmingId, StringComparison.Ordinal))
        {
            moveTo = confirming is not null ? RemoveCancelPrefix + confirming : RemovePrefix + _confirmingId;
            _confirmingId = confirming;
        }
        if (force || key != _watchersKey)
        {
            _watchersKey = key;
            _watcherWorkViewActions.Clear();
            RebuildPanel(_watchers, () => NumberHits(view.Rows.Select(CreateWatcherCard).ToList()), moveTo, () => _watchersTab, waitWhileDisabled: true);
        }
    }

    // ---- watcher cards ------------------------------------------------------------------------------------

    private Control CreateWatcherCard(BooruWatcherRowView row)
    {
        BooruWatcherListModel model = _model.Watchers;
        BooruWatcherView watcher = row.Watcher;
        DateTimeOffset? cooling = model.ActiveCooldown(watcher);
        // The query line names the card; the border itself carries no name, so the query is read once.
        Border card = new() { Name = "BooruWatcher:" + watcher.WatcherId };
        card.Classes.Add("workspace-card");
        StackPanel panel = new() { Spacing = 8 };

        Grid head = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        head.Children.Add(new SelectableTextBlock
        {
            Text = watcher.Query, Classes = { "mono", "card-title" }, TextWrapping = TextWrapping.Wrap
        });
        Border chip = new() { Child = new TextBlock { Text = BooruText.WatcherStatus(row, cooling is not null) }, VerticalAlignment = VerticalAlignment.Top };
        chip.Classes.Add("workspace-status");
        chip.Classes.Add(watcher.Status switch
        {
            BooruWatcherStatus.Active or BooruWatcherStatus.Checking => "workspace-status-active",
            BooruWatcherStatus.Cooldown => cooling is not null ? "workspace-status-warning" : "workspace-status-active",
            BooruWatcherStatus.Error => "workspace-status-error",
            _ => "workspace-status-none"
        });
        Grid.SetColumn(chip, 1);
        head.Children.Add(chip);
        panel.Children.Add(head);

        string next = watcher.Status == BooruWatcherStatus.Paused ? Texts.T("booru.ui.watcher.pausedNext")
            : BooruText.Time(watcher.NextCheckAt, "booru.ui.watcher.notScheduled");
        panel.Children.Add(new TextBlock
        {
            Text = Texts.T("booru.ui.watcher.lastCheck", BooruText.Time(watcher.LastCheckedAt, "booru.ui.watcher.never"))
                + " · " + Texts.T("booru.ui.watcher.nextCheck", next),
            Classes = { "workspace-meta" }
        });
        panel.Children.Add(new TextBlock
        {
            Text = Texts.T("booru.ui.watcher.hits", watcher.HitCount, watcher.QueuedCount), Classes = { "workspace-meta" }
        });
        if (watcher.LatestPostId is { } latest)
        {
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.latest", latest), Classes = { "workspace-meta" } });
            if (_showInMedia != null && _isPostSavedInMedia?.Invoke(latest) == true)
                panel.Children.Add(CreateShowInMediaAction(WatcherShowInMediaPrefix + watcher.WatcherId,
                    Texts.T("booru.ui.watcher.showInMediaFor", latest, watcher.Query), latest));
            if (BooruPostLinks.TryCanonical(latest, out string latestUrl))
                panel.Children.Add(CreateLinkActions(WatcherOpenPrefix + watcher.WatcherId, WatcherCopyPrefix + watcher.WatcherId, latestUrl,
                    Texts.T("booru.ui.watcher.latestOpenFor", latest, watcher.Query), Texts.T("booru.ui.watcher.latestCopyFor", latest, watcher.Query), _watcherWorkViewActions));
        }
        if (!watcher.BaselineEstablished)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.baseline"), Classes = { "workspace-help" } });
        if (watcher.BudgetLimited)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.budget"), Classes = { "workspace-help" } });
        if (cooling is { } until)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.cooldown", BooruText.Time(until)), Classes = { "workspace-warning" } });
        if (watcher.Status == BooruWatcherStatus.Error)
            panel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.error", BooruText.CodeText(watcher.ErrorCode)), Classes = { "workspace-error" } });
        if (row.Message is { } message)
        {
            TextBlock text = new() { Text = BooruText.Notice(message), TextWrapping = TextWrapping.Wrap };
            text.Classes.Add(message.Kind switch
            {
                BooruNoticeKind.Success => "workspace-status-success",
                BooruNoticeKind.Warning => "workspace-warning",
                BooruNoticeKind.Error => "workspace-error",
                _ => "workspace-meta"
            });
            // Announced through the persistent watcher notice line (_watcherNoticeText); this rebuilt line would stay silent.
            panel.Children.Add(text);
        }

        WrapPanel actions = new() { Classes = { "workspace-toolbar" } };
        BooruWatcherBlock checkBlock = model.CheckBlock(row);
        Button check = WatcherButton(CheckPrefix + watcher.WatcherId, "booru.ui.watcher.checkNow", "booru.ui.watcher.checkNowFor",
            watcher.Query, checkBlock, cooling, async () => await model.CheckNowAsync(watcher.WatcherId));
        RegisterWorkViewAction(check, "booru.watcher.check-now", _watcherWorkViewActions);
        actions.Children.Add(check);
        if (watcher.Status == BooruWatcherStatus.Paused)
        {
            Button resume = WatcherButton(ResumePrefix + watcher.WatcherId, "booru.ui.watcher.resume", "booru.ui.watcher.resumeFor",
                watcher.Query, model.ResumeBlock(row), cooling, async () => await model.ResumeAsync(watcher.WatcherId), ghost: true);
            RegisterWorkViewAction(resume, "booru.watcher.pause", _watcherWorkViewActions);
            actions.Children.Add(resume);
        }
        else
        {
            Button pause = WatcherButton(PausePrefix + watcher.WatcherId, "booru.ui.watcher.pause", "booru.ui.watcher.pauseFor",
                watcher.Query, model.PauseBlock(row), cooling, async () => await model.PauseAsync(watcher.WatcherId), ghost: true);
            RegisterWorkViewAction(pause, "booru.watcher.pause", _watcherWorkViewActions);
            actions.Children.Add(pause);
        }
        Button remove = WatcherButton(RemovePrefix + watcher.WatcherId, "booru.ui.watcher.remove", "booru.ui.watcher.removeFor",
            watcher.Query, model.RemoveBlock(row), cooling, () => { model.RequestRemoval(watcher.WatcherId); return Task.CompletedTask; });
        remove.Classes.Add("danger");
        RegisterWorkViewAction(remove, "booru.watcher.remove", _watcherWorkViewActions);
        actions.Children.Add(remove);
        if (BooruWatcherListModel.CanRestartFromNow(watcher))
        {
            Button restart = WatcherButton(RestartPrefix + watcher.WatcherId, "booru.ui.watcher.restart", "booru.ui.watcher.restartFor",
                watcher.Query, model.RestartBlock(row), cooling, async () => await model.RestartFromNowAsync(watcher.WatcherId), ghost: true);
            RegisterWorkViewAction(restart, "booru.watcher.restart", _watcherWorkViewActions);
            actions.Children.Add(restart);
        }
        panel.Children.Add(actions);

        if (row.ConfirmingRemoval)
        {
            Border confirm = new();
            confirm.Classes.Add("workspace-warning");
            StackPanel confirmPanel = new() { Spacing = 8 };
            confirmPanel.Children.Add(new TextBlock { Text = Texts.T("booru.ui.watcher.removeAsk", watcher.Query), TextWrapping = TextWrapping.Wrap });
            WrapPanel confirmActions = new() { Classes = { "workspace-toolbar" } };
            // Safe answer first; it also gets the focus when the question opens (RenderWatchers).
            Button no = new() { Name = RemoveCancelPrefix + watcher.WatcherId, Content = Texts.T("booru.ui.cancel") };
            no.Click += (_, _) => model.CancelRemoval(watcher.WatcherId);
            Button yes = new() { Name = RemoveConfirmPrefix + watcher.WatcherId, Content = Texts.T("booru.ui.watcher.removeConfirm") };
            yes.Classes.Add("danger");
            AutomationProperties.SetName(yes, Texts.T("booru.ui.watcher.removeFor", watcher.Query));
            yes.IsEnabled = !row.Busy;
            yes.Click += async (_, _) => await model.ConfirmRemovalAsync(watcher.WatcherId);
            confirmActions.Children.Add(no);
            confirmActions.Children.Add(yes);
            confirmPanel.Children.Add(confirmActions);
            confirm.Child = confirmPanel;
            panel.Children.Add(confirm);
        }
        card.Child = panel;
        AttachCardMenu(card, () => ComposeWatcherMenu(card, watcher.WatcherId, watcher.Query));
        ConfigureWatcherCard(card, watcher.Query);
        return card;
    }

    private static Button WatcherButton(string name, string labelKey, string nameKey, string query, BooruWatcherBlock block,
        DateTimeOffset? cooling, Func<Task> action, bool ghost = false)
    {
        Button button = new() { Name = name, Content = Texts.T(labelKey), IsEnabled = block == BooruWatcherBlock.None };
        // Check now keeps its outline as the card's one findable action, Remove keeps its red one; the rest is quiet (UX round 3).
        if (ghost) button.Classes.Add("ghost");
        string accessible = Texts.T(nameKey, query);
        AutomationProperties.SetName(button, accessible);
        string why = BooruText.WatcherBlock(block, cooling);
        ToolTip.SetTip(button, block == BooruWatcherBlock.None ? accessible : why);
        ToolTip.SetShowOnDisabled(button, true);
        if (block != BooruWatcherBlock.None) AutomationProperties.SetHelpText(button, why);
        button.Click += async (_, _) => await action();
        return button;
    }
}
