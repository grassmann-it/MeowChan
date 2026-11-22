using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Persistence;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.PluginHost.Taggers;

internal enum LocalTaggerModelIssue { Limit, FreeSpace, Integrity }

internal sealed class LocalTaggerModelException(LocalTaggerModelIssue issue) : IOException(issue.ToString())
{
    internal LocalTaggerModelIssue Issue { get; } = issue;
}

internal sealed record LocalTaggerModelPackage(string ManifestPath, LocalTaggerModelManifest Manifest, string ManifestSha256, int ManifestBytes)
{
    internal string DirectoryPath => Path.GetDirectoryName(ManifestPath)!;
    internal long RequiredBytes => ManifestBytes + Manifest.SizeBytes;
}

internal sealed record LocalTaggerModelPreview(LocalTaggerModelPackage Package, long? FreeBytes);

/// <summary>One installed package per profile. The active pointer commits the manifest and weights together.</summary>
internal sealed class LocalTaggerModelStore
{
    // Installation and worker use share the lease's slot gate, including multiple wrappers for the same profile.
    private static readonly ConditionalWeakTable<ProfileLease, SemaphoreSlim> Gates = new();
    private const string ActiveFile = "active";
    private const string PendingFile = "active.pending";
    private const int PointerBytes = 66; // one slot letter, LF and the ASCII SHA-256
    private const int CopyBufferBytes = 256 * 1024;
    private readonly ProfileLease _lease;
    private readonly SemaphoreSlim _gate;
    private readonly string _root;

    internal LocalTaggerModelStore(ProfileLease lease, LocalTaggerModelSettings? settings = null)
    {
        _lease = lease;
        Settings = settings ?? new();
        if (!Settings.IsValid) throw new ArgumentOutOfRangeException(nameof(settings));
        _root = Path.Combine(lease.ProfileDirectory, "tagger-model");
        _gate = Gates.GetValue(lease, static _ => new SemaphoreSlim(1, 1));
    }

    internal LocalTaggerModelSettings Settings { get; }

    internal async Task<LocalTaggerModelPreview> PreviewAsync(string manifestPath, CancellationToken token)
    {
        (LocalTaggerModelPackage package, _) = await ReadManifestAsync(Path.GetFullPath(manifestPath), token).ConfigureAwait(false);
        return new(package, AvailableBytes());
    }

    internal async Task<LocalTaggerModelPackage?> VerifyInstalledAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await VerifyActiveAsync(token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    // Keep the verified pair available until the caller has stopped its worker and finished reading the resources.
    internal async Task<T> WithVerifiedModelAsync<T>(Func<LocalTaggerModelPackage?, Task<T>> use, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await use(await VerifyActiveAsync(token).ConfigureAwait(false)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task<LocalTaggerModelPackage> ImportAsync(LocalTaggerModelPreview preview, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
            _lease.EnsureMatches(_lease.ProfileDirectory);
            (LocalTaggerModelPackage package, byte[] manifestBytes) =
                await ReadManifestAsync(preview.Package.ManifestPath, token).ConfigureAwait(false);
            if (package.ManifestSha256 != preview.Package.ManifestSha256)
                throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);

            RejectLink(_root);
            Directory.CreateDirectory(_root);
            (char Slot, string Hash)? active = await ReadPointerAsync(token).ConfigureAwait(false);
            char slot = active?.Slot == 'a' ? 'b' : 'a';
            string destination = SlotPath(slot);
            string pending = Path.Combine(_root, PendingFile);
            RejectLink(destination);
            RejectLink(pending);
            DeleteSlot(destination);
            File.Delete(pending);
            if (AvailableBytes() is long available && available < package.RequiredBytes + PointerBytes)
                throw new LocalTaggerModelException(LocalTaggerModelIssue.FreeSpace);

            bool committed = false;
            try
            {
                Directory.CreateDirectory(destination);
                string weights = WeightsPath(package.DirectoryPath);
                await using (FileStream output = CreateFile(WeightsPath(destination)))
                    await VerifyWeightsAsync(weights, package.Manifest, output, token).ConfigureAwait(false);

                string installedManifest = Path.Combine(destination, LocalTaggerModelManifest.FileName);
                await WriteFileAsync(installedManifest, manifestBytes, token).ConfigureAwait(false);
                await WriteFileAsync(pending, Encoding.ASCII.GetBytes(slot + "\n" + package.ManifestSha256), token).ConfigureAwait(false);
                _lease.RunStoreWrite(admission, () =>
                {
                    token.ThrowIfCancellationRequested();
                    File.Move(pending, Path.Combine(_root, ActiveFile), overwrite: true);
                    return true;
                });
                committed = true;
                if (active is { } previous) TryDeleteSlot(SlotPath(previous.Slot));
                return package with { ManifestPath = installedManifest };
            }
            finally
            {
                if (!committed)
                {
                    TryDeleteSlot(destination);
                    TryDeleteFile(pending);
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<LocalTaggerModelPackage?> VerifyActiveAsync(CancellationToken token)
    {
        _lease.EnsureMatches(_lease.ProfileDirectory);
        RejectLink(_root);
        if (await ReadPointerAsync(token).ConfigureAwait(false) is not { } active) return null;
        string directory = SlotPath(active.Slot);
        RejectLink(directory);
        (LocalTaggerModelPackage package, _) = await ReadManifestAsync(
            Path.Combine(directory, LocalTaggerModelManifest.FileName), token).ConfigureAwait(false);
        if (package.ManifestSha256 != active.Hash) throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        await VerifyWeightsAsync(WeightsPath(directory), package.Manifest, null, token).ConfigureAwait(false);
        return package;
    }

    private async Task<(char Slot, string Hash)?> ReadPointerAsync(CancellationToken token)
    {
        string path = Path.Combine(_root, ActiveFile);
        RejectLink(path);
        if (!File.Exists(path)) return null;
        string value = Encoding.ASCII.GetString(await CompilerWire.ReadBoundedAsync(path, PointerBytes, token).ConfigureAwait(false));
        if (value.Length != PointerBytes || value[0] is not ('a' or 'b') || value[1] != '\n' || !CompilerWire.IsHash(value[2..]))
            throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        return (value[0], value[2..]);
    }

    private async Task<(LocalTaggerModelPackage Package, byte[] Bytes)> ReadManifestAsync(string path, CancellationToken token)
    {
        RejectLink(path);
        if (new FileInfo(path).Length > Settings.MaximumManifestBytes) throw new LocalTaggerModelException(LocalTaggerModelIssue.Limit);
        byte[] bytes = await CompilerWire.ReadBoundedAsync(path, Settings.MaximumManifestBytes, token).ConfigureAwait(false);
        return (new(path, LocalTaggerModelManifest.Parse(bytes, Settings), CompilerWire.Hash(bytes), bytes.Length), bytes);
    }

    private static async Task VerifyWeightsAsync(string path, LocalTaggerModelManifest manifest, FileStream? output, CancellationToken token)
    {
        RejectLink(path);
        using SafeFileHandle input = CompilerWire.OpenRead(path);
        if (RandomAccess.GetLength(input) != manifest.SizeBytes) throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferBytes];
        long length = 0;
        int read;
        while ((read = await RandomAccess.ReadAsync(input, buffer, length, token).ConfigureAwait(false)) != 0)
        {
            length += read;
            if (length > manifest.SizeBytes) throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
            hash.AppendData(buffer, 0, read);
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (length != manifest.SizeBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != manifest.Sha256)
            throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        output?.Flush(flushToDisk: true);
    }

    private long? AvailableBytes()
    {
        // Choose the deepest mounted drive containing the actual store, including profile mounts below the root.
        try
        {
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            DriveInfo? drive = DriveInfo.GetDrives().Where(item => string.Equals(_root, Path.TrimEndingDirectorySeparator(item.Name), comparison)
                || _root.StartsWith(Path.EndsInDirectorySeparator(item.Name) ? item.Name : item.Name + Path.DirectorySeparatorChar, comparison))
                .MaxBy(item => item.Name.Length);
            return drive?.AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static FileStream CreateFile(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        CopyBufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task WriteFileAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using FileStream output = CreateFile(path);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private string SlotPath(char slot) => Path.Combine(_root, slot.ToString());
    private static string WeightsPath(string directory) => Path.Combine(directory, LocalTaggerModelManifest.WeightsFileName);

    private static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Local tagger model paths cannot be symbolic links.");
    }

    private static void DeleteSlot(string path)
    {
        // Only the two files this store owns may be removed. A foreign file prevents reuse of the slot.
        if (!Directory.Exists(path)) return;
        RejectLink(path);
        File.Delete(WeightsPath(path));
        File.Delete(Path.Combine(path, LocalTaggerModelManifest.FileName));
        Directory.Delete(path);
    }

    private static void TryDeleteSlot(string path)
    {
        try { DeleteSlot(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
