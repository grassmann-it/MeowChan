using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal BooruSearchTabStore CreateBooruSearchTabStore() => new(_profileLease, _logger);
}
