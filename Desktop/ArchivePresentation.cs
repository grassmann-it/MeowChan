using System;
using System.Collections.Generic;
using System.IO;
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
    public string Identity => Summary.Id.Source + " /" + Summary.Id.Board + "/ · " + Summary.Id.Thread;
    public string Status => Summary.LastCapturedAt == null ? Texts.T("archive.mediaOnly")
        : Texts.T("archive.postsCount", Summary.PostCount);
    public int Posts => Summary.PostCount;
    public string MediaStatus => Work == null ? Texts.T("gallery.unknown") : WorkStatusFormatter.Format(Work).Progress;
    public string SourceStatus => Texts.T(Summary.SourceState switch
    {
        ThreadSourceState.Archived => "archive.sourceArchived",
        ThreadSourceState.NotFound => "archive.sourceMissing",
        _ => "archive.sourceActive"
    });
    public string WorkStatus => Work == null ? Texts.T("gallery.unknown") : WorkStatusFormatter.Format(Work).Status;
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
    public bool LargePreview { get; init; }
    public Func<CancellationToken, Task<Bitmap?>>? LoadThumbnail { get; init; }
    public Action? OpenPreview { get; init; }
    public string Label => Attachment.OriginalFileName ?? Attachment.MediaId ?? Texts.T("archive.deletedAttachment");
}

internal sealed record ArchivePostRow(string Id, string Header, string Subject, string Body,
    IReadOnlyList<ArchiveAttachmentRow> Attachments)
{
    public bool HasAttachments => Attachments.Count > 0;
    public IReadOnlyList<ArchiveQuoteRow> Quotes { get; init; } = Array.Empty<ArchiveQuoteRow>();
    public string BacklinksLabel => Texts.T("archive.backlinks", Id);
}
internal sealed record ArchiveQuoteRow(ArchiveQuote Quote, bool Backlink = false)
{
    public string Label => Backlink ? Quote.Origin.Thread.Source + " /" + Quote.Origin.Thread.Board + "/ "
        + Quote.Origin.Thread.Thread + " · #" + Quote.Origin.PostId : Quote.Label;
}

internal sealed record ArchiveAssetRow(MediaAssetSnapshot Manifest, MediaAssetReference Asset)
{
    public Func<bool>? FavoriteAvailable { get; set; }
    public Func<bool>? IsFavorite { get; set; }
    public Func<Task>? ToggleFavoriteAsync { get; set; }
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
