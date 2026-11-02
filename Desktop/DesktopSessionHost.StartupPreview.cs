using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    // Display data has its own channel. It never enters Projection or the handle maps.
    private StartupPreviewSnapshot? _startupPreview;
    private StartupPreviewSnapshot? _startupPreviewCheckpoint;
    internal StartupPreviewSnapshot? StartupPreview => Volatile.Read(ref _startupPreview);
    internal event Action? StartupPreviewChanged;
    internal Func<CancellationToken, Task>? AfterStartupPreviewForTests { get; set; }

    private async Task LoadStartupPreviewAsync(CancellationToken cancellationToken)
    {
        if (Config.SaveUrlsOnExit)
        {
            try
            {
                StartupPreviewSnapshot? preview = new StartupPreviewStore(ProfileDirectory, _profileLease).Load(cancellationToken);
                lock (_lifecycle)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_state != DesktopSessionState.Starting) return;
                    Volatile.Write(ref _startupPreview, preview);
                }
                if (preview != null)
                    foreach (Action observer in StartupPreviewChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
                        try { observer(); } catch (Exception error) { _logger.Debug(error, "Startup preview observer failed."); }
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
            {
                // A disposable display cache is never an authoritative recovery dependency.
                _logger.Debug("Startup preview unavailable ({Kind}); normal restoration continues.", error.GetType().Name);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (AfterStartupPreviewForTests is { } barrier) await barrier(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    // Called before Stopping, while holding the lifecycle lock. No I/O on this path.
    private void CaptureStartupPreviewCheckpoint()
    {
        DesktopSessionProjection projection = Projection;
        if (!Config.SaveUrlsOnExit || _state != DesktopSessionState.Ready || projection.IsStale
            || projection.View.State != DesktopSessionState.Ready) return;
        // At most256 rows/128 chars per label: even fully JSON-escaped labels fit the1MiB store cap.
        const int checkpointRows = 256;
        static string Label(string? value)
        {
            string text = new((value ?? string.Empty).Where(c => !char.IsControl(c)).Take(129).ToArray());
            if (text.Length <= 128) return text;
            int length = char.IsHighSurrogate(text[126]) ? 126 : 127;
            return text[..length] + "…";
        }
        List<StartupPreviewRow> rows = new();
        foreach (DesktopThreadEntry entry in projection.View.Threads.Take(checkpointRows))
        {
            int completed = Math.Max(0, entry.Snapshot.CompletedMedia), known = Math.Max(completed, entry.Snapshot.KnownMedia);
            rows.Add(new(false, Label(entry.HostName), Label(entry.Snapshot.Id.Board), Label(entry.Snapshot.Title), known, completed));
        }
        foreach (DesktopBoardEntry entry in projection.View.Boards.Take(checkpointRows - rows.Count))
            rows.Add(new(true, Label(entry.HostName), Label(entry.Snapshot.Id.Board), Label(entry.Snapshot.Name), 0, 0));
        _startupPreviewCheckpoint = new(DateTimeOffset.UtcNow, projection.View.Threads.Count, projection.View.Boards.Count, rows.AsReadOnly());
    }

    // Runs on the shutdown worker, before owner disposal; failure must not stop real persistence/cleanup.
    private void SaveStartupPreviewCheckpoint()
    {
        if (!Config.SaveUrlsOnExit || _startupPreviewCheckpoint is not { } snapshot) return;
        try
        {
            Config.EnsureOrdinaryWritesAllowed();
            new StartupPreviewStore(ProfileDirectory, _profileLease).Save(snapshot);
        }
        catch (Exception error)
        {
            _logger.Debug("Startup preview checkpoint not saved ({Kind}).", error.GetType().Name);
        }
    }
}
