using MeowChan.Desktop;

namespace MeowChan
{
public partial class ArchivePage
{
    private ReaderConversationOrdering? _conversationOrdering;

    /// <summary>
    /// The conversation nesting limit for this session. It deliberately is not persisted here; P1 owns the eventual
    /// Config/Settings mapping. Values are always clamped before they reach the ordering worker.
    /// </summary>
    internal int ReaderThreadingDepth
    {
        get => _conversationOrdering?.Depth ?? ReaderConversationTree.DepthDefault;
        set
        {
            int depth = ReaderConversationTree.ClampDepth(value);
            if (_conversationOrdering == null || _conversationOrdering.Depth == depth) return;
            _conversationOrdering.Depth = depth;
            if (_readerView.Conversation) RequestProjectionRebuild();
        }
    }

    partial void AttachConversationView()
    {
        _conversationOrdering = new ReaderConversationOrdering(_readerView);
        RegisterPostOrdering(_conversationOrdering);
        RegisterViewOption("reader.view.conversation", () => _readerView.Conversation,
            () => _readerView.Conversation = !_readerView.Conversation);
    }
}
}
