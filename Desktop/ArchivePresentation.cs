using System;
using System.Collections.Generic;
using System.IO;
using oChan.Archive;
using oChan.Desktop.Localization;
using oChan.Storage;

namespace oChan.Desktop;

internal sealed record ArchiveThreadRow(ArchiveThreadSummary Summary)
{
    public string Title => Summary.Title;
    public string Identity => Summary.Id.Source + " /" + Summary.Id.Board + "/ · " + Summary.Id.Thread;
    public string Status => Summary.LastCapturedAt == null ? Texts.T("archive.mediaOnly")
        : Texts.T("archive.postsCount", Summary.PostCount);
}

internal sealed record ArchiveAttachmentRow(CapturedAttachment Attachment)
{
    public string Label => Attachment.OriginalFileName ?? Attachment.MediaId ?? Texts.T("archive.deletedAttachment");
}

internal sealed record ArchivePostRow(string Id, string Header, string Subject, string Body,
    IReadOnlyList<ArchiveAttachmentRow> Attachments)
{
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
    public string Name => Asset.OriginalFileName ?? System.IO.Path.GetFileName(Asset.Path);
    public string Size => (Asset.Length / 1_000_000d).ToString("0.###", Texts.Culture) + " MB";
    public string Version => Asset.Sha256[..Math.Min(12, Asset.Sha256.Length)];
    public string Path => Asset.Path;
}

internal static class ArchivePresentation
{
    internal static string PlainText(string? html) => ArchiveText.PlainText(html);
}
