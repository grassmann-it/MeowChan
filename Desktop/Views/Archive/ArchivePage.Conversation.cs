using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan
{
public partial class ArchivePage
{
    private ReaderConversationOrdering? _conversationOrdering;

    /// <summary>The conversation nesting limit, clamped and applied through the shared reader preferences.</summary>
    internal int ReaderThreadingDepth
    {
        get => _conversationOrdering?.Depth ?? ReaderConversationTree.DepthDefault;
        set => ApplyReaderDisplayPreferences(ReaderThumbnailWidth, ReaderDateStyle, ReaderPageSize, value);
    }

    // ContainerPrepared sets the initial indent, but a realized card survives window/split/sidebar resizing.
    // Update only realized containers on width changes; never rebuild the projection or rebind the rows.
    internal static void AttachConversationLayout(ListBox posts)
    {
        posts.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged) return;
            foreach (Control container in posts.GetRealizedContainers())
                DecorateReaderPost(container, posts.ItemFromContainer(container) as ArchivePostRow, e.NewSize.Width);
        };
    }

    partial void AttachConversationView()
    {
        AttachConversationLayout(ArchivePosts);
        _conversationOrdering = new ReaderConversationOrdering(_readerView);
        RegisterPostOrdering(_conversationOrdering);
        RegisterViewOption("reader.view.conversation", () => _readerView.Conversation,
            () => _readerView.Conversation = !_readerView.Conversation);
    }
}
}
