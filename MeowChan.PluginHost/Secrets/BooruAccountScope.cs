using System.Security.Cryptography;
using System.Text;
using MeowChan.Booru;

namespace MeowChan.PluginHost.Secrets;

/// <summary>Host-approved owner and destination of one account. Worker settings never supply this scope.</summary>
internal sealed record BooruAccountScope
{
    private BooruAccountScope(string pluginId, string executionRevision, string sourceId, string instanceId, string origin)
    {
        PluginId = pluginId;
        ExecutionRevision = executionRevision;
        SourceId = sourceId;
        InstanceId = instanceId;
        Origin = origin;
    }

    internal string PluginId { get; }
    internal string ExecutionRevision { get; }
    internal string SourceId { get; }
    internal string InstanceId { get; }
    internal string Origin { get; }

    internal static BooruAccountScope Create(string pluginId, string executionRevision,
        string sourceId, string instanceId, Uri canonicalOrigin)
    {
        string? origin = HttpsOrigin(canonicalOrigin);
        if (origin is null || canonicalOrigin.PathAndQuery != "/"
            || !BooruPostKey.IsToken(pluginId) || !BooruPostKey.IsToken(executionRevision)
            || !BooruPostKey.IsValidScope(sourceId, instanceId))
            throw new ArgumentException("The Booru account scope is invalid.");

        return new(pluginId, executionRevision, sourceId, instanceId, origin);
    }

    internal bool MatchesOrigin(Uri uri) => HttpsOrigin(uri) == Origin;

    internal byte[] Fingerprint() =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', PluginId, ExecutionRevision, SourceId, InstanceId, Origin)));

    private static string? HttpsOrigin(Uri? uri) =>
        uri is { IsAbsoluteUri: true, IsDefaultPort: true } && uri.Scheme == Uri.UriSchemeHttps
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.IdnHost.Length != 0
        ? Uri.UriSchemeHttps + "://" + uri.IdnHost : null;
}
