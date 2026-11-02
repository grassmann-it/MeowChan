using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Desktop;

internal sealed record StartupPreviewRow(bool IsBoard, string Source, string Board, string Title,
    int KnownMedia, int CompletedMedia);

internal sealed record StartupPreviewSnapshot(DateTimeOffset CapturedAtUtc, int ThreadCount,
    int BoardCount, IReadOnlyList<StartupPreviewRow> Rows);

/// <summary>Disposable text-only display cache, never a session or an authority source.</summary>
internal sealed class StartupPreviewStore
{
    internal const int MaximumRows = 2000;
    internal const int MaximumBytes = 1048576;
    internal const int MaximumTextLength = 512;
    private readonly ProfileLease _lease;
    private readonly string _path;

    internal StartupPreviewStore(string profileDirectory, ProfileLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.EnsureMatches(profileDirectory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "startup-preview.json");
    }

    internal StartupPreviewSnapshot? Load(CancellationToken cancellationToken = default) =>
        _lease.RunExclusive(() =>
        {
            _lease.EnsureMatches(Path.GetDirectoryName(_path)!);
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks();
            byte[] bytes;
            try
            {
                using FileStream file = new(_path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                bytes = ReadBounded(file, cancellationToken);
            }
            catch (FileNotFoundException) { return null; }

            try
            {
                using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
                JsonElement root = document.RootElement;
                RequireFields(root, "schemaVersion", "capturedAtUtc", "threadCount", "boardCount", "rows");
                if (Integer(root, "schemaVersion") != 1)
                    throw Invalid("Unsupported schema version.");
                JsonElement captured = root.GetProperty("capturedAtUtc");
                if (captured.ValueKind != JsonValueKind.String || !captured.TryGetDateTimeOffset(out DateTimeOffset at))
                    throw Invalid("Invalid capture timestamp.");
                JsonElement rows = root.GetProperty("rows");
                if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > MaximumRows)
                    throw Invalid("Invalid row count.");
                var parsed = new StartupPreviewRow[rows.GetArrayLength()];
                int index = 0;
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireFields(row, "isBoard", "source", "board", "title", "knownMedia", "completedMedia");
                    JsonElement kind = row.GetProperty("isBoard");
                    if (kind.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw Invalid("Invalid row kind.");
                    parsed[index++] = new(kind.GetBoolean(), Text(row, "source"), Text(row, "board"),
                        Text(row, "title"), Integer(row, "knownMedia"), Integer(row, "completedMedia"));
                }
                return CopyAndValidate(new(at, Integer(root, "threadCount"), Integer(root, "boardCount"), parsed), cancellationToken);
            }
            catch (JsonException error) { throw new InvalidDataException("Invalid startup preview JSON.", error); }
        });

    internal void Save(StartupPreviewSnapshot snapshot, CancellationToken cancellationToken = default) =>
        _lease.RunExclusive(() =>
        {
            _lease.EnsureMatches(Path.GetDirectoryName(_path)!);
            cancellationToken.ThrowIfCancellationRequested();
            StartupPreviewSnapshot captured = CopyAndValidate(snapshot, cancellationToken);
            byte[] bytes = Encode(captured, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks();
            string temporary = Path.Combine(_lease.ProfileDirectory, ".startup-preview-" + Guid.NewGuid().ToString("N") + ".tmp");
            bool ownsTemporary = false;
            try
            {
                FileStreamOptions options = new()
                {
                    Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None
                };
                if (!OperatingSystem.IsWindows())
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (FileStream file = new(temporary, options))
                {
                    ownsTemporary = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    file.Write(bytes);
                    file.Flush(flushToDisk: true);
                }
                // Unlike authoritative stores, a malformed/future cache can be replaced.
                cancellationToken.ThrowIfCancellationRequested();
                RejectLinks();
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, _path, overwrite: true);
                ownsTemporary = false;
            }
            finally
            {
                if (ownsTemporary)
                {
                    try { File.Delete(temporary); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            return true;
        });

    private static byte[] ReadBounded(Stream stream, CancellationToken cancellationToken)
    {
        // Do not trust Length: an already-open file may grow. Consume at most cap + 1.
        byte[] buffer = new byte[MaximumBytes + 1];
        int count = 0;
        while (count < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, count, buffer.Length - count);
            if (read == 0) break;
            count += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (count > MaximumBytes) throw Invalid("Byte limit exceeded.");
        return buffer.AsSpan(0, count).ToArray();
    }

    private static StartupPreviewSnapshot CopyAndValidate(StartupPreviewSnapshot snapshot, CancellationToken token)
    {
        if (snapshot is null || snapshot.Rows is null || snapshot.CapturedAtUtc == default ||
            snapshot.CapturedAtUtc.Offset != TimeSpan.Zero || snapshot.ThreadCount < 0 || snapshot.BoardCount < 0 ||
            snapshot.Rows.Count > MaximumRows)
            throw Invalid("Invalid snapshot.");
        var rows = new StartupPreviewRow[snapshot.Rows.Count];
        int threads = 0, boards = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            StartupPreviewRow row = snapshot.Rows[i];
            if (row is null || !ValidText(row.Source, false) || !ValidText(row.Board, false) ||
                !ValidText(row.Title, true) || row.KnownMedia < 0 || row.CompletedMedia < 0 || row.CompletedMedia > row.KnownMedia)
                throw Invalid("Invalid display row.");
            rows[i] = row with { }; // No caller-owned row/list retained.
            if (row.IsBoard) boards++; else threads++;
        }
        if (snapshot.ThreadCount < threads || snapshot.BoardCount < boards)
            throw Invalid("Totals are smaller than the displayed row counts.");
        token.ThrowIfCancellationRequested();
        return new(snapshot.CapturedAtUtc, snapshot.ThreadCount, snapshot.BoardCount, Array.AsReadOnly(rows));
    }

    private static bool ValidText(string? value, bool allowEmpty)
    {
        if (value is null || value.Length > MaximumTextLength || (!allowEmpty && value.Length == 0)) return false;
        foreach (char character in value)
            if (char.IsControl(character)) return false;
        return true;
    }

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (Array.IndexOf(fields, property.Name) < 0 || !seen.Add(property.Name) || property.Value.ValueKind == JsonValueKind.Null)
                throw Invalid("Unknown, duplicate or null field.");
        }
        if (seen.Count != fields.Length) throw Invalid("Missing field.");
    }

    private static int Integer(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result)) throw Invalid("Expected an integer.");
        return result;
    }

    private static string Text(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String) throw Invalid("Expected display text.");
        return value.GetString()!;
    }

    private static byte[] Encode(StartupPreviewSnapshot snapshot, CancellationToken token)
    {
        using CappedBuffer buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { MaxDepth = 8 }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("capturedAtUtc", snapshot.CapturedAtUtc);
            writer.WriteNumber("threadCount", snapshot.ThreadCount);
            writer.WriteNumber("boardCount", snapshot.BoardCount);
            writer.WriteStartArray("rows");
            foreach (StartupPreviewRow row in snapshot.Rows)
            {
                token.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteBoolean("isBoard", row.IsBoard);
                writer.WriteString("source", row.Source);
                writer.WriteString("board", row.Board);
                writer.WriteString("title", row.Title);
                writer.WriteNumber("knownMedia", row.KnownMedia);
                writer.WriteNumber("completedMedia", row.CompletedMedia);
                writer.WriteEndObject();
                writer.Flush(); // Bound actual escaped UTF-8 output, not character estimates.
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }
        return buffer.ToArray();
    }

    private sealed class CappedBuffer : MemoryStream
    {
        public CappedBuffer() : base(MaximumBytes) { }
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }
        private void Check(int count)
        {
            if (Position + count > MaximumBytes) throw Invalid("Encoded byte limit exceeded.");
        }
    }

    private void RejectLinks()
    {
        // Check the fixed leaf AND every ancestor, even if the leaf is absent.
        string? current = _path;
        bool leaf = true;
        while (current is not null)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("Linked cache paths are not allowed.");
                if (leaf && (attributes & FileAttributes.Directory) != 0) throw Invalid("Cache must be a regular file.");
            }
            catch (FileNotFoundException) when (leaf) { }
            current = Path.GetDirectoryName(current);
            leaf = false;
        }
    }

    private static InvalidDataException Invalid(string message) => new("Startup preview: " + message);
}
