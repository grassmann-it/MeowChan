using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private readonly WrapPanel _pagerRow = new() { HorizontalAlignment = HorizontalAlignment.Center, ItemSpacing = 8, LineSpacing = 4 };
    private readonly TextBox _jumpPage = new() { Width = 64, MaxLength = 4 };
    private readonly Button _jump = new() { Focusable = false, IsTabStop = false };
    private BooruResultRevision? _jumpRevision;

    private void InitializePager()
    {
        AutomationProperties.SetAutomationId(_previous, PreviousName);
        AutomationProperties.SetAutomationId(_next, NextName);
        AutomationProperties.SetAutomationId(_pageInfo, "BooruPageInfo");
        AutomationProperties.SetAutomationId(_bottomBar, "BooruResultsFooter");
        AutomationProperties.SetAutomationId(_jumpPage, "BooruPageNumber");
        AutomationProperties.SetAutomationId(_jump, "BooruPageJump");
        _pagerRow.Children.Add(_pager);
        _pagerRow.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { _jumpPage, _jump } });
        _jumpPage.TextChanged += (_, _) => _jump.IsEnabled = JumpTarget(_model.Search.View).HasValue;
        _jumpPage.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Handled || ShortcutServiceCapturing() || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;
            e.Handled = true;
            await JumpAsync();
        }, RoutingStrategies.Tunnel);
        _jump.Click += async (_, _) => await JumpAsync();
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel);
    }

    private int? JumpTarget(BooruSearchView view) =>
        int.TryParse(_jumpPage.Text, NumberStyles.None, CultureInfo.CurrentCulture, out int page)
        && page >= 1 && page <= (view.LastResultPage ?? view.PagerLimits.Pages)
        && (page < view.Page && view.HasPreviousPage || page > view.Page && view.HasNextPage) ? page : null;

    private Task JumpAsync() => JumpTarget(_model.Search.View) is { } page
        ? ChangePageAsync(page - _model.Search.View.Page) : Task.CompletedTask;

    private async void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _closed || ShortcutServiceCapturing() || !_pager.IsEffectivelyVisible
            || !IsSearchItem(_tabs.SelectedItem) || _mode.IsDropDownOpen) return;
        bool previous = ShortcutService.Current.Matches(e, "pagination.previous", "booru");
        if (!previous && !ShortcutService.Current.Matches(e, "pagination.next", "booru")) return;
        if (!(previous ? _previous : _next).IsEnabled) return;
        e.Handled = true;
        await ChangePageAsync(previous ? -1 : 1);
    }

    private void RenderPager(BooruSearchView view)
    {
        if (_jumpRevision != view.ResultRevision)
        {
            _jumpRevision = view.ResultRevision;
            _jumpPage.Text = string.Empty;
        }
        _jump.IsEnabled = JumpTarget(view).HasValue;
        string jump = Texts.T("booru.ui.page.jump");
        _jumpPage.PlaceholderText = Texts.T("workview.overview.page");
        AutomationProperties.SetName(_jumpPage, jump);
        ToolTip.SetTip(_jumpPage, jump);
        IconButtons.Apply(_jump, "arrow-right", jump);
        SetPagerTooltip(_previous, "pagination.previous");
        SetPagerTooltip(_next, "pagination.next");

        int? last = view.LastResultPage ?? (view.IsEnd && !view.Rows.IsEmpty ? view.Page : null);
        string current = view.Page.ToString(CultureInfo.CurrentCulture);
        _pageInfo.Text = last is { } total
            ? Texts.T("booru.ui.page.info", view.Page, total, view.PagerLimits.PostsPerPage)
            : Texts.T("booru.ui.page.current", view.Status == BooruSearchStatus.Results ? current + "+" : current, view.PagerLimits.PostsPerPage);
        _pageLimit.IsVisible = view.PageLimitReached;
        _pageLimit.Text = view.PagerLimits.Pages == Config.BooruSearchPagesMaximum
            ? Texts.T("booru.ui.page.providerLimit", view.ProviderName, view.PagerLimits.Pages)
            : Texts.T("booru.ui.page.limit", view.PagerLimits.Pages, view.PagerLimits.PostsPerPage);
    }

    private static void SetPagerTooltip(Button button, string command)
    {
        if (CommandCatalog.ShortcutTooltipDisplay(command, "booru") is { } shortcut)
            ToolTip.SetTip(button, AutomationProperties.GetName(button) + " · " + shortcut);
    }
}
