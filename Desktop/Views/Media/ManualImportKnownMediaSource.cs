using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Import;

namespace MeowChan.Desktop;

/// <summary>
/// What the library holds, for the import dialog's dedupe check: built once and handed to every check of the dialog
/// session, so adding a file or flipping a switch does not rebuild the media index. <see cref="Invalidate"/> (after an
/// import changed the library) makes the next check build again. A caller's token only ends its own wait; the build
/// belongs to the source and ends with <see cref="Dispose"/> (dialog closed) or when it fails.
/// </summary>
internal sealed class ManualImportKnownMediaSource : IDisposable
{
    private readonly Func<CancellationToken, Task<ManualImportKnownMedia>> _build;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task<ManualImportKnownMedia>? _current;
    private int _builds;

    internal ManualImportKnownMediaSource(Func<CancellationToken, Task<ManualImportKnownMedia>> build) =>
        _build = build ?? throw new ArgumentNullException(nameof(build));

    /// <summary>How many times the library was actually built; lets a test see the reuse.</summary>
    internal int Builds => Volatile.Read(ref _builds);

    internal Task<ManualImportKnownMedia> GetAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Task<ManualImportKnownMedia> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_lifetime.IsCancellationRequested, this);
            if (_current is not { } current || current.IsFaulted || current.IsCanceled)
            {
                Interlocked.Increment(ref _builds);
                _current = current = Task.Run(() => _build(_lifetime.Token), _lifetime.Token);
            }
            task = current;
        }
        return task.WaitAsync(token);
    }

    /// <summary>The library changed: the next check builds it again.</summary>
    internal void Invalidate()
    {
        lock (_gate) _current = null;
    }

    public void Dispose()
    {
        lock (_gate) _current = null;
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }
}
