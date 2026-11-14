using System;
using System.IO;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    /// <summary>
    /// The durable id of this profile (<see cref="ProfileInstanceIdentity"/>), created once when the profile has none.
    /// Null when the identity file is damaged (it is kept, never replaced) or cannot be written now; callers then work
    /// without an id.
    /// </summary>
    internal ProfileInstanceIdentity? EnsureProfileIdentity()
    {
        try { return ProfileInstanceIdentity.ReadOrCreate(_profileLease); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or ObjectDisposedException)
        {
            _logger.Warning(error, "The profile identity is unavailable.");
            return null;
        }
    }
}
