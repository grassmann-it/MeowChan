using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private readonly ComboBox _sourcePicker = new()
    {
        Name = "BooruSourcePicker",
        HorizontalAlignment = HorizontalAlignment.Left,
        IsVisible = false
    };

    private ComboBox BuildSourcePicker()
    {
        AutomationProperties.SetAutomationId(_sourcePicker, _sourcePicker.Name);
        _sourcePicker.IsVisible = _model.Sources.Count > 1;
        if (!_sourcePicker.IsVisible) return _sourcePicker;
        _sourcePicker.ItemsSource = _model.Sources.Select(source => new ComboBoxItem
        {
            Content = source.Search.ProviderName,
            Tag = source.Search
        }).ToArray();
        _sourcePicker.SelectionChanged += (_, _) =>
        {
            if (_applying || _sourcePicker.SelectedItem is not ComboBoxItem { Tag: IBooruSearchPort source }) return;
            DismissResultViewer();
            ReleaseGallery();
            _cursorPostId = null;
            _selectedResults.Clear();
            SearchTabs.SelectSource(source.ProviderId, source.InstanceId);
        };
        return _sourcePicker;
    }

    private void RenderSourcePicker(BooruSearchView view)
    {
        if (_sourcePicker.ItemsSource is not ComboBoxItem[] items) return;
        string label = Texts.T("booru.tabs.source");
        AutomationProperties.SetName(_sourcePicker, label);
        ToolTip.SetTip(_sourcePicker, label);
        _sourcePicker.SelectedItem = items.FirstOrDefault(item => item.Tag is IBooruSearchPort source
            && source.ProviderId == view.ProviderId && source.InstanceId == view.InstanceId);
    }

    internal const string SearchOverflowName = "BooruSearchOverflow";
    internal const string OpenSearchName = "BooruOpenSearchInBrowser";
    internal const string RatingSwitchPrefix = "BooruSearchRating:";
    private const string OpenSearchIcon = "external-link";
    private const string WatchSearchCommand = "booru.search.watch";
    private const string DownloadAllCommand = "booru.search.download-all";
    private readonly Button _searchOverflow = new()
    {
        Name = SearchOverflowName, Classes = { "subtle", "icon-square" }, Focusable = false, IsTabStop = false
    };
    private readonly Button _openSearch = new() { Name = OpenSearchName };
    private readonly MenuItem _openSearchItem = new();
    private readonly RatingSwitch[] _ratings = BooruRatingDraft.Codes.Select(code => new RatingSwitch(code)).ToArray();

    private SearchToolbar BuildSearchToolbar()
    {
        AutomationProperties.SetAutomationId(_alternateSubmit, AlternateSubmitName);
        CommandBindings.EnsureIconBeforeText(_alternateSubmit, "plus");
        _alternateSubmit.Click += async (_, _) => await SubmitSearchAsync(alternate: true);
        _downloadAll.Click += async (_, _) =>
        {
            if (_model.Search.Bulk.IsRunning) _model.Search.CancelBulk();
            else await _model.Search.DownloadAllAsync();
        };
        _bulkHost.Child = _bulkText;
        AutomationProperties.SetLiveSetting(_bulkText, AutomationLiveSetting.Polite);

        _searchOverflow.Content = CommandBindings.ButtonIcon("more-horizontal", 16, byKey: true);
        MenuFlyout menu = new() { Placement = PlacementMode.BottomEdgeAlignedRight, OverlayInputPassThroughElement = _searchOverflow };
        PopupToggle.BindFlyout(_searchOverflow, menu);
        AutomationProperties.SetAutomationId(_openSearch, OpenSearchName);
        _openSearchItem.Icon = CommandBindings.IconFromKey(OpenSearchIcon, deferMask: true);
        SearchAction.ConnectItem(_openSearch, _openSearchItem);
        _openSearch.Click += (_, _) =>
        {
            if (BooruSearchLinks.ForSearch(_model.Search.View) is { } url)
                OpenActions.Start(() => OpenLinkAsync(url, failureKey: "booru.tabs.openSearchFailed"), "booru.search.openBrowser");
        };
        StackPanel ratings = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (RatingSwitch rating in _ratings)
        {
            ratings.Children.Add(rating.Button);
            rating.Button.Click += (_, _) => ToggleRating(rating.Code);
        }
        SearchAction[] actions =
        [
            SearchAction.ForCommand(_alternateSubmit, AlternateSubmitCommand),
            SearchAction.ForCommand(_createWatcher, WatchSearchCommand),
            SearchAction.ForCommand(_downloadAll, DownloadAllCommand),
            new(_openSearch, _openSearchItem),
            new(ratings, _ratings.Select(rating => rating.Item).ToArray())
        ];
        menu.Opening += (_, _) =>
        {
            EffectiveShortcutBindings bindings = ShortcutService.Current;
            foreach (SearchAction action in actions) action.RefreshGesture(bindings);
        };
        menu.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!menu.IsOpen && _input.IsEffectivelyVisible) FocusSearchInput();
        }, DispatcherPriority.Input);

        SearchToolbar toolbar = new(_mode, _input, _submit, _cancel, _searchOverflow, menu, actions);
        AutomationProperties.SetAutomationId(toolbar, "BooruSearchToolbar");
        return toolbar;
    }

    private void RenderSearchToolbar(BooruSearchView view)
    {
        string label = Texts.T("workview.more");
        ToolTip.SetTip(_searchOverflow, label);
        AutomationProperties.SetName(_searchOverflow, label);

        string open = Texts.T("booru.tabs.openSearch");
        bool canOpen = BooruSearchLinks.CanOpen(view);
        string openTip = canOpen ? open : Texts.T("booru.tabs.openSearchNeedsQuery");
        IconButtons.Apply(_openSearch, OpenSearchIcon, open);
        _openSearchItem.Header = open;
        AutomationProperties.SetName(_openSearchItem, open);
        _openSearch.IsEnabled = canOpen;
        ToolTip.SetTip(_openSearch, openTip);
        ToolTip.SetShowOnDisabled(_openSearch, true);
        AutomationProperties.SetHelpText(_openSearch, openTip);

        bool canRate = CanRate(view);
        string selected = canRate ? BooruRatingDraft.Selected(view.Input) : string.Empty;
        foreach (RatingSwitch rating in _ratings) rating.Render(selected.Contains(rating.Code), canRate);
    }

    private static bool CanRate(BooruSearchView view) =>
        view.ProviderId == DanbooruPostReference.ProviderId && view.Mode == BooruInputMode.Tags && view.Parsed.Kind != BooruInputKind.Post;

    private void ToggleRating(char rating)
    {
        BooruSearchView view = _model.Search.View;
        if (_closed || !CanRate(view)) return;
        _input.Text = BooruRatingDraft.Toggle(view.Input, rating);
    }

    private static string AddSearchShortcut(string label, string command) =>
        CommandCatalog.ShortcutTooltipDisplay(command, "booru") is { } gesture ? label + " · " + gesture : label;

    private static HelpContent BuildSearchHelp(BooruSearchView view)
    {
        List<string> paragraphs = [Texts.T("booru.ui.provider", view.ProviderName), Texts.T("booru.ui.intro")];
        if (ExecutedSearchText(view) is { } executed) paragraphs.Add(executed);
        if (DraftSearchText(view) is { } draft) paragraphs.Add(draft);
        if (view.Mode == BooruInputMode.Tags) paragraphs.Add(Texts.T("booru.ui.query.hint"));
        if (!view.HasContentRules) paragraphs.Add(Texts.T("booru.ui.noFilterNote"));
        return new HelpContent(Texts.T("booru.ui.title"), paragraphs, []);
    }

    private static string? ExecutedSearchText(BooruSearchView view) =>
        view.ExecutedQuery is { } query ? Texts.T("booru.ui.query.sent", query)
        : view.ExecutedPostId is { } postId ? Texts.T("booru.ui.post.sent", postId) : null;

    private static string? DraftSearchText(BooruSearchView view)
    {
        string? draft = view.Parsed.Kind switch
        {
            BooruInputKind.Tags when !string.Equals(view.Parsed.Canonical, view.ExecutedQuery, StringComparison.OrdinalIgnoreCase)
                => view.Parsed.Canonical,
            BooruInputKind.Post when view.Parsed.PostId != view.ExecutedPostId => "#" + view.Parsed.PostId,
            BooruInputKind.Invalid => view.Input.Trim(),
            BooruInputKind.Empty when view.ExecutedQuery != null || view.ExecutedPostId != null =>
                Texts.T("booru.ui.query.draftEmpty"),
            _ => null
        };
        return string.IsNullOrEmpty(draft) ? null : Texts.T("booru.ui.query.draft", draft);
    }

    private sealed class RatingSwitch
    {
        private readonly string _labelKey;

        internal RatingSwitch(char code)
        {
            Code = code;
            _labelKey = "booru.tabs.rating." + code;
            Button = new ToggleButton { Name = RatingSwitchPrefix + code, Content = code.ToString(), Classes = { "breakdown-chip" } };
            Item = new MenuItem { ToggleType = MenuItemToggleType.CheckBox };
            AutomationProperties.SetAutomationId(Button, Button.Name);
            SearchAction.ConnectItem(Button, Item);
        }

        internal char Code { get; }
        internal ToggleButton Button { get; }
        internal MenuItem Item { get; }

        internal void Render(bool selected, bool enabled)
        {
            string label = Texts.T(_labelKey);
            Button.IsChecked = Item.IsChecked = selected;
            Button.IsEnabled = enabled;
            Item.Header = label;
            AutomationProperties.SetName(Button, label);
            AutomationProperties.SetName(Item, label);
            ToolTip.SetTip(Button, label);
            ToolTip.SetShowOnDisabled(Button, true);
        }
    }

    private sealed class SearchAction(Control control, params MenuItem[] items)
    {
        internal static SearchAction ForCommand(Button button, string command)
        {
            MenuItem item = new();
            item.Bind(MenuItem.HeaderProperty, button.GetObservable(Button.ContentProperty));
            ConnectItem(button, item);
            return new SearchAction(button, item) { Command = command };
        }

        internal static void ConnectItem(Button button, MenuItem item)
        {
            item.Bind(IsEnabledProperty, button.GetObservable(IsEnabledProperty));
            item.Bind(AutomationProperties.HelpTextProperty, button.GetObservable(AutomationProperties.HelpTextProperty));
            item.Bind(ToolTip.TipProperty, button.GetObservable(ToolTip.TipProperty));
            ToolTip.SetShowOnDisabled(item, true);
            AutomationProperties.SetAutomationId(item, button.Name + "Overflow");
            item.Click += (_, _) =>
            {
                if (button.IsEffectivelyEnabled) CommandBindings.InvokeAction(button);
            };
        }

        private string? Command { get; init; }
        internal Control Control { get; } = control;
        internal Border Slot { get; } = new() { Child = control };
        internal MenuItem[] Items { get; } = items;
        internal double Width => Control.DesiredSize.Width + SearchToolbar.Gap;

        internal void RefreshGesture(EffectiveShortcutBindings bindings)
        {
            if (Command is not { } command) return;
            MenuItem item = Items[0];
            item.Icon ??= CommandBindings.Icon(command);
            item.InputGesture = bindings.For(command)
                .FirstOrDefault(gesture => ShortcutHelp.ActsHere(command, gesture, "booru", bindings))
                ?.Resolve(bindings.Platform);
        }

        internal void SetInline(bool inline)
        {
            Slot.IsVisible = inline;
            foreach (MenuItem item in Items) item.IsVisible = !inline;
        }
    }

    private sealed class SearchToolbar : Panel
    {
        internal const double Gap = 8;
        private const double MinimumInputWidth = 160;
        private const double PreferredInputWidth = 320;
        private const double MaximumInputWidth = 512;

        private readonly Control _mode, _input, _submit, _cancel, _overflow;
        private readonly SearchAction[] _optional;
        private readonly MenuFlyout _menu;
        private readonly StackPanel _actions = new() { Name = "BooruSearchActions", Orientation = Orientation.Horizontal, Spacing = Gap };

        internal SearchToolbar(Control mode, Control input, Control submit, Control cancel, Control overflow,
            MenuFlyout menu, SearchAction[] optional)
        {
            (_mode, _input, _submit, _cancel, _overflow, _menu, _optional) = (mode, input, submit, cancel, overflow, menu, optional);
            AutomationProperties.SetAutomationId(_actions, _actions.Name);
            foreach (SearchAction action in optional)
            {
                _actions.Children.Add(action.Slot);
                foreach (MenuItem item in action.Items) menu.Items.Add(item);
            }
            _actions.Children.Add(cancel);
            _actions.Children.Add(overflow);
            Children.AddRange([mode, input, submit, _actions]);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _mode.Measure(Avalonia.Size.Infinity);
            _submit.Measure(Avalonia.Size.Infinity);
            _cancel.Measure(Avalonia.Size.Infinity);
            // Measure the controls inside their slots so hidden actions can move back as soon as they fit.
            foreach (SearchAction action in _optional) action.Control.Measure(Avalonia.Size.Infinity);
            double budget = availableSize.Width - PrimaryWidth - PreferredInputWidth;
            if (_cancel.IsVisible) budget -= _cancel.DesiredSize.Width + Gap;
            double optionalWidth = _optional.Sum(action => action.Width);
            int inline = _optional.Length;
            bool needsOverflow = optionalWidth > budget;
            if (needsOverflow)
            {
                _overflow.IsVisible = true;
                _overflow.Measure(Avalonia.Size.Infinity);
                budget -= _overflow.DesiredSize.Width + Gap;
                while (inline > 0 && optionalWidth > budget) optionalWidth -= _optional[--inline].Width;
            }
            else
            {
                if (_menu.IsOpen) _menu.Hide();
                _overflow.IsVisible = false;
            }
            for (int i = 0; i < _optional.Length; i++) _optional[i].SetInline(i < inline);
            _actions.Measure(Avalonia.Size.Infinity);
            double modeBudget = availableSize.Width - MinimumInputWidth - (ReservedWidth - _mode.DesiredSize.Width);
            if (_mode.DesiredSize.Width > modeBudget)
                _mode.Measure(new Size(Math.Max(0, modeBudget), double.PositiveInfinity));
            double width = double.IsPositiveInfinity(availableSize.Width) ? ReservedWidth + PreferredInputWidth : availableSize.Width;
            _input.Measure(new Size(InputWidth(width), double.PositiveInfinity));
            double height = Math.Max(Math.Max(_mode.DesiredSize.Height, _input.DesiredSize.Height),
                Math.Max(_submit.DesiredSize.Height, _actions.DesiredSize.Height));
            return new Size(width, height);
        }

        private double PrimaryWidth => _mode.DesiredSize.Width + _submit.DesiredSize.Width + 2 * Gap;
        private double ReservedWidth => PrimaryWidth + _actions.DesiredSize.Width + Gap;
        private double InputWidth(double width) => Math.Clamp(width - ReservedWidth, 0, MaximumInputWidth);

        protected override Size ArrangeOverride(Size finalSize)
        {
            double modeWidth = _mode.DesiredSize.Width;
            double inputWidth = InputWidth(finalSize.Width);
            double submitX = modeWidth + inputWidth + 2 * Gap;
            _mode.Arrange(new Rect(0, 0, modeWidth, finalSize.Height));
            _input.Arrange(new Rect(modeWidth + Gap, 0, inputWidth, finalSize.Height));
            _submit.Arrange(new Rect(submitX, 0, _submit.DesiredSize.Width, finalSize.Height));
            _actions.Arrange(new Rect(submitX + _submit.DesiredSize.Width + Gap, 0, _actions.DesiredSize.Width, finalSize.Height));
            return finalSize;
        }
    }
}
