using System;
using System.Collections.Generic;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

/// <summary>
/// The rule behind "Expand replies automatically" (<see cref="Config.ReaderExpandReplies"/>), kept apart from the page so it can be tested
/// without a window. It works on the post rows (the view models), never on controls: a recycled container shows whatever its row says, so the
/// rule is applied when a card is realised and is idempotent. A post the reader collapsed by hand stays collapsed while its thread is open.
/// </summary>
internal sealed class ReplyAutoExpandPolicy
{
    private ResourceId? _thread;
    private readonly HashSet<string> _collapsedByReader = new(StringComparer.Ordinal);

    /// <summary>The setting.</summary>
    internal bool Enabled { get; set; }

    /// <summary>The posts of the open thread whose replies the reader collapsed.</summary>
    internal int CollapsedCount => _collapsedByReader.Count;

    /// <summary>The thread that is open (null for none). Showing another thread drops the memory of the previous one.</summary>
    internal void SwitchThread(ResourceId? thread)
    {
        if (_thread == thread) return;
        _collapsedByReader.Clear();
        _thread = thread;
    }

    /// <summary>Whether the setting opens the replies of this post now: not for a stub, a post in the conversation view (it nests replies itself),
    /// a post without known replies or a post the reader collapsed.</summary>
    internal bool ShouldExpand(ArchivePostRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Enabled && _thread != null && !row.IsStub && !row.SuppressReplyToggle && row.HasBacklinks
            && !_collapsedByReader.Contains(row.Id);
    }

    /// <summary>
    /// A card of <paramref name="row"/> is realised (also: its reply count arrived, or the setting was switched on while it is shown). Marks the
    /// row realised and, when the rule applies, opens its replies on the row, which is before the container's first measure. Returns whether the
    /// first page of replies still has to be loaded (one page per realised card, exactly what the header counter loads).
    /// </summary>
    internal bool Realise(ArchivePostRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.RepliesRealised = true;
        if (!ShouldExpand(row)) return false;
        if (!row.RepliesExpanded)
        {
            row.RepliesAutoExpanded = true;
            row.RepliesExpanded = true;
        }
        return row.RepliesExpanded && !row.RepliesLoaded && !row.RepliesLoading && !row.HasRepliesError;
    }

    /// <summary>The reader's own choice at the header counter: collapsing is remembered for the open thread, expanding forgets it.</summary>
    internal void RememberChoice(ArchivePostRow row, bool open)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.RepliesAutoExpanded = false;
        if (_thread == null) return;
        if (open) _collapsedByReader.Remove(row.Id);
        else _collapsedByReader.Add(row.Id);
    }

    /// <summary>
    /// The setting was switched on: the cards that exist now open (the others when they are realised). <paramref name="hasCard"/> says whether a row has a
    /// live card at this moment: a card realised earlier and since recycled away is not one (that flag is never reset), so switching the setting on deep in
    /// a big thread starts the loads of the cards around the reader only, never one per card ever shown. Returns those that still load.
    /// </summary>
    internal List<ArchivePostRow> Enable(IEnumerable<ArchivePostRow> rows, Func<ArchivePostRow, bool> hasCard)
    {
        ArgumentNullException.ThrowIfNull(hasCard);
        Enabled = true;
        List<ArchivePostRow> toLoad = new();
        foreach (ArchivePostRow row in rows)
            if (row.RepliesRealised && hasCard(row) && Realise(row)) toLoad.Add(row);
        return toLoad;
    }

    /// <summary>The setting was switched off: only what it opened closes again; a post the reader opened stays open, and loaded replies stay.</summary>
    internal void Disable(IEnumerable<ArchivePostRow> rows)
    {
        Enabled = false;
        foreach (ArchivePostRow row in rows)
        {
            if (!row.RepliesAutoExpanded) continue;
            row.RepliesAutoExpanded = false;
            row.RepliesExpanded = false;
        }
    }
}
