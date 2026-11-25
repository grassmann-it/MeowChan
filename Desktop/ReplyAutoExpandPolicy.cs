using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

/// <summary>Tracks the reply expansion owned by the current reader selection and the reader's choices for the open thread.</summary>
internal sealed class ReplyAutoExpandPolicy
{
    private ResourceId? _thread;
    private readonly Dictionary<string, bool> _manualChoices = new(StringComparer.Ordinal);

    internal bool Enabled { get; set; }
    internal ArchivePostRow? AutomaticallyOpened { get; private set; }

    internal void SwitchThread(ResourceId? thread)
    {
        if (_thread == thread) return;
        Focus(null);
        _manualChoices.Clear();
        _thread = thread;
    }

    internal void Focus(ArchivePostRow? row)
    {
        ArchivePostRow? automatic = Enabled && _thread != null
            && row is { IsStub: false, SuppressReplyToggle: false, HasBacklinks: true }
            && !_manualChoices.ContainsKey(row.Id)
            && (!row.RepliesExpanded || ReferenceEquals(row, AutomaticallyOpened))
            ? row : null;
        if (ReferenceEquals(AutomaticallyOpened, automatic)) return;
        ArchivePostRow? previous = AutomaticallyOpened;
        AutomaticallyOpened = automatic;
        if (previous != null) previous.RepliesExpanded = false;
        if (automatic != null) automatic.RepliesExpanded = true;
    }

    internal void RememberChoice(ArchivePostRow row, bool open)
    {
        if (ReferenceEquals(AutomaticallyOpened, row)) AutomaticallyOpened = null;
        if (_thread != null) _manualChoices[row.Id] = open;
        row.RepliesExpanded = open;
    }

    internal KeyValuePair<string, bool>[] CaptureChoices() => _manualChoices.ToArray();

    internal void RestoreChoices(IEnumerable<KeyValuePair<string, bool>> choices)
    {
        _manualChoices.Clear();
        foreach ((string id, bool open) in choices) _manualChoices[id] = open;
    }

    internal void RestoreChoice(ArchivePostRow row)
    {
        if (_manualChoices.TryGetValue(row.Id, out bool open)) row.RepliesExpanded = open;
    }
}
