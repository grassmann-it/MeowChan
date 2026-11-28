using MeowChan.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal BooruContentProfileSnapshot ReadBooruMd5ContentProfile() => new BooruContentProfileStore(_profileLease).Load();
}
