using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan.Transfer;

internal sealed partial class NativePrivateApplyJournal
{
    private ProfileDirectoryIdentity? _rootIdentity;
    private readonly Dictionary<Guid, ProfileDirectoryIdentity> _operationIdentities = new();

    private void CheckParents()
    {
        _lease.EnsureDirectoryIdentity();
        CheckDirectory(_lease.ProfileDirectory);
        RejectLinks(_root);
        if (Exists(_root))
        {
            CheckDirectory(_root);
            _rootIdentity ??= ProfileDirectoryIdentity.Capture(_root);
            _rootIdentity.EnsureMatches(_root);
        }
        else if (_rootIdentity is not null) throw Invalid();
    }

    private void CheckOperationDirectory(Guid id)
    {
        string directory = OperationDirectory(id);
        CheckDirectory(directory);
        if (!_operationIdentities.TryGetValue(id, out ProfileDirectoryIdentity? identity))
        {
            identity = ProfileDirectoryIdentity.Capture(directory);
            _operationIdentities.Add(id, identity);
        }
        identity.EnsureMatches(directory);
    }

    private void EnsureRoot()
    {
        CheckParents();
        if (!Exists(_root))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_root);
            else Directory.CreateDirectory(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        CheckParents();
    }

    private static void EnsureNewOperationDirectory(string path)
    {
        RejectLinks(path);
        if (Exists(path)) throw Invalid();
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CheckDirectory(path);
        if (Directory.EnumerateFileSystemEntries(path).Any()) throw Invalid();
    }

    private static void CheckDirectory(string path)
    {
        RejectLinks(path);
        if (!Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) == 0) throw Invalid();
    }

    private static bool Exists(string path)
    {
        // File.Exists hides access errors and directories. GetAttributes does not
        // treat either an unreadable entry or a dangling reparse/link as absence.
        RejectLinks(path);
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void RejectLinks(string path)
    {
        try { ArchiveExportMetadataFile.RejectLinks(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { throw Invalid(); }
    }

    private string[] OperationDirectories()
    {
        CheckParents();
        var paths = new List<string>();
        foreach (string path in Directory.EnumerateFileSystemEntries(_root))
        {
            if (paths.Count >= MaximumOperations) throw Invalid();
            _ = ParseOperationName(Path.GetFileName(path));
            CheckDirectory(path);
            paths.Add(path);
        }
        paths.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        return paths.ToArray();
    }

    private static Guid ParseOperationName(string name)
    {
        if (!Guid.TryParseExact(name, "N", out Guid id) || id == Guid.Empty || name != id.ToString("N")) throw Invalid();
        return id;
    }

    // Unknown/pending evidence is preserved and fails closed. A gap is not an
    // empty chain. Enumeration and record bytes are bounded independently.
    private static int ValidateOperationDirectory(string directory)
    {
        CheckDirectory(directory);
        var names = new HashSet<string>(StringComparer.Ordinal);
        int max = 0, events = 0;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            if (names.Count >= MaximumEvents + 3) throw Invalid();
            string name = Path.GetFileName(path);
            if (!names.Add(name)) throw Invalid();
            RejectLinks(path);
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw Invalid();
            if (name is "claim.json" or "request.json" or "receipt.json") continue;
            if (name.Length != 15 || !name.StartsWith("event-", StringComparison.Ordinal)
                || !name.EndsWith(".json", StringComparison.Ordinal)
                || !int.TryParse(name.AsSpan(6, 4), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int sequence)
                || sequence < 1 || sequence > MaximumEvents || name != EventName(sequence)) throw Invalid();
            events++; max = Math.Max(max, sequence);
        }
        if (!names.Contains("claim.json") || !names.Contains("request.json") || events != max) throw Invalid();
        return events;
    }

    private JournalClaim ReadClaim(Guid id)
    {
        JournalClaim claim = ParseClaim(ReadBounded(Path.Combine(OperationDirectory(id), "claim.json"), MaximumRecordBytes));
        if (claim.OperationId != id) throw Invalid();
        return claim;
    }

    // No inventory, claim, payload, chain or target access on this path. Missing
    // receipt is distinct from a present but malformed/unreadable receipt.
    private JournalReceipt? ReadHistoricalReceipt(Guid id)
    {
        ValidateOperationId(id);
        CheckParents();
        string directory = OperationDirectory(id);
        if (!Exists(directory))
        {
            if (_operationIdentities.ContainsKey(id)) throw Invalid();
            return null;
        }
        CheckOperationDirectory(id);
        string path = Path.Combine(directory, "receipt.json");
        if (!Exists(path)) return null;
        JournalReceipt receipt = ParseReceipt(ReadBounded(path, MaximumRecordBytes));
        if (receipt.Value.OperationGuid != id) throw Invalid();
        CheckFinalReceipt(receipt.Value);
        return receipt;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        RejectLinks(path);
        using FileStream input = RegularProfileFile.OpenRead(path);
        long sampledLength = input.Length;
        if (sampledLength <= 0 || sampledLength > maximum) throw Invalid();
        byte[] bytes = new byte[(int)sampledLength];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1 || input.Length != sampledLength) throw Invalid();
        return bytes;
    }

    private static void PublishCreateOnly(string path, byte[] bytes, int maximum)
    {
        if (bytes.Length <= 0 || bytes.Length > maximum) throw Invalid();
        RejectLinks(path);
        CheckDirectory(Path.GetDirectoryName(path)!);
        if (Exists(path)) { CompareExisting(path, bytes, maximum); return; }
        string pending = path + ".pending-" + Guid.NewGuid().ToString("N");
        bool ownPending = false, attempted = false, acknowledged = false;
        try
        {
            RejectLinks(pending);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write,
                Share = FileShare.None, BufferSize = 4096, Options = FileOptions.SequentialScan };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (FileStream output = new(pending, options))
            {
                ownPending = true;
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            // Before publication validate the exact file, not just in-memory bytes.
            CompareExisting(pending, bytes, maximum);
            RejectLinks(path); CheckDirectory(Path.GetDirectoryName(path)!);
            attempted = true;
            bool published = false;
            try
            {
                if (OperatingSystem.IsWindows()) File.Move(pending, path, overwrite: false);
                else if (LinkNoOverwrite(pending, path) != 0) throw new IOException("Private journal publication was not acknowledged.");
                published = true;
            }
            finally
            {
                // Even a failed syscall/ack may have published. Uncancelled exact
                // same-name readback is mandatory; never pick another operation.
                if (Exists(path))
                {
                    CompareExisting(path, bytes, maximum);
                    acknowledged = true;
                }
            }
            if (!published && !acknowledged) throw Invalid();
        }
        catch (IOException) when (attempted && acknowledged)
        {
            // Exact occupied bytes reconcile this logical publication; no replace.
        }
        finally
        {
            // Only our known prepublication stage or a stage whose final bytes
            // were exactly acknowledged can be removed. Uncertain attempts stay.
            if (ownPending && (!attempted || acknowledged))
            {
                RejectLinks(pending);
                try { File.Delete(pending); }
                catch (FileNotFoundException) { }
                // Failed cleanup is observable, not a false successful operation.
            }
        }
    }

    private static void CompareExisting(string path, byte[] expected, int maximum)
    {
        if (!ReadBounded(path, maximum).AsSpan().SequenceEqual(expected)) throw Invalid();
    }

    private static string EventName(int sequence) => "event-" + sequence.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + ".json";
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void CheckRecordSize(byte[] bytes) { if (bytes.Length <= 0 || bytes.Length > MaximumRecordBytes) throw Invalid(); }

    [DllImport("System.Native", EntryPoint = "SystemNative_Link", SetLastError = true)]
    private static extern int LinkNoOverwrite([MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);
}
