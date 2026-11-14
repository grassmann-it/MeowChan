using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private long _readingPositionGeneration;
    internal long ReadingPositionGeneration => Interlocked.Read(ref _readingPositionGeneration);
    private string ReadingPositionPath => Path.Combine(_profileLease.ProfileDirectory, "reading-position.json");
    // The reading history (R5) is written in the same final-write batch as the position: no second write frequency.
    private string ReadingHistoryPath => Path.Combine(_profileLease.ProfileDirectory, "reading-history.json");

    // Invalidate synchronously, before joining the existing profile-owner command queue.
    // A capture already waiting for IO can therefore never resurrect a forgotten anchor.
    private void InvalidateReadingPositionWrites() => Interlocked.Increment(ref _readingPositionGeneration);

    internal Task RememberReadingPositionAsync(LocalReadingPosition position, long generation)
    {
        position.Validate();
        if (generation != ReadingPositionGeneration) return Task.CompletedTask;
        lock (_finalWrites)
        {
            // Recheck after taking the staging lock: an older caller cannot replace a newer
            // pending anchor if forget/disable advanced the generation while this caller waited.
            if (generation != ReadingPositionGeneration) return Task.CompletedTask;
            if (_finalWritesSealed || _finalWriteFamilyState is FinalWriteFamilyState.TerminalNoAuthority or FinalWriteFamilyState.Released)
                return Task.FromException(new FinalWriteUnavailableException("Reading-position family closed."));
            FinalWriteBatch batch = PendingFinalWrite("reading");
            batch.Position = position; batch.Generation = generation; batch.Revision = ++_finalWriteRevision;
            StartFinalWriteLoop("reading");
            return _finalWriteSuspensionError != null ? Task.FromException(_finalWriteSuspensionError) : batch.Completion.Task;
        }
    }

    // The final-writer loop owns _commands. Generation protects forget/disable against every older batch.
    private Task WriteReadingPositionCoreAsync(LocalReadingPosition position, long generation, ProfileAdmissionToken admission)
    {
        _profileLease.Admission.RunWithToken(admission, () =>
        {
            if (generation != ReadingPositionGeneration || !Config.RememberReadingPosition) return true;
            string destination = ReadingPositionPath;
            string pending = destination + ".pending-" + Guid.NewGuid().ToString("N");
            try
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(position, ReadingPositionJsonContext.Default.LocalReadingPosition);
                using (FileStream file = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                if (generation == ReadingPositionGeneration && Config.RememberReadingPosition)
                {
                    File.Move(pending, destination, overwrite: true);
                    WriteReadingHistory(position, generation);
                }
            }
            finally { File.Delete(pending); }
            return true;
        }, ProfileAdmissionKind.Ordinary);
        return Task.CompletedTask;
    }

    // The newest place of this thread goes to the front of the list. A damaged or unreadable list never fails the position write
    // (the position is the contract); the next write starts a fresh list.
    private void WriteReadingHistory(LocalReadingPosition position, long generation)
    {
        string pending = ReadingHistoryPath + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            LocalReadingHistory? current = null;
            try { current = ReadReadingHistoryFile(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { _logger.Warning(error, "The reading history could not be read; starting a new list."); }
            LocalReadingHistory next = LocalReadingHistory.Add(current, position, DateTimeOffset.UtcNow, Config.ReadingHistoryLimit);
            using (FileStream file = new(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(next.Serialize());
                file.Flush(flushToDisk: true);
            }
            if (generation == ReadingPositionGeneration && Config.RememberReadingPosition)
                File.Move(pending, ReadingHistoryPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _logger.Warning(error, "The reading history could not be written."); }
        finally { try { File.Delete(pending); } catch (Exception) { } }
    }

    private LocalReadingHistory? ReadReadingHistoryFile()
    {
        try
        {
            if (new FileInfo(ReadingHistoryPath).Length > LocalReadingHistory.MaximumFileBytes)
                throw new InvalidDataException("The saved reading history is too large.");
            return LocalReadingHistory.Parse(File.ReadAllBytes(ReadingHistoryPath));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>
    /// The recent reading places, newest first, at most <see cref="Config.ReadingHistoryLimit"/>. Empty without the opt-in. A profile
    /// that saved its position before the list existed shows that one position until the next one is remembered.
    /// </summary>
    internal Task<IReadOnlyList<LocalReadingHistoryEntry>> ReadReadingHistoryAsync() =>
        RunInteractiveAsync<IReadOnlyList<LocalReadingHistoryEntry>>(nameof(ReadReadingHistoryAsync), "reading", async () =>
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!Config.RememberReadingPosition) return Array.Empty<LocalReadingHistoryEntry>();
            LocalReadingHistory? history = ReadReadingHistoryFile();
            if (history != null) return history.Newest(Config.ReadingHistoryLimit);
            FileInfo info = new(ReadingPositionPath);
            if (!info.Exists) return Array.Empty<LocalReadingHistoryEntry>();
            if (info.Length > 16384) throw new InvalidDataException("The saved reading position is too large.");
            LocalReadingPosition position = JsonSerializer.Deserialize(File.ReadAllBytes(ReadingPositionPath), ReadingPositionJsonContext.Default.LocalReadingPosition)
                ?? throw new InvalidDataException("The saved reading position is empty.");
            position.Validate();
            return new[] { new LocalReadingHistoryEntry(position, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)) };
        }
        finally { _commands.Release(); }
    });

    internal Task<LocalReadingPosition?> ReadReadingPositionAsync() => RunInteractiveAsync<LocalReadingPosition?>(nameof(ReadReadingPositionAsync), "reading", async () =>
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureReady();
            if (!Config.RememberReadingPosition) return null;
            byte[] bytes;
            try
            {
                if (new FileInfo(ReadingPositionPath).Length > 16384)
                    throw new InvalidDataException("The saved reading position is too large.");
                bytes = File.ReadAllBytes(ReadingPositionPath);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            LocalReadingPosition position = JsonSerializer.Deserialize(bytes, ReadingPositionJsonContext.Default.LocalReadingPosition)
                ?? throw new InvalidDataException("The saved reading position is empty.");
            position.Validate();
            return position;
        }
        finally { _commands.Release(); }
    });

    internal Task ForgetReadingPositionAsync()
    {
        return RunInteractiveAsync(nameof(ForgetReadingPositionAsync), "reading", async () =>
        {
            await _commands.WaitAsync().ConfigureAwait(false);
            try { EnsureReady(); DeleteReadingPositionFile(); }
            finally { _commands.Release(); }
        }, InvalidateReadingPositionWrites);
    }

    // Forgetting (and the opt-out) removes the position and the list; one failing delete never keeps the other file alive.
    private void DeleteReadingPositionFile()
    {
        try { File.Delete(ReadingHistoryPath); }
        finally { File.Delete(ReadingPositionPath); }
    }
}
