using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    private long _readingPositionGeneration;
    internal long ReadingPositionGeneration => Interlocked.Read(ref _readingPositionGeneration);
    private string ReadingPositionPath => Path.Combine(_profileLease.ProfileDirectory, "reading-position.json");

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
            if (_finalWritesSealed) return Task.FromException(new InvalidOperationException("Final reading-position writes are closed."));
            FinalWriteBatch batch = PendingFinalWrite("reading");
            batch.Position = position; batch.Generation = generation;
            StartFinalWriteLoop("reading");
            return batch.Completion.Task;
        }
    }

    // The final-writer loop owns _commands. Generation protects forget/disable against every older batch.
    private Task WriteReadingPositionCoreAsync(LocalReadingPosition position, long generation)
    {
        if (generation != ReadingPositionGeneration || !Config.RememberReadingPosition) return Task.CompletedTask;
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
                File.Move(pending, destination, overwrite: true);
        }
        finally { File.Delete(pending); }
        return Task.CompletedTask;
    }

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

    private void DeleteReadingPositionFile() => File.Delete(ReadingPositionPath);
}
