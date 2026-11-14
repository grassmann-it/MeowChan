using System;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Storage;

/// <summary>
/// Media folders whose manifest is not a file but answered by a catalog: the items of a read-only folder source
/// (<see cref="FolderSources.FolderSourceManifests"/>). A provider owns every folder below its root; <see cref="MediaAssetReader.OpenAsync"/>
/// asks it before reading anything from disk, so every view (thumbnails, gallery, export, revalidation) reads such a file
/// through the ordinary verified path. Other folders are read as before. One registration per profile and process.
/// </summary>
internal static class VirtualMediaManifests
{
    private sealed record Provider(string Root, Func<string, CancellationToken, Task<MediaAssetSnapshot>> Open);

    private static ImmutableArray<Provider> s_providers = ImmutableArray<Provider>.Empty;

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Answers every folder below <paramref name="root"/> with <paramref name="open"/> until the result is disposed.</summary>
    internal static IDisposable Register(string root, Func<string, CancellationToken, Task<MediaAssetSnapshot>> open)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(open);
        Provider provider = new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), open);
        ImmutableInterlocked.Update(ref s_providers, list => list.Add(provider));
        return new Registration(provider);
    }

    /// <summary>True (with the provider's answer) when <paramref name="fullDirectory"/> lies below a registered root.</summary>
    internal static bool TryOpen(string fullDirectory, CancellationToken cancellationToken, out Task<MediaAssetSnapshot> snapshot)
    {
        foreach (Provider provider in s_providers)
            if (fullDirectory.StartsWith(provider.Root + Path.DirectorySeparatorChar, Comparison))
            {
                snapshot = provider.Open(fullDirectory, cancellationToken);
                return true;
            }
        snapshot = null!;
        return false;
    }

    private sealed class Registration(Provider provider) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                ImmutableInterlocked.Update(ref s_providers, list => list.Remove(provider));
        }
    }
}
