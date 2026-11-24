using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan;

// "Expand replies automatically" (Config.ReaderExpandReplies, default off): every post opens its nested reply cards when its card is realised.
// The state lives on the row (the view model), so a recycled container shows whatever the row says; the rule (ReplyAutoExpandPolicy) runs from
// ContainerPrepared, before the container's first measure, so the card never grows after it is shown. Replies still load lazily, one first page
// per realised card, exactly as the header counter does. A post the reader collapsed by hand stays collapsed while its thread is open.
public partial class ArchivePage
{
    private readonly ReplyAutoExpandPolicy _replyAutoExpand = new();

    /// <summary>The setting; a change applies to the open thread at once (the cards that exist now open or close, the others when they appear).</summary>
    internal bool ReaderExpandRepliesPreference
    {
        get => _replyAutoExpand.Enabled;
        set
        {
            if (_replyAutoExpand.Enabled == value) return;
            if (_ended || ArchivePosts.ItemsSource is not ReaderPostRows rows) { _replyAutoExpand.Enabled = value; return; }
            // The selected card keeps its place while the cards above and below it change height.
            Func<Task>? keepSelected = CaptureReaderCountLayoutCorrection();
            _replyAutoExpand.SwitchThread(_thread?.Summary.Id);
            if (value) foreach (ArchivePostRow row in _replyAutoExpand.Enable(rows.Materialized, HasLiveCard)) _ = LoadInlineRepliesAsync(row);
            else _replyAutoExpand.Disable(rows.Materialized);
            if (keepSelected != null) _ = keepSelected();
        }
    }

    /// <summary>The rule for one card: opens its replies on the row (before the first measure) and starts the first page when it is not loaded.</summary>
    internal void ApplyAutomaticReplies(ArchivePostRow row)
    {
        _replyAutoExpand.SwitchThread(_thread?.Summary.Id);
        if ((_replyAutoExpand.Realise(row) || row.RepliesExpanded && !row.RepliesLoaded && !row.HasRepliesError) && !_ended)
            _ = LoadInlineRepliesAsync(row);
    }

    // Reply counts arrive after the first cards are realised; only cards already shown open their replies then (the rest do when realised).
    private void ApplyAutomaticRepliesToRealised(ArchivePostRow[] rows)
    {
        if (!_replyAutoExpand.Enabled) return;
        foreach (ArchivePostRow row in rows)
            if (row.RepliesRealised && HasLiveCard(row)) ApplyAutomaticReplies(row);
    }

    // The row's card exists in the post list now (not one that was shown earlier and recycled away).
    private bool HasLiveCard(ArchivePostRow row) => ArchivePosts.ContainerFromItem(row) != null;

    // A click on the header counter is the reader's own choice.
    private void RememberReplyChoice(ArchivePostRow row, bool open)
    {
        _replyAutoExpand.SwitchThread(_thread?.Summary.Id);
        _replyAutoExpand.RememberChoice(row, open);
    }
}
