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

    internal Task RememberReadingPositionAsync(LocalReadingPosition position, long generation) => Task.Run(async () =>
    {
        position.Validate();
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (generation != ReadingPositionGeneration || !Config.RememberReadingPosition) return;
            EnsureReady();
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
                // There is deliberately no backup/history of this privacy-sensitive single entry.
                if (generation == ReadingPositionGeneration && Config.RememberReadingPosition)
                    File.Move(pending, destination, overwrite: true);
            }
            finally { File.Delete(pending); }
        }
        finally { _commands.Release(); }
    });

    internal Task<LocalReadingPosition?> ReadReadingPositionAsync() => Task.Run(async () =>
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
        InvalidateReadingPositionWrites();
        return Task.Run(async () =>
        {
            await _commands.WaitAsync().ConfigureAwait(false);
            try { EnsureReady(); DeleteReadingPositionFile(); }
            finally { _commands.Release(); }
        });
    }

    private void DeleteReadingPositionFile() => File.Delete(ReadingPositionPath);
}
