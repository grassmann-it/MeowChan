using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

/// <summary>
/// What an export or transfer surface must say before it runs: the private categories no package carries in this
/// slice (own tags of threads, bound media and loose local files). It never implies a round trip.
/// </summary>
internal static class PrivateTransferDisclosure
{
    internal static string Key(NativePrivateMetadataOmission omission) => omission switch
    {
        NativePrivateMetadataOmission.PrivateTags => "tags.export.omitted",
        _ => throw new ArgumentOutOfRangeException(nameof(omission))
    };

    /// <summary>Localized disclosure lines, one per omitted category, in a stable order.</summary>
    internal static IReadOnlyList<string> Lines() => NativePrivateMetadataSnapshot.Omissions.Select(item => Texts.T(Key(item))).ToArray();
}
