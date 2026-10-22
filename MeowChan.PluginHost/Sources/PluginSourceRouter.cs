using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Sources;

/// <summary>Bounded declarative routing. This type never calls plugin code.</summary>
public sealed class PluginSourceRouter(SourceDescription description)
{
    public bool TryResolve(Uri uri, out SourceResourceKind kind, out string board, out string? thread,
        out Uri? canonical)
    {
        kind = default; board = string.Empty; thread = null; canonical = null;
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0) return false;
        foreach (UrlRoute route in description.Routes)
        {
            if (!string.Equals(uri.IdnHost, route.Host, StringComparison.OrdinalIgnoreCase)) continue;
            string path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
            if (path.EndsWith('/') && !route.AllowTrailingSlash) continue;
            string[] actual = path.TrimEnd('/').Split('/');
            string[] expected = route.PathTemplate.Trim('/').Split('/');
            if (actual.Length != expected.Length && !(route.AllowTitleSegment && actual.Length == expected.Length + 1)) continue;
            string? candidateBoard = null, candidateThread = null;
            bool match = true;
            for (int index = 0; index < expected.Length; index++)
            {
                string template = expected[index];
                string value = Uri.UnescapeDataString(actual[index]);
                int start = template.IndexOf('{');
                if (start < 0) { if (value != template) match = false; continue; }
                int end = template.IndexOf('}', start);
                string prefix = template[..start], suffix = template[(end + 1)..];
                if (!value.StartsWith(prefix, StringComparison.Ordinal) || !value.EndsWith(suffix, StringComparison.Ordinal)
                    || value.Length <= prefix.Length + suffix.Length) { match = false; break; }
                string identity = value.Substring(prefix.Length, value.Length - prefix.Length - suffix.Length);
                if (!SafeIdentity(identity)) { match = false; break; }
                if (template[start..(end + 1)] == "{board}") candidateBoard = route.LowercaseBoardId ? identity.ToLowerInvariant() : identity;
                else candidateThread = identity;
            }
            if (!match || candidateBoard == null || route.Kind == SourceResourceKind.Thread && candidateThread == null) continue;
            kind = route.Kind; board = candidateBoard; thread = candidateThread;
            canonical = Canonical(kind, board, thread);
            return true;
        }
        return false;
    }

    public Uri Canonical(SourceResourceKind kind, string board, string? thread = null)
    {
        if (!SafeIdentity(board) || thread != null && !SafeIdentity(thread)) throw new ArgumentException("Invalid source resource identity.");
        UrlRoute route = description.Routes.Single(value => value.IsCanonical && value.Kind == kind);
        string path = route.PathTemplate.Replace("{board}", Uri.EscapeDataString(board), StringComparison.Ordinal)
            .Replace("{thread}", Uri.EscapeDataString(thread ?? string.Empty), StringComparison.Ordinal);
        return new Uri("https://" + route.Host + path);
    }

    private static bool SafeIdentity(string value) => value.Length is > 0 and <= 128
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
