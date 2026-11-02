using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Transfer;

internal enum TransferPublicationError
{
    None, InvalidDestination, DestinationExists, ExportFailed, StageValidationFailed, StaleSnapshot, IoFailure
}

// No path/exception text in default diagnostics. Not an import/replace capability.
internal sealed class TransferArchivePublication
{
    internal TransferPublicationError Error { get; init; }
    internal TransferArchiveError ArchiveError { get; init; }
    internal bool IsPublished => Error == TransferPublicationError.None;
    internal long? BytesWritten { get; init; }
    internal string? PackageFingerprint { get; init; }
    internal bool StageRetained { get; set; }
}

/// <summary>
/// Create-only local file publication. Existing files/directories are never overwritten.
/// Uses a private sibling stage and full same-handle validation, then atomic no-clobber
/// hard-link publication on Unix or no-overwrite File.Move on Windows.
/// Unix filesystems without hard-link support fail closed; there is no rename/copy fallback.
/// Cooperative local filesystem safety, not an OS sandbox or power-loss durability proof.
/// </summary>
internal static class NativeTransferArchivePublisher
{
    internal static async Task<TransferArchivePublication> PublishAsync(NativeTransferArchiveSnapshot snapshot,
        string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        string full, parent;
        try
        {
            if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination)
                || Path.EndsInDirectorySeparator(destination)) return Fail(TransferPublicationError.InvalidDestination);
            full = Path.GetFullPath(destination);
            parent = Path.GetDirectoryName(full)!;
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(Path.GetFileName(full)) || !Directory.Exists(parent))
                return Fail(TransferPublicationError.InvalidDestination);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { return Fail(TransferPublicationError.InvalidDestination); }

        string? stage = null;
        bool stageOwned = false, cancellationObserved = false;
        TransferArchivePublication? result = null;
        try
        {
            ArchiveExportMetadataFile.RejectLinks(full);
            if (File.Exists(full) || Directory.Exists(full)) return result = Fail(TransferPublicationError.DestinationExists);
            stage = Path.Combine(parent, ".meowchan-transfer-" + Guid.NewGuid().ToString("N") + ".pending");
            ArchiveExportMetadataFile.RejectLinks(stage);
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                BufferSize = 64 * 1024, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            TransferArchiveWriteResult written;
            // Remember a real cancellation before disposal can supersede it. All cleanup still runs.
            await using (FileStream output = new(stage, options))
            {
                stageOwned = true;
                try
                {
                    written = await NativeTransferArchiveExporter.WriteAsync(snapshot, output, cancellationToken).ConfigureAwait(false);
                    if (!written.IsComplete) return result = new() { Error = TransferPublicationError.ExportFailed, ArchiveError = written.Error };
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                    output.Position = 0;
                    TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output, cancellationToken).ConfigureAwait(false);
                    if (!inspected.IsPackageValid || inspected.Package!.Envelope.Fingerprint != written.Package!.Envelope.Fingerprint
                        || output.Length != written.BytesWritten)
                        return result = Fail(TransferPublicationError.StageValidationFailed);
                    if (!await NativeTransferArchiveExporter.IsCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false))
                        return result = Fail(TransferPublicationError.StaleSnapshot);
                }
                catch (OperationCanceledException) { cancellationObserved = true; throw; }
            }
            // No overwrite flag or replacement fallback, even if a competing writer won.
            // On Unix, File.Move(..., overwrite:false) can first observe an absent target and
            // then use rename(2), whose destination replacement is allowed. Link(2) is the
            // atomic no-clobber publication primitive for these same-directory stages.
            ArchiveExportMetadataFile.RejectLinks(full);
            ArchiveExportMetadataFile.RejectLinks(stage);
            cancellationToken.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                File.Move(stage, full, overwrite: false);
            }
            else if (LinkNoOverwrite(stage, full) != 0)
            {
                throw new IOException("The destination could not be published without replacement.");
            }

            // Commit point: cancellation after the successful publication must NOT report failure.
            result = new()
            {
                Error = TransferPublicationError.None, BytesWritten = written.BytesWritten,
                PackageFingerprint = written.Package!.Envelope.Fingerprint
            };
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    ArchiveExportMetadataFile.RejectLinks(stage);
                    File.Delete(stage);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException
                    or NotSupportedException or System.Security.SecurityException)
                { result.StageRetained = true; }
            }
            stageOwned = false;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (cancellationObserved) { throw new OperationCanceledException(cancellationToken); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ObjectDisposedException
            or System.Security.SecurityException or Microsoft.Data.Sqlite.SqliteException or FormatException)
        { return result = Fail(TransferPublicationError.IoFailure); }
        finally
        {
            // Only the exact stage created by this invocation; never scan/delete orphan exports or originals.
            if (stageOwned && stage != null)
            {
                try
                {
                    ArchiveExportMetadataFile.RejectLinks(stage);
                    File.Delete(stage);
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
                { if (result != null) result.StageRetained = true; }
            }
        }
    }
    [DllImport("System.Native", EntryPoint = "SystemNative_Link", SetLastError = true)]
    private static extern int LinkNoOverwrite([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    private static TransferArchivePublication Fail(TransferPublicationError error) => new() { Error = error };
}
