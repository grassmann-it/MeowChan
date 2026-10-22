using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace MeowChan.Desktop.Updates;

/// <summary>One profile's update state. The host's profile lease owns the single-writer boundary.</summary>
public sealed class UpdateStateStore
{
    private const int MaximumStateBytes = 16 * 1024;
    public string FilePath { get; }

    public UpdateStateStore(string profileDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        FilePath = Path.Combine(Path.GetFullPath(profileDirectory), "update-state.json");
    }

    public UpdateState Load()
    {
        if (!File.Exists(FilePath)) return new UpdateState();
        using FileStream stream = File.OpenRead(FilePath);
        if (stream.Length > MaximumStateBytes) throw new InvalidDataException("Update state exceeds its size limit.");
        try
        {
            StateDocument? document = JsonSerializer.Deserialize<StateDocument>(stream);
            if (document is null || document.SchemaVersion != 1) throw new InvalidDataException("Unsupported update state.");
            SemanticVersion? skipped = null;
            if (document.SkippedVersion is not null && !SemanticVersion.TryParse(document.SkippedVersion, out skipped))
                throw new InvalidDataException("Invalid skipped update version.");
            UpdateInfo? found = null;
            if (document.FoundVersion is not null)
            {
                if (!SemanticVersion.TryParse(document.FoundVersion, out SemanticVersion? version)
                    || !Uri.TryCreate(document.ReleaseUrl, UriKind.Absolute, out Uri? release)
                    || !Uri.TryCreate(document.DownloadUrl, UriKind.Absolute, out Uri? download))
                    throw new InvalidDataException("Invalid stored update.");
                found = new UpdateInfo(version, release, download);
                if (!UpdateReleaseParser.IsSafeStoredInfo(found)) throw new InvalidDataException("Unsafe stored update destinations.");
            }
            else if (document.ReleaseUrl is not null || document.DownloadUrl is not null)
                throw new InvalidDataException("Incomplete stored update.");
            return new UpdateState(document.LastCheckedUtc, found, skipped);
        }
        catch (JsonException error) { throw new InvalidDataException("Update state contains invalid JSON; it has been preserved.", error); }
    }

    public void Save(UpdateState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        // Refuse to silently replace corrupt state, including corruption since the last read.
        _ = Load();
        if (state.FoundUpdate is not null && !UpdateReleaseParser.IsSafeStoredInfo(state.FoundUpdate))
            throw new InvalidDataException("Unsafe update destinations.");
        StateDocument document = new()
        {
            SchemaVersion = 1,
            LastCheckedUtc = state.LastCheckedUtc,
            FoundVersion = state.FoundUpdate?.Version.ToString(),
            ReleaseUrl = state.FoundUpdate?.ReleaseUri.AbsoluteUri,
            DownloadUrl = state.FoundUpdate?.DownloadUri.AbsoluteUri,
            SkippedVersion = state.SkippedVersion?.ToString()
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        string directory = Path.GetDirectoryName(FilePath)!;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".update-state-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class StateDocument
    {
        public StateDocument() { }
        public int SchemaVersion { get; set; }
        public DateTimeOffset? LastCheckedUtc { get; set; }
        public string? FoundVersion { get; set; }
        public string? ReleaseUrl { get; set; }
        public string? DownloadUrl { get; set; }
        public string? SkippedVersion { get; set; }
    }
}
