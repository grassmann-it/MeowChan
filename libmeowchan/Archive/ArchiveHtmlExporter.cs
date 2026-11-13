using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.Archive;

public sealed record ArchiveHtmlExportOptions(bool IncludeMedia = true)
{
    /// <summary>Language of generated document text (en, de, es, fr or ru, including regional tags).
    /// Unsupported or missing languages use English. Does not translate captured source content.</summary>
    public string Language { get; init; } = "en";
}
public enum ArchiveExportOmissionReason { MediaNotIncluded, ExcludedByRule, Unavailable, DifferentVersion, VerificationFailed, NoMediaIdentity }
public sealed record ArchiveExportOmission(string? PostId, string? MediaId, ArchiveExportOmissionReason Reason);
public enum ArchiveHtmlExportPhase { ReadingMedia, WritingPosts }
public sealed record ArchiveHtmlExportProgress(ArchiveHtmlExportPhase Phase, int PostsWritten, int FilesCopied);
public sealed record ArchiveHtmlExportResult(string Directory, string IndexPath, int PostCount, int FileCount,
    IReadOnlyList<ArchiveExportOmission> Omissions)
{
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public ArchiveExportMetadata? Metadata { get; init; }
    public string? BackupDirectory { get; init; }
}
internal sealed record ArchiveExportSnapshot(ArchivedThreadView View, IReadOnlyList<CandidateDecision> Decisions);

/// <summary>Exports a consistent local catalog read to a new independent offline thread folder.
/// Never fetches, repairs media, reevaluates rules, or overwrites an existing export.</summary>
public static class ArchiveHtmlExporter
{
    public static async Task<ArchiveHtmlExportResult> ExportAsync(ArchiveReader reader, ResourceId thread,
        string destinationDirectory, ArchiveHtmlExportOptions? options = null,
        IProgress<ArchiveHtmlExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        string? parent = Path.GetDirectoryName(destination);
        if (parent == null || !Directory.Exists(parent)) throw new DirectoryNotFoundException("The export parent directory must exist.");
        using ArchiveExportTargetLease targetLease = ArchiveExportTargetLease.Acquire(destination);
        ArchiveExportReplacement.Recover(destination);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("The export destination already exists; choose a new folder.");
        ArchiveExportSnapshot snapshot = await reader.ReadExportAsync(thread, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The thread is not in the local archive.");
        return await Task.Run(() => WriteAsync(snapshot, destination, options ?? new ArchiveHtmlExportOptions(), progress, cancellationToken, null),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Explicitly replaces a previously inspected export. Expected identity must match at commit.</summary>
    public static async Task<ArchiveHtmlExportResult> UpdateAsync(ArchiveReader reader, ResourceId thread,
        string destinationDirectory, Guid expectedExportId, int expectedGeneration,
        ArchiveHtmlExportOptions? options = null, bool confirmPreserveBackup = false,
        IProgress<ArchiveHtmlExportProgress>? progress = null, CancellationToken cancellationToken = default,
        string? expectedObservedFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (expectedExportId == Guid.Empty || expectedGeneration < 1) throw new ArgumentException("An expected export identity is required.");
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        using ArchiveExportTargetLease targetLease = ArchiveExportTargetLease.Acquire(destination);
        ArchiveExportReplacement.Recover(destination);
        ArchiveExportInspection existing = await ArchiveExportMetadataFile.InspectAsync(destination, thread, cancellationToken).ConfigureAwait(false);
        if (existing.Metadata.ExportId != expectedExportId || existing.Metadata.Generation != expectedGeneration)
            throw new IOException("The selected export changed; inspect it again before updating.");
        if (expectedObservedFingerprint != null && existing.ObservedFingerprint != expectedObservedFingerprint)
            throw new IOException("The selected export contents changed; inspect them again before updating.");
        ArchiveExportSnapshot snapshot = await reader.ReadExportAsync(thread, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The thread is not in the local archive.");
        string profile = Path.GetDirectoryName(reader.DatabasePath)!;
        if (IsSameOrAncestor(destination, profile) || IsSameOrAncestor(profile, destination) || snapshot.View.MediaDirectories.Where(media =>
            !Path.GetFullPath(media).Equals(Path.GetFullPath(profile), OperatingSystem.IsWindows() ?
                StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Any(media =>
            IsSameOrAncestor(destination, media) || IsSameOrAncestor(media, destination)))
            throw new InvalidDataException("An export cannot replace its profile or source media directory.");
        return await WriteAsync(snapshot, destination, options ?? new ArchiveHtmlExportOptions(), progress,
            cancellationToken, new ArchiveExportUpdate(existing, confirmPreserveBackup)).ConfigureAwait(false);
    }

    internal static bool IsSameOrAncestor(string possibleAncestor, string protectedPath)
    {
        string first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(possibleAncestor));
        string second = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedPath));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return first.Equals(second, comparison) || second.StartsWith(first + Path.DirectorySeparatorChar, comparison);
    }

    public static Task<ArchiveExportInspection> InspectAsync(string destinationDirectory, ResourceId thread,
        CancellationToken cancellationToken = default) => ArchiveExportMetadataFile.InspectAsync(destinationDirectory, thread, cancellationToken);

    private static async Task<ArchiveHtmlExportResult> WriteAsync(ArchiveExportSnapshot snapshot, string destination,
        ArchiveHtmlExportOptions options, IProgress<ArchiveHtmlExportProgress>? progress, CancellationToken token,
        ArchiveExportUpdate? update)
    {
        string stage = Path.Combine(Path.GetDirectoryName(destination)!, ".meowchan-export-" + Guid.NewGuid().ToString("N"));
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(stage);
        try
        {
            ArchiveHtmlExportResult rendered = await RenderAsync(snapshot, stage, "index.html", "style.css", "media/",
                new Dictionary<string, string>(StringComparer.Ordinal), null, false, options, progress, token).ConfigureAwait(false);
            ArchivedThreadView view = snapshot.View;
            List<string> warnings = rendered.Warnings.ToList();
            await File.WriteAllTextAsync(Path.Combine(stage, "style.css"), Css, new UTF8Encoding(false), token).ConfigureAwait(false);
            ArchiveExportMetadata metadata = await ArchiveExportMetadataFile.CreateAsync(stage, view, options,
                update?.Existing.Metadata.ExportId ?? Guid.NewGuid(),
                (update?.Existing.Metadata.Generation ?? 0) + 1, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string? backup = null;
            string? publicationWarning = null;
            if (update == null)
                Directory.Move(stage, destination);
            else
            {
                ArchiveExportPublication publication = await ArchiveExportReplacement.PublishAsync(stage, destination, update, metadata, token).ConfigureAwait(false);
                backup = publication.BackupDirectory;
                publicationWarning = publication.Warning;
            }
            if (publicationWarning != null) warnings.Add(publicationWarning);
            return new ArchiveHtmlExportResult(destination, Path.Combine(destination, "index.html"), rendered.PostCount, rendered.FileCount,
                rendered.Omissions) { Warnings = Array.AsReadOnly(warnings.Distinct().ToArray()),
                    Metadata = metadata, BackupDirectory = backup };
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }
    internal static async Task<ArchiveHtmlExportResult> RenderAsync(ArchiveExportSnapshot snapshot, string stage,
        string pagePath, string cssReference, string mediaPrefix, Dictionary<string, string> copied,
        Func<ArchiveQuote, string?>? quoteResolver, bool verifyEachOrigin, ArchiveHtmlExportOptions options,
        IProgress<ArchiveHtmlExportProgress>? progress, CancellationToken token)
    {
        ArchivedThreadView view = snapshot.View;
        HtmlExportLocalization text = new(options.Language);
        Dictionary<(string, string), CandidateDecision> decisions = snapshot.Decisions.ToDictionary(
            decision => (decision.Metadata.Key.MediaId, decision.Metadata.Key.Variant));
        List<(MediaAssetSnapshot Manifest, MediaAssetReference Asset)> assets = new();
        List<string> warnings = new();
        if (options.IncludeMedia)
        {
            progress?.Report(new ArchiveHtmlExportProgress(ArchiveHtmlExportPhase.ReadingMedia, 0, 0));
            foreach (string directory in view.MediaDirectories)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory, token).ConfigureAwait(false);
                    if (manifest.Warning != null) warnings.Add(manifest.Warning);
                    if (manifest.Status == MediaManifestStatus.Missing) warnings.Add("A saved media manifest is unavailable.");
                    for (int offset = 0; offset < manifest.Count; offset += 500)
                        assets.AddRange(manifest.GetPage(offset, 500).Select(asset => (manifest, asset)));
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                { warnings.Add("A saved media directory could not be read."); }
            }
        }
        List<ArchiveExportOmission> omissions = new();
        HashSet<string> capturedIds = view.Posts.Select(post => post.PostId).ToHashSet(StringComparer.Ordinal);
        int files = 0;
        int posts = 0;
        Directory.CreateDirectory(Path.Combine(stage, "media"));
        await using FileStream output = new(Path.Combine(stage, pagePath), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        await using StreamWriter html = new(output, new UTF8Encoding(false));
        await html.WriteLineAsync("<!doctype html><html lang=\"" + text.Language + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"" + ContentSecurityPolicy + "\"><title>" + Encode(view.Summary.Title) + "</title><link rel=\"stylesheet\" href=\"" + Encode(cssReference) + "\"></head><body>" + HtmlExportChrome.ThemeInputs(text) + "<div class=\"page\"><header class=\"top\"><div class=\"bar\"><div class=\"heading\"><h1>" + Encode(view.Summary.Title) + "</h1><p class=\"source\">" + Encode(view.Summary.Id.Source + " /" + view.Summary.Id.Board + "/ " + view.Summary.Id.Thread) + "</p></div><div class=\"controls\">" + HtmlExportChrome.ThemeSwitch(text) + "</div></div><p class=\"meta\">"
            + text.Html(HtmlExportText.LastCapture, view.Summary.LastCapturedAt?.ToString("u", CultureInfo.InvariantCulture) ?? text.Get(HtmlExportText.NoPosts))
            + "</p><p class=\"meta\">" + text.Html(HtmlExportText.Retention) + " "
            + text.Html(quoteResolver == null ? HtmlExportText.ExternalThreads : HtmlExportText.UncapturedReferences) + "</p></header><main>");
        if (quoteResolver != null) await html.WriteLineAsync("<nav><a href=\"../index.html\">" + text.Html(HtmlExportText.CollectionStart) + "</a></nav>");
        // Like the reader: the opening post is the thread number on 4chan/8kun, else the first captured post.
        string? openingPost = view.Summary.Id.Thread is { } threadNumber && view.FindPostIndex(threadNumber) >= 0 ? threadNumber
            : view.Summary.Id.Source is "4chan" or "8kun" || view.PostCount == 0 ? null : view.Posts[0].PostId;
        foreach (CapturedPost post in view.Posts)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<ArchiveQuote> quotes = ArchiveQuoteParser.Parse(view.Summary.Id, view.Summary.Url, post);
            // Same-thread references to posts that were never captured are struck through in the text, like in the reader.
            HashSet<string> dead = quotes.Where(quote => quote.Target is { } target && target.Thread == view.Summary.Id && !capturedIds.Contains(target.PostId))
                .Select(quote => quote.Label).ToHashSet(StringComparer.Ordinal);
            (string bodyText, IReadOnlyList<ArchiveTextMark> bodyMarks) = ArchiveText.PlainTextWithMarks(post.Html);
            // One header line like the reader card: Subject · Name · Date · No. (the number is the post's own anchor).
            await html.WriteLineAsync("<article id=\"" + PostAnchor(post.PostId) + "\"" + (post.PostId == openingPost ? " class=\"op\"" : "") + "><h2 class=\"post-head\">"
                + Part("subject", post.Subject) + Part("author", post.Author) + Part("date", post.PostedAt?.ToString("u", CultureInfo.InvariantCulture))
                + "<a class=\"no\" href=\"#" + PostAnchor(post.PostId) + "\">" + text.Html(HtmlExportText.PostNumber, post.PostId) + "</a>"
                + (post.IsDeleted ? " <span class=\"removed\">" + text.Html(HtmlExportText.Removed) + "</span>" : "") + "</h2>");
            // File line and thumbnail first, floated beside the text as on the imageboards; several files form a row above it.
            IReadOnlyList<CapturedAttachment> attachments = post.Attachments ?? Array.Empty<CapturedAttachment>();
            if (attachments.Count != 0)
            {
                await html.WriteLineAsync(attachments.Count == 1 ? "<div class=\"files\">" : "<div class=\"files multi\">");
                foreach (CapturedAttachment attachment in attachments)
                    await AttachmentAsync(post.PostId, attachment).ConfigureAwait(false);
                await html.WriteLineAsync("</div>");
            }
            await html.WriteLineAsync("<div class=\"body\">" + BodyHtml(bodyText, bodyMarks, dead, "(" + text.Get(HtmlExportText.PostNotCaptured) + ")") + "</div>");
            foreach (ArchiveQuote quote in quotes)
            {
                string? quotePath = quoteResolver == null
                    ? quote.Target?.Thread == view.Summary.Id && capturedIds.Contains(quote.Target.PostId) ? "#" + PostAnchor(quote.Target.PostId) : null
                    : quoteResolver(quote);
                if (quotePath != null) await html.WriteLineAsync("<a class=\"quote\" href=\"" + Encode(quotePath) + "\">" + Encode(quote.Label) + "</a>");
                else await html.WriteLineAsync("<span class=\"omission\">" + Encode(quote.Label) + " — "
                    + text.Html(quote.IsExternal ? HtmlExportText.ExternalReference : quote.Target?.Thread == view.Summary.Id ? HtmlExportText.PostNotCaptured : quote.Target != null ? HtmlExportText.OtherThread : HtmlExportText.UnresolvedReference) + "</span>");
            }
            await html.WriteLineAsync("</article>");
            posts++;
            if (posts == 1 || posts % 25 == 0 || posts == view.PostCount)
                progress?.Report(new ArchiveHtmlExportProgress(ArchiveHtmlExportPhase.WritingPosts, posts, files));
        }
        // Old/media-only threads remain useful as a local gallery, without inventing posts.
        if (view.PostCount == 0)
        {
            await html.WriteLineAsync("<article><h2>" + text.Html(HtmlExportText.SavedMedia) + "</h2><p>" + text.Html(HtmlExportText.GalleryDescription) + "</p>");
            if (!options.IncludeMedia) await html.WriteLineAsync("<p class=\"omission\">" + text.Html(HtmlExportText.GalleryOmitted) + "</p>");
            await html.WriteLineAsync("<div class=\"files multi\">");
            foreach ((MediaAssetSnapshot manifest, MediaAssetReference asset) in assets.Where(pair => pair.Asset.IsCurrent && pair.Asset.Variant == "original"))
            {
                token.ThrowIfCancellationRequested();
                await AttachmentAsync(null, new CapturedAttachment(0, asset.MediaIdentifier, asset.Variant,
                    OriginalFileName: asset.OriginalFileName, ExpectedSize: asset.Length, ExpectedMd5: asset.Md5), (manifest, asset)).ConfigureAwait(false);
            }
            await html.WriteLineAsync("</div></article>");
        }
        await html.WriteLineAsync("</main><footer><h2>" + text.Html(HtmlExportText.ExportResult) + "</h2><p>" + text.Html(HtmlExportText.ResultCounts, posts, files, omissions.Count) + "</p>");
        foreach (string warning in warnings.Distinct()) await html.WriteLineAsync("<p class=\"omission\">" + text.WarningHtml(warning) + "</p>");
        await html.WriteLineAsync("<p>" + text.Html(HtmlExportText.VerifiedOffline) + "</p></footer></div></body></html>");
        await html.FlushAsync(token).ConfigureAwait(false);
        await html.DisposeAsync().ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);

        return new ArchiveHtmlExportResult(stage, Path.Combine(stage, pagePath), posts, files,
            Array.AsReadOnly(omissions.ToArray())) { Warnings = warnings.Distinct().ToArray() };

        async Task AttachmentAsync(string? postId, CapturedAttachment attachment,
            (MediaAssetSnapshot Manifest, MediaAssetReference Asset)? mediaOnly = null)
        {
            ArchiveExportOmissionReason? reason = null;
            (MediaAssetSnapshot Manifest, MediaAssetReference Asset)? chosen = null;
            MediaAttachmentBinding? provenance = null;
            if (!options.IncludeMedia) reason = ArchiveExportOmissionReason.MediaNotIncluded;
            else if (attachment.MediaId == null) reason = ArchiveExportOmissionReason.NoMediaIdentity;
            else
            {
                ResolvedMediaAttachment? resolved = postId == null ? null : MediaAttachmentResolver.Resolve(
                    assets.Select(pair => pair.Manifest).Distinct(), view.Summary.Id, postId, attachment);
                if (decisions.TryGetValue((attachment.MediaId, attachment.Variant), out CandidateDecision? decision)
                    && decision.Selection.Outcome == MediaSelectionOutcome.Excluded) reason = ArchiveExportOmissionReason.ExcludedByRule;
                else if (resolved != null) chosen = (resolved.Snapshot, resolved.Asset);
                else if (postId == null && view.PostCount == 0) chosen = mediaOnly;
                if (chosen == null && reason == null) reason = assets.Any(pair => pair.Asset.MediaIdentifier == attachment.MediaId
                    && pair.Asset.Variant == attachment.Variant) ? ArchiveExportOmissionReason.DifferentVersion : ArchiveExportOmissionReason.Unavailable;
                provenance = resolved?.Binding;
            }
            string? relative = null;
            if (chosen is (MediaAssetSnapshot manifest, MediaAssetReference asset))
            {
                string extension = SafeExtension(asset.Path);
                string name = asset.Sha256.ToLowerInvariant() + extension;
                if (!copied.TryGetValue(name, out relative) || verifyEachOrigin)
                {
                    Stream? source = null;
                    try { source = await manifest.OpenVerifiedReadAsync(asset.AssetId, token).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                    { reason = ArchiveExportOmissionReason.VerificationFailed; }
                    if (source != null)
                    {
                        string copyPath = Path.Combine(stage, "media", name + ".pending-" + Guid.NewGuid().ToString("N"));
                        bool matches;
                        await using (source)
                        await using (FileStream target = new(copyPath, FileMode.CreateNew, FileAccess.ReadWrite,
                            FileShare.None, 81920, FileOptions.Asynchronous))
                        {
                            await source.CopyToAsync(target, token).ConfigureAwait(false);
                            target.Position = 0;
                            byte[] digest = await SHA256.HashDataAsync(target, token).ConfigureAwait(false);
                            matches = target.Length == asset.Length && Convert.ToHexString(digest).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
                        }
                        if (!matches)
                        {
                            File.Delete(copyPath);
                            reason = ArchiveExportOmissionReason.VerificationFailed;
                        }
                        else
                        {
                            relative = mediaPrefix + name;
                            if (!copied.ContainsKey(name))
                            {
                                File.Move(copyPath, Path.Combine(stage, "media", name));
                                copied.Add(name, name);
                                files++;
                            }
                            else File.Delete(copyPath);
                        }
                    }
                }
            }
            if (relative != null && reason == null) relative = mediaPrefix + Path.GetFileName(relative);
            string label = Encode(attachment.OriginalFileName ?? attachment.MediaId ?? text.Get(HtmlExportText.DeletedAttachment));
            if (reason != null)
            {
                omissions.Add(new ArchiveExportOmission(postId, attachment.MediaId, reason.Value));
                await html.WriteLineAsync("<p class=\"omission\">" + label + " — " + text.Html(text.Omission(reason.Value)) + "</p>");
            }
            else if (relative != null)
            {
                // File line (name and saved size) above the thumbnail, like the reader; the thumbnail opens the full file.
                string extension = Path.GetExtension(relative);
                bool image = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";
                bool video = extension is ".webm" or ".mp4";
                string size = chosen is { } sized ? " <span class=\"size\">" + text.Html(HtmlExportText.SavedFile, sized.Asset.Length) + "</span>" : "";
                await html.WriteLineAsync("<figure class=\"" + (video ? "file video" : "file") + "\"><figcaption>"
                    + (image ? "<span class=\"name\">" + label + "</span>" : "<a class=\"name\" href=\"" + relative + "\" download>" + label + "</a>") + size + "</figcaption>");
                if (image)
                    await html.WriteLineAsync("<a class=\"thumb\" href=\"" + relative + "\"><img loading=\"lazy\" src=\"" + relative + "\" alt=\"" + label + "\"></a>");
                else if (video)
                    await html.WriteLineAsync("<video controls preload=\"metadata\" src=\"" + relative + "\"></video>");
                if (chosen != null && provenance != null)
                {
                    if (provenance.Acceptance.Kind == MediaAcceptanceKind.PassiveImport)
                        await html.WriteLineAsync("<p class=\"meta\">" + text.Html(HtmlExportText.Imported) + "</p>");
                    if (provenance.Acceptance.Kind == MediaAcceptanceKind.ExplicitProviderMismatch)
                        await html.WriteLineAsync("<p class=\"meta\">" + text.Html(HtmlExportText.Mismatch) + "</p>");
                    string unspecified = text.Get(HtmlExportText.Unspecified);
                    await html.WriteLineAsync("<details><summary>" + text.Html(HtmlExportText.SourceInfo) + "</summary><p>"
                        + text.Html(HtmlExportText.Size, provenance.Provider.ExpectedSize?.ToString(CultureInfo.InvariantCulture) ?? unspecified)
                        + "<br>MD5: " + Encode(provenance.Provider.ExpectedMd5 ?? unspecified)
                        + "<br>SHA-1: " + Encode(provenance.Provider.ExpectedSha1 ?? unspecified)
                        + (provenance.Provider.ProviderSizeHint is { } hint ? "<br>"
                            + text.Html(HtmlExportText.SizeHint, hint.RawValue, hint.UnitLabel, hint.Precision.ToString()) : "")
                        + "<br>" + text.Html(HtmlExportText.SavedSha1, provenance.Actual.Sha1 ?? unspecified)
                        + "<br>SHA-256: " + Encode(provenance.Provider.ExpectedSha256 ?? unspecified) + "</p></details>");
                }
                await html.WriteLineAsync("</figure>");
            }
        }
    }
    // One encoded header part followed by a space, or nothing when the post has no such field.
    private static string Part(string name, string? value) => string.IsNullOrWhiteSpace(value) ? ""
        : "<span class=\"" + name + "\">" + Encode(value) + "</span> ";
    internal const string ContentSecurityPolicy = "default-src 'none'; style-src 'self'; img-src 'self'; media-src 'self'; base-uri 'none'; form-action 'none'";
    internal static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    // The body is the plain text in styled spans (greentext, spoilers, code, external links, struck-through dead
    // references); every character stays encoded text and the spans cover it exactly once.
    internal static string BodyHtml(string plain, IReadOnlyList<ArchiveTextMark>? marks = null,
        IReadOnlySet<string>? deadReferences = null, string? deadNote = null)
    {
        StringBuilder body = new(plain.Length + 32);
        foreach (ArchiveTextRun run in ArchiveBodyRuns.Split(plain, marks))
        {
            string slice = plain.Substring(run.Start, run.Length);
            if (run.Style == ArchiveTextStyle.None) { AppendReferences(body, slice, deadReferences, deadNote); continue; }
            // The line break stays outside the spans.
            string core = slice.TrimEnd('\n');
            string breaks = slice[core.Length..];
            List<string> classes = new();
            if ((run.Style & ArchiveTextStyle.Spoiler) != 0) classes.Add("spoiler\" tabindex=\"0");
            if ((run.Style & ArchiveTextStyle.Code) != 0) classes.Add("code");
            if ((run.Style & ArchiveTextStyle.Link) != 0) classes.Add("link");
            if ((run.Style & ArchiveTextStyle.Greentext) != 0) classes.Add("greentext");
            foreach (string name in classes) body.Append("<span class=\"").Append(name).Append("\">");
            AppendReferences(body, core, deadReferences, deadNote);
            for (int index = 0; index < classes.Count; index++) body.Append("</span>");
            body.Append(breaks);
        }
        return body.ToString();
    }

    private static void AppendReferences(StringBuilder body, string text, IReadOnlySet<string>? dead, string? note)
    {
        int written = 0;
        if (dead is { Count: > 0 })
            for (int index = 0; index + 1 < text.Length; index++)
            {
                if (text[index] != '>' || text[index + 1] != '>' || index > 0 && text[index - 1] == '>') continue;
                string? label = dead.Where(candidate => text.AsSpan(index).StartsWith(candidate, StringComparison.Ordinal)
                    && !(index + candidate.Length < text.Length && char.IsLetterOrDigit(text[index + candidate.Length])))
                    .OrderByDescending(candidate => candidate.Length).FirstOrDefault();
                if (label == null) continue;
                body.Append(Encode(text[written..index])).Append("<s class=\"dead\">").Append(Encode(label)).Append("</s>");
                if (!string.IsNullOrEmpty(note)) body.Append(" <span class=\"note\">").Append(Encode(note)).Append("</span>");
                written = index + label.Length;
                index = written - 1;
            }
        body.Append(Encode(text[written..]));
    }
    internal static string PostAnchor(string id) => "p-" + Convert.ToHexString(Encoding.UTF8.GetBytes(id)).ToLowerInvariant();
    private static string SafeExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".png" => ".png", ".jpg" => ".jpg", ".jpeg" => ".jpeg", ".gif" => ".gif", ".webp" => ".webp", ".bmp" => ".bmp", ".webm" => ".webm", ".mp4" => ".mp4", _ => ".bin" };
    // Calm offline version of the reader cards (design language D3, reader study 2026-11-13). Every colour is a fixed copy
    // of a Desktop/WorkspaceStyles.axaml token (named in the comment); there is no build-time coupling. Local fonts only:
    // Noto Sans (the reader's body font) and Inter (the UI font) are used when installed, never downloaded, so the sheet
    // has no url(), @import or @font-face. Reply posts are boxes that fit their content up to a maximum width; the opening
    // post sits directly on the page. Dark is the default, the light palette follows a light system scheme; print is light.
    // The manual theme switch (HtmlExportChrome) needs no script: the radio inputs precede .page, so "#theme-dark:checked~.page"
    // re-declares the palette. The palette therefore lives on .page, not :root; each palette is written twice (system and manual).
    internal const string Css = """
        /* MeowChan HTML export. Colours: Desktop/WorkspaceStyles.axaml (Dark and Light theme dictionaries). */
        :root{color-scheme:dark light}
        html{background:#121416}
        .page,#theme-dark:checked~.page{color-scheme:dark;
        --bg:#121416;/* Workspace.Background */--panel:#1b1e22;/* Workspace.Panel */--raised:#24282d;/* Workspace.Raised */
        --border:#2d3239;/* Workspace.Border */--card:#20242a;/* Workspace.Card */--edge:#5a636e;/* Workspace.CardEdge */
        --text:#eef1f4;/* Workspace.Text */--muted:#a4adb8;/* Workspace.Muted */--accent:#6aa7ff;/* Workspace.Accent */
        --accent-surface:#25344a;/* Workspace.AccentSurface */--hover:#30363e;/* Workspace.Hover */
        --greentext:#9ccb6a;/* Workspace.ReaderGreentext */--code:#171a1e;/* Workspace.ReaderCodeSurface */
        --spoiler:#333a42;/* Workspace.ReaderSpoiler */--warning:#e8be72;/* Workspace.Warning */
        --scrim:rgba(0,0,0,.4);/* Workspace.Scrim #66000000 */--shadow:0 4px 16px rgba(0,0,0,.4);/* Workspace.Shadow.Overlay */
        --ui:Inter,ui-sans-serif,system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;
        --read:"Noto Sans",ui-sans-serif,system-ui,-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;
        --mono:ui-monospace,"SF Mono",Menlo,Consolas,"Liberation Mono",monospace}
        @media (prefers-color-scheme:light){html{background:#e3dfd8}.page{color-scheme:light;
        --bg:#e3dfd8;/* Workspace.Background */--panel:#efece6;/* Workspace.Panel */--raised:#dbd5cb;/* Workspace.Raised */
        --border:#a89f92;/* Workspace.Border */--card:#faf7f1;/* Workspace.Card */--edge:#7a7062;/* Workspace.CardEdge */
        --text:#1a1a1a;/* Workspace.Text */--muted:#575148;/* Workspace.Muted */--accent:#803b06;/* Workspace.Accent */
        --accent-surface:#e7d4be;/* Workspace.AccentSurface */--hover:#d2cabe;/* Workspace.Hover */
        --greentext:#386217;/* Workspace.ReaderGreentext */--code:#ece7dd;/* Workspace.ReaderCodeSurface */
        --spoiler:#d3cdc2;/* Workspace.ReaderSpoiler */--warning:#795100;/* Workspace.Warning */
        --scrim:rgba(0,0,0,.2);/* Workspace.Scrim #33000000 */--shadow:0 4px 16px rgba(0,0,0,.15)/* Workspace.Shadow.Overlay */}}
        #theme-light:checked~.page{color-scheme:light;
        --bg:#e3dfd8;/* Workspace.Background */--panel:#efece6;/* Workspace.Panel */--raised:#dbd5cb;/* Workspace.Raised */
        --border:#a89f92;/* Workspace.Border */--card:#faf7f1;/* Workspace.Card */--edge:#7a7062;/* Workspace.CardEdge */
        --text:#1a1a1a;/* Workspace.Text */--muted:#575148;/* Workspace.Muted */--accent:#803b06;/* Workspace.Accent */
        --accent-surface:#e7d4be;/* Workspace.AccentSurface */--hover:#d2cabe;/* Workspace.Hover */
        --greentext:#386217;/* Workspace.ReaderGreentext */--code:#ece7dd;/* Workspace.ReaderCodeSurface */
        --spoiler:#d3cdc2;/* Workspace.ReaderSpoiler */--warning:#795100;/* Workspace.Warning */
        --scrim:rgba(0,0,0,.2);/* Workspace.Scrim #33000000 */--shadow:0 4px 16px rgba(0,0,0,.15)/* Workspace.Shadow.Overlay */}
        body{margin:0}
        .page{min-height:100vh;box-sizing:border-box;background:var(--bg);color:var(--text);font:15px/1.5 var(--ui);-webkit-text-size-adjust:100%;text-size-adjust:100%}
        .page>*{max-width:72rem;margin-left:auto;margin-right:auto}
        .page{padding:24px 16px 48px}
        h1,h2{overflow-wrap:anywhere;line-height:1.3}
        a{color:var(--accent);overflow-wrap:anywhere}
        :focus-visible{outline:2px solid var(--accent);outline-offset:2px}
        .meta{color:var(--muted);font-size:.8125rem}
        .toggle{position:fixed;top:0;left:0;width:1px;height:1px;margin:0;opacity:0;pointer-events:none}
        .controls{display:flex;flex-wrap:wrap;align-items:center;gap:8px}
        .switch{display:inline-flex;align-items:center;gap:8px;font-size:.8125rem}
        .switch-name{color:var(--muted)}
        .segments{display:inline-flex;background:var(--raised);border:1px solid var(--edge);border-radius:5px;overflow:hidden}
        .segments label{padding:3px 10px;line-height:1.5;color:var(--text);cursor:pointer;user-select:none;-webkit-user-select:none}
        .segments label+label{border-left:1px solid var(--edge)}
        .segments label:hover{background:var(--hover)}
        #theme-system:checked~.page label[for=theme-system],#theme-light:checked~.page label[for=theme-light],#theme-dark:checked~.page label[for=theme-dark]{background:var(--accent-surface);box-shadow:inset 0 -2px 0 var(--accent);font-weight:600}
        #theme-system:focus-visible~.page label[for=theme-system],#theme-light:focus-visible~.page label[for=theme-light],#theme-dark:focus-visible~.page label[for=theme-dark]{outline:2px solid var(--accent);outline-offset:-2px}
        header.top,footer{background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:16px 20px}
        header.top{margin-bottom:24px}
        header.top p{margin:4px 0 0}
        .bar{display:flex;flex-wrap:wrap;align-items:flex-start;justify-content:space-between;gap:12px 24px}
        .heading{min-width:0;flex:1 1 20rem}
        h1{font-size:1.25rem;font-weight:600;margin:0}
        .source{color:var(--muted);font-weight:600}
        main{display:flex;flex-direction:column;align-items:flex-start;gap:8px}
        main>nav{margin-bottom:8px;font-size:.875rem}
        article{box-sizing:border-box;max-width:min(100%,52rem);min-width:0;display:flow-root;background:var(--card);border:1px solid var(--edge);border-radius:5px;padding:10px 12px 12px}
        article.op{align-self:stretch;max-width:100%;background:transparent;border-color:transparent;margin-bottom:8px}
        article>h2{font-size:1rem;font-weight:600;margin:0 0 6px}
        article>p{margin:4px 0 0}
        .posts{font-size:.875rem;line-height:1.7}
        h2.post-head{display:flex;flex-wrap:wrap;align-items:baseline;gap:2px 8px;font-size:.8125rem;font-weight:400;color:var(--muted)}
        .post-head .subject{font-size:.9375rem;font-weight:600;color:var(--text)}
        .post-head .no{color:var(--muted);text-decoration:none}
        .post-head .no:hover{color:var(--accent);text-decoration:underline}
        .removed{color:var(--warning)}
        .body{font:1rem/1.45 var(--read);white-space:pre-wrap;overflow-wrap:anywhere;display:flow-root}
        .greentext{color:var(--greentext)}
        .link{color:var(--accent);text-decoration:underline}
        .code{font-family:var(--mono);font-size:.9em;background:var(--code);border-radius:3px;padding:0 .2em}
        .dead{color:var(--muted)}
        .note{color:var(--muted);font-style:italic}
        .spoiler{background:var(--spoiler);color:var(--spoiler);border-radius:3px;cursor:pointer}
        .spoiler *{color:inherit}
        .spoiler:hover,.spoiler:focus{color:var(--text)}
        .quote,.omission{display:block;margin:6px 0 0;font-size:.875rem}
        .quote{text-decoration:none}
        .quote:hover{text-decoration:underline}
        .omission{color:var(--warning)}
        .files{float:left;max-width:100%;margin:2px 16px 8px 0}
        .files.multi{float:none;display:flex;flex-wrap:wrap;gap:8px 16px;margin-right:0}
        .file{margin:0;max-width:260px}
        .file.video{max-width:480px}
        .file figcaption{font-size:.8125rem;color:var(--muted);overflow-wrap:anywhere;margin-bottom:4px}
        .file .name{color:var(--text)}
        .file a.name{color:var(--accent)}
        .thumb{display:inline-block;max-width:100%}
        .file img{display:block;max-width:100%;max-height:250px;width:auto;height:auto;border-radius:5px}
        .file video{display:block;max-width:100%;max-height:360px;border-radius:5px}
        .file .meta{margin:4px 0 0}
        .file details{font-size:.8125rem;color:var(--muted);margin-top:4px}
        footer{margin-top:24px;font-size:.875rem;color:var(--muted)}
        footer h2{font-size:.9375rem;font-weight:600;color:var(--text);margin:0 0 4px}
        footer p{margin:4px 0 0}
        @media (max-width:560px){.page{padding:12px 12px 32px}header.top,footer{padding:12px 14px}.files{float:none;margin-right:0}article{max-width:100%}}
        @media print{
        /* Print is always the light palette on white paper, whatever the screen theme. */
        :root{color-scheme:light}
        .page,#theme-light:checked~.page,#theme-dark:checked~.page{color-scheme:light;--bg:#fff;--panel:#fff;--card:#fff;--edge:#a89f92;--border:#a89f92;--text:#000;--muted:#575148;--accent:#803b06;
        --greentext:#386217;--code:#ece7dd;--spoiler:#ece7dd;--warning:#795100;padding:0;min-height:0;font-size:11pt}
        html{background:#fff}
        .page>*{max-width:none}
        .controls{display:none}
        article{break-inside:avoid-page}
        .post-head,.file{break-inside:avoid}
        .spoiler{color:inherit}
        a{text-decoration:none}}
        """;
}
