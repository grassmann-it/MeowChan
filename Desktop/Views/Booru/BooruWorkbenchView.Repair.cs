using System;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The repair route for a running Danbooru build that fails with <c>sdk-mismatch</c>, on both tabs. The view only
/// presents the host's <see cref="Repair"/> state and calls the host's callbacks; it never selects or trusts a build.
/// </summary>
internal sealed partial class BooruWorkbenchView
{
    internal const string ReviewBuildName = "BooruReviewCurrentBuild";
    internal const string OpenPluginName = "BooruRepairOpenPlugin";
    internal const string RepairNoteName = "BooruRepairNote";
    internal const string WatcherRepairName = "BooruWatcherRepair";
    internal const string WatcherRepairTextName = "BooruWatcherRepairText";
    internal const string WatcherReviewBuildName = "BooruWatchersReviewCurrentBuild";
    internal const string WatcherOpenPluginName = "BooruWatchersRepairOpenPlugin";
    internal const string WatcherRepairNoteName = "BooruWatcherRepairNote";

    /// <summary>The route's controls of one tab: the primary review action, the blocked case's way to the plugin, a reason line.</summary>
    private sealed class RepairControls
    {
        internal RepairControls(string review, string open, string note)
        {
            Review = new Button { Name = review, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
            Review.Classes.Add("primary");
            Open = new Button { Name = open, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
            Note = new TextBlock { Name = note, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
            Note.Classes.Add("workspace-help");
            AutomationProperties.SetLiveSetting(Note, AutomationLiveSetting.Polite);
        }

        internal Button Review { get; }
        internal Button Open { get; }
        internal TextBlock Note { get; }
    }

    private readonly RepairControls _searchRepair = new(ReviewBuildName, OpenPluginName, RepairNoteName);
    private readonly RepairControls _watcherRepair = new(WatcherReviewBuildName, WatcherOpenPluginName, WatcherRepairNoteName);
    private readonly Border _watcherRepairHost = new() { Name = WatcherRepairName, IsVisible = false };
    private readonly TextBlock _watcherRepairText = new() { Name = WatcherRepairTextName, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private BooruBuildRepair _repair = BooruBuildRepair.None;
    private (string Key, object?[] Args)? _repairNotice;

    /// <summary>
    /// The host's reading of the repair route (session observation, plugin snapshot, store previews). Setting it re-renders
    /// both tabs and retires a transient route notice.
    /// </summary>
    internal BooruBuildRepair Repair
    {
        get => _repair;
        set
        {
            _repair = value ?? BooruBuildRepair.None;
            _repairNotice = null;
            if (!_closed) Render(force: false);
        }
    }

    /// <summary>Why the review cannot open right now (a text key), or null. Evaluated on every render and on activation.</summary>
    internal Func<string?>? ReviewUnavailableReason { get; set; }

    /// <summary>The blocked case's next step: the Danbooru plugin's details on the Plugins page.</summary>
    internal Func<Task>? OpenPluginDetails { get; set; }

    /// <summary>Re-evaluates the route's availability (for example when the Plugins page became idle).</summary>
    internal void RefreshRepairPresentation()
    {
        if (!_closed) Render(force: false);
    }

    /// <summary>A transient, announced line under the route's actions: why the last activation did not open the review.</summary>
    internal void ShowRepairNotice(string key, params object?[] args)
    {
        if (_closed) return;
        _repairNotice = (key, args);
        Render(force: false);
    }

    internal string RepairStatusText => BooruText.RepairStatus(_repair);

    /// <summary>The Search tab's status box and route controls, for headless checks and the smoke.</summary>
    internal (Border Host, TextBlock Text, Button Retry, Button Review, Button Open, TextBlock Note) SearchRepairForTests =>
        (_statusHost, _statusText, _retry, _searchRepair.Review, _searchRepair.Open, _searchRepair.Note);

    /// <summary>The Watchers tab's repair box and route controls, for headless checks and the smoke.</summary>
    internal (Border Host, TextBlock Text, Button Review, Button Open, TextBlock Note) WatcherRepairForTests =>
        (_watcherRepairHost, _watcherRepairText, _watcherRepair.Review, _watcherRepair.Open, _watcherRepair.Note);

    internal Task ReviewCurrentBuildAsync() => ReviewCurrentBuildAsync(_searchRepair);

    private async Task ReviewCurrentBuildAsync(RepairControls origin)
    {
        if (_closed || !_attached || !IsEffectivelyVisible || !IsEffectivelyEnabled
            || !BooruText.RepairKnown(_model.Search.View, _repair) || _repair.Stage is BooruRepairStage.Blocked or BooruRepairStage.ConfirmedPending
            || ReviewCurrentBuild is not { } review || !CommandRouter.IsRoutingContextActive(this, this)) return;
        // No silent click: a route that cannot run says why, in the line under the button.
        if (ReviewUnavailableReason?.Invoke() is { } reason)
        {
            ShowRepairNotice(reason);
            return;
        }
        _repairNotice = null;
        await review();
        FocusRepairAfterRoute(origin);
    }

    private async Task OpenPluginDetailsAsync(RepairControls origin)
    {
        if (_closed || !_attached || !IsEffectivelyVisible || _repair.Stage != BooruRepairStage.Blocked
            || OpenPluginDetails is not { } open || !CommandRouter.IsRoutingContextActive(this, this)) return;
        await open();
    }

    /// <summary>When the dialog closed, focus returns to this tab's route: its action, else (a confirmed repair) its status line.</summary>
    private void FocusRepairAfterRoute(RepairControls origin) => Dispatcher.UIThread.Post(() =>
    {
        if (_closed || !_attached) return;
        Control target = origin.Review is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } ? origin.Review
            : origin.Open is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true } ? origin.Open
            : ReferenceEquals(origin, _searchRepair) ? _statusText : _watcherRepairText;
        if (target is TextBlock text) text.Focusable = true;
        target.BringIntoView();
        target.Focus(NavigationMethod.Tab);
    }, DispatcherPriority.Loaded);

    private void WireRepair(RepairControls controls, Panel host)
    {
        controls.Review.Click += async (_, _) => await ReviewCurrentBuildAsync(controls);
        controls.Open.Click += async (_, _) => await OpenPluginDetailsAsync(controls);
        CommandBindings.EnsureIconBeforeText(controls.Review, "check");
        host.Children.Add(controls.Review);
        host.Children.Add(controls.Open);
        host.Children.Add(controls.Note);
    }

    private Control BuildWatcherRepair()
    {
        StackPanel content = new() { Spacing = 8 };
        content.Children.Add(_watcherRepairText);
        WireRepair(_watcherRepair, content);
        _watcherRepairHost.Child = content;
        return _watcherRepairHost;
    }

    private BooruNoticeKind RepairTone() => _repair.Stage == BooruRepairStage.ConfirmedPending ? BooruNoticeKind.Info : BooruNoticeKind.Error;

    private void RenderRepairControls(RepairControls controls, bool shown)
    {
        bool reviewable = shown && _repair.Stage is BooruRepairStage.Unknown or BooruRepairStage.Reviewable && ReviewCurrentBuild != null;
        string? reasonKey = reviewable ? ReviewUnavailableReason?.Invoke() : null;
        string? reason = reasonKey is null ? null : Texts.T(reasonKey);
        // One name for the dialog everywhere: the Plugins header's "Review bundled repairs".
        string label = Texts.T("plugins.repair.review");
        controls.Review.Content = label; // the catalog icon is the content template; the text stays the content
        AutomationProperties.SetName(controls.Review, label);
        controls.Review.IsVisible = reviewable;
        controls.Review.IsEnabled = reviewable && reason is null;
        ToolTip.SetTip(controls.Review, reason);
        ToolTip.SetShowOnDisabled(controls.Review, true);
        AutomationProperties.SetHelpText(controls.Review, reason ?? string.Empty);

        bool open = shown && _repair.Stage == BooruRepairStage.Blocked && OpenPluginDetails != null;
        controls.Open.Content = Texts.T("booru.ui.repair.openPlugin");
        AutomationProperties.SetName(controls.Open, Texts.T("booru.ui.repair.openPlugin"));
        controls.Open.IsVisible = open;

        string? note = shown && _repairNotice is { } notice ? Texts.T(notice.Key, notice.Args) : reason;
        controls.Note.Text = note ?? string.Empty;
        controls.Note.IsVisible = !string.IsNullOrEmpty(note);
        AutomationProperties.SetName(controls.Note, note ?? string.Empty);
    }

    private void RenderWatcherRepair()
    {
        bool known = BooruText.RepairKnown(_model.Search.View, _repair);
        _watcherRepairHost.IsVisible = known;
        if (known)
        {
            BooruNoticeKind tone = RepairTone();
            _watcherRepairText.Text = BooruText.RepairStatus(_repair);
            AutomationProperties.SetName(_watcherRepairText, _watcherRepairText.Text);
            AutomationProperties.SetLiveSetting(_watcherRepairText, tone == BooruNoticeKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
            ApplyTone(_watcherRepairHost, tone);
        }
        RenderRepairControls(_watcherRepair, known);
    }
}
