namespace MeowChan.PluginSdk;

public enum SourceResourceKind { Board, Thread }

/// <summary>
/// Declarative exact path template. Only {board} and {thread} placeholders are supported.
/// Placeholders occupy one nonempty escaped path segment (a literal suffix is allowed).
/// Hosts are exact; no regular expressions, query templates or callbacks are executable here.
/// A source declares exactly one canonical route per supported resource kind. Aliases are
/// additional routes; host routing ignores query and fragment and emits the canonical route.
/// Trailing slash and an optional inert title segment are explicit alias options. Board
/// case normalization is explicit and never applied to opaque thread identities.
/// </summary>
public sealed record UrlRoute(SourceResourceKind Kind, string Host, string PathTemplate,
    bool IsCanonical = false, bool AllowTrailingSlash = true, bool AllowTitleSegment = false,
    bool LowercaseBoardId = false);
