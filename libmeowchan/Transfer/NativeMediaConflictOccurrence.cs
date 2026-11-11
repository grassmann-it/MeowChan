using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Storage;

namespace MeowChan.Transfer;

/// <summary>
/// Slot digests for the conflict ledger. V2 names the slot (resource, post, position) only, so the same
/// alternative bytes seen under different provider tuples (attachment revision keys) are ONE ledger row.
/// V1 additionally hashed the original revision key; rows recorded under V1 keep their identity and are
/// still recognised, so a decided V1 conflict never reopens as a V2 duplicate.
/// </summary>
internal static class NativeMediaConflictOccurrence
{
    internal static string V1(MediaOccurrence occurrence) => Hash("native-media-conflict-occurrence-v1", occurrence, occurrence.OriginalRevisionKey ?? "");
    internal static string V2(MediaOccurrence occurrence) => Hash("native-media-conflict-occurrence-v2", occurrence, null);

    private static string Hash(string domain, MediaOccurrence o, string? revision)
    {
        string[] parts = revision == null
            ? [domain, o.Resource.Source, o.Resource.Board, o.Resource.Thread ?? "", o.PostId, o.Position.ToString(CultureInfo.InvariantCulture)]
            : [domain, o.Resource.Source, o.Resource.Board, o.Resource.Thread ?? "", o.PostId, o.Position.ToString(CultureInfo.InvariantCulture), revision];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts)))).ToLowerInvariant();
    }
}
