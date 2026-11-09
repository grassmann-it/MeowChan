using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Transfer;

internal enum NativeTransferStageError
{
    None, InvalidStagingDirectory, ContainerLimit, InvalidPackage, IoFailure, PrivateMetadataNotYetSupported
}

// Cancellation must remain distinguishable while retaining the cleanup outcome without local paths.
internal sealed class NativeTransferStageCanceledException(CancellationToken cancellationToken)
    : OperationCanceledException("Native transfer staging was cancelled.", cancellationToken)
{
    internal bool StageRetained { get; set; }
}

// Deliberately path-free. A cleanup failure is an explicit retained/unknown stage, not a success.
internal sealed record NativeTransferStageResult(NativeTransferImportStage? Stage, NativeTransferStageError Error,
    TransferEnvelopeError EnvelopeError = TransferEnvelopeError.None,
    TransferCatalogError CatalogError = TransferCatalogError.None)
{
    internal bool StageRetained { get; set; }
}

/// <summary>
/// Owns one bounded, fully validated copy of an incoming native package. No caller stream/path
/// is consulted after creation. This is staging, NOT catalog/store admission or an active grant.
/// File sharing coordinates cooperating processes; it is not an OS sandbox against a hostile host.
/// </summary>
internal sealed class NativeTransferImportStage : IAsyncDisposable
{
    private readonly FileStream _file;
    private readonly string _path;
    private readonly Dictionary<string, TransferZipEntry> _payloads;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposeRequested;
    private bool _disposed;

    private NativeTransferImportStage(FileStream file, string path, NativeTransferPackage package,
        IReadOnlyList<TransferZipEntry> entries)
    {
        _file = file; _path = path; Package = package;
        _payloads = entries.Where(entry => entry.Name.StartsWith("payload/", StringComparison.Ordinal))
            .ToDictionary(entry => entry.Name[8..], StringComparer.Ordinal);
    }

    // Passive, owned immutable metadata; reading this property never confers store authority.
    internal NativeTransferPackage Package { get; }
    internal bool StageRetained { get; private set; }

    /// <summary>
    /// Copies from the caller's current position to EOF, without seeking or owning that stream.
    /// The pre-existing absolute stagingDirectory must belong to the importing profile/operation.
    /// Cancellation is thrown. All failure paths clean only the file created by this invocation.
    /// </summary>
    internal static async Task<NativeTransferStageResult> CreateAsync(Stream source, string stagingDirectory,
        CancellationToken cancellationToken = default, TransferEnvelopeLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= new TransferEnvelopeLimits();
        if (!limits.IsWithinSafetyCaps()) throw new ArgumentOutOfRangeException(nameof(limits));
        cancellationToken.ThrowIfCancellationRequested();
        string parent;
        try
        {
            if (string.IsNullOrWhiteSpace(stagingDirectory) || !Path.IsPathFullyQualified(stagingDirectory)
                || !Directory.Exists(stagingDirectory)) return new(null, NativeTransferStageError.InvalidStagingDirectory);
            parent = Path.GetFullPath(stagingDirectory);
            ArchiveExportMetadataFile.RejectLinks(parent);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException or InvalidDataException
            or UnauthorizedAccessException or System.Security.SecurityException)
        { return new(null, NativeTransferStageError.InvalidStagingDirectory); }

        FileStream? file = null;
        string path = Path.Combine(parent, ".meowchan-import-" + Guid.NewGuid().ToString("N") + ".pending");
        bool ownsPath = false;
        NativeTransferStageResult? result = null;
        NativeTransferStageCanceledException? cancelled = null;
        try
        {
            ArchiveExportMetadataFile.RejectLinks(path);
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                BufferSize = 64 * 1024, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            file = new FileStream(path, options); ownsPath = true;
            byte[] buffer = new byte[64 * 1024];
            long copied = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // At most one byte beyond the cap is consumed, even for lying/nonseekable sources.
                int wanted = (int)Math.Min(buffer.Length, limits.ContainerBytes - copied + 1);
                int count = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0) break;
                copied += count;
                if (copied > limits.ContainerBytes)
                    return result = new(null, NativeTransferStageError.ContainerLimit, TransferEnvelopeError.ContainerLimit);
                await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            file.Position = 0;
            TransferPackageInspection inspection = await NativeTransferPackageValidator.InspectAsync(file, cancellationToken, limits).ConfigureAwait(false);
            if (!inspection.IsPackageValid)
                return result = new(null, NativeTransferStageError.InvalidPackage, inspection.EnvelopeError, inspection.CatalogError);
            // Inspection is passive and does not grant receiving consent. A validated
            // private component may travel with the retained stage, but downstream
            // plans must still carry an owner-issued decision; no private value is
            // applied by staging.
            // Reuse the same bounded ZIP reader. No raw handle or package-supplied path escapes.
            IReadOnlyList<TransferZipEntry> entries = await NativeTransferZip.ReadDirectoryAsync(file, limits, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            NativeTransferImportStage stage = new(file, path, inspection.Package!, entries);
            file = null; ownsPath = false; // Transfer the exact handle and cleanup duty, not just its path.
            return result = new(stage, NativeTransferStageError.None);
        }
        catch (OperationCanceledException)
        {
            // Do not leak arbitrary caller exception text/inner exceptions or lose a failed cleanup.
            cancelled = new NativeTransferStageCanceledException(cancellationToken);
            throw cancelled;
        }
        catch (TransferEnvelopeException error)
        { return result = new(null, NativeTransferStageError.InvalidPackage, error.Error); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException
            or ObjectDisposedException or System.Security.SecurityException)
        { return result = new(null, NativeTransferStageError.IoFailure); }
        finally
        {
            bool retained = false;
            if (file != null)
            {
                try { await file.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) when (CleanupError(error)) { retained = true; }
            }
            if (ownsPath) retained |= !DeleteOwnStage(path);
            if (result != null) result.StageRetained = retained;
            if (cancelled != null) cancelled.StageRetained = retained;
        }
    }

    /// <summary>
    /// Copies only a validated payload to an UNPUBLISHED caller-owned staging stream, then
    /// remeasures length/SHA256/MD5/SHA1/CRC. Failure can leave partial bytes in destination;
    /// the caller must discard them. This method never publishes to a store/original path.
    /// Calls serialize on the single exclusive package handle. The destination stays open.
    /// </summary>
    internal async Task<TransferMeasuredBlob> CopyPayloadToAsync(string sha256, Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfDisposing();
        if (sha256 == null || !_payloads.TryGetValue(sha256, out TransferZipEntry? entry))
            throw new ArgumentException("The payload does not belong to this stage.", nameof(sha256));
        cancellationToken.ThrowIfCancellationRequested();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            if (!destination.CanWrite) throw new ArgumentException("An unpublished writable stream is required.", nameof(destination));
            _file.Position = entry.DataOffset;
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            using IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            TransferCrc32 crc = new();
            byte[] buffer = new byte[64 * 1024];
            long copied = 0;
            while (copied < entry.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(buffer.Length, entry.Length - copied);
                int count = await _file.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new InvalidDataException("The staged payload is truncated.");
                hash.AppendData(buffer, 0, count); md5.AppendData(buffer, 0, count); sha1.AppendData(buffer, 0, count);
                crc.Add(buffer.AsSpan(0, count));
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                copied += count;
            }
            cancellationToken.ThrowIfCancellationRequested();
            TransferMeasuredBlob measured = new(copied, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                Convert.ToBase64String(md5.GetHashAndReset()), Convert.ToBase64String(sha1.GetHashAndReset()));
            if (crc.Value != entry.Crc32 || measured != Package.Envelope.MeasuredPayloads[sha256])
                throw new InvalidDataException("The staged payload differs from its validated bytes.");
            return measured;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await _file.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) when (CleanupError(error)) { StageRetained = true; }
            StageRetained |= !DeleteOwnStage(_path);
        }
        finally { _gate.Release(); }
    }

    private void ThrowIfDisposing()
    {
        if (Volatile.Read(ref _disposeRequested) != 0) throw new ObjectDisposedException(nameof(NativeTransferImportStage));
    }

    private static bool DeleteOwnStage(string path)
    {
        try
        {
            ArchiveExportMetadataFile.RejectLinks(path);
            File.Delete(path);
            return true;
        }
        catch (Exception error) when (CleanupError(error)) { return false; }
    }
    private static bool CleanupError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or NotSupportedException or ObjectDisposedException or System.Security.SecurityException;
}
