using System;
using System.Threading;

namespace MeowChan.Tags;

/// <summary>
/// The configurable write limits of the private tag store (user settings), shared by every store instance of one
/// profile. Reads are bounded only by the fixed technical ceilings <see cref="PrivateTagStore.MaximumTags"/> and
/// <see cref="PrivateTagStore.MaximumBytes"/>, so lowering a limit never makes an existing file unreadable: it only
/// stops new tags (count) or further growth of the file (bytes). Thread-safe; a change applies to the next write.
/// </summary>
internal sealed class PrivateTagLimits
{
    /// <summary>Default number of own tags (the fixed limit before it became a setting).</summary>
    internal const int DefaultTagLimit = 1024;
    internal const int MinimumTagLimit = 1024;
    internal const int MaximumTagLimit = PrivateTagStore.MaximumTags;
    /// <summary>Default size limit of private-tags.json (the fixed limit before it became a setting).</summary>
    internal const int DefaultDocumentLimitBytes = 32 * 1024 * 1024;
    internal const int MinimumDocumentLimitBytes = 8 * 1024 * 1024;
    internal const int MaximumDocumentLimitBytes = PrivateTagStore.MaximumBytes;

    private int _tagLimit = DefaultTagLimit;
    private long _documentLimitBytes = DefaultDocumentLimitBytes;

    /// <summary>Most own tags a write may leave defined; a new tag at or above it is refused.</summary>
    internal int TagLimit
    {
        get => Volatile.Read(ref _tagLimit);
        set
        {
            if (value is < MinimumTagLimit or > MaximumTagLimit)
                throw new ArgumentOutOfRangeException(nameof(value), "The own tag limit is outside its supported range.");
            Volatile.Write(ref _tagLimit, value);
        }
    }

    /// <summary>Largest size a write may grow private-tags.json to. A write that does not grow the file is always allowed.</summary>
    internal long DocumentLimitBytes
    {
        get => Interlocked.Read(ref _documentLimitBytes);
        set
        {
            if (value is < MinimumDocumentLimitBytes or > MaximumDocumentLimitBytes)
                throw new ArgumentOutOfRangeException(nameof(value), "The own tag file limit is outside its supported range.");
            Interlocked.Exchange(ref _documentLimitBytes, value);
        }
    }
}
