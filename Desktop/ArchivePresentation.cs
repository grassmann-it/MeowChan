using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;
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
    IReadOnlyList<ArchiveAttachmentRow> Attachments);

internal sealed record ArchiveAssetRow(MediaAssetSnapshot Manifest, MediaAssetReference Asset)
{
    public string Name => Asset.OriginalFileName ?? System.IO.Path.GetFileName(Asset.Path);
    public string Size => (Asset.Length / 1_000_000d).ToString("0.###", Texts.Culture) + " MB";
    public string Version => Asset.Sha256[..Math.Min(12, Asset.Sha256.Length)];
    public string Path => Asset.Path;
}

internal static class ArchivePresentation
{
    private static readonly Regex ActiveElements = new Regex(@"<(?:script|style)\b[^>]*>[\s\S]*?</(?:script|style)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Breaks = new Regex(@"<br\b[^>]*>|</(?:p|div|blockquote)\s*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Tags = new Regex(@"<[^>]*>", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(200));

    internal static string PlainText(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        // HTML remains stored source data. The reader neither executes it nor asks a browser to render it.
        string text = WebUtility.HtmlDecode(Tags.Replace(Breaks.Replace(ActiveElements.Replace(html, string.Empty), "\n"), string.Empty));
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
