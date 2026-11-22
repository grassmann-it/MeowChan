using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
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

    private static string? DraftSearchText(BooruSearchView view) => view.Parsed.Kind switch
    {
        BooruInputKind.Tags when view.Parsed.Canonical != view.ExecutedQuery => Texts.T("booru.ui.query.draft", view.Parsed.Canonical),
        BooruInputKind.Post when view.Parsed.PostId != view.ExecutedPostId => Texts.T("booru.ui.query.draft", "#" + view.Parsed.PostId),
        _ => null
    };

    private sealed class SearchToolbar : Panel
    {
        private const double Gap = 8;
        private const double MinimumInputWidth = 160;
        private const double PreferredInputWidth = 320;

        private readonly Control _mode, _input, _submit, _actions;
        private bool _wrapSubmit, _wrapActions;
        private double _firstLineHeight;

        internal SearchToolbar(Control mode, Control input, Control submit, Control actions)
        {
            (_mode, _input, _submit, _actions) = (mode, input, submit, actions);
            Children.AddRange([mode, input, submit, actions]);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            _mode.Measure(Avalonia.Size.Infinity);
            _submit.Measure(Avalonia.Size.Infinity);
            _actions.Measure(Avalonia.Size.Infinity);
            double primaryWidth = _mode.DesiredSize.Width + _submit.DesiredSize.Width + 2 * Gap;
            double inlineWidth = primaryWidth + PreferredInputWidth + Gap + _actions.DesiredSize.Width;
            double width = double.IsPositiveInfinity(availableSize.Width) ? inlineWidth : availableSize.Width;

            _wrapActions = width < inlineWidth;
            _wrapSubmit = width < primaryWidth + MinimumInputWidth;
            if (_wrapActions) _actions.Measure(new Size(width, double.PositiveInfinity));
            if (_wrapSubmit) _submit.Measure(new Size(width, double.PositiveInfinity));
            _mode.Measure(new Size(Math.Max(0, width - Gap - MinimumInputWidth), double.PositiveInfinity));
            _input.Measure(new Size(InputWidth(width), double.PositiveInfinity));

            _firstLineHeight = Math.Max(_mode.DesiredSize.Height, _input.DesiredSize.Height);
            if (!_wrapSubmit) _firstLineHeight = Math.Max(_firstLineHeight, _submit.DesiredSize.Height);
            if (!_wrapActions) _firstLineHeight = Math.Max(_firstLineHeight, _actions.DesiredSize.Height);
            double height = _firstLineHeight;
            if (_wrapSubmit) height += Gap + _submit.DesiredSize.Height;
            if (_wrapActions) height += Gap + _actions.DesiredSize.Height;
            return new Size(width, height);
        }

        private double InputWidth(double width)
        {
            double reserved = _mode.DesiredSize.Width + Gap;
            if (!_wrapSubmit) reserved += _submit.DesiredSize.Width + Gap;
            if (!_wrapActions) reserved += _actions.DesiredSize.Width + Gap;
            return Math.Max(0, width - reserved);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double modeWidth = _mode.DesiredSize.Width;
            double inputWidth = InputWidth(finalSize.Width);
            _mode.Arrange(new Rect(0, 0, modeWidth, _firstLineHeight));
            _input.Arrange(new Rect(modeWidth + Gap, 0, inputWidth, _firstLineHeight));
            double submitWidth = _submit.DesiredSize.Width;
            double submitX = _wrapSubmit ? 0 : modeWidth + inputWidth + 2 * Gap;
            double submitY = _wrapSubmit ? _firstLineHeight + Gap : 0;
            double submitHeight = _wrapSubmit ? _submit.DesiredSize.Height : _firstLineHeight;
            _submit.Arrange(new Rect(submitX, submitY, submitWidth, submitHeight));
            double actionsX = _wrapActions ? 0 : submitX + submitWidth + Gap;
            double actionsY = _wrapActions ? submitY + submitHeight + Gap : 0;
            _actions.Arrange(new Rect(actionsX, actionsY, finalSize.Width - actionsX, _actions.DesiredSize.Height));
            return finalSize;
        }
    }
}
