using MeowChan.Desktop.Booru;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    /// <summary>
    /// The ports behind the Danbooru window. They are supplied by the code that owns the host-side search service,
    /// the download queue and the watcher scheduler; until that wiring exists this stays <c>null</c> and the menu
    /// entry stays hidden. The UI never creates a queue, a store or a scheduler of its own.
    /// </summary>
    internal BooruWorkbenchServices? BooruServices { get; set; }
}
