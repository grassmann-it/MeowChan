using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Storage;

public sealed partial class MediaStore
{
    /// <summary>
    /// The manifest source address of a manually imported file. A fixed neutral value: the manifest never records where
    /// an imported file came from (no path, no file: URI).
    /// </summary>
    internal const string LocalImportSourceUrl = "meowchan-import:local-file";

    /// <summary>
    /// Copies one local file into a managed media folder and publishes it in that folder's manifest, with the same steps as
    /// a download commit: a create-only temporary file, hashing while copying, flush, the directory lease, a create-only
    /// move and an atomic manifest replacement. The copy must have exactly <paramref name="expectedLength"/> bytes and
    /// <paramref name="expectedSha256"/> (measured when the import was checked); a source that changed since fails with
    /// <see cref="InvalidDataException"/> and leaves nothing behind. A foreign file at the destination is never
    /// overwritten (a conflict name is used); a retry of the same item with the same bytes reuses the published file.
    /// A damaged manifest is never replaced. After publication starts, cancellation no longer splits the commit.
    /// The source stream stays owned by the caller.
    /// </summary>
    internal async Task<MediaCommitResult> CommitLocalFileAsync(string directory, string requestedFileName, string mediaIdentifier,
        string? originalFileName, Stream source, string expectedSha256, long expectedLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaIdentifier);
        directory = Path.GetFullPath(directory);
        if (!IsSafeFileName(requestedFileName) || IsMetadataName(requestedFileName))
            throw new ArgumentException("The requested name must be a portable file name outside the store's metadata namespace.", nameof(requestedFileName));
        if (expectedSha256 is not { Length: 64 } || expectedLength < 0) throw new ArgumentException("A measured SHA-256 and length are required.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, requestedFileName);
        string temporaryPath = Path.Combine(directory, ".meowchan-media-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            FileDigest digest;
            await using (FileStream temporaryStream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                digest = await CopyAndHashAsync(source, temporaryStream, cancellationToken, null, expectedLength).ConfigureAwait(false);
                if (digest.Length != expectedLength || digest.Sha256 != expectedSha256)
                    throw new InvalidDataException("The source file changed after it was checked.");
                await temporaryStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                temporaryStream.Flush(flushToDisk: true);
            }

            SemaphoreSlim directoryLock = _directoryLocks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
            await directoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using FileStream directoryLease = await AcquireDirectoryLeaseAsync(directory, cancellationToken).ConfigureAwait(false);
                ForgetVerifiedFile(destination);
                ManifestRead manifestRead = await ReadWriterManifestAsync(directory, cancellationToken).ConfigureAwait(false);
                if (manifestRead.Corrupt) throw new InvalidDataException("The managed import folder has a damaged manifest.");
                string publishedPath = destination;
                bool reused = false;
                if (manifestRead.Manifest.Entries.TryGetValue(requestedFileName, out MediaManifestEntry? existing))
                {
                    // Item ids are unique: an entry under this name is only ever an earlier attempt of this very item.
                    if (existing.MediaIdentifier != mediaIdentifier || existing.Sha256 != digest.Sha256 || existing.Length != digest.Length
                        || !TryGetEntryPath(directory, existing.FileName, out string? recorded)
                        || !await HasSameContentAsync(recorded!, digest, cancellationToken).ConfigureAwait(false))
                        throw new InvalidDataException("The managed import folder already records other bytes for this item.");
                    return new MediaCommitResult(recorded!, digest.Length, digest.Sha256, true)
                    { AssetId = GetAssetId(requestedFileName, existing) };
                }
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    if (!Directory.Exists(destination) && await HasSameContentAsync(destination, digest, cancellationToken).ConfigureAwait(false))
                        reused = true; // A stop between the move and the manifest save left exactly these bytes.
                    else
                    {
                        for (int conflictNumber = 0; ; conflictNumber = checked(conflictNumber + 1))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            publishedPath = GetConflictPath(destination, digest.Sha256, conflictNumber);
                            if (!File.Exists(publishedPath) && !Directory.Exists(publishedPath)) break;
                            if (!Directory.Exists(publishedPath)
                                && await HasSameContentAsync(publishedPath, digest, cancellationToken).ConfigureAwait(false)) { reused = true; break; }
                        }
                    }
                }
                MediaManifestEntry entry = new()
                {
                    MediaIdentifier = mediaIdentifier,
                    Variant = "original",
                    SourceUrl = LocalImportSourceUrl,
                    FileName = Path.GetFileName(publishedPath),
                    Length = digest.Length,
                    Sha256 = digest.Sha256,
                    Md5 = Convert.ToBase64String(digest.Md5),
                    Sha1 = Convert.ToBase64String(digest.Sha1),
                    OriginalFileName = originalFileName
                };
                ValidateOriginalPublicationPath(manifestRead.Manifest, publishedPath);
                cancellationToken.ThrowIfCancellationRequested();
                // No overwrite: a competing or foreign destination is never truncated.
                if (!reused) _moveFile(temporaryPath, publishedPath, false);
                manifestRead.Manifest.Entries[requestedFileName] = entry;
                AddAsset(manifestRead.Manifest, requestedFileName, entry);
                await SaveManifestAsync(directory, manifestRead, CancellationToken.None).ConfigureAwait(false);
                return new MediaCommitResult(publishedPath, digest.Length, digest.Sha256, reused) { AssetId = GetAssetId(requestedFileName, entry) };
            }
            finally
            {
                directoryLock.Release();
            }
        }
        finally
        {
            DeleteOwnTemporaryFile(temporaryPath);
        }
    }
}
