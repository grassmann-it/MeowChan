using System;
using System.Threading.Tasks;
using MeowChan.Desktop;

namespace MeowChan;

public partial class ArchivePage
{
    private readonly ReplyAutoExpandPolicy _replyAutoExpand = new();

    internal bool ReaderExpandRepliesPreference
    {
        get => _replyAutoExpand.Enabled;
        set
        {
            if (_ended || _replyAutoExpand.Enabled == value) return;
            _replyAutoExpand.Enabled = value;
            UpdateFocusedReplies(keepPosition: true);
        }
    }

    private void UpdateFocusedReplies(bool keepPosition = false)
    {
        if (_ended || !_replyAutoExpand.Enabled && _replyAutoExpand.AutomaticallyOpened == null) return;
        Func<Task>? restore = keepPosition && !ReaderJumpOwnsScroll ? CaptureReaderCountLayoutCorrection() : null;
        ArchivePostRow? previous = _replyAutoExpand.AutomaticallyOpened;
        _replyAutoExpand.Focus(ArchivePosts.SelectedItem as ArchivePostRow);
        ArchivePostRow? current = _replyAutoExpand.AutomaticallyOpened;
        if (current != null) LoadExpandedReplies(current);
        if (restore != null && !ReferenceEquals(previous, current))
            OpenActions.Start(restore, "archive.reader.replies.layout");
    }
}
