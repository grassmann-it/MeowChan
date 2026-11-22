using System;
using System.IO;
using System.Text.Json;
using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    internal void ConfigureBooruSearchTabTargets(BooruWorkbenchView view)
    {
        BooruSearchTabStore store = new(_profileLease);
        try
        {
            view.ConfigureSearchTabTargets(store.Read(), state =>
            {
                try
                {
                    store.Write(state);
                    return true;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                    or InvalidOperationException or JsonException)
                {
                    _logger.Warning(error, "Could not save Booru search tabs");
                    return false;
                }
            });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or JsonException)
        {
            _logger.Warning(error, "Could not restore Booru search tabs");
        }
    }
}
