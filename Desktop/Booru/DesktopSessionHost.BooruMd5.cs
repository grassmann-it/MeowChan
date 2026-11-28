using MeowChan.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    /// <summary>The effective filter for MD5 matches; null (no lookup) unless a choice is in force.</summary>
    internal BooruContentFilter? ReadBooruMd5ContentFilter() =>
        ReadEffectiveBooruContent() is { State: BooruContentProfileState.Ready } profile ? profile.Filter : null;
}
