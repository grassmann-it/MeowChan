// Bounded SDK-only Wakaba HTML adapter. Fchan is intentionally HTTP-only: the
// adapter never upgrades the scheme, sets disclaimer cookies or follows mirrors.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.PluginSdk;

namespace MeowChan.Plugins.Examples.Fchan;

public sealed class FchanPlugin : ISourcePlugin
{
    public const string SourceId = "fchan";
    private const string Host = "fchan.us";
    private static readonly Uri Origin = new Uri("http://" + Host + "/");
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());
    private static readonly Regex ThreadLink = new Regex(@"(?<path>/(?<board>[A-Za-z0-9_]+)/res/(?<thread>[0-9]+)\.html)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ElementId = new Regex(@"<(?<tag>div|td)\b[^>]*\bid\s*=\s*[""'](?<prefix>thread|reply)(?<id>[0-9]+)[""'][^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Anchor = new Regex(@"<a\b(?<attrs>[^>]*?)\bhref\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<body>.*?)</a\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public PluginDescription Describe() => new PluginDescription("meowchan.source.fchan", "Fchan", "1.0.0", SdkContract.Version,
        "Reads Fchan Wakaba board pages and public thread HTML without HTTPS or cookie upgrades.",
        [Description()], Array.Empty<FilterDescription>(), EmptySchema,
        [new InformationEntry("support", "Accepted links", InformationKind.Text, "Accepts HTTP board and thread links from fchan.us."),
         new InformationEntry("maturity", "Status", InformationKind.Text, "MeowChan · experimental; HTTP-only source."),
         new InformationEntry("limitations", "Limitations", InformationKind.Text, "There is no catalog.json. Board pages are parsed conservatively; dates and disclaimer cookies are not inferred or written.")],
        Author: "MeowChan contributors", DocumentationUrl: new Uri("http://fchan.us/"));

    private static SourceDescription Description() => new SourceDescription(SourceId, "Fchan", Origin,
        SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
        [new UrlRoute(SourceResourceKind.Board, Host, "/{board}/", IsCanonical: true) { BoardIdentifierKind = RouteIdentifierKind.AsciiWord },
         new UrlRoute(SourceResourceKind.Thread, Host, "/{board}/res/{thread}.html", IsCanonical: true, AllowTrailingSlash: false)
         { BoardIdentifierKind = RouteIdentifierKind.AsciiWord, ThreadIdentifierKind = RouteIdentifierKind.Decimal }],
        [new HttpHostDeclaration(Host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)], EmptySchema,
        PostOrderKind.DecimalPostId) { Maturity = SourceMaturity.Experimental };

    public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Board.SourceId != SourceId || !BoardValid(request.Board.BoardId)) return new CatalogFailure(request.Board, InvalidResource());
        if (request.Cursor is not null) return new CatalogFailure(request.Board, new FailureDetail("unsupported_cursor", "Fchan has numbered HTML pages, not cursor pagination."));
        if (request.Page is < 0) return new CatalogFailure(request.Board, new FailureDetail("invalid_page", "Page must be nonnegative."));
        int page = request.Page ?? 0;
        Uri endpoint = BoardPage(request.Board.BoardId, page);
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get, endpoint,
            Headers: new Dictionary<string, string> { ["Accept"] = "text/html" }, Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new CatalogDeferred(request.Board, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new CatalogFailure(request.Board, failure.Failure);
        if (result is not HostHttpResponse response) return new CatalogFailure(request.Board, InvalidHttpResult());
        if (!SameEndpoint(endpoint, response.FinalUri)) return new CatalogFailure(request.Board, UnexpectedEndpoint(response.StatusCode));
        if (response.StatusCode == 304) return request.HasSnapshot && HasHttpValidator(request.Validators) ? new CatalogUnchanged(request.Board, request.Validators!) : new CatalogFailure(request.Board, MissingBaseline(304));
        if (response.StatusCode == 404) return new CatalogFailure(request.Board, new FailureDetail("board_unavailable", "The requested Fchan board page was not found.", 404));
        if (response.StatusCode != 200) return new CatalogFailure(request.Board, HttpFailure(response.StatusCode));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes) return new CatalogFailure(request.Board, TooLarge());
        try
        {
            string html = Decode(response.Body);
            if (LooksLikeDisclaimer(html)) return new CatalogFailure(request.Board, DisclaimerRequired());
            List<SourceThreadReference> threads = ParseCatalog(request.Board, html);
            CatalogSnapshot snapshot = new CatalogSnapshot(new SourceCatalogSnapshot(request.Board, threads.AsReadOnly(), IsComplete: false, Validators: response.Validators));
            if (SdkValidation.ValidateCatalogResult(request, snapshot, Description()).Count != 0) return new CatalogFailure(request.Board, InvalidPayload());
            return snapshot;
        }
        catch (Exception error) when (PayloadError(error)) { return new CatalogFailure(request.Board, InvalidPayload()); }
    }

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Thread.SourceId != SourceId || !BoardValid(request.Thread.BoardId) || !DecimalIdValid(request.Thread.ThreadId)) return new ThreadFailure(request.Thread, InvalidResource());
        Uri endpoint = ThreadUri(request.Thread.BoardId, request.Thread.ThreadId);
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get, endpoint,
            Headers: new Dictionary<string, string> { ["Accept"] = "text/html" }, Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred) return new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new ThreadFailure(request.Thread, failure.Failure);
        if (result is not HostHttpResponse response) return new ThreadFailure(request.Thread, InvalidHttpResult());
        if (!SameEndpoint(endpoint, response.FinalUri)) return new ThreadFailure(request.Thread, UnexpectedEndpoint(response.StatusCode));
        if (response.StatusCode == 404) return new ThreadNotFound(request.Thread, 404);
        if (response.StatusCode == 304) return request.HasSnapshot && HasHttpValidator(request.Validators) ? new ThreadUnchanged(request.Thread, request.Validators!) : new ThreadFailure(request.Thread, MissingBaseline(304));
        if (response.StatusCode != 200) return new ThreadFailure(request.Thread, HttpFailure(response.StatusCode));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes) return new ThreadFailure(request.Thread, TooLarge());
        try
        {
            string html = Decode(response.Body);
            if (LooksLikeDisclaimer(html)) return new ThreadFailure(request.Thread, DisclaimerRequired());
            ThreadSnapshot snapshot = ParseThread(request.Thread, html, response.Validators);
            if (SdkValidation.ValidateThreadResult(request, snapshot, Description()).Count != 0) return new ThreadFailure(request.Thread, InvalidPayload());
            return snapshot;
        }
        catch (Exception error) when (PayloadError(error)) { return new ThreadFailure(request.Thread, InvalidPayload()); }
    }

    private static List<SourceThreadReference> ParseCatalog(SourceBoardId board, string html)
    {
        Dictionary<string, SourceThreadReference> found = new Dictionary<string, SourceThreadReference>(StringComparer.Ordinal);
        foreach (Match anchor in Anchor.Matches(html))
        {
            string href = WebUtility.HtmlDecode(anchor.Groups["href"].Value.Trim());
            if (!Uri.TryCreate(Origin, href, out Uri? candidate) || candidate is null
                || candidate.IdnHost != Host || candidate.Scheme != Origin.Scheme
                || candidate.Query.Length != 0 || candidate.Fragment.Length != 0) continue;
            Match match = ThreadLink.Match(candidate.AbsolutePath);
            if (!match.Success || !string.Equals(match.Groups["board"].Value, board.BoardId, StringComparison.OrdinalIgnoreCase)) continue;
            string id = match.Groups["thread"].Value;
            if (!DecimalIdValid(id)) continue;
            string path = match.Groups["path"].Value;
            Uri url = new Uri(Origin, path);
            if (!found.ContainsKey(id)) found.Add(id, new SourceThreadReference(new SourceThreadId(SourceId, board.BoardId, id), url));
        }
        if (found.Count > SdkLimits.MaximumCollectionCount) throw new InvalidDataException("Catalog exceeds the collection limit.");
        return found.Values.ToList();
    }

    private static ThreadSnapshot ParseThread(SourceThreadId thread, string html, SourceValidators? validators)
    {
        List<SourcePost> posts = new List<SourcePost>();
        foreach (Match match in ElementId.Matches(html))
        {
            string id = match.Groups["id"].Value;
            string? body = ExtractElement(html, match.Index, match.Groups["tag"].Value);
            if (body is null) throw new InvalidDataException("Unbalanced post element.");
            SourcePost post = ParsePost(thread, id, body);
            if (posts.All(existing => existing.PostId != id)) posts.Add(post);
        }
        if (posts.Count == 0) throw new InvalidDataException("No supported Wakaba posts were found.");
        string opener = posts[0].PostId;
        if (opener != thread.ThreadId) throw new InvalidDataException("The first post is not the requested thread opener.");
        return new ThreadSnapshot(new SourceThreadSnapshot(thread, ThreadUri(thread.BoardId, thread.ThreadId),
            Subject(posts[0]), SourceThreadState.Active, posts.AsReadOnly(), validators));
    }

    private static SourcePost ParsePost(SourceThreadId thread, string id, string html)
    {
        SourceFieldPresence fields = SourceFieldPresence.None;
        string? author = SpanText(html, "postername");
        if (author is not null) fields |= SourceFieldPresence.Author;
        string? subject = SpanText(html, "subject");
        if (subject is not null) fields |= SourceFieldPresence.Subject;
        string? text = FirstElementInner(html, "blockquote");
        if (text is not null) fields |= SourceFieldPresence.Text;
        List<SourceAttachment> attachments = ParseAttachments(html, id);
        if (attachments.Count != 0) fields |= SourceFieldPresence.Attachments;
        return new SourcePost(id, 0, fields, Subject: subject, Author: author, Text: text, TextFormat: SourceTextFormat.Html,
            Attachments: attachments.Count == 0 ? null : attachments.AsReadOnly());
    }

    private static List<SourceAttachment> ParseAttachments(string html, string postId)
    {
        List<SourceAttachment> attachments = new List<SourceAttachment>();
        foreach (Match match in Anchor.Matches(html))
        {
            string href = WebUtility.HtmlDecode(match.Groups["href"].Value.Trim());
            if (!Uri.TryCreate(Origin, href, out Uri? url) || url is null || url.IdnHost != Host || url.Scheme != Origin.Scheme
                || url.Query.Length != 0 || url.Fragment.Length != 0 || !url.AbsolutePath.StartsWith("/src/", StringComparison.Ordinal)) continue;
            string filename = url.AbsolutePath[(url.AbsolutePath.LastIndexOf('/') + 1)..];
            if (filename.Length == 0 || filename is "." or ".." || filename.Contains("..", StringComparison.Ordinal)) throw new InvalidDataException("Unsafe original media path.");
            string? extension = Extension(filename);
            attachments.Add(new SourceAttachment(attachments.Count, postId + "-" + attachments.Count.ToString(CultureInfo.InvariantCulture),
                Url: url, OriginalFileName: WebUtility.HtmlDecode(StripTags(match.Groups["body"].Value)).Trim(), Extension: extension));
            if (attachments.Count > 1024) throw new InvalidDataException("Too many attachments.");
        }
        return attachments;
    }

    private static string? Subject(SourcePost post) => post.Subject;
    private static string? SpanText(string html, string className)
    {
        Match match = Regex.Match(html, @"<span\b[^>]*\bclass\s*=\s*[""'][^""']*\b" + Regex.Escape(className) + @"\b[^""']*[""'][^>]*>(?<body>.*?)</span\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(StripTags(match.Groups["body"].Value)).Trim() : null;
    }
    private static string? FirstElementInner(string html, string tag)
    {
        Match match = Regex.Match(html, @"<" + tag + @"\b[^>]*>(?<body>.*?)</" + tag + @"\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["body"].Value.Trim() : null;
    }
    private static string? ExtractElement(string html, int index, string tag)
    {
        Match open = Regex.Match(html[index..], @"^<" + tag + @"\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!open.Success) return null;
        int contentStart = index + open.Length; int depth = 1;
        Regex token = new Regex(@"</?" + tag + @"\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match item in token.Matches(html, contentStart))
        {
            if (item.Value.StartsWith("</", StringComparison.Ordinal)) depth--; else if (!item.Value.EndsWith("/>", StringComparison.Ordinal)) depth++;
            if (depth == 0) return html[contentStart..item.Index];
        }
        return null;
    }
    private static string StripTags(string value) => Regex.Replace(value, "<[^>]*>", "", RegexOptions.Singleline);
    private static string Decode(byte[] body) => Encoding.Latin1.GetString(body);
    private static bool LooksLikeDisclaimer(string html) => html.Contains("disclaimer", StringComparison.OrdinalIgnoreCase) && !html.Contains("whole_thread", StringComparison.OrdinalIgnoreCase);
    private static Uri BoardPage(string board, int page) => new Uri(Origin, board + "/" + (page == 0 ? "index.html" : page.ToString(CultureInfo.InvariantCulture) + ".html"));
    private static Uri ThreadUri(string board, string thread) => new Uri(Origin, board + "/res/" + Uri.EscapeDataString(thread) + ".html");
    private static string? Extension(string filename) { int dot = filename.LastIndexOf('.'); return dot > 0 && dot < filename.Length - 1 ? filename[dot..] : null; }
    private static bool SameEndpoint(Uri expected, Uri actual) => actual.IsAbsoluteUri && actual.Scheme == expected.Scheme && actual.IdnHost == expected.IdnHost && actual.Port == expected.Port && actual.AbsolutePath == expected.AbsolutePath && actual.Query.Length == 0 && actual.Fragment.Length == 0 && actual.UserInfo.Length == 0;
    private static bool BoardValid(string board) => !string.IsNullOrWhiteSpace(board) && board.Length <= 64 && board.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static bool DecimalIdValid(string id) => !string.IsNullOrEmpty(id) && id.Length <= SdkLimits.MaximumIdentifierLength && id.All(c => c is >= '0' and <= '9') && (id.Length == 1 || id[0] != '0');
    private static bool HasHttpValidator(SourceValidators? v) => !string.IsNullOrWhiteSpace(v?.EntityTag) || v?.LastModified is not null || !string.IsNullOrWhiteSpace(v?.Revision);
    private static FailureDetail InvalidResource() => new FailureDetail("invalid_resource", "Source, board or thread identity is invalid.");
    private static FailureDetail InvalidHttpResult() => new FailureDetail("invalid_http_result", "Unknown host HTTP result.");
    private static FailureDetail UnexpectedEndpoint(int? status) => new FailureDetail("unexpected_endpoint", "The response is not from the requested HTTP-only endpoint.", status);
    private static FailureDetail MissingBaseline(int status) => new FailureDetail("missing_baseline", "304 requires an accepted resource and its HTTP validator.", status);
    private static FailureDetail DisclaimerRequired() => new FailureDetail("user_action_required", "Fchan returned a disclaimer/interstitial; MeowChan does not set an acceptance cookie.");
    private static FailureDetail HttpFailure(int status) => new FailureDetail("http_failure", "The Fchan HTML response was not a verified resource result.", status);
    private static FailureDetail InvalidPayload() => new FailureDetail("invalid_payload", "The HTML response has no supported bounded Wakaba shape.", 200);
    private static FailureDetail TooLarge() => new FailureDetail("metadata_too_large", "The HTML response exceeds the SDK body limit.", 200);
    private static bool PayloadError(Exception error) => error is InvalidDataException or ArgumentException;
}
