using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MeowChan.Persistence;

internal sealed record ExportHistoryLimits(int SizeMiB, int Entries)
{
    internal const int DefaultSizeMiB = 4, MinimumSizeMiB = 1, MaximumSizeMiB = 64;
    internal const int DefaultEntries = 20_000, MinimumEntries = 1, MaximumEntries = 100_000;
    internal const string LimitError = "The configured export history limit was exceeded.";

    private int MaximumBytes => SizeMiB * 1024 * 1024;

    internal string? ReadText(string path)
    {
        try { return RegularProfileFile.ReadBoundedText(path, MaximumBytes); }
        catch (ProfileDocumentTooLargeException) { throw new InvalidDataException(LimitError); }
    }

    internal void CheckCount(int count)
    {
        if (count > Entries) throw new InvalidDataException(LimitError);
    }

    internal void Write<T>(string path, T index, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        CheckCount(count);
        string json = JsonSerializer.Serialize(index);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
            throw new InvalidDataException(LimitError);
        AtomicJsonFile.WriteAllText(path, json, backupCurrentFile: true, token);
    }
}
