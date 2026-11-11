using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;

namespace MeowChan.Storage;

public sealed partial class MediaStore
{
    private const string ReclaimPrefix = ".reclaim-";

    /// <summary>Bytes the candidate root occupies, or 0 when it is already gone. Reads sizes only.</summary>
    internal static long MeasureCandidateRoot(string operationsParent, string directory)
    {
        string root = ValidateReclaimTarget(operationsParent, directory, out bool exists);
        if (!exists) return 0;
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            total += new FileInfo(file).Length;
        return total;
    }

    /// <summary>
    /// Removes one candidate root that the catalog proved unreferenced. The root is renamed first (atomic, so a
    /// crash leaves either the intact root or a leftover `.reclaim-*` sibling) and deleted afterwards. Callers
    /// must hold the catalog's write admission; the root's own lease is taken only to prove nothing else uses it.
    /// Returns the bytes freed (0 when the root was already gone). Leftover `.reclaim-*` siblings are swept first.
    /// </summary>
    internal async Task<long> ReclaimCandidateRootAsync(string operationsParent, string directory, CancellationToken token = default)
    {
        string root = ValidateReclaimTarget(operationsParent, directory, out bool exists);
        SweepReclaimLeftovers(Path.GetDirectoryName(root)!);
        if (!exists) return 0;
        long bytes = MeasureCandidateRoot(operationsParent, root);
        string lease = Path.Combine(root, DirectoryLeaseFileName);
        if (File.Exists(lease))
            using (await OpenExistingCandidateLeaseAsync(lease, token).ConfigureAwait(false)) { }
        token.ThrowIfCancellationRequested();
        string parked = Path.Combine(Path.GetDirectoryName(root)!, ReclaimPrefix + Guid.NewGuid().ToString("N"));
        Directory.Move(root, parked);
        TryDelete(parked);
        return bytes;
    }

    private static string ValidateReclaimTarget(string operationsParent, string directory, out bool exists)
    {
        if (string.IsNullOrWhiteSpace(operationsParent) || !Path.IsPathFullyQualified(operationsParent))
            throw new InvalidDataException("An absolute operations parent is required.");
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(operationsParent));
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new InvalidDataException("An absolute candidate root is required.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!PathsEqual(full, Path.TrimEndingDirectorySeparator(directory))
            || !PathsEqual(Path.GetDirectoryName(full)!, parent))
            throw new InvalidDataException("The candidate root is not a canonical immediate child of its operations parent.");
        string name = Path.GetFileName(full);
        if (!name.StartsWith(PassiveCandidatePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name[PassiveCandidatePrefix.Length..], "N", out Guid suffix)
            || suffix.ToString("N") != name[PassiveCandidatePrefix.Length..])
            throw new InvalidDataException("Not a passive candidate directory.");
        exists = Directory.Exists(full);
        if (exists) ArchiveExportMetadataFile.RejectLinks(full);
        else if (File.Exists(full)) throw new InvalidDataException("The candidate root path is occupied by a file.");
        return full;
    }

    private static void SweepReclaimLeftovers(string parent)
    {
        if (!Directory.Exists(parent)) return;
        foreach (string leftover in Directory.EnumerateDirectories(parent, ReclaimPrefix + "*"))
        {
            string name = Path.GetFileName(leftover);
            if (name.Length == ReclaimPrefix.Length + 32 && Guid.TryParseExact(name[ReclaimPrefix.Length..], "N", out _)
                && new DirectoryInfo(leftover).LinkTarget == null) TryDelete(leftover);
        }
    }

    private static void TryDelete(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
