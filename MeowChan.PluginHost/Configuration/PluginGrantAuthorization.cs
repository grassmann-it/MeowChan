using System.Security.Cryptography;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration;

public sealed record PluginGrantAuthorizationSnapshot(FilterDataAccess Access, string Revision)
{
    public bool Allows(FilterDataAccess required) => required != FilterDataAccess.None && (Access & required) == required;
}

public static class PluginGrantAuthorization
{
    /// <summary>Unions explicit matching grants. The token changes on any matching revoke/regrant, including equal access.</summary>
    public static PluginGrantAuthorizationSnapshot Resolve(PluginProfileSnapshot profile, string pluginId, string filterKey,
        string sourceId, string? boardId, string? ruleId)
    {
        PluginGrantSnapshot[] grants = profile.Grants.Where(grant => grant.PluginId == pluginId && grant.FilterKey == filterKey
            && grant.Scope.SourceId == sourceId && (grant.Scope.BoardId is null || grant.Scope.BoardId == boardId)
            && (grant.Scope.RuleId is null || grant.Scope.RuleId == ruleId))
            .OrderBy(grant => grant.Scope.BoardId, StringComparer.Ordinal).ThenBy(grant => grant.Scope.RuleId, StringComparer.Ordinal).ToArray();
        FilterDataAccess access = grants.Aggregate(FilterDataAccess.None, (current, grant) => current | grant.Access);
        return new(access, Convert.ToHexString(SHA256.HashData(SdkJson.Serialize(grants))));
    }
}
