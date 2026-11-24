using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Controls;

namespace MeowChan.Desktop.Booru;

internal sealed partial class BooruWorkbenchView
{
    private void AttachGalleryResultContext()
    {
        Control stage = _gallery.Stage;
        _ = new ContextMenuHost(stage, () => ShownResult() is { } result ? ComposeResultMenu(result) : null, FocusGalleryStage);
        ResultActions? pressed = null;
        MiddleClickTracker middle = new();
        stage.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            pressed = MiddleClickTracker.IsPress(e, stage) && !ItemActivation.IsOwnedByInteractivePart(e.Source, stage)
                ? ShownResult() : null;
            middle.Pressed(pressed);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        stage.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            if (!MiddleClickTracker.IsRelease(e)) return;
            ResultActions? result = pressed;
            pressed = null;
            if (middle.Released(MiddleClickTracker.HitAt(e, stage) is { } hit
                && !ItemActivation.IsOwnedByInteractivePart(hit, stage) ? result : null) && result?.IsCurrent() == true)
                e.Handled = result.Run(ResultAction.Open);
        }, RoutingStrategies.Tunnel);
    }

    private ResultActions? ShownResult()
    {
        if (_resultView != BooruResultView.Gallery || _gallery.CurrentPostId is not { } id) return null;
        BooruSearchView view = _model.Search.View;
        BooruResultRowView? row = view.Rows.FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Post.PostId, id));
        return row == null ? null : new ResultActions(this, BooruResultTarget.From(view, row),
            () => _resultView == BooruResultView.Gallery && _gallery.CurrentPostId == id);
    }
}
