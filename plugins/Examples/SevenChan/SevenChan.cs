// Bounded SDK-only Kusaba/KusabaX-style HTML adapter. The parser accepts only
// the selectors observed for 7chan.org; transport, pacing and media I/O remain
// owned by the MeowChan host.
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

namespace MeowChan.Plugins.Examples.SevenChan;

public sealed class SevenChanPlugin : ISourcePlugin
{
    public const string SourceId = "sevenchan";
    private const string Host = "7chan.org";
    private static readonly Uri Origin = new Uri("https://" + Host + "/");
    private static readonly SettingsSchema EmptySchema = new SettingsSchema("1", Array.Empty<SettingDefinition>());
    private static readonly Regex CatalogAnchor = new Regex(
        @"<a\b(?=[^>]*\bclass\s*=\s*[""'][^""']*\bcattile\b[^""']*[""'])[^>]*\bhref\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<body>.*?)</a\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    // Kusaba X prints the post id unquoted (<div class="post" id=677>); quoted ids stay accepted.
    private static readonly Regex PostOpen = new Regex(
        @"<div\b(?=[^>]*\bclass\s*=\s*[""'][^""']*\bpost\b[^""']*[""'])[^>]*\bid\s*=\s*(?:[""'](?<id>[0-9]+)[""']|(?<id>[0-9]+)(?=[\s/>]))[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Attribute = new Regex(
        @"\b(?<name>[A-Za-z][A-Za-z0-9:_-]*)\s*=\s*[""'](?<value>[^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public PluginDescription Describe() => new PluginDescription("meowchan.source.sevenchan", "7chan",
        "1.0.0", SdkContract.Version,
        "Reads the documented 7chan Kusaba/KusabaX catalog and thread HTML resources.",
        [Description()], Array.Empty<FilterDescription>(), EmptySchema,
        [new InformationEntry("support", "Accepted links", InformationKind.Text,
            "Accepts documented board and thread links from 7chan.org."),
         new InformationEntry("maturity", "Status", InformationKind.Text,
            "MeowChan · experimental; HTML contract and current availability are not a stability promise."),
         new InformationEntry("limitations", "Limitations", InformationKind.Text,
            "No JSON fallback, posting, login, cookie handling, archive inference or preview support. Dates are not normalized without a proven time contract.")],
        Author: "MeowChan contributors", DocumentationUrl: new Uri("https://7chan.org/"));

    private static SourceDescription Description() => new SourceDescription(SourceId, "7chan", Origin,
        SourceCapabilities.ThreadRead | SourceCapabilities.BoardCatalog | SourceCapabilities.PostCapture | SourceCapabilities.OriginalMedia,
        [new UrlRoute(SourceResourceKind.Board, Host, "/{board}/", IsCanonical: true)
            { BoardIdentifierKind = RouteIdentifierKind.AsciiWord },
         new UrlRoute(SourceResourceKind.Thread, Host, "/{board}/res/{thread}.html", IsCanonical: true, AllowTrailingSlash: false)
            { BoardIdentifierKind = RouteIdentifierKind.AsciiWord, ThreadIdentifierKind = RouteIdentifierKind.Decimal }],
        [new HttpHostDeclaration(Host, HttpHostPurpose.Metadata | HttpHostPurpose.OriginalMedia)],
        EmptySchema, PostOrderKind.DecimalPostId) { Maturity = SourceMaturity.Experimental };

    public async Task<CatalogReadResult> ReadCatalogAsync(CatalogReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Board.SourceId != SourceId || !BoardValid(request.Board.BoardId))
            return new CatalogFailure(request.Board, InvalidResource());
        if (request.Cursor is not null || request.Page is not null)
            return new CatalogFailure(request.Board, new FailureDetail("unsupported_pagination",
                "7chan exposes one documented catalog.html observation; no JSON, cursor or page contract is assumed."));

        Uri endpoint = new Uri(Origin, Uri.EscapeDataString(request.Board.BoardId) + "/catalog.html");
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get, endpoint,
            Headers: new Dictionary<string, string> { ["Accept"] = "text/html" },
            Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred)
            return new CatalogDeferred(request.Board, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new CatalogFailure(request.Board, failure.Failure);
        if (result is not HostHttpResponse response) return new CatalogFailure(request.Board, InvalidHttpResult());
        if (!SameEndpoint(endpoint, response.FinalUri))
            return new CatalogFailure(request.Board, UnexpectedEndpoint(response.StatusCode));
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasHttpValidator(request.Validators)
                ? new CatalogUnchanged(request.Board, request.Validators!)
                : new CatalogFailure(request.Board, MissingBaseline(304));
        if (response.StatusCode == 404)
            return new CatalogFailure(request.Board, new FailureDetail("board_unavailable",
                "The documented 7chan catalog was not found.", 404));
        if (response.StatusCode != 200) return new CatalogFailure(request.Board, HttpFailure(response.StatusCode));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes)
            return new CatalogFailure(request.Board, TooLarge());
        try
        {
            string html = Decode(response.Body);
            CatalogSnapshot snapshot = new CatalogSnapshot(new SourceCatalogSnapshot(request.Board,
                ParseCatalog(request.Board, html).AsReadOnly(), IsComplete: false, Validators: response.Validators));
            return SdkValidation.ValidateCatalogResult(request, snapshot, Description()).Count == 0
                ? snapshot
                : new CatalogFailure(request.Board, InvalidPayload());
        }
        catch (Exception error) when (PayloadError(error))
        {
            return new CatalogFailure(request.Board, InvalidPayload());
        }
    }

    public async Task<ThreadReadResult> ReadThreadAsync(ThreadReadRequest request, PluginCallContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Thread.SourceId != SourceId || !BoardValid(request.Thread.BoardId)
            || !DecimalIdValid(request.Thread.ThreadId))
            return new ThreadFailure(request.Thread, InvalidResource());

        Uri endpoint = ThreadUri(request.Thread.BoardId, request.Thread.ThreadId);
        HostHttpResult result = await context.Http.SendAsync(new HostHttpRequest(SourceId, HostHttpMethod.Get, endpoint,
            Headers: new Dictionary<string, string> { ["Accept"] = "text/html" },
            Validators: request.HasSnapshot ? request.Validators : null), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result is HostHttpDeferred deferred)
            return new ThreadDeferred(request.Thread, deferred.RetryAt, deferred.ReasonCode, deferred.HttpStatusCode);
        if (result is HostHttpFailure failure) return new ThreadFailure(request.Thread, failure.Failure);
        if (result is not HostHttpResponse response) return new ThreadFailure(request.Thread, InvalidHttpResult());
        if (!SameEndpoint(endpoint, response.FinalUri))
            return new ThreadFailure(request.Thread, UnexpectedEndpoint(response.StatusCode));
        if (response.StatusCode == 404) return new ThreadNotFound(request.Thread, 404);
        if (response.StatusCode == 304)
            return request.HasSnapshot && HasHttpValidator(request.Validators)
                ? new ThreadUnchanged(request.Thread, request.Validators!)
                : new ThreadFailure(request.Thread, MissingBaseline(304));
        if (response.StatusCode != 200) return new ThreadFailure(request.Thread, HttpFailure(response.StatusCode));
        if (response.Body.Length > SdkLimits.MaximumMetadataBytes)
            return new ThreadFailure(request.Thread, TooLarge());
        try
        {
            ThreadSnapshot snapshot = ParseThread(request.Thread, Decode(response.Body), response.Validators);
            return SdkValidation.ValidateThreadResult(request, snapshot, Description()).Count == 0
                ? snapshot
                : new ThreadFailure(request.Thread, InvalidPayload());
        }
        catch (Exception error) when (PayloadError(error))
        {
            return new ThreadFailure(request.Thread, InvalidPayload());
        }
    }

    private static List<SourceThreadReference> ParseCatalog(SourceBoardId board, string html)
    {
        List<SourceThreadReference> threads = new List<SourceThreadReference>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match anchor in CatalogAnchor.Matches(html))
        {
            Uri? url = SameHostHttps(anchor.Groups["href"].Value, out Uri? candidate) ? candidate : null;
            if (url is null) continue;
            Match route = Regex.Match(url.AbsolutePath,
                @"^/(?<board>[A-Za-z0-9_]+)/res/(?<thread>[0-9]+)\.html$", RegexOptions.CultureInvariant);
            if (!route.Success || !string.Equals(route.Groups["board"].Value, board.BoardId, StringComparison.Ordinal)) continue;
            string id = route.Groups["thread"].Value;
            if (!DecimalIdValid(id) || !seen.Add(id)) continue;
            string body = anchor.Groups["body"].Value;
            string? subject = ClassText(body, "catsubject");
            string? teaser = ClassInner(body, "catteaser");
            int? replies = ClassInteger(body, "catreplies");
            int? files = ClassInteger(body, "catimages");
            threads.Add(new SourceThreadReference(new SourceThreadId(SourceId, board.BoardId, id),
                ThreadUri(board.BoardId, id), subject, teaser, SourceTextFormat.Html,
                ReplyCount: replies, AttachmentCount: files));
            if (threads.Count > SdkLimits.MaximumCollectionCount)
                throw new InvalidDataException("Catalog exceeds the SDK collection limit.");
        }
        if (threads.Count == 0 && !html.Contains("kusaba.", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("cattile", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The response has no documented 7chan catalog shape.");
        return threads;
    }

    private static ThreadSnapshot ParseThread(SourceThreadId thread, string html, SourceValidators? validators)
    {
        List<SourcePost> posts = new List<SourcePost>();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in PostOpen.Matches(html))
        {
            string postId = match.Groups["id"].Value;
            if (!DecimalIdValid(postId) || !seen.Add(postId))
                throw new InvalidDataException("The response contains a duplicate or invalid post identity.");
            string? postHtml = ExtractElement(html, match.Index, "div");
            if (postHtml is null) throw new InvalidDataException("An observed post element is unbalanced.");
            posts.Add(ParsePost(thread, postId, postHtml, posts.Count));
        }
        if (posts.Count == 0 || posts[0].PostId != thread.ThreadId)
            throw new InvalidDataException("The response does not contain the requested thread opener.");
        return new ThreadSnapshot(new SourceThreadSnapshot(thread, ThreadUri(thread.BoardId, thread.ThreadId),
            posts[0].Subject, SourceThreadState.Active, posts.AsReadOnly(), validators));
    }

    private static SourcePost ParsePost(SourceThreadId thread, string postId, string html, long ordinal)
    {
        SourceFieldPresence fields = SourceFieldPresence.None;
        string? subject = ClassText(html, "subject");
        string? author = ClassText(html, "postername");
        string? text = ClassInner(html, "message");
        if (subject is not null) fields |= SourceFieldPresence.Subject;
        if (author is not null) fields |= SourceFieldPresence.Author;
        if (text is not null) fields |= SourceFieldPresence.Text;
        List<SourceAttachment> attachments = ParseAttachments(thread, postId, html);
        if (attachments.Count != 0) fields |= SourceFieldPresence.Attachments;
        return new SourcePost(postId, ordinal, fields, Subject: subject, Author: author, Text: text,
            TextFormat: SourceTextFormat.Html, Attachments: attachments.Count == 0 ? null : attachments.AsReadOnly());
    }

    private static List<SourceAttachment> ParseAttachments(SourceThreadId thread, string postId, string html)
    {
        List<SourceAttachment> attachments = new List<SourceAttachment>();
        // A file is linked several times (name, download icon, thumbnail); one original is one attachment.
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        List<Match> anchors = Anchors(html).ToList();
        // The poster's file name is only printed in the download icon's attribute; the first link of a file in a
        // multi-file post reads "pdf (5.75MB)" (type and size), which is not a file name.
        Dictionary<string, string> downloadNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match anchor in anchors)
        {
            string? download = AttributeValue(anchor.Groups["attrs"].Value, "download");
            string? target = AttributeValue(anchor.Groups["attrs"].Value, "href");
            if (!string.IsNullOrWhiteSpace(download) && target is not null
                && Uri.TryCreate(Origin, WebUtility.HtmlDecode(target).Trim(), out Uri? linked) && linked is not null)
                downloadNames.TryAdd(linked.AbsoluteUri, WebUtility.HtmlDecode(download).Trim());
        }
        Dictionary<string, SourceSizeHint> sizeHints = SizeHints(anchors, html);
        foreach (Match anchor in anchors)
        {
            string href = AttributeValue(anchor.Groups["attrs"].Value, "href") ?? string.Empty;
            if (!Uri.TryCreate(Origin, WebUtility.HtmlDecode(href).Trim(), out Uri? candidate)
                || candidate is null || candidate.Scheme != Origin.Scheme || candidate.IdnHost != Host
                || candidate.Query.Length != 0 || candidate.Fragment.Length != 0
                || !candidate.AbsolutePath.StartsWith("/" + thread.BoardId + "/src/", StringComparison.Ordinal)
                || !SafeMediaPath(candidate.AbsolutePath)) continue;
            if (!seen.Add(candidate.AbsoluteUri)) continue;
            string fileName = candidate.AbsolutePath[(candidate.AbsolutePath.LastIndexOf('/') + 1)..];
            if (fileName.Length == 0) throw new InvalidDataException("An original media link has no file name.");
            string? extension = Extension(fileName);
            string? displayName = StripTags(anchor.Groups["body"].Value).Trim();
            downloadNames.TryGetValue(candidate.AbsoluteUri, out string? downloadName);
            attachments.Add(new SourceAttachment(attachments.Count,
                postId + "-" + attachments.Count.ToString(CultureInfo.InvariantCulture),
                Url: candidate, OriginalFileName: FileNameOf(extension, displayName, downloadName, fileName),
                Extension: extension) { ProviderSizeHint = sizeHints.GetValueOrDefault(candidate.AbsoluteUri) });
            if (attachments.Count > 1024) throw new InvalidDataException("Too many attachments.");
        }
        return attachments;
    }

    // 7chan prints a rounded size, always in KiB/MiB/GiB steps: "(355.92KB, 2048x1536, a.jpg)" after a single file
    // and "pdf (5.75MB)" as the text of the first link of a file in a multi-file post. It is an annotation, not an
    // exact byte count, so it is reported as a rounded size hint and never as ExpectedSize.
    private static readonly Regex DisplayedSize = new Regex(
        @"\(\s*(?<value>[0-9]{1,12}(?:\.[0-9]{1,3})?)\s*(?<unit>[KMG]?B)\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DashedSize = new Regex(@"(?<![\w.])-\s*" + DisplayedSize.ToString(),
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TypeAndSizeLabel = new Regex(
        @"^[A-Za-z0-9]{1,32}\s*\(\s*[0-9]{1,12}(?:\.[0-9]{1,3})?\s*[KMG]?B\s*\)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Dictionary<string, SourceSizeHint> SizeHints(List<Match> anchors, string html)
    {
        Dictionary<string, SourceSizeHint> hints = new Dictionary<string, SourceSizeHint>(StringComparer.Ordinal);
        void Add(string? href, string text, Regex pattern)
        {
            Match size = pattern.Match(text);
            if (!size.Success || href is null
                || !Uri.TryCreate(Origin, WebUtility.HtmlDecode(href).Trim(), out Uri? target) || target is null) return;
            hints.TryAdd(target.AbsoluteUri,
                new SourceSizeHint(size.Groups["value"].Value, size.Groups["unit"].Value, SourceSizePrecision.Rounded));
        }
        // Multi-file post: the text of the first link is the whole label.
        foreach (Match anchor in anchors)
        {
            string label = StripTags(anchor.Groups["body"].Value).Trim();
            if (TypeAndSizeLabel.IsMatch(label)) Add(AttributeValue(anchor.Groups["attrs"].Value, "href"), label, DisplayedSize);
        }
        // Single-file post: the file_size paragraph holds the file link and, after it, the size.
        foreach (Match block in Regex.Matches(html,
            @"<p\b(?=[^>]*\bclass\s*=\s*[""'][^""']*\bfile_size\b[^""']*[""'])[^>]*>(?<body>.*?)</p\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            Match? first = Anchors(block.Groups["body"].Value).FirstOrDefault();
            if (first is not null) Add(AttributeValue(first.Groups["attrs"].Value, "href"), StripTags(block.Groups["body"].Value), DashedSize);
        }
        return hints;
    }

    private static IEnumerable<Match> Anchors(string html)
        => Regex.Matches(html, @"<a\b(?<attrs>[^>]*?)>(?<body>.*?)</a\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline).Cast<Match>();

    private static string? ClassInner(string html, string className)
    {
        Match match = Regex.Match(html,
            @"<(?<tag>[A-Za-z][A-Za-z0-9]*)\b(?=[^>]*\bclass\s*=\s*[""'][^""']*\b" + Regex.Escape(className)
            + @"\b[^""']*[""'])[^>]*>(?<body>.*?)</\k<tag>\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["body"].Value.Trim() : null;
    }

    private static string? ClassText(string html, string className)
    {
        string? value = ClassInner(html, className);
        return value is null ? null : WebUtility.HtmlDecode(StripTags(value)).Trim();
    }

    private static int? ClassInteger(string html, string className)
    {
        string? value = ClassText(html, className);
        if (value is null) return null;
        Match number = Regex.Match(value, @"(?<![0-9])[0-9]+(?![0-9])", RegexOptions.CultureInvariant);
        return number.Success && int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            && parsed >= 0 ? parsed : null;
    }

    private static string? AttributeValue(string attributes, string name)
    {
        foreach (Match match in Attribute.Matches(attributes))
            if (string.Equals(match.Groups["name"].Value, name, StringComparison.OrdinalIgnoreCase))
                return match.Groups["value"].Value;
        return null;
    }

    private static string? ExtractElement(string html, int index, string tag)
    {
        Match open = Regex.Match(html[index..], @"^<" + tag + @"\b[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!open.Success) return null;
        int contentStart = index + open.Length;
        int depth = 1;
        Regex token = new Regex(@"</?" + tag + @"\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        foreach (Match item in token.Matches(html, contentStart))
        {
            if (item.Value.StartsWith("</", StringComparison.Ordinal)) depth--;
            else if (!item.Value.EndsWith("/>", StringComparison.Ordinal)) depth++;
            if (depth == 0) return html[contentStart..item.Index];
        }
        return null;
    }

    private static bool SameHostHttps(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(Origin, WebUtility.HtmlDecode(value).Trim(), out Uri? candidate)
            || candidate is null || candidate.Scheme != Origin.Scheme || candidate.IdnHost != Host
            || candidate.Query.Length != 0 || candidate.Fragment.Length != 0 || candidate.UserInfo.Length != 0)
            return false;
        uri = candidate;
        return true;
    }

    private static bool SafeMediaPath(string path)
    {
        string file = path[(path.LastIndexOf('/') + 1)..];
        return file.Length != 0 && file != "." && file != ".." && !file.Contains("..", StringComparison.Ordinal);
    }

    private static string StripTags(string value) => Regex.Replace(value, "<[^>]*>", string.Empty,
        RegexOptions.Singleline);
    private static string Decode(byte[] body) => Encoding.UTF8.GetString(body);
    private static string? Extension(string name)
    {
        int dot = name.LastIndexOf('.');
        string? extension = dot > 0 && dot < name.Length - 1 ? name[dot..] : null;
        return extension is not null && Regex.IsMatch(extension, @"^\.[A-Za-z0-9]{1,32}$", RegexOptions.CultureInvariant)
            ? extension : null;
    }

    // The first name that ends in the extension of the linked file: the link text (a plain file name for a single
    // file), the download attribute, then the link's own file name. "pdf (5.75MB)" ends in ".75MB)" and is skipped.
    private static string FileNameOf(string? extension, string? displayName, string? downloadName, string urlFileName)
    {
        foreach (string? candidate in new[] { displayName, downloadName })
            if (!string.IsNullOrEmpty(candidate) && candidate.Length <= 255
                && candidate.IndexOfAny(['/', '\\', '\0', '\r', '\n']) < 0
                && (extension is null
                    ? Extension(candidate) is null
                    : candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && candidate.Length > extension.Length))
                return candidate;
        string decoded = Uri.UnescapeDataString(urlFileName);
        return decoded.IndexOfAny(['/', '\\', '\0', '\r', '\n']) < 0 && decoded.Length <= 255 ? decoded : urlFileName;
    }

    private static Uri ThreadUri(string board, string thread)
        => new Uri(Origin, Uri.EscapeDataString(board) + "/res/" + Uri.EscapeDataString(thread) + ".html");
    private static bool SameEndpoint(Uri expected, Uri actual)
        => actual.IsAbsoluteUri && actual.Scheme == expected.Scheme && actual.IdnHost == expected.IdnHost
            && actual.Port == expected.Port && actual.AbsolutePath == expected.AbsolutePath
            && actual.Query == expected.Query && actual.Fragment.Length == 0 && actual.UserInfo.Length == 0;
    private static bool BoardValid(string board)
        => !string.IsNullOrWhiteSpace(board) && board.Length <= 64
            && board.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    private static bool DecimalIdValid(string id)
        => !string.IsNullOrEmpty(id) && id.Length <= SdkLimits.MaximumIdentifierLength
            && id.All(c => c is >= '0' and <= '9') && (id.Length == 1 || id[0] != '0');
    private static bool HasHttpValidator(SourceValidators? validators)
        => !string.IsNullOrWhiteSpace(validators?.EntityTag) || validators?.LastModified is not null
            || !string.IsNullOrWhiteSpace(validators?.Revision);
    private static FailureDetail InvalidResource() => new FailureDetail("invalid_resource", "Source, board or thread identity is invalid.");
    private static FailureDetail InvalidHttpResult() => new FailureDetail("invalid_http_result", "Unknown host HTTP result.");
    private static FailureDetail UnexpectedEndpoint(int? status) => new FailureDetail("unexpected_endpoint", "The response is not from the requested 7chan metadata endpoint.", status);
    private static FailureDetail MissingBaseline(int status) => new FailureDetail("missing_baseline", "304 requires an accepted resource and its HTTP validator.", status);
    private static FailureDetail HttpFailure(int status) => new FailureDetail("http_failure", "The 7chan HTML response was not a verified resource result.", status);
    private static FailureDetail InvalidPayload() => new FailureDetail("invalid_payload", "The HTML response has no supported bounded 7chan shape.", 200);
    private static FailureDetail TooLarge() => new FailureDetail("metadata_too_large", "The metadata response exceeds the SDK body limit.", 200);
    private static bool PayloadError(Exception error) => error is InvalidDataException or ArgumentException or FormatException;
}
