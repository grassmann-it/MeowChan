using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Gallery;
using MeowChan.Persistence;
using MeowChan.Storage;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.Desktop;

internal static class GallerySidebarOriginal
{
    internal static Task<Stream> OpenAsync(GalleryEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (entry.Manifest.FindById(entry.Asset.AssetId) != entry.Asset)
            throw new InvalidDataException("The requested original does not belong to this manifest.");
        VerifiedFileMemo memo = GalleryImageCache.Shared.VerifiedFiles;
        return OperatingSystem.IsWindows() ? OpenWindowsAsync(entry, memo, token)
            : entry.Manifest.OpenSessionVerifiedReadAsync(entry.Asset.AssetId, memo, token);
    }

    private static async Task<Stream> OpenWindowsAsync(GalleryEntry entry, VerifiedFileMemo memo, CancellationToken token)
    {
        string path = entry.Asset.Path;
        // This regular-file handle denies writers and renames until the returned read handle is open.
        using FileStream observation = RegularProfileFile.OpenRead(path);
        if (observation.Length != entry.Asset.Length) throw new InvalidDataException("The original changed length.");
        RegularProfileFileIdentity? identity = RegularProfileFile.TryGetIdentity(observation);
        // On Windows timestamps alone can survive a same-length rewrite. Use the file's last journal record as well.
        long? sequence = identity.HasValue ? ReadChangeSequence(observation.SafeFileHandle) : null;
        if (sequence.HasValue && memo.IsVerified(path, identity, entry.Asset.Sha256, sequence))
            return RegularProfileFile.OpenRead(path);
        Stream verified = await entry.Manifest.OpenVerifiedReadAsync(entry.Asset.AssetId, token).ConfigureAwait(false);
        if (sequence.HasValue) memo.Remember(path, identity, entry.Asset.Sha256, sequence);
        return verified;
    }

    private static long? ReadChangeSequence(SafeFileHandle file)
    {
        const uint readFileUsnData = 0x000900EB;
        byte[] record = new byte[1024]; // USN_RECORD header and one NTFS filename
        if (!DeviceIoControl(file, readFileUsnData, IntPtr.Zero, 0, record, record.Length, out int size, IntPtr.Zero)
            || size < 8) return null;
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4)) switch { 2 => 24, 3 => 40, _ => -1 };
        if (offset < 0 || size < offset + sizeof(long)) return null;
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(offset));
        return sequence > 0 ? sequence : null;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize,
        [Out] byte[] output, int outputSize, out int bytesReturned, IntPtr overlapped);
}
