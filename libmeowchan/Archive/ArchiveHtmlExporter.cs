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

public sealed record ArchiveHtmlExportOptions(bool IncludeMedia = true);
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
            ArchivedThreadView view = snapshot.View;
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
            Dictionary<string, string> copied = new(StringComparer.Ordinal);
            List<ArchiveExportOmission> omissions = new();
            HashSet<string> capturedIds = view.Posts.Select(post => post.PostId).ToHashSet(StringComparer.Ordinal);
            int files = 0;
            int posts = 0;
            Directory.CreateDirectory(Path.Combine(stage, "media"));
            await using FileStream output = new(Path.Combine(stage, "index.html"), FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous);
            await using StreamWriter html = new(output, new UTF8Encoding(false));
            await html.WriteLineAsync("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'self'; img-src 'self'; media-src 'self'; base-uri 'none'; form-action 'none'\"><title>" + Encode(view.Summary.Title) + "</title><link rel=\"stylesheet\" href=\"style.css\"></head><body><header><h1>" + Encode(view.Summary.Title) + "</h1><p>" + Encode(view.Summary.Id.Source + " /" + view.Summary.Id.Board + "/ " + view.Summary.Id.Thread) + "</p><p>Last observed capture: " + Encode(view.Summary.LastCapturedAt?.ToString("u", CultureInfo.InvariantCulture) ?? "No captured posts") + "</p><p>Only observed posts are retained. Missing source posts do not erase earlier captures. External threads are not included in this folder.</p></header><main>");
            foreach (CapturedPost post in view.Posts)
            {
                token.ThrowIfCancellationRequested();
                await html.WriteLineAsync("<article id=\"" + PostAnchor(post.PostId) + "\"><h2>#" + Encode(post.PostId) + " " + Encode(post.Subject) + "</h2><p class=\"meta\">" + Encode(post.Author)
                    + " " + Encode(post.PostedAt?.ToString("u", CultureInfo.InvariantCulture)) + (post.IsDeleted ? " · Removed at source" : "") + "</p><div class=\"body\">" + Encode(ArchiveText.PlainText(post.Html)) + "</div>");
                foreach (ArchiveQuote quote in ArchiveQuoteParser.Parse(view.Summary.Id, view.Summary.Url, post))
                {
                    bool captured = quote.Target?.Thread == view.Summary.Id && capturedIds.Contains(quote.Target.PostId);
                    if (captured) await html.WriteLineAsync("<a class=\"quote\" href=\"#" + PostAnchor(quote.Target!.PostId) + "\">" + Encode(quote.Label) + "</a>");
                    else await html.WriteLineAsync("<span class=\"omission\">" + Encode(quote.Label) + " — "
                        + (quote.IsExternal ? "External reference" : quote.Target?.Thread == view.Summary.Id ? "Post not captured" : quote.Target != null ? "Other thread, outside this export" : "Unresolved reference") + "</span>");
                }
                foreach (CapturedAttachment attachment in post.Attachments ?? Array.Empty<CapturedAttachment>())
                    await AttachmentAsync(post.PostId, attachment).ConfigureAwait(false);
                await html.WriteLineAsync("</article>");
                posts++;
                if (posts == 1 || posts % 25 == 0 || posts == view.PostCount)
                    progress?.Report(new ArchiveHtmlExportProgress(ArchiveHtmlExportPhase.WritingPosts, posts, files));
            }
            // Old/media-only threads remain useful as a local gallery, without inventing posts.
            if (view.PostCount == 0)
            {
                await html.WriteLineAsync("<article><h2>Saved media</h2><p>No post text was captured. This gallery contains current manifest-published originals.</p>");
                if (!options.IncludeMedia) await html.WriteLineAsync("<p class=\"omission\">Saved media omitted by export choice.</p>");
                foreach ((MediaAssetSnapshot manifest, MediaAssetReference asset) in assets.Where(pair => pair.Asset.IsCurrent && pair.Asset.Variant == "original"))
                {
                    token.ThrowIfCancellationRequested();
                    await AttachmentAsync(null, new CapturedAttachment(0, asset.MediaIdentifier, asset.Variant,
                        OriginalFileName: asset.OriginalFileName, ExpectedSize: asset.Length, ExpectedMd5: asset.Md5)).ConfigureAwait(false);
                }
                await html.WriteLineAsync("</article>");
            }
            await html.WriteLineAsync("</main><footer><h2>Export result</h2><p>" + posts + " captured posts · " + files + " copied media files · " + omissions.Count + " attachment omissions</p>");
            foreach (string warning in warnings.Distinct()) await html.WriteLineAsync("<p class=\"omission\">" + Encode(warning) + "</p>");
            await html.WriteLineAsync("<p>Media bytes were verified against their manifest before copying. This folder requires no MeowChan profile, server or network connection.</p></footer></body></html>");
            await html.FlushAsync(token).ConfigureAwait(false);
            await html.DisposeAsync().ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
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
            return new ArchiveHtmlExportResult(destination, Path.Combine(destination, "index.html"), posts, files,
                Array.AsReadOnly(omissions.ToArray())) { Warnings = Array.AsReadOnly(warnings.Distinct().ToArray()),
                    Metadata = metadata, BackupDirectory = backup };

            async Task AttachmentAsync(string? postId, CapturedAttachment attachment)
            {
                ArchiveExportOmissionReason? reason = null;
                (MediaAssetSnapshot Manifest, MediaAssetReference Asset)? chosen = null;
                if (!options.IncludeMedia) reason = ArchiveExportOmissionReason.MediaNotIncluded;
                else if (attachment.MediaId == null) reason = ArchiveExportOmissionReason.NoMediaIdentity;
                else if (decisions.TryGetValue((attachment.MediaId, attachment.Variant), out CandidateDecision? decision)
                    && decision.Selection.Outcome == MediaSelectionOutcome.Excluded) reason = ArchiveExportOmissionReason.ExcludedByRule;
                else
                {
                    (MediaAssetSnapshot Manifest, MediaAssetReference Asset)[] matches = assets.Where(pair => pair.Asset.MediaIdentifier == attachment.MediaId
                        && pair.Asset.Variant == attachment.Variant).ToArray();
                    chosen = matches.Where(pair => (attachment.ExpectedSize == null || pair.Asset.Length == attachment.ExpectedSize)
                            && (attachment.ExpectedMd5 == null || pair.Asset.Md5 == attachment.ExpectedMd5))
                        .OrderByDescending(pair => pair.Asset.IsCurrent).ThenBy(pair => pair.Asset.AssetId, StringComparer.Ordinal)
                        .Select(pair => ((MediaAssetSnapshot Manifest, MediaAssetReference Asset)?)pair).FirstOrDefault();
                    if (chosen == null) reason = matches.Length == 0 ? ArchiveExportOmissionReason.Unavailable : ArchiveExportOmissionReason.DifferentVersion;
                }
                string? relative = null;
                if (chosen is (MediaAssetSnapshot manifest, MediaAssetReference asset))
                {
                    string extension = SafeExtension(asset.Path);
                    string name = asset.Sha256.ToLowerInvariant() + extension;
                    if (!copied.TryGetValue(name, out relative))
                    {
                        Stream? source = null;
                        try { source = await manifest.OpenVerifiedReadAsync(asset.AssetId, token).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
                        { reason = ArchiveExportOmissionReason.VerificationFailed; }
                        if (source != null)
                        {
                            bool matches;
                            await using (source)
                            await using (FileStream target = new(Path.Combine(stage, "media", name), FileMode.CreateNew, FileAccess.ReadWrite,
                                FileShare.None, 81920, FileOptions.Asynchronous))
                            {
                                await source.CopyToAsync(target, token).ConfigureAwait(false);
                                target.Position = 0;
                                byte[] digest = await SHA256.HashDataAsync(target, token).ConfigureAwait(false);
                                matches = target.Length == asset.Length && Convert.ToHexString(digest).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
                            }
                            if (!matches)
                            {
                                File.Delete(Path.Combine(stage, "media", name));
                                reason = ArchiveExportOmissionReason.VerificationFailed;
                            }
                            else
                            {
                                relative = "media/" + name;
                                copied.Add(name, relative);
                                files++;
                            }
                        }
                    }
                }
                string label = Encode(attachment.OriginalFileName ?? attachment.MediaId ?? "Deleted attachment");
                if (reason != null)
                {
                    omissions.Add(new ArchiveExportOmission(postId, attachment.MediaId, reason.Value));
                    await html.WriteLineAsync("<p class=\"omission\">" + label + " — " + OmissionText(reason.Value) + "</p>");
                }
                else if (relative != null)
                {
                    string extension = Path.GetExtension(relative);
                    if (extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp")
                        await html.WriteLineAsync("<figure><a href=\"" + relative + "\"><img loading=\"lazy\" src=\"" + relative + "\" alt=\"" + label + "\"></a><figcaption>" + label + "</figcaption></figure>");
                    else if (extension is ".webm" or ".mp4")
                        await html.WriteLineAsync("<figure><video controls preload=\"metadata\" src=\"" + relative + "\"></video><figcaption><a href=\"" + relative + "\" download>" + label + "</a></figcaption></figure>");
                    else await html.WriteLineAsync("<p><a href=\"" + relative + "\" download>" + label + "</a></p>");
                }
            }
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
    }
    private static string Encode(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
    private static string PostAnchor(string id) => "p-" + Convert.ToHexString(Encoding.UTF8.GetBytes(id)).ToLowerInvariant();
    private static string SafeExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".png" => ".png", ".jpg" => ".jpg", ".jpeg" => ".jpeg", ".gif" => ".gif", ".webp" => ".webp", ".bmp" => ".bmp", ".webm" => ".webm", ".mp4" => ".mp4", _ => ".bin" };
    private static string OmissionText(ArchiveExportOmissionReason reason) => reason switch
    {
        ArchiveExportOmissionReason.MediaNotIncluded => "Media omitted by export choice",
        ArchiveExportOmissionReason.ExcludedByRule => "Excluded by the original download rule",
        ArchiveExportOmissionReason.Unavailable => "Saved media unavailable",
        ArchiveExportOmissionReason.DifferentVersion => "No saved version matches the captured attachment",
        ArchiveExportOmissionReason.VerificationFailed => "Saved media failed verification or could not be read",
        _ => "No retained media identity"
    };
    private const string Css = """
        :root{color-scheme:dark light;font:16px/1.5 system-ui,sans-serif;background:#15181c;color:#e0e3e6}
        body{max-width:960px;margin:auto;padding:24px}header,footer,article{background:#20242a;border:1px solid #363b44;border-radius:6px;padding:20px;margin-bottom:16px}
        h1,h2{overflow-wrap:anywhere}h2{font-size:1.05rem}.body{white-space:pre-wrap;overflow-wrap:anywhere}.meta{color:#aab2be;font-size:.85rem}
        a{color:#88b8ff;overflow-wrap:anywhere}.quote,.omission{display:block;margin:8px 0}.omission{color:#e9bc7d;font-size:.9rem}figure{margin:16px 0}img,video{display:block;max-width:100%;max-height:600px}figcaption{overflow-wrap:anywhere}
        """;
}
