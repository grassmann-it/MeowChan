namespace MeowChan.PluginSdk;

public enum SourceResourceKind { Board, Thread }

/// <summary>Declarative alphabet of an extracted identity, excluding route literal affixes.</summary>
public enum RouteIdentifierKind
{
    /// <summary>Existing opaque SDK identity, escaped as one URL path segment.</summary>
    PortableSegment = 0,
    /// <summary>One or more ASCII letters, decimal digits or underscores.</summary>
    AsciiWord = 1,
    /// <summary>One or more ASCII decimal digits; no numeric conversion or size limit is implied.</summary>
    Decimal = 2
}

/// <summary>
/// Declarative exact path template. Only {board} and {thread} placeholders are supported.
/// Placeholders occupy one nonempty escaped path segment (a literal suffix is allowed).
/// Hosts are exact; no regular expressions, query templates or callbacks are executable here.
/// A source declares exactly one canonical route per supported resource kind. Aliases are
/// additional routes; host routing ignores query and fragment and emits the canonical route.
/// Trailing slash and an optional inert title segment are explicit alias options. Board
/// case normalization is explicit and never applied to opaque thread identities.
/// Literal path text uses System.Uri escaping/normalization. A trailing slash option
/// accepts both slash forms; an optional title precedes the trailing slash, if present.
/// A substituted identity must not form a complete dot segment; literal affixes may
/// keep otherwise opaque dot identities representable without removing the segment.
/// </summary>
public sealed record UrlRoute(SourceResourceKind Kind, string Host, string PathTemplate,
    bool IsCanonical = false, bool AllowTrailingSlash = true, bool AllowTitleSegment = false,
    bool LowercaseBoardId = false)
{
    /// <summary>Optional board identity alphabet. The default preserves existing opaque identities.</summary>
    public RouteIdentifierKind BoardIdentifierKind { get; init; } = RouteIdentifierKind.PortableSegment;

    /// <summary>Optional thread identity alphabet, applied only to routes of kind Thread.</summary>
    public RouteIdentifierKind ThreadIdentifierKind { get; init; } = RouteIdentifierKind.PortableSegment;
}
