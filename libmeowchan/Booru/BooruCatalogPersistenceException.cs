using System;
using System.IO;

namespace MeowChan.Booru;

/// <summary>
/// Marks a failure at the catalog write which records a Booru admission. Queue failures deliberately do not use
/// this type: the watcher scheduler can therefore distinguish local persistence rejection from queue admission refusal.
/// </summary>
internal sealed class BooruCatalogPersistenceException : IOException
{
    internal BooruCatalogPersistenceException(Exception innerException)
        : this(innerException, refusalPending: false)
    {
    }

    internal BooruCatalogPersistenceException(Exception innerException, bool refusalPending)
        : base("The Booru catalog rejected an admission write.", innerException)
    {
        RefusalPending = refusalPending;
    }

    internal bool RefusalPending { get; }

    /// <summary>
    /// Exceptions that the catalog exposes for a failed store mutation. This predicate is used only immediately
    /// around the catalog write; it must not be applied to the whole queue-admission operation.
    /// </summary>
    internal static bool IsStoreWriteFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or InvalidOperationException
        or ObjectDisposedException
        or ArgumentException;
}
