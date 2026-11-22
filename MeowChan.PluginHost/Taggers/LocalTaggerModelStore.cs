using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MeowChan.Persistence;
using MeowChan.PluginHost.Compilation;
using MeowChan.PluginHost.Contracts;

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
    private const string RootName = "tagger-model";
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
        _root = Path.Combine(lease.ProfileDirectory, RootName);
        _gate = Gates.GetValue(lease, static _ => new SemaphoreSlim(1, 1));
    }

    internal LocalTaggerModelSettings Settings { get; }

    internal async Task<LocalTaggerModelPreview> PreviewAsync(string manifestPath, CancellationToken token)
    {
        string path = Path.GetFullPath(manifestPath);
        using FileStream input = RegularProfileFile.OpenRead(path);
        (LocalTaggerModelPackage package, _) = await ReadManifestAsync(input, path, token).ConfigureAwait(false);
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

    internal async Task<LocalTaggerModelPackage> ImportAsync(LocalTaggerModelPreview preview, CancellationToken token, Action? beforePublish = null)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using ProfileAdmissionToken admission = _lease.Admission.EnterStoreWriter(token);
            using LocalTaggerDirectory profile = LocalTaggerDirectory.OpenProfile(_lease.ProfileDirectory);
            _lease.EnsureDirectoryIdentity(profile.Handle);
            LocalTaggerModelPackage package;
            byte[] manifestBytes;
            using (FileStream sourceManifest = RegularProfileFile.OpenRead(preview.Package.ManifestPath))
                (package, manifestBytes) = await ReadManifestAsync(sourceManifest, preview.Package.ManifestPath, token).ConfigureAwait(false);
            if (package.ManifestSha256 != preview.Package.ManifestSha256)
                throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);

            using LocalTaggerDirectory root = profile.OpenDirectory(RootName, create: true)
                ?? throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
            (string Slot, string Hash)? active = await ReadPointerAsync(root, token).ConfigureAwait(false);
            string slot = active?.Slot == "a" ? "b" : "a";
            using LocalTaggerDirectory? previous = active is { } old ? TryOpenPreviousSlot(root, old.Slot) : null;
            using LocalTaggerDirectory destination = root.OpenDirectory(slot, create: true)
                ?? throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
            DeleteSlotFiles(destination);
            root.DeleteFile(PendingFile);
            if (AvailableBytes() is long available && available < package.RequiredBytes + PointerBytes)
                throw new LocalTaggerModelException(LocalTaggerModelIssue.FreeSpace);

            bool committed = false;
            try
            {
                using (FileStream weights = RegularProfileFile.OpenRead(Path.Combine(package.DirectoryPath, LocalTaggerModelManifest.WeightsFileName)))
                using (FileStream output = destination.CreateFile(LocalTaggerModelManifest.WeightsFileName))
                    await VerifyWeightsAsync(weights, package.Manifest, output, token).ConfigureAwait(false);

                await WriteFileAsync(destination, LocalTaggerModelManifest.FileName, manifestBytes, token).ConfigureAwait(false);
                await WriteFileAsync(root, PendingFile, Encoding.ASCII.GetBytes(slot + "\n" + package.ManifestSha256), token).ConfigureAwait(false);
                beforePublish?.Invoke();
                _lease.RunStoreWrite(admission, () =>
                {
                    token.ThrowIfCancellationRequested();
                    EnsureAttached(profile, root, slot, destination);
                    root.ReplaceFile(PendingFile, ActiveFile);
                    return true;
                });
                committed = true;
                if (previous is not null) TryDeleteSlotFiles(previous);
                return package with { ManifestPath = InstalledManifestPath(slot) };
            }
            finally
            {
                if (!committed)
                {
                    TryDeleteSlotFiles(destination);
                    try { root.DeleteFile(PendingFile); }
                    catch (Exception error) when (IsCleanupFailure(error)) { }
                }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<LocalTaggerModelPackage?> VerifyActiveAsync(CancellationToken token)
    {
        using LocalTaggerDirectory profile = LocalTaggerDirectory.OpenProfile(_lease.ProfileDirectory);
        _lease.EnsureDirectoryIdentity(profile.Handle);
        using LocalTaggerDirectory? root = profile.OpenDirectory(RootName);
        if (root is null || await ReadPointerAsync(root, token).ConfigureAwait(false) is not { } active) return null;
        using LocalTaggerDirectory directory = root.OpenDirectory(active.Slot)
            ?? throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        using FileStream manifest = directory.OpenRead(LocalTaggerModelManifest.FileName)
            ?? throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        (LocalTaggerModelPackage package, _) = await ReadManifestAsync(
            manifest, InstalledManifestPath(active.Slot), token).ConfigureAwait(false);
        if (package.ManifestSha256 != active.Hash) throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        using FileStream weights = directory.OpenRead(LocalTaggerModelManifest.WeightsFileName)
            ?? throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        await VerifyWeightsAsync(weights, package.Manifest, null, token).ConfigureAwait(false);
        EnsureAttached(profile, root, active.Slot, directory);
        return package;
    }

    private void EnsureAttached(LocalTaggerDirectory profile, LocalTaggerDirectory root, string slot, LocalTaggerDirectory directory)
    {
        try
        {
            _lease.EnsureDirectoryIdentity();
            profile.EnsureChildMatches(RootName, root);
            root.EnsureChildMatches(slot, directory);
        }
        catch (InvalidDataException) { throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity); }
    }

    private static async Task<(string Slot, string Hash)?> ReadPointerAsync(LocalTaggerDirectory root, CancellationToken token)
    {
        using FileStream? input = root.OpenRead(ActiveFile);
        if (input is null) return null;
        byte[] bytes = await ReadBytesAsync(input, PointerBytes, LocalTaggerModelIssue.Integrity, token).ConfigureAwait(false);
        string value = Encoding.ASCII.GetString(bytes);
        if (value.Length != PointerBytes || value[0] is not ('a' or 'b') || value[1] != '\n' || !CompilerWire.IsHash(value[2..]))
            throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        return (value[..1], value[2..]);
    }

    private async Task<(LocalTaggerModelPackage Package, byte[] Bytes)> ReadManifestAsync(FileStream input, string path, CancellationToken token)
    {
        byte[] bytes = await ReadBytesAsync(input, Settings.MaximumManifestBytes, LocalTaggerModelIssue.Limit, token).ConfigureAwait(false);
        return (new(path, LocalTaggerModelManifest.Parse(bytes, Settings), CompilerWire.Hash(bytes), bytes.Length), bytes);
    }

    private static async Task<byte[]> ReadBytesAsync(FileStream input, int maximum, LocalTaggerModelIssue issue, CancellationToken token)
    {
        long length = input.Length;
        if (length > maximum) throw new LocalTaggerModelException(issue);
        byte[] bytes = new byte[checked((int)length)];
        await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (input.ReadByte() != -1) throw new LocalTaggerModelException(issue);
        return bytes;
    }

    private static async Task VerifyWeightsAsync(FileStream input, LocalTaggerModelManifest manifest, FileStream? output, CancellationToken token)
    {
        if (input.Length != manifest.SizeBytes) throw new LocalTaggerModelException(LocalTaggerModelIssue.Integrity);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferBytes];
        long length = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
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

    private static async Task WriteFileAsync(LocalTaggerDirectory directory, string name, byte[] bytes, CancellationToken token)
    {
        using FileStream output = directory.CreateFile(name);
        await output.WriteAsync(bytes, token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
    }

    private string InstalledManifestPath(string slot) => Path.Combine(_root, slot, LocalTaggerModelManifest.FileName);

    private static LocalTaggerDirectory? TryOpenPreviousSlot(LocalTaggerDirectory root, string slot)
    {
        try { return root.OpenDirectory(slot); }
        catch (Exception error) when (IsCleanupFailure(error)) { return null; }
    }

    private static void DeleteSlotFiles(LocalTaggerDirectory slot)
    {
        // Keep the two slot directories so cleanup never has to unlink a potentially replaced directory name.
        slot.DeleteFile(LocalTaggerModelManifest.WeightsFileName);
        slot.DeleteFile(LocalTaggerModelManifest.FileName);
    }

    private static void TryDeleteSlotFiles(LocalTaggerDirectory slot)
    {
        try { DeleteSlotFiles(slot); }
        catch (Exception error) when (IsCleanupFailure(error)) { }
    }

    private static bool IsCleanupFailure(Exception error) => error is IOException or UnauthorizedAccessException or InvalidDataException;
}
