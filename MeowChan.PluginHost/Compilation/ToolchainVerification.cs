using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.PluginHost.Compilation;

/// <summary>One full verification of the delivered toolchain: toolchain.json and every file it lists were hashed
/// completely, and each file's identity was stamped from the very handle that was hashed, before its first byte was read.
/// The stamps never stand in for a hash: they only let a later use within the same pass prove that no verified file
/// was replaced or written since (<see cref="Unchanged"/>). Any doubt means "changed", and the caller verifies again.</summary>
internal sealed class ToolchainVerification
{
    private readonly Entry[] _entries;
    private ToolchainVerification(string directory, ToolchainManifest manifest, Entry[] entries)
    {
        Directory = directory;
        Manifest = manifest;
        _entries = entries;
    }

    internal string Directory { get; }
    internal ToolchainManifest Manifest { get; }

    private readonly record struct Entry(string RelativePath, string FullPath, FileStamp Stamp);

    // Bounded. Measured 13.11.2026 (171 files, 15.4 MiB, page cache warm, 16 CPUs, load 17): serial async reads 19 ms
    // (p90 81 ms under thread-pool pressure), one synchronous reader 14 ms, two 9 ms (p90 30 ms); four or eight readers
    // gained nothing more, so more parallel I/O at startup is not worth it.
    internal static int Parallelism => Environment.ProcessorCount >= 4 ? 2 : 1;

    internal static async Task<ToolchainVerification> VerifyAsync(string directory, CancellationToken cancellationToken, int? parallelism = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int lanes = Math.Clamp(parallelism ?? Parallelism, 1, 16);
        // Synchronous reads on a dedicated thread: no thread-pool hop per read chunk while startup keeps the pool busy.
        return await Task.Factory.StartNew(() => Verify(directory, lanes, cancellationToken), cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default).ConfigureAwait(false);
    }

    private static ToolchainVerification Verify(string directory, int parallelism, CancellationToken cancellationToken)
    {
        string manifestPath = CompilerWire.ResolvePath(directory, "toolchain.json");
        byte[] buffer = new byte[262144];
        (byte[] json, FileStamp manifestStamp) = ReadManifest(manifestPath, buffer, cancellationToken);
        ToolchainManifest manifest = CompilerWire.ParseToolchainManifest(json);
        ToolchainFile[] files = manifest.Files;
        // Same path rules as the serial reader, all checked before any file is opened.
        string[] paths = files.Select(file => CompilerWire.ResolvePath(directory, file.Path)).ToArray();
        Result[] results = new Result[files.Length];
        long reserved = 0;
        try
        {
            Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                () => new byte[262144], (index, loop, own) =>
                {
                    try { results[index] = Hash(paths[index], own, ref reserved, cancellationToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { results[index] = new(0, null, default, ExceptionDispatchInfo.Capture(exception)); loop.Stop(); }
                    return own;
                }, _ => { });
        }
        catch (Exception exception) when (exception is OperationCanceledException or AggregateException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        // Failures are reported in manifest order, as the serial reader does.
        Entry[] entries = new Entry[files.Length + 1];
        entries[0] = new("toolchain.json", manifestPath, manifestStamp);
        for (int index = 0; index < files.Length; index++)
        {
            Result result = results[index];
            if (result.Failure is { } failure) failure.Throw();
            if (result.Sha256 is null) continue; // Not reached: a stopped loop always recorded the failure that stopped it.
            if (result.Sha256 != files[index].Sha256)
                throw new InvalidDataException($"Compiler toolchain integrity check failed: {files[index].Path}");
            entries[index + 1] = new(files[index].Path, paths[index], result.Stamp);
        }
        if (results.Any(result => result.Sha256 is null))
            throw new InvalidDataException("Compiler toolchain integrity check did not complete.");
        CompilerWire.CheckToolchainFingerprints(manifest);
        return new(directory, manifest, entries);
    }

    private readonly record struct Result(long Length, string? Sha256, FileStamp Stamp, ExceptionDispatchInfo? Failure);

    private static (byte[] Bytes, FileStamp Stamp) ReadManifest(string path, byte[] buffer, CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = CompilerWire.OpenRead(path);
        FileStamp stamp = FileStamp.Of(handle, path);
        try
        {
            if (RandomAccess.GetLength(handle) > CompilerWire.MaximumResponseBytes)
                throw new InvalidDataException($"File exceeds {CompilerWire.MaximumResponseBytes} bytes: {path}");
            using MemoryStream output = new();
            int count;
            while ((count = RandomAccess.Read(handle, buffer, output.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Length + count > CompilerWire.MaximumResponseBytes)
                    throw new InvalidDataException($"File exceeds {CompilerWire.MaximumResponseBytes} bytes: {path}");
                output.Write(buffer, 0, count);
            }
            return (output.ToArray(), stamp);
        }
        catch (NotSupportedException exception) { throw new IOException($"Only regular seekable files can be read: {path}", exception); }
    }

    // Same open rules, per-file bound and total bound as the serial reader. The total is reserved before reading,
    // so parallel readers never read more than the serial reader could.
    private static Result Hash(string path, byte[] buffer, ref long reserved, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SafeFileHandle handle = CompilerWire.OpenRead(path);
        // Stamped before the first read: any later write or replacement moves the stamp, even one during hashing.
        FileStamp stamp = FileStamp.Of(handle, path);
        try
        {
            long declared = RandomAccess.GetLength(handle);
            if (declared > CompilerWire.MaximumToolchainFileBytes)
                throw new InvalidDataException($"File exceeds {CompilerWire.MaximumToolchainFileBytes} bytes: {path}");
            if (Interlocked.Add(ref reserved, declared) > CompilerWire.MaximumToolchainBytes)
                throw new InvalidDataException($"Compiler toolchain integrity check failed: {path}");
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            int count;
            while ((count = RandomAccess.Read(handle, buffer, length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                length += count;
                if (length > CompilerWire.MaximumToolchainFileBytes)
                    throw new InvalidDataException($"File exceeds {CompilerWire.MaximumToolchainFileBytes} bytes: {path}");
                if (length > declared && Interlocked.Add(ref reserved, count) > CompilerWire.MaximumToolchainBytes)
                    throw new InvalidDataException($"Compiler toolchain integrity check failed: {path}");
                hash.AppendData(buffer, 0, count);
            }
            return new(length, Convert.ToHexStringLower(hash.GetHashAndReset()), stamp, null);
        }
        catch (NotSupportedException exception) { throw new IOException($"Only regular seekable files can be read: {path}", exception); }
    }

    /// <summary>True only if every verified path still resolves under the same rules to a file whose identity stamp
    /// equals the one taken when it was hashed. Cheap (one stat per file, no read); a false answer is never an error by
    /// itself, it only obliges the caller to verify again before using the result.</summary>
    internal bool Unchanged()
    {
        foreach (Entry entry in _entries)
        {
            try
            {
                // Without a native file identity the path rules are re-checked too; with one, an equal device/inode
                // already proves it is the same hashed file, whatever path led there.
                if (!entry.Stamp.Native && CompilerWire.ResolvePath(Directory, entry.RelativePath) != entry.FullPath) return false;
                if (FileStamp.Of(entry.FullPath) != entry.Stamp) return false;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { return false; }
        }
        return true;
    }
}

/// <summary>File identity for change detection inside one pass, never for trust. On Linux: device, inode, size and the
/// modification and status-change times in nanoseconds (the status-change time cannot be set back by a writer).
/// Elsewhere, or when statx is unavailable: size, last write and creation time from the file system API.</summary>
internal readonly record struct FileStamp(bool Native, ulong Device, ulong Inode, long Length,
    long ModifiedSeconds, uint ModifiedNanoseconds, long ChangedSeconds, uint ChangedNanoseconds)
{
    private const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100, AtEmptyPath = 0x1000;
    private const uint Required = 0x40 | 0x80 | 0x100 | 0x200; // STATX_MTIME | STATX_CTIME | STATX_INO | STATX_SIZE
    private const uint BasicStats = 0x7ff;
    private static int _statxUnavailable;

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, [Out] byte[] buffer);

    /// <summary>Identity of the open file itself (the inode that is about to be hashed).</summary>
    internal static FileStamp Of(SafeFileHandle handle, string path)
    {
        if (OperatingSystem.IsLinux() && Volatile.Read(ref _statxUnavailable) == 0)
        {
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                if (TryNative((int)handle.DangerousGetHandle(), "", AtEmptyPath | AtSymlinkNoFollow) is { } stamp) return stamp;
            }
            catch (FileNotFoundException) { } // Not for an open handle; the portable stamp below decides.
            finally { if (added) handle.DangerousRelease(); }
        }
        return Portable(path);
    }

    /// <summary>Identity of whatever the path names now; the final component is not followed.</summary>
    internal static FileStamp Of(string path)
    {
        if (OperatingSystem.IsLinux() && Volatile.Read(ref _statxUnavailable) == 0 &&
            TryNative(AtFdCwd, path, AtSymlinkNoFollow) is { } stamp) return stamp;
        return Portable(path);
    }

    private static FileStamp? TryNative(int directory, string path, int flags)
    {
        byte[] buffer = new byte[256];
        int status;
        try { status = Statx(directory, path, flags, BasicStats, buffer); }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        { Volatile.Write(ref _statxUnavailable, 1); return null; }
        if (status != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 2 or 20) throw new FileNotFoundException("A verified toolchain file is missing.", path); // ENOENT, ENOTDIR
            // ENOSYS/EPERM (old kernel, seccomp): use the portable stamp from now on. Anything else: portable this once.
            if (error is 38 or 1) Volatile.Write(ref _statxUnavailable, 1);
            return null;
        }
        ReadOnlySpan<byte> s = buffer;
        if ((MemoryMarshal.Read<uint>(s) & Required) != Required) return null;
        ulong device = ((ulong)MemoryMarshal.Read<uint>(s[136..]) << 32) | MemoryMarshal.Read<uint>(s[140..]);
        return new(true, device, MemoryMarshal.Read<ulong>(s[32..]), (long)MemoryMarshal.Read<ulong>(s[40..]),
            MemoryMarshal.Read<long>(s[112..]), MemoryMarshal.Read<uint>(s[120..]),
            MemoryMarshal.Read<long>(s[96..]), MemoryMarshal.Read<uint>(s[104..]));
    }

    private static FileStamp Portable(string path)
    {
        FileInfo info = new(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new FileNotFoundException("A verified toolchain file is missing or no longer a regular file.", path);
        return new(false, 0, 0, info.Length, info.LastWriteTimeUtc.Ticks, 0, info.CreationTimeUtc.Ticks, 0);
    }
}
