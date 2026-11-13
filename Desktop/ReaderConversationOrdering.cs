using System;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

/// <summary>
/// The P4 conversation ordering adapter. It is intentionally small: the quote graph and the pure tree rule own all
/// parsing and ordering work, while the page owns only the session switch and depth invalidation.
/// </summary>
internal sealed class ReaderConversationOrdering : IReaderPostOrdering
{
    private readonly ReaderViewState _state;
    private int _depth;

    internal ReaderConversationOrdering(ReaderViewState state, int depth = ReaderConversationTree.DepthDefault)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _depth = ReaderConversationTree.ClampDepth(depth);
    }

    internal int Depth
    {
        get => _depth;
        set => _depth = ReaderConversationTree.ClampDepth(value);
    }

    public bool IsActive(ResourceId thread) => _state.Conversation;

    // Depth is the complete input for this adapter. The active switch is supplied separately by IsActive.
    public long Revision => _depth;

    public ReaderOrderEntry[] Order(ArchivedThreadView thread, ReaderQuoteGraph graph, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(graph);
        if (!graph.Matches(thread)) throw new ArgumentException("The quote graph belongs to another thread.", nameof(graph));

        string? openingPost = ArchivePresentation.OpeningPostId(thread);
        int openingIndex = openingPost == null ? -1 : thread.FindPostIndex(openingPost);
        ReaderConversationEntry[] ordered = ReaderConversationTree.Order(thread.PostCount, openingIndex, graph.TargetsOf,
            _depth, token);
        ReaderOrderEntry[] result = new ReaderOrderEntry[ordered.Length];
        for (int position = 0; position < ordered.Length; position++)
        {
            ReaderConversationEntry entry = ordered[position];
            result[position] = new ReaderOrderEntry(entry.ThreadIndex, entry.Depth, entry.ParentIndex);
        }
        return result;
    }
}
