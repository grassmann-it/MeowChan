using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;
using MeowChan.Storage;
using MeowChan.Persistence;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

internal sealed partial record ArchiveThreadRow(ArchiveThreadSummary Summary, ThreadWorkSnapshot? Work = null)
{
    public string PreviewText { get; init; } = string.Empty;
    public ArchiveAttachmentRow? PreviewAttachment { get; init; }
    public bool HasPreviewAttachment => PreviewAttachment != null;
    public string Title => Summary.Title;
    public string TitleOrId => string.IsNullOrWhiteSpace(Title) ? "#" + Summary.Id.Thread : Title;
    public string Identity => Summary.Id.Source + " /" + Summary.Id.Board + "/ · " + Summary.Id.Thread;
    public string Status => Summary.LastCapturedAt == null ? Texts.T("archive.mediaOnly")
        : Texts.Count("archive.postsCount", Summary.PostCount);
    public int Posts => Summary.PostCount;
    public string SourceStatus => Texts.T(Summary.SourceState switch
    {
        ThreadSourceState.Archived => "archive.sourceArchived",
        ThreadSourceState.NotFound => "archive.sourceMissing",
        _ => "archive.sourceActive"
    });
    public string WorkStatus => Work == null ? Texts.T("gallery.unknown") : WorkStatusFormatter.Format(Work).Status;
    public string CompactSource => Texts.T(Summary.SourceState switch
    {
        ThreadSourceState.Archived => "archive.columns.sourceArchived",
        ThreadSourceState.NotFound => "archive.columns.sourceMissing",
        _ => "archive.columns.sourceActive"
    });
    public string CompactWork => Work == null ? "—" : Texts.T(Work.Intent switch
    {
        WorkIntent.Suppressed => "archive.columns.workStopped",
        WorkIntent.Completed => "archive.columns.workDone",
        WorkIntent.Repairing => "archive.columns.workRepair",
        _ when Work.CapturePending || Work.Media.Any(item => item.Status == MediaWorkStatus.NeedsIntervention) => "archive.columns.workAttention",
        _ => "archive.columns.workWatching"
    });
    public string LastCapture => Summary.LastCapturedAt?.ToLocalTime().ToString("g", Texts.Culture) ?? Texts.T("gallery.unknown");
    public string GalleryLabel => Texts.T("gallery.open");
    public string ReadLabel => Texts.T("gallery.read");
    public string ExportLabel => Texts.T("gallery.export");
}

internal sealed record ArchiveAttachmentRow(CapturedAttachment Attachment)
{
    public Func<bool>? FavoriteAvailable { get; set; }
    public Func<bool>? IsFavorite { get; set; }
    public Func<Task>? ToggleFavoriteAsync { get; set; }
    public Func<string?>? FavoriteError { get; set; }
    public bool LargePreview { get; init; }
    /// <summary>Reader image width; zero keeps the legacy list-preview default.</summary>
    public int ThumbnailWidth { get; init; }
    public Func<CancellationToken, Task<Bitmap?>>? LoadThumbnail { get; init; }
    public Action? OpenPreview { get; init; }
    public MediaAttachmentBinding? Binding { get; set; }
    public string Label => (Attachment.OriginalFileName ?? Attachment.MediaId ?? Texts.T("archive.deletedAttachment"))
        + (MediaProvenanceText.Summary(Binding) is { Length: > 0 } provenance ? "\n" + provenance : string.Empty);
    // The capture records the provider's byte size but no pixel dimensions, so the line shows
    // "name.ext (size)" when the size is known and the bare name otherwise.
    public string FileLine => (Attachment.OriginalFileName ?? Attachment.MediaId ?? string.Empty)
        + (Attachment.ExpectedSize is > 0 and long size ? " (" + FileSize(size) + ")" : string.Empty);

    internal static string FileSize(long bytes) => SizeText.Format(bytes);
}

// One header line in imageboard order: Subject · Name · Date · No. (the number copies ">>No.").
internal sealed record ArchivePostRow(string Id, string Subject, string Body,
    IReadOnlyList<ArchiveAttachmentRow> Attachments) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public string Author { get; init; } = string.Empty;
    public string Posted { get; init; } = string.Empty;
    public bool IsDeleted { get; init; }
    // Posts that follow the last one read, set on the first of them (the reading-mark line above its header).
    public int UnreadCount { get; init; }
    public bool HasUnreadLine => UnreadCount > 0;
    public string UnreadLabel => Texts.Count("reader.unreadLine", UnreadCount);
    // The opening post sits directly on the page (classic imageboard); replies are cards.
    public bool IsOpeningPost { get; init; }
    public bool HasSubject => Subject.Length > 0;
    public bool HasAuthor => Author.Length > 0;
    public bool HasPosted => Posted.Length > 0;
    public string RemovedLabel => Texts.T("archive.deletedPost");
    public string NumberLabel => Texts.T("reader.postNumber", Id);
    public string QuoteReference => ">>" + Id;
    public string CopyNumberLabel => Texts.T("reader.copyPostNumber", Id);
    public bool HasAttachments => Attachments.Count > 0;
    // Hidden media read as a text-only post: the body takes the whole width and no thumbnail is loaded.
    public bool IsTextOnly => !HasAttachments || MediaHidden;

    // Reader view (plan P0). Each row is built for one page of the projection and never changes afterwards.
    /// <summary>Index in the thread read (not the shown position).</summary>
    public int ThreadIndex { get; init; } = -1;
    /// <summary>Indentation level of the conversation view; 0 everywhere else.</summary>
    public int Depth { get; init; }
    /// <summary>The shown parent in the conversation view.</summary>
    public string? ParentId { get; init; }
    public string DepthLabel => Depth > 0 && ParentId != null ? Texts.T("reader.view.depthLabel", ParentId, Depth) : string.Empty;
    /// <summary>The view shows only a placeholder line with the number, the reason and Show.</summary>
    public bool IsStub { get; init; }
    public string StubText { get; init; } = string.Empty;
    public string ShowStubLabel => Texts.T("reader.view.show");
    public bool IsHighlighted { get; init; }
    /// <summary>Shown after an explicit jump or Show although the view hides it.</summary>
    public bool IsRevealed { get; init; }
    public string RevealedLabel => Texts.T("reader.view.revealed");
    public string HideAgainLabel => Texts.T("reader.view.hideAgain");
    /// <summary>"Hide media": the media slot gives way to a short line with the number of attachments.</summary>
    public bool MediaHidden { get; init; }
    public bool ShowsMedia => HasAttachments && !MediaHidden;
    public bool HasMediaHiddenLine => HasAttachments && MediaHidden;
    public string MediaHiddenLabel => Texts.Count("reader.view.mediaHidden", Attachments.Count, Attachments.Count);
    /// <summary>The header date's tooltip: the full date and time.</summary>
    public string PostedFull { get; init; } = string.Empty;
    /// <summary>The conversation view nests the replies itself, so the header's reply counter would show them twice.</summary>
    public bool SuppressReplyToggle { get; init; }
    public IReadOnlyList<ArchiveQuoteRow> Quotes { get; init; } = Array.Empty<ArchiveQuoteRow>();
    // Spoiler, code and external-link ranges of Body (set with Quotes; empty for unformatted posts).
    public IReadOnlyList<ArchiveTextMark> Marks { get; init; } = Array.Empty<ArchiveTextMark>();
    private ReaderBodyDocument? _bodyDocument;
    // Greentext, spoilers, code, links and inline >>No. over the plain Body (built once, after Quotes is set).
    // A reference to a post that was never captured is struck through and followed by a short note.
    public ReaderBodyDocument BodyDocument => _bodyDocument ??= ReaderBodyFormatting.Format(Body, Quotes, Marks,
        " " + Texts.T("reader.quoteNotCaptured"));
    private bool _expanded;
    // Set while the row is built (the page remembers expanded posts for the session) and when the post is folded or unfolded.
    public bool StartExpanded { init => _expanded = value; }
    public Action<bool>? ExpandedChanged { get; init; }
    public bool Expanded
    {
        get => _expanded;
        set { if (_expanded == value) return; _expanded = value; ExpandedChanged?.Invoke(value); Changed(nameof(Expanded)); }
    }
    private bool _spoilersRevealed;
    // One reveal per post and session; the recycled body control shows whatever the post says.
    public bool SpoilersRevealed
    {
        get => _spoilersRevealed;
        set { if (_spoilersRevealed == value) return; _spoilersRevealed = value; Changed(nameof(SpoilersRevealed)); }
    }
    // Only a reference the text does not contain keeps its visible button; the row is otherwise the
    // keyboard/screen-reader path for the inline references and opens on keyboard focus.
    public bool HasUnplacedQuotes => BodyDocument.HasUnplacedQuotes;
    public string QuotesLabel => Texts.T("reader.quotedPosts");
    private int? _backlinkCount;
    private IReadOnlyList<ArchiveQuoteRow> _replies = Array.Empty<ArchiveQuoteRow>();
    private bool _repliesExpanded;
    private bool _repliesLoading;
    private bool _repliesLoaded;
    private int _repliesOffset;
    private string? _repliesError;
    public int? BacklinkCount => _backlinkCount;
    // The header counter is the toggle for the nested reply cards below the text.
    public bool HasBacklinks => _backlinkCount > 0;
    // Shown for a known count above zero, and for a failed load so a local retry stays possible.
    public bool HasReplyToggle => !SuppressReplyToggle && (HasBacklinks || HasRepliesError);
    public string ReplyToggleLabel => Texts.T("reader.repliesToggle");
    public string BacklinkCountLabel => Texts.T("reader.replyCount", _backlinkCount?.ToString(Texts.Culture) ?? "…");
    public string BacklinksLabel => Texts.T("archive.backlinks", Id) + " · " +
        (_backlinkCount?.ToString(Texts.Culture) ?? "…");
    // Only a confirmed, successful zero is non-interactive. Unknown/error states
    // retain the expander so a local retry remains possible.
    public bool HasConfirmedNoReplies => _backlinkCount == 0 && !HasRepliesError && !RepliesLoading;
    public bool CanExpandReplies => !HasConfirmedNoReplies;
    public IReadOnlyList<ArchiveQuoteRow> Replies => _replies;
    public bool RepliesExpanded
    {
        get => _repliesExpanded;
        set { if (_repliesExpanded == value) return; _repliesExpanded = value; Changed(nameof(RepliesExpanded)); }
    }
    public bool RepliesLoading => _repliesLoading;
    public bool RepliesLoaded => _repliesLoaded;
    public int RepliesOffset => _repliesOffset;
    public bool HasMoreReplies => _backlinkCount > _replies.Count;
    public string MoreRepliesLabel => Texts.T("archive.moreReplies");
    public string RepliesLoadingLabel => Texts.T("archive.loading");
    public string RepliesPageLabel => _replies.Count.ToString(Texts.Culture) + " / " +
        (_backlinkCount?.ToString(Texts.Culture) ?? "…");
    public string? RepliesError => _repliesError;
    public bool HasRepliesError => _repliesError != null;

    public void SetBacklinkCount(int count)
    {
        _backlinkCount = count;
        Changed(nameof(BacklinkCount), nameof(BacklinksLabel), nameof(HasMoreReplies), nameof(RepliesPageLabel),
            nameof(HasConfirmedNoReplies), nameof(CanExpandReplies), nameof(HasBacklinks), nameof(HasReplyToggle), nameof(BacklinkCountLabel));
    }
    public void SetReplies(IReadOnlyList<ArchiveQuoteRow> replies, int total, int offset)
    {
        _replies = replies; _repliesOffset = offset; _repliesError = null; _repliesLoaded = true;
        SetBacklinkCount(total);
        Changed(nameof(Replies), nameof(RepliesOffset), nameof(RepliesLoaded), nameof(HasMoreReplies), nameof(RepliesPageLabel),
            nameof(RepliesError), nameof(HasRepliesError), nameof(HasReplyToggle));
    }
    public void SetRepliesLoading(bool loading)
    { _repliesLoading = loading; Changed(nameof(RepliesLoading), nameof(HasConfirmedNoReplies), nameof(CanExpandReplies)); }
    public void SetRepliesError(string? error)
    { _repliesError = error; Changed(nameof(RepliesError), nameof(HasRepliesError), nameof(HasConfirmedNoReplies), nameof(CanExpandReplies), nameof(HasReplyToggle)); }
    private void Changed(params string[] names)
    {
        foreach (string name in names) PropertyChanged?.Invoke(this,
            new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
internal sealed record ArchiveQuoteRow(ArchiveQuote Quote, bool Backlink = false, ResourceId? ReaderThread = null)
{
    // The quoted post is in the open thread's identity but was never captured (checked once when the page is built).
    public bool IsDead { get; init; }
    // A reply of the open thread shown as a nested card: "No. 101 · name · date" and the first lines of its text.
    public string? ReplyHeader { get; init; }
    public string ReplyExcerpt { get; init; } = string.Empty;
    public bool HasReplyExcerpt => ReplyExcerpt.Length > 0;
    public string ReplyCardHeader => ReplyHeader ?? Label;
    // 4chan and 8kun number the opening post with the thread number, so the target is exactly the opening post of its thread;
    // other sources get no such marker. A backlink is a reply to the open post, not a reference of it, and carries no marker.
    public bool TargetsOpeningPost => !Backlink && Quote.Target is { Thread.Source: "4chan" or "8kun" } target && target.PostId == target.Thread.Thread;
    public bool IsCrossThread => !Backlink && Quote.Target is { } target && target.Thread != Quote.Origin.Thread;
    // "(OP)" or "(other thread)" behind a reference; another thread's opening post is marked as the other thread only.
    // A source that already writes such a marker into the label of its anchor keeps its own.
    public string Marker => Choose("reader.quote.opMarker", "reader.quote.crossMarker");
    public string MarkerAccessible => Choose("reader.quote.opAccessible", "reader.quote.crossAccessible");
    private string Choose(string opKey, string crossKey) => IsCrossThread
        ? Quote.Label.Contains('\u2192') || Quote.Label.Contains("(cross", StringComparison.OrdinalIgnoreCase) ? string.Empty : Texts.T(crossKey)
        : TargetsOpeningPost && !Quote.Label.Contains("(OP)", StringComparison.OrdinalIgnoreCase) ? Texts.T(opKey) : string.Empty;
    public string AccessibleLabel => Backlink ? Quote.Origin.Thread.Source + " /" + Quote.Origin.Thread.Board + "/ "
        + Quote.Origin.Thread.Thread + " · #" + Quote.Origin.PostId
        : MarkerAccessible is { Length: > 0 } accessible ? Quote.Label + ", " + accessible : Quote.Label;
    public string Label => Backlink ? ReaderThread == Quote.Origin.Thread ? "#" + Quote.Origin.PostId : AccessibleLabel
        : Marker is { Length: > 0 } marker ? Quote.Label + " " + marker : Quote.Label;
}

internal sealed record ArchiveAssetRow(MediaAssetSnapshot Manifest, MediaAssetReference Asset) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private int? _rating;
    private bool _ratingUnavailable;
    public int? Rating { get => _rating; set { _rating = value; PropertyChanged?.Invoke(this, new(nameof(RatingLabel))); } }
    public bool RatingUnavailable { get => _ratingUnavailable; set { _ratingUnavailable = value; PropertyChanged?.Invoke(this, new(nameof(RatingLabel))); } }
    public string RatingLabel => RatingUnavailable ? MediaRatingText.LocalizedUnavailable : MediaRatingText.LocalizedValue(Rating);
    public Func<bool>? FavoriteAvailable { get; set; }
    public Func<bool>? IsFavorite { get; set; }
    public Func<Task>? ToggleFavoriteAsync { get; set; }
    public Func<string?>? FavoriteError { get; set; }
    public string Name => Asset.OriginalFileName ?? System.IO.Path.GetFileName(Asset.Path);
    public string Size => SizeText.Format(Asset.Length);
    public string TileSummary => Size + " · " + Texts.T(Asset.IsCurrent ? "gallery.latestVersions" : "gallery.earlierVersion");
    public string Version => Asset.Sha256[..Math.Min(12, Asset.Sha256.Length)];
    public string Path => Asset.Path;
}

internal static class ArchivePresentation
{
    internal static string PlainText(string? html) => ArchiveText.PlainText(html);

    /// <summary>
    /// The excerpt of a post that quote previews and reply cards show: a header "&gt;&gt;No. · author · date" and the first
    /// lines of the text (the first attachment name when the post has no text). Only the limits differ between the two.
    /// </summary>
    internal static (string Header, string Body) PostExcerpt(CapturedPost post, int maxLines, int maxCharacters,
        ReaderDateStyle dateStyle = ReaderDateStyle.System, DateTimeOffset? openingPostAt = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(post);
        string header = string.Join(" \u00B7 ", new[]
        {
            Texts.T("reader.postNumber", post.PostId), post.Author ?? string.Empty,
            ReaderDateText.Posted(post.PostedAt, dateStyle, openingPostAt, now)
        }.Where(part => part.Length > 0));
        string body = ArchivePage.ShortenText(PlainText(post.Html), maxLines, maxCharacters);
        if (body.Length == 0 && post.Attachments?.FirstOrDefault(attachment => !string.IsNullOrEmpty(attachment.OriginalFileName)) is { } file)
            body = file.OriginalFileName!;
        return (header, body);
    }

    // 4chan/8kun number the opening post with the thread number, so a thread whose OP was not
    // captured has none. Other sources: the first captured post opens the thread.
    internal static string? OpeningPostId(ArchivedThreadView thread) => OpeningPostId(thread.Summary.Id,
        id => thread.FindPostIndex(id) >= 0, thread.PostCount == 0 ? null : thread.GetPosts(0, 1)[0].PostId);

    internal static string? OpeningPostId(ResourceId thread, Func<string, bool> captured, string? firstPost) =>
        thread.Thread is { } id && captured(id) ? id : thread.Source is "4chan" or "8kun" ? null : firstPost;
}
