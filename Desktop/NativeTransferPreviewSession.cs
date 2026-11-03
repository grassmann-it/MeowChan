using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Desktop;

internal enum NativeTransferPreviewState { Idle, Reading, Inventory, Rejected, Canceled, Closed }
internal enum NativeTransferPreviewError
{
    None, InvalidSource, InvalidWorkDirectory, UnsupportedPlatform, InvalidPackage,
    PrivateMetadataNotYetSupported, LimitExceeded, ReadFailure
}

// Only an inventory, never an incoming Package, Stage, source path, or apply capability.
internal sealed record NativeTransferPreviewSnapshot(long Generation, NativeTransferPreviewState State,
    NativeTransferPreviewError Error, TransferImportPreview? Inventory, bool CleanupRetained);

/// <summary>
/// Preview-only ingress. The caller supplies a pre-existing, trusted app-owned work parent,
/// not a package-supplied path. Each request creates a private random child; only that child
/// is removed, nonrecursively. No catalog/store/profile writer or source provider is opened.
/// InspectAsync/Cancel/Dispose are thread-safe; the UI pulls Current on its own dispatcher.
/// </summary>
internal sealed class NativeTransferPreviewSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly string _workParent;
    private readonly TransferEnvelopeLimits _limits;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly HashSet<Request> _requests = new();
    private NativeTransferPreviewSnapshot _current = new(0, NativeTransferPreviewState.Idle,
        NativeTransferPreviewError.None, null, false);
    private TaskCompletionSource? _disposal;
    private bool _closed, _retained;
    private long _generation;

    internal NativeTransferPreviewSession(string workParentDirectory, TransferEnvelopeLimits? limits = null)
    {
        _workParent = workParentDirectory;
        _limits = limits ?? new();
        if (!_limits.IsWithinSafetyCaps()) throw new ArgumentOutOfRangeException(nameof(limits));
    }

    // Scheduling/cleanup probes only; do not replace the regular-file opener, real writer,
    // validator or Stage. Production callers leave these unset. Exceptions stay opaque.
    internal Func<CancellationToken, Task>? BeforeOpenForTests { get; set; }
    internal Func<CancellationToken, Task>? AfterStageForTests { get; set; }
    internal Action<string>? BeforeDirectoryCleanupForTests { get; set; }

    internal NativeTransferPreviewSnapshot Current { get { lock (_sync) return _current; } }

    internal Task<NativeTransferPreviewSnapshot> InspectAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        Request request;
        Request[] previous;
        lock (_sync)
        {
            if (_closed) return Task.FromResult(_current);
            previous = _requests.ToArray();
            request = new(++_generation, cancellationToken);
            _requests.Add(request);
            _current = new(_generation, NativeTransferPreviewState.Reading, NativeTransferPreviewError.None, null, _retained);
        }
        foreach (Request old in previous) old.Cancel();
        // Includes synchronous native regular-file open / Flush(true) / metadata work off the UI.
        _ = Task.Run(() => RunAsync(request, sourcePath));
        return request.Done.Task;
    }

    internal void Cancel()
    {
        Request[] pending;
        lock (_sync)
        {
            if (_closed) return;
            pending = _requests.ToArray();
            _current = new(++_generation, NativeTransferPreviewState.Canceled, NativeTransferPreviewError.None, null, _retained);
        }
        foreach (Request request in pending) request.Cancel();
    }

    private async Task RunAsync(Request request, string sourcePath)
    {
        NativeTransferPreviewState state = NativeTransferPreviewState.Rejected;
        NativeTransferPreviewError error = NativeTransferPreviewError.ReadFailure;
        TransferImportPreview? inventory = null;
        NativeTransferImportStage? stage = null;
        string? directory = null;
        bool entered = false, retained = false;
        CancellationToken token = request.Token;
        try
        {
            await _serial.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (BeforeOpenForTests is { } before) await before(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Reject ancestor links as well as special-file/name-swap protection on the handle.
            if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            { error = NativeTransferPreviewError.InvalidSource; return; }
            ArchiveExportMetadataFile.RejectLinks(sourcePath);
            using (FileStream source = RegularProfileFile.OpenRead(sourcePath))
            {
                token.ThrowIfCancellationRequested();
                try { directory = CreateWorkDirectory(); }
                catch (Exception) { error = NativeTransferPreviewError.InvalidWorkDirectory; return; }
                NativeTransferStageResult result = await NativeTransferImportStage.CreateAsync(source, directory, token, _limits).ConfigureAwait(false);
                retained |= result.StageRetained;
                stage = result.Stage;
                if (stage == null) { error = Map(result.Error); return; }
            } // No reopen of source after the own Stage has accepted its exact bytes.
            if (AfterStageForTests is { } after) await after(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            TransferImportPreviewInspection preview = NativeTransferImportPreview.Create(stage.Package, token);
            if (!preview.HasPreview) { error = NativeTransferPreviewError.InvalidPackage; return; }
            token.ThrowIfCancellationRequested();
            inventory = preview.Preview;
            error = NativeTransferPreviewError.None;
            state = NativeTransferPreviewState.Inventory;
        }
        catch (NativeTransferStageCanceledException canceled)
        { retained |= canceled.StageRetained; state = NativeTransferPreviewState.Canceled; error = NativeTransferPreviewError.None; }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { state = NativeTransferPreviewState.Canceled; error = NativeTransferPreviewError.None; }
        catch (PlatformNotSupportedException) { error = NativeTransferPreviewError.UnsupportedPlatform; }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException
            or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        { error = NativeTransferPreviewError.InvalidSource; }
        catch (Exception) { error = NativeTransferPreviewError.ReadFailure; }
        finally
        {
            // Stage cleanup must finish before inspecting its retention outcome or deleting parent.
            if (stage != null)
            {
                try { await stage.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { retained = true; }
                retained |= stage.StageRetained;
            }
            if (directory != null)
            {
                try
                {
                    BeforeDirectoryCleanupForTests?.Invoke(directory);
                    ArchiveExportMetadataFile.RejectLinks(directory);
                    Directory.Delete(directory, recursive: false); // Unknown children are never removed.
                }
                catch (Exception) { retained = true; }
            }
            if (entered) _serial.Release();
            NativeTransferPreviewSnapshot published;
            lock (_sync)
            {
                _retained |= retained;
                // Even an uncancellable obsolete operation can report cleanup uncertainty,
                // but it can never republish its inventory/error or undo Cancel/Closed.
                if (!_closed && request.Generation == _generation)
                {
                    if (token.IsCancellationRequested)
                    { state = NativeTransferPreviewState.Canceled; error = NativeTransferPreviewError.None; inventory = null; }
                    _current = new(_generation, state, error, inventory, _retained);
                }
                else _current = _current with { CleanupRetained = _retained };
                published = _current;
            }
            // Keep the request in the drain set until its completion signal is published.
            // DisposeAsync may snapshot that set between cleanup and this point.
            request.Dispose();
            request.Done.TrySetResult(published);
            lock (_sync) _requests.Remove(request);
        }
    }

    private string CreateWorkDirectory()
    {
        if (string.IsNullOrWhiteSpace(_workParent) || !Path.IsPathFullyQualified(_workParent))
            throw new InvalidDataException();
        ArchiveExportMetadataFile.RejectLinks(_workParent);
        if ((File.GetAttributes(_workParent) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device))
            != FileAttributes.Directory) throw new InvalidDataException();
        string child = Path.Combine(_workParent, ".meowchan-preview-" + Guid.NewGuid().ToString("N"));
        ArchiveExportMetadataFile.RejectLinks(child);
        if (Directory.Exists(child) || File.Exists(child)) throw new InvalidDataException();
        // Cooperative trusted parent + unpredictable child, not a hostile-host mkdir sandbox.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(child);
        else Directory.CreateDirectory(child, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return child;
    }

    private static NativeTransferPreviewError Map(NativeTransferStageError error) => error switch
    {
        NativeTransferStageError.PrivateMetadataNotYetSupported => NativeTransferPreviewError.PrivateMetadataNotYetSupported,
        NativeTransferStageError.ContainerLimit => NativeTransferPreviewError.LimitExceeded,
        NativeTransferStageError.InvalidStagingDirectory => NativeTransferPreviewError.InvalidWorkDirectory,
        NativeTransferStageError.InvalidPackage => NativeTransferPreviewError.InvalidPackage,
        _ => NativeTransferPreviewError.ReadFailure
    };

    public ValueTask DisposeAsync()
    {
        Request[] pending;
        TaskCompletionSource done;
        lock (_sync)
        {
            if (_disposal != null) return new(_disposal.Task);
            done = _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closed = true;
            _current = new(++_generation, NativeTransferPreviewState.Closed, NativeTransferPreviewError.None, null, _retained);
            pending = _requests.ToArray();
        }
        foreach (Request request in pending) request.Cancel();
        _ = DrainAsync(pending, done);
        return new(done.Task);
    }

    private async Task DrainAsync(Request[] pending, TaskCompletionSource done)
    {
        await Task.WhenAll(pending.Select(request => request.Done.Task)).ConfigureAwait(false);
        _serial.Dispose();
        done.TrySetResult();
    }

    private sealed class Request(long generation, CancellationToken token) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        internal long Generation { get; } = generation;
        internal CancellationToken Token => _cancellation.Token;
        internal TaskCompletionSource<NativeTransferPreviewSnapshot> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Cancel()
        {
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception) { /* caller-owned cancellation callbacks stay opaque */ }
        }
        public void Dispose() => _cancellation.Dispose();
    }
}
