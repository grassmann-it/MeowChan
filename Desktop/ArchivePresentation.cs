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

internal sealed record ArchiveThreadRow(ArchiveThreadSummary Summary, ThreadWorkSnapshot? Work = null)
{
    public string PreviewText { get; init; } = string.Empty;
    public ArchiveAttachmentRow? PreviewAttachment { get; init; }
    public bool HasPreviewAttachment => PreviewAttachment != null;
    public string Title => Summary.Title;
    public string TitleOrId => string.IsNullOrWhiteSpace(Title) ? "#" + Summary.Id.Thread : Title;
    public string Identity => Summary.Id.Source + " /" + Summary.Id.Board + "/ · " + Summary.Id.Thread;
    public string Status => Summary.LastCapturedAt == null ? Texts.T("archive.mediaOnly")
        : Texts.T("archive.postsCount", Summary.PostCount);
    public int Posts => Summary.PostCount;
    public string MediaStatus => Work == null ? Texts.T("gallery.unknown") : WorkStatusFormatter.Format(Work).Progress;
    public string CompactMedia => Work == null ? "—" : Work.SavedMedia.ToString(Texts.Culture) + "/" + Work.Media.Count.ToString(Texts.Culture);
    public int SavedMediaCount => Work?.SavedMedia ?? -1;
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
    public Func<CancellationToken, Task<Bitmap?>>? LoadThumbnail { get; init; }
    public Action? OpenPreview { get; init; }
    public string Label => Attachment.OriginalFileName ?? Attachment.MediaId ?? Texts.T("archive.deletedAttachment");
}

internal sealed record ArchivePostRow(string Id, string Header, string Subject, string Body,
    IReadOnlyList<ArchiveAttachmentRow> Attachments) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public bool HasAttachments => Attachments.Count > 0;
    public bool IsTextOnly => !HasAttachments;
    public IReadOnlyList<ArchiveQuoteRow> Quotes { get; init; } = Array.Empty<ArchiveQuoteRow>();
    private int? _backlinkCount;
    private IReadOnlyList<ArchiveQuoteRow> _replies = Array.Empty<ArchiveQuoteRow>();
    private bool _repliesExpanded;
    private bool _repliesLoading;
    private bool _repliesLoaded;
    private int _repliesOffset;
    private string? _repliesError;
    public int? BacklinkCount => _backlinkCount;
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
            nameof(HasConfirmedNoReplies), nameof(CanExpandReplies));
    }
    public void SetReplies(IReadOnlyList<ArchiveQuoteRow> replies, int total, int offset)
    {
        _replies = replies; _repliesOffset = offset; _repliesError = null; _repliesLoaded = true;
        SetBacklinkCount(total);
        Changed(nameof(Replies), nameof(RepliesOffset), nameof(RepliesLoaded), nameof(HasMoreReplies), nameof(RepliesPageLabel),
            nameof(RepliesError), nameof(HasRepliesError));
    }
    public void SetRepliesLoading(bool loading)
    { _repliesLoading = loading; Changed(nameof(RepliesLoading), nameof(HasConfirmedNoReplies), nameof(CanExpandReplies)); }
    public void SetRepliesError(string? error)
    { _repliesError = error; Changed(nameof(RepliesError), nameof(HasRepliesError), nameof(HasConfirmedNoReplies), nameof(CanExpandReplies)); }
    private void Changed(params string[] names)
    {
        foreach (string name in names) PropertyChanged?.Invoke(this,
            new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
internal sealed record ArchiveQuoteRow(ArchiveQuote Quote, bool Backlink = false, ResourceId? ReaderThread = null)
{
    public string AccessibleLabel => Backlink ? Quote.Origin.Thread.Source + " /" + Quote.Origin.Thread.Board + "/ "
        + Quote.Origin.Thread.Thread + " · #" + Quote.Origin.PostId : Quote.Label;
    public string Label => Backlink && ReaderThread == Quote.Origin.Thread ? "#" + Quote.Origin.PostId : AccessibleLabel;
}

internal sealed record ArchiveAssetRow(MediaAssetSnapshot Manifest, MediaAssetReference Asset) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private int? _rating;
    private bool _ratingUnavailable;
    public int? Rating { get => _rating; set { _rating = value; PropertyChanged?.Invoke(this, new(nameof(RatingLabel))); } }
    public bool RatingUnavailable { get => _ratingUnavailable; set { _ratingUnavailable = value; PropertyChanged?.Invoke(this, new(nameof(RatingLabel))); } }
    public string RatingLabel => RatingUnavailable ? MediaRatingText.Get("Unavailable", "Nicht verfügbar") : MediaRatingText.Value(Rating);
    public Func<bool>? FavoriteAvailable { get; set; }
    public Func<bool>? IsFavorite { get; set; }
    public Func<Task>? ToggleFavoriteAsync { get; set; }
    public Func<string?>? FavoriteError { get; set; }
    public string Name => Asset.OriginalFileName ?? System.IO.Path.GetFileName(Asset.Path);
    public string Size => (Asset.Length / 1_000_000d).ToString("0.###", Texts.Culture) + " MB";
    public string TileSummary => Size + " · " + Texts.T(Asset.IsCurrent ? "gallery.latestVersions" : "gallery.earlierVersion");
    public string Version => Asset.Sha256[..Math.Min(12, Asset.Sha256.Length)];
    public string Path => Asset.Path;
}

internal static class ArchivePresentation
{
    internal static string PlainText(string? html) => ArchiveText.PlainText(html);
}
