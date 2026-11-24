using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private enum ResultAction { Load, Cancel, Retry, ShowInMedia, Open, Copy }

    private sealed record ResultCommand(
        ResultAction Action, string Prefix, string Label, string Name, string Icon, string Tip,
        string? WorkViewId = null, string? Reason = null);

    private readonly record struct ResultLinkScope(BooruResultTarget Target, object? Tab);

    private ResultLinkScope? CaptureResultLink(BooruResultTarget? target) =>
        target != null ? new(target, _tabs.SelectedItem) : null;

    private bool IsCurrentResultLink(ResultLinkScope scope) =>
        !_closed && ReferenceEquals(scope.Tab, _tabs.SelectedItem) && _model.Search.ResultRow(scope.Target) != null;

    /// <summary>One binding shared by the tile or list row and every surface that acts on it.</summary>
    private sealed class ResultActions(BooruWorkbenchView owner, BooruResultTarget target)
    {
        private readonly string? _url = BooruPostLinks.TryCanonical(target.Post, out string url) ? url : null;

        internal BooruResultTarget Target => target;
        internal string PostId => target.Post.PostId;
        internal bool IsCurrent() => IsSearchItem(owner._tabs.SelectedItem) && CurrentRow() != null;

        internal bool CanRun(ResultAction action) => CurrentRow() is { } row && IsAvailable(action, row);

        internal bool IsAvailable(ResultAction action, BooruResultRowView row) => action switch
        {
            ResultAction.Load => row.CanLoad && !row.CanRetry,
            ResultAction.Cancel => row.CanCancel,
            ResultAction.Retry => row.CanRetry,
            ResultAction.ShowInMedia => row.Load == BooruRowLoad.AlreadyLoaded && owner._showInMedia != null
                && (owner._isPostSavedInMedia == null || owner._isPostSavedInMedia(PostId)),
            ResultAction.Open or ResultAction.Copy => _url != null,
            _ => false
        };

        internal bool Run(ResultAction action)
        {
            if (!CanRun(action)) return false;
            OpenActions.Start(() => ExecuteAsync(action), "booru.result." + action);
            return true;
        }

        private Task ExecuteAsync(ResultAction action) => action switch
        {
            ResultAction.Load => owner._model.Search.LoadOriginalAsync(PostId, target),
            ResultAction.Cancel => owner._model.Search.CancelOriginalAsync(PostId, target),
            ResultAction.Retry => owner._model.Search.RetryOriginalAsync(PostId, target),
            ResultAction.ShowInMedia => owner.ShowInMediaAsync(PostId, target),
            ResultAction.Open => owner.OpenLinkAsync(_url!, target),
            ResultAction.Copy => owner.CopyLinkAsync(_url!, target),
            _ => Task.CompletedTask
        };

        internal IEnumerable<ResultCommand> Commands(BooruResultRowView row)
        {
            string postTitle = Texts.T("booru.ui.post.title", PostId);
            string cancel = Texts.T("booru.ui.cancel");
            string cancelName = $"{cancel}: {postTitle}";
            bool withdrawn = row.Post.IsDeleted || row.Post.IsBanned;
            string original = BooruText.Original(row.Post.OriginalAccess, withdrawn);
            string? reason = row.Post.OriginalAccess != BooruOriginalAccess.Available ? original : BooruText.Load(row);
            string loadName = Texts.T("booru.ui.loadFor", PostId);
            string retry = BooruText.RetryOriginal(row);

            if (row.CanRetry)
                yield return new(ResultAction.Retry, RetryLoadPrefix, retry, $"{retry}: {postTitle}", CardMenuIcons.Retry,
                    reason ?? $"{retry}: {postTitle}", "booru.result.retry", reason);
            if (row.CanCancel)
                yield return new(ResultAction.Cancel, CancelLoadPrefix, cancel, cancelName, CardMenuIcons.Cancel,
                    row.CanRetry ? cancelName : reason ?? cancelName, "booru.result.cancel", row.CanRetry ? null : reason);
            if (!row.CanRetry && !row.CanCancel)
                yield return new(ResultAction.Load, LoadPrefix, Texts.T("booru.ui.load"), loadName, CardMenuIcons.Load,
                    reason ?? loadName + Environment.NewLine + original, "booru.result.load", reason);

            foreach (ResultCommand link in LinkCommands()) yield return link;
            if (IsAvailable(ResultAction.ShowInMedia, row))
            {
                string name = Texts.T("booru.ui.result.showInMediaFor", PostId);
                yield return new(ResultAction.ShowInMedia, ShowInMediaPrefix, Texts.T("booru.ui.result.showInMedia"), name,
                    CommandCatalog.Get("thread.show-in-media").Icon, name);
            }
        }

        internal IEnumerable<ResultCommand> LinkCommands()
        {
            if (_url == null) yield break;
            yield return new(ResultAction.Open, OpenPostPrefix, Texts.T("booru.media.openPost"),
                Texts.T("booru.ui.post.openFor", PostId), CommandCatalog.Get("post.open-source").Icon,
                Texts.T("booru.ui.post.openTip", _url), "booru.result.open");
            yield return new(ResultAction.Copy, CopyLinkPrefix, Texts.T("booru.media.copyPost"),
                Texts.T("booru.ui.post.copyFor", PostId), CommandCatalog.Get("post.copy-link").Icon,
                Texts.T("booru.ui.post.copy") + Environment.NewLine + _url, "booru.result.copy-link");
        }

        internal BooruResultRowView? CurrentRow() => owner._closed ? null : owner._model.Search.ResultRow(target);
    }

    private ResultActions BindResult(BooruSearchView view, BooruResultRowView row)
    {
        ResultActions result = new(this, BooruResultTarget.From(view, row));
        _resultActions.Add(result);
        return result;
    }

    private void AddResultCommands(BooruResultRowView row, ResultActions result, Panel actions)
    {
        bool primary = true;
        foreach (ResultCommand command in result.Commands(row))
        {
            Button button = new() { Name = command.Prefix + result.PostId, Content = command.Label };
            if (command.Action is ResultAction.Open or ResultAction.Copy)
            {
                button.Classes.Add("ghost");
                IconButtons.Apply(button, command.Action == ResultAction.Copy ? "copy" : command.Icon, command.Name);
                SizeCompactAction(button);
            }
            else
            {
                if (primary) button.Classes.Add("primary");
                if (command.Action == ResultAction.ShowInMedia) button.Classes.Add("ghost");
                CompactAction(button, command.Icon);
            }
            primary = false;
            button.IsEnabled = result.IsAvailable(command.Action, row);
            AutomationProperties.SetName(button, command.Name);
            ToolTip.SetTip(button, command.Tip);
            ToolTip.SetShowOnDisabled(button, true);
            if (command.Reason != null) AutomationProperties.SetHelpText(button, command.Reason);
            button.Click += (_, _) => { if (result.IsCurrent()) result.Run(command.Action); };
            if (command.WorkViewId is { } id) RegisterWorkViewAction(button, id, _resultWorkViewButtons);
            actions.Children.Add(button);
        }
    }
}
