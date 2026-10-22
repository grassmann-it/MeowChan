using MeowChan.Boards.Base;
using MeowChan.Interfaces;

namespace MeowChan.PluginHost.Sources;

/// <summary>Optional host attachment points; the core remains independent of this assembly.</summary>
public static class PluginRegistryAdapter
{
    public static Task RegisterBeforeRestoreAsync(Registry registry, IEnumerable<IImageBoard> providers,
        CancellationToken cancellationToken = default) => registry.RegisterProvidersBeforeRestoreAsync(providers, cancellationToken);

    public static long GetWatchGeneration(IThread thread) => thread is BaseThread owned ? owned.WatchGeneration : 0;
}
