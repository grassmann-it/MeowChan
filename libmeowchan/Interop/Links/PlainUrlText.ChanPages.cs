using System.Text.RegularExpressions;

namespace MeowChan.Interop.Links;

internal sealed partial class PlainUrlText
{
    // Deliberately narrower than SDK routing. Match the ORIGINAL text, not Uri's
    // normalized host/path: aliases, ports, queries, escapes and title segments
    // must not turn into apparently safe canonical identities.
    private static readonly Regex FourChanPage = new(
        @"\Ahttps://boards\.4chan\.org/[a-z0-9_]{1,256}/(?:thread/[0-9]{1,256}(?:#p[0-9]{1,256})?)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex EightKunPage = new(
        @"\Ahttps://8kun\.top/[a-z0-9_]{1,256}/(?:res/[0-9]{1,256}\.html(?:#[0-9]{1,256})?)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Built-in public page grammar, not a configurable arbitrary-URL attestation.
    /// Boards are Source; thread pages (with or without a numeric anchor) are Post.
    /// Post describes a page link, not proof that an anchored post exists. SDK route
    /// matching ignores fragments; these restricted inert anchors remain unchanged.
    /// Neither support nor safety implies admission, live availability or acquisition.
    /// </summary>
    internal static PlainUrlText ForSupportedChanPages() => new(
        value => ClassifyChanPage(value) != PassiveLinkKind.Unknown, ClassifyChanPage);

    private static PassiveLinkKind ClassifyChanPage(string value)
    {
        if (!FourChanPage.IsMatch(value) && !EightKunPage.IsMatch(value))
            return PassiveLinkKind.Unknown;
        return value.EndsWith("/", System.StringComparison.Ordinal)
            ? PassiveLinkKind.Source : PassiveLinkKind.Post;
    }
}
