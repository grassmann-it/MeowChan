using System;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>Gives the gallery exclusive ownership of media playback while it is opening or visible.</summary>
internal sealed class PlaybackArbitrationGate
{
    private readonly object _sync = new();
    private Task _releaseTask = Task.CompletedTask;
    private bool _galleryOwnsPlayback;

    internal bool GalleryOwnsPlayback
    {
        get { lock (_sync) return _galleryOwnsPlayback; }
    }

    /// <summary>Claims playback for the gallery and releases the competing inline players once per handoff.</summary>
    internal Task EnterGalleryAsync(Func<Task> releaseInlinePlayers)
    {
        ArgumentNullException.ThrowIfNull(releaseInlinePlayers);
        lock (_sync)
        {
            if (_galleryOwnsPlayback) return _releaseTask;
            _galleryOwnsPlayback = true;
            _releaseTask = releaseInlinePlayers();
            return _releaseTask;
        }
    }

    /// <summary>Returns ownership to the inline surfaces after the gallery has closed.</summary>
    internal void ExitGallery()
    {
        lock (_sync)
        {
            _galleryOwnsPlayback = false;
            _releaseTask = Task.CompletedTask;
        }
    }
}
