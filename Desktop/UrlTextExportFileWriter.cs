using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal enum UrlTextExportFileWriteStatus { Saved, Canceled, Failed, PartialRetained }
internal enum UrlTextExportFileWriteError
{
    None, Canceled, InvalidPlan, InvalidDestination, LimitExceeded, UnsupportedPlatform,
    MissingParent, UnsafeParent, DestinationExists, IoFailure, CreationUncertain
}

/// <summary>
/// Fixed diagnostics only. PartialRetained means output MAY exist, not ownership
/// or proof of file existence. CreationUncertain includes a failed CreateNew
/// attempt with no acquired handle; never infer no-file or permit automatic cleanup.
/// Failed includes known precheck collisions, so it is not a path-absence claim either.
/// </summary>
internal sealed record UrlTextExportFileWriteResult(UrlTextExportFileWriteStatus Status,
    UrlTextExportFileWriteError Error);

/// <summary>
/// Create-only local-path writer for a plan's defensive byte copy, not a consent gate.
/// Parent checks are observations, NOT a check-to-use/rename defense. An absolute
/// path (including a mapped drive/mount) is not proof of a physically local volume.
/// No provider streams, mkdir, overwrite, retry, deletion, or persistent state.
/// </summary>
internal sealed class UrlTextExportFileWriter
{
    internal const int MaximumBytes = 1_048_576;
    internal const int MaximumPathCharacters = 4096;
    // Non-root segments, including the filename; at most this many parent checks.
    internal const int MaximumPathComponents = 128;
    private readonly UrlTextExportAfterOpenTestGate? _afterOpenForTests;

    internal UrlTextExportFileWriter(UrlTextExportAfterOpenTestGate? afterOpenForTests = null)
        => _afterOpenForTests = afterOpenForTests;

    internal Task<UrlTextExportFileWriteResult> WriteAsync(ArchiveUrlTextExportPlan? plan,
        string? destination, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromResult(new UrlTextExportFileWriteResult(UrlTextExportFileWriteStatus.Canceled,
                UrlTextExportFileWriteError.Canceled));
        if (plan is null || plan.ByteCount is <= 0 or > MaximumBytes)
            return Task.FromResult(Failed(UrlTextExportFileWriteError.InvalidPlan));
        UrlTextExportFileWriteError error = ValidatePath(destination, out string? full, out string[]? parts);
        if (error != UrlTextExportFileWriteError.None) return Task.FromResult(Failed(error));
        byte[] bytes = plan.CopyUtf8();
        // Filesystem metadata, open, Flush(true) and Dispose may block. The returned
        // task joins the physical operation; no timeout abandons an owned handle.
        return Task.Run(() => WriteCoreAsync(bytes, full!, parts!, cancellationToken), CancellationToken.None);
    }

    private async Task<UrlTextExportFileWriteResult> WriteCoreAsync(byte[] bytes, string full,
        string[] parts, CancellationToken token)
    {
        FileStream? output = null;
        bool createAttempted = false, handleAcquired = false;
        UrlTextExportFileWriteError error = UrlTextExportFileWriteError.None;
        try
        {
            token.ThrowIfCancellationRequested();
            UrlTextExportFileWriteError parents = InspectParents(full, parts, token);
            if (parents != UrlTextExportFileWriteError.None) return Failed(parents);
            // Includes dangling leaf links, directories, and device entries. No
            // existing leaf is opened, even after a native overwrite confirmation.
            if (new FileInfo(full).LinkTarget != null || LeafExists(full))
                return Failed(UrlTextExportFileWriteError.DestinationExists);
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                BufferSize = 4096, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            token.ThrowIfCancellationRequested();
            // CreateNew is the no-clobber authority even if the leaf precheck races.
            // A throwing constructor can still have caused creation. Mark the
            // attempt BEFORE invoking it; no acquired handle is not a no-file proof.
            createAttempted = true;
            output = new FileStream(full, options);
            handleAcquired = true;
            if (_afterOpenForTests != null)
                await _afterOpenForTests.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await output.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
            await output.FlushAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { error = UrlTextExportFileWriteError.Canceled; }
        catch (Exception) { error = UrlTextExportFileWriteError.IoFailure; }
        finally
        {
            // Success is decided AFTER actual disposal, not in a using-body return.
            // Disposal failure cannot turn an incomplete operation into Saved.
            if (output != null)
                try { output.Dispose(); }
                catch (Exception) { error = UrlTextExportFileWriteError.IoFailure; }
        }
        // Observe cancellation through disposal as well. As with any OS I/O,
        // cancellation is cooperative, not atomic with create or the final result.
        if (error == UrlTextExportFileWriteError.None && token.IsCancellationRequested)
            error = UrlTextExportFileWriteError.Canceled;
        if (error == UrlTextExportFileWriteError.None)
            return new(UrlTextExportFileWriteStatus.Saved, error);
        return ClassifyFailure(createAttempted, handleAcquired, error);
        // Never delete by pathname: even an empty own file could have been replaced.
    }

    // Pure failure-state seam used by the real path, not an injected OS opener.
    // Tests can check the uncertainty contract without pretending to reproduce a
    // constructor failure AFTER its OS create side effect on a particular platform.
    internal static UrlTextExportFileWriteResult ClassifyFailure(bool createAttempted,
        bool handleAcquired, UrlTextExportFileWriteError error)
    {
        if (error == UrlTextExportFileWriteError.None) error = UrlTextExportFileWriteError.IoFailure;
        if (handleAcquired) return new(UrlTextExportFileWriteStatus.PartialRetained, error);
        if (createAttempted) return new(UrlTextExportFileWriteStatus.PartialRetained,
            UrlTextExportFileWriteError.CreationUncertain);
        return new(error == UrlTextExportFileWriteError.Canceled
            ? UrlTextExportFileWriteStatus.Canceled : UrlTextExportFileWriteStatus.Failed, error);
    }

    private static UrlTextExportFileWriteError InspectParents(string full, string[] parts, CancellationToken token)
    {
        string current = Path.GetPathRoot(full)!;
        // Root first: never inspect a descendant through an observed linked parent.
        for (int i = 0; i < parts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i > 0) current = Path.Combine(current, parts[i - 1]);
            try
            {
                if (new DirectoryInfo(current).LinkTarget != null)
                    return UrlTextExportFileWriteError.UnsafeParent;
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                    || (attributes & FileAttributes.Directory) == 0)
                    return UrlTextExportFileWriteError.UnsafeParent;
            }
            catch (FileNotFoundException) { return UrlTextExportFileWriteError.MissingParent; }
            catch (DirectoryNotFoundException) { return UrlTextExportFileWriteError.MissingParent; }
        }
        return UrlTextExportFileWriteError.None;
    }

    private static bool LeafExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static UrlTextExportFileWriteError ValidatePath(string? destination,
        out string? full, out string[]? parts)
    {
        full = null;
        parts = null;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return UrlTextExportFileWriteError.UnsupportedPlatform;
        if (destination is null || destination.Length == 0) return UrlTextExportFileWriteError.InvalidDestination;
        // Bound all subsequent path work/allocation. No shell, URI, CWD, home or
        // environment expansion. Double-leading separators are remote/namespace
        // syntax, rejected even on Unix rather than treated as ordinary '/'.
        if (destination.Length > MaximumPathCharacters) return UrlTextExportFileWriteError.LimitExceeded;
        if (string.IsNullOrWhiteSpace(destination)) return UrlTextExportFileWriteError.InvalidDestination;
        if (destination.StartsWith("//", StringComparison.Ordinal)
            || destination.StartsWith(@"\\", StringComparison.Ordinal)
            || (!OperatingSystem.IsWindows() && destination.Contains('\\'))
            || !Path.IsPathFullyQualified(destination) || Path.EndsInDirectorySeparator(destination))
            return UrlTextExportFileWriteError.InvalidDestination;
        for (int i = 0; i < destination.Length; i++)
        {
            char ch = destination[i];
            if (char.IsControl(ch)) return UrlTextExportFileWriteError.InvalidDestination;
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= destination.Length || !char.IsLowSurrogate(destination[++i]))
                    return UrlTextExportFileWriteError.InvalidDestination;
            }
            else if (char.IsLowSurrogate(ch)) return UrlTextExportFileWriteError.InvalidDestination;
        }
        try
        {
            string root = Path.GetPathRoot(destination)!;
            // On Windows permit only drive-absolute syntax, never mixed-slash UNC,
            // extended/device/NT namespaces. A mapped drive still is not local proof.
            if (OperatingSystem.IsWindows()
                && (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':'))
                return UrlTextExportFileWriteError.InvalidDestination;
            parts = destination[root.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.None);
            if (parts.Length > MaximumPathComponents) return UrlTextExportFileWriteError.LimitExceeded;
            foreach (string part in parts)
            {
                // Reject lexical traversal/aliases before normalization can erase
                // an entered linked component. No silent path repair.
                if (part.Length == 0 || part is "." or ".."
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return UrlTextExportFileWriteError.InvalidDestination;
                if (OperatingSystem.IsWindows()
                    && (part.EndsWith('.') || part.EndsWith(' ') || IsWindowsDeviceName(part)))
                    return UrlTextExportFileWriteError.InvalidDestination;
            }
            full = Path.GetFullPath(destination);
            // No extension enforcement/appending: the picker policy belongs to UI.
            return UrlTextExportFileWriteError.None;
        }
        catch (Exception) { return UrlTextExportFileWriteError.InvalidDestination; }
    }

    private static bool IsWindowsDeviceName(string part)
    {
        int dot = part.IndexOf('.');
        // Win32 device recognition also ignores spaces before an extension.
        string name = (dot < 0 ? part : part[..dot]).TrimEnd(' ');
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
            || name.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (name[3] is >= '0' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
    }

    private static UrlTextExportFileWriteResult Failed(UrlTextExportFileWriteError error)
        => new(UrlTextExportFileWriteStatus.Failed, error);
}

/// <summary>
/// Bounded test-only after-open barrier/fault. No callbacks, stream substitution,
/// path/handle/byte access, or external tasks; production leaves this absent.
/// A timed-out/canceled barrier owns no background work that could outlive disposal.
/// </summary>
internal sealed class UrlTextExportAfterOpenTestGate
{
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool _failAfterOpen;
    internal static readonly TimeSpan MaximumHold = TimeSpan.FromSeconds(10);

    internal UrlTextExportAfterOpenTestGate(bool failAfterOpen = false) => _failAfterOpen = failAfterOpen;
    internal Task Opened => _opened.Task;
    internal void Release() => _release.TrySetResult();

    internal async Task WaitAsync(CancellationToken token)
    {
        _opened.TrySetResult();
        await _release.Task.WaitAsync(MaximumHold, token).ConfigureAwait(false);
        if (_failAfterOpen) throw new IOException("Synthetic URLtext after-open failure.");
    }
}
