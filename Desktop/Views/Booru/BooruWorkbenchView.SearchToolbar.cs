using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    internal const string SearchOverflowName = "BooruSearchOverflow";
    private readonly Button _searchOverflow = new()
    {
        Name = SearchOverflowName, Classes = { "subtle", "icon-square" }, Focusable = false, IsTabStop = false
    };

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

        AutomationProperties.SetAutomationId(_searchOverflow, SearchOverflowName);
        _searchOverflow.Content = CommandBindings.ButtonIcon("more-horizontal", 16, byKey: true);
        MenuFlyout menu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
        _searchOverflow.Flyout = menu;
        SearchAction alternate = new(_alternateSubmit);
        menu.Opening += (_, _) =>
        {
            EffectiveShortcutBindings bindings = ShortcutService.Current;
            alternate.Item.InputGesture = bindings.For(AlternateSubmitCommand)
                .FirstOrDefault(gesture => ShortcutHelp.ActsHere(AlternateSubmitCommand, gesture, "booru", bindings))
                ?.Resolve(bindings.Platform);
        };
        menu.Closed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!menu.IsOpen && _input.IsEffectivelyVisible) FocusSearchInput();
        }, DispatcherPriority.Input);

        SearchToolbar toolbar = new(_mode, _input, _submit, _cancel, _searchOverflow, menu,
            [alternate, new(_createWatcher), new(_downloadAll)]);
        AutomationProperties.SetAutomationId(toolbar, "BooruSearchToolbar");
        return toolbar;
    }

    private void RenderSearchOverflow()
    {
        string label = Texts.T("booru.tabs.moreActions");
        ToolTip.SetTip(_searchOverflow, label);
        AutomationProperties.SetName(_searchOverflow, label);
    }

    private static HelpContent BuildSearchHelp(BooruSearchView view)
    {
        List<string> paragraphs = [Texts.T("booru.ui.provider", view.ProviderName), Texts.T("booru.ui.intro")];
        if (ExecutedSearchText(view) is { } executed) paragraphs.Add(executed);
        if (DraftSearchText(view) is { } draft) paragraphs.Add(draft);
        if (view.Mode == BooruInputMode.Tags) paragraphs.Add(Texts.T("booru.ui.query.hint"));
        paragraphs.Add(Texts.T("booru.ui.noFilterNote"));
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

    private sealed class SearchAction
    {
        internal SearchAction(Button button)
        {
            Button = button;
            Slot = new Border { Child = button };
            Item = new MenuItem();
            Item.Bind(MenuItem.HeaderProperty, button.GetObservable(Button.ContentProperty));
            Item.Bind(IsEnabledProperty, button.GetObservable(IsEnabledProperty));
            Item.Bind(AutomationProperties.HelpTextProperty, button.GetObservable(AutomationProperties.HelpTextProperty));
            Item.Bind(ToolTip.TipProperty, button.GetObservable(ToolTip.TipProperty));
            ToolTip.SetShowOnDisabled(Item, true);
            AutomationProperties.SetAutomationId(Item, button.Name + "Overflow");
            Item.Click += (_, _) =>
            {
                if (button.IsEffectivelyEnabled) CommandBindings.InvokeAction(button);
            };
        }

        internal Button Button { get; }
        internal Border Slot { get; }
        internal MenuItem Item { get; }
        internal double Width => Button.DesiredSize.Width + SearchToolbar.Gap;

        internal void SetInline(bool inline)
        {
            Slot.IsVisible = inline;
            Item.IsVisible = !inline;
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
            _actions.Children.Add(overflow);
            foreach (SearchAction action in optional)
            {
                _actions.Children.Add(action.Slot);
                menu.Items.Add(action.Item);
            }
            _actions.Children.Add(cancel);
            Children.AddRange([mode, input, submit, _actions]);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _mode.Measure(Avalonia.Size.Infinity);
            _submit.Measure(Avalonia.Size.Infinity);
            _cancel.Measure(Avalonia.Size.Infinity);
            // Measure the buttons inside their slots so hidden actions can move back as soon as they fit.
            foreach (SearchAction action in _optional) action.Button.Measure(Avalonia.Size.Infinity);
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
