using System;
using System.Threading;
using MeowChan.Archive;

namespace MeowChan.Desktop;

/// <summary>
/// The reader's "View" switches. They apply to every thread and last for the session (nothing is stored); each starts
/// off, which is today's view. Changed fires once per actual change.
/// </summary>
internal sealed class ReaderViewState
{
    private bool _hideMedia, _mediaOnly, _conversation;

    /// <summary>Media slots show a short "Media hidden (n)" line instead; thumbnails are not loaded.</summary>
    public bool HideMedia { get => _hideMedia; set => Set(ref _hideMedia, value); }
    /// <summary>Posts without attachments are hidden; the opening post always stays.</summary>
    public bool MediaOnly { get => _mediaOnly; set => Set(ref _mediaOnly, value); }
    /// <summary>Owned by the conversation view (P4); P0 only stores it.</summary>
    public bool Conversation { get => _conversation; set => Set(ref _conversation, value); }

    /// <summary>No switch of the session's view is on (nothing hidden, no filter, no conversation).</summary>
    public bool IsDefault => !_hideMedia && !_mediaOnly && !_conversation;

    public event EventHandler? Changed;

    private void Set(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>"Only posts with media": a post without attachments is hidden; the opening post always stays visible.</summary>
internal sealed class ReaderMediaOnlyClassifier : IReaderPostClassifier
{
    private readonly ReaderViewState _state;
    internal ReaderMediaOnlyClassifier(ReaderViewState state) => _state = state;

    public bool IsActive(ResourceId thread) => _state.MediaOnly;
    public bool UsesQuoteGraph => false;
    // The only input is the switch, and IsActive already reflects it.
    public long Revision => 1;

    public void Classify(ArchivedThreadView thread, ReaderQuoteGraph? graph, ReaderPostVerdict[] verdicts, CancellationToken token)
    {
        string? openingPost = ArchivePresentation.OpeningPostId(thread);
        var posts = thread.Posts;
        for (int index = 0; index < posts.Count; index++)
        {
            if ((index & 255) == 0) token.ThrowIfCancellationRequested();
            CapturedPost post = posts[index];
            if (post.Attachments is { Count: > 0 } || post.PostId == openingPost) continue;
            verdicts[index] = new ReaderPostVerdict(ReaderPostVisibility.Hidden, ReaderPostMarks.None, null);
        }
    }
}
