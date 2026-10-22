using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources;

/// <summary>Bounded declarative routing. This type never calls plugin code.</summary>
public sealed class PluginSourceRouter(SourceDescription description)
{
    public bool TryResolve(Uri uri, out SourceResourceKind kind, out string board, out string? thread, out Uri? canonical)
    {
        kind = default; board = string.Empty; thread = null; canonical = null;
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || (description.BaseUri.IsDefaultPort ? !uri.IsDefaultPort : uri.Port != description.BaseUri.Port)) return false;
        foreach (UrlRoute route in description.Routes)
        {
            if (!string.Equals(uri.IdnHost, route.Host, StringComparison.OrdinalIgnoreCase)) continue;
            string path = uri.AbsolutePath;
            if (!route.AllowTrailingSlash && path.EndsWith('/') != route.PathTemplate.EndsWith('/')) continue;
            if (path.EndsWith("//", StringComparison.Ordinal)) continue;
            string[] actual = path.Trim('/').Split('/');
            string[] expected = route.PathTemplate.Trim('/').Split('/');
            if (actual.Length != expected.Length && !(route.AllowTitleSegment && actual.Length == expected.Length + 1)) continue;
            string? candidateBoard = null, candidateThread = null;
            bool match = true;
            for (int index = 0; index < expected.Length; index++)
            {
                string template = expected[index];
                string value = actual[index];
                int start = template.IndexOf('{');
                if (start < 0) { if (value != NormalizeLiteral(template)) match = false; continue; }
                int end = template.IndexOf('}', start);
                string prefix = NormalizeLiteral(template[..start]), suffix = NormalizeLiteral(template[(end + 1)..]);
                if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(suffix, StringComparison.Ordinal)
                    || value.Length <= prefix.Length + suffix.Length) { match = false; break; }
                string identity = Uri.UnescapeDataString(value.Substring(prefix.Length, value.Length - prefix.Length - suffix.Length));
                bool boardSegment = template[start..(end + 1)] == "{board}";
                if (!ValidIdentity(identity, boardSegment ? route.BoardIdentifierKind : route.ThreadIdentifierKind)) { match = false; break; }
                if (boardSegment) candidateBoard = route.LowercaseBoardId ? identity.ToLowerInvariant() : identity;
                else candidateThread = identity;
            }
            if (!match || candidateBoard == null || route.Kind == SourceResourceKind.Thread && candidateThread == null) continue;
            kind = route.Kind;
            UrlRoute primary = description.Routes.Single(value => value.IsCanonical && value.Kind == route.Kind);
            board = primary.LowercaseBoardId ? candidateBoard.ToLowerInvariant() : candidateBoard;
            thread = candidateThread;
            try { canonical = Canonical(kind, board, thread); }
            catch (ArgumentException) { continue; }
            return true;
        }
        return false;
    }

    public Uri Canonical(SourceResourceKind kind, string board, string? thread = null)
    {
        UrlRoute route = description.Routes.Single(value => value.IsCanonical && value.Kind == kind);
        if (!ValidIdentity(board, route.BoardIdentifierKind) || thread != null && !ValidIdentity(thread, route.ThreadIdentifierKind))
            throw new ArgumentException("Invalid source resource identity.");
        string template = string.Join('/', route.PathTemplate.Split('/').Select(segment =>
        {
            int start = segment.IndexOf('{'), end = segment.IndexOf('}');
            return start < 0 ? NormalizeLiteral(segment) : NormalizeLiteral(segment[..start]) + segment[start..(end + 1)] + NormalizeLiteral(segment[(end + 1)..]);
        }));
        string path = template.Replace("{board}", Uri.EscapeDataString(route.LowercaseBoardId ? board.ToLowerInvariant() : board), StringComparison.Ordinal)
            .Replace("{thread}", Uri.EscapeDataString(thread ?? string.Empty), StringComparison.Ordinal);
        if (path.Split('/').Any(segment => Uri.UnescapeDataString(segment) is "." or "..")) throw new ArgumentException("Dot segments cannot identify a source resource.");
        string authority = description.BaseUri.Scheme + "://" + route.Host + (description.BaseUri.IsDefaultPort ? "" : ":" + description.BaseUri.Port);
        return new Uri(authority + path);
    }

    private static string NormalizeLiteral(string value) => new Uri("https://route.invalid/_" + value + "_").AbsolutePath[2..^1];
    private static bool ValidIdentity(string value, RouteIdentifierKind kind) => value.Length is > 0 and <= SdkLimits.MaximumIdentifierLength
        && value == value.Trim() && !value.Any(char.IsControl) && kind switch
        {
            RouteIdentifierKind.Decimal => value.All(character => character is >= '0' and <= '9'),
            RouteIdentifierKind.AsciiWord => value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'),
            _ => true
        };
}
