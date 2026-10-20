using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using MeowChan.Persistence;

namespace MeowChan.Admission;

/// <summary>Always-durable board admission choices, independent of optional saved URL snapshots.</summary>
public sealed class BoardAdmissionStore
{
    private readonly string _path;
    private readonly ProfileLease _lease;
    private Dictionary<string, BoardAdmissionPolicy> _policies;
    private bool _primaryValid;
    public string? LoadWarning { get; }

    public BoardAdmissionStore(string directory, ProfileLease lease)
    {
        lease.EnsureMatches(directory);
        _lease = lease;
        _path = Path.Combine(lease.ProfileDirectory, "board_admission.json");
        var loaded = lease.RunExclusive(() => Load());
        _policies = loaded.Policies;
        _primaryValid = loaded.PrimaryValid;
        LoadWarning = loaded.Warning;
    }
    public BoardAdmissionPolicy Get(string url) => Volatile.Read(ref _policies).GetValueOrDefault(url) ?? BoardAdmissionPolicy.All;
    public void Save(string url, BoardAdmissionPolicy policy)
    {
        _lease.RunExclusive(() =>
        {
            var next = new Dictionary<string, BoardAdmissionPolicy>(_policies, StringComparer.Ordinal) { [url] = policy };
            BoardAdmissionDocument document = new() { Boards = next.ToDictionary(pair => pair.Key, pair => new BoardPolicyDocument
            { Rules = pair.Value.Rules.ToArray(), Exceptions = new(pair.Value.Exceptions, StringComparer.Ordinal) }, StringComparer.Ordinal) };
            AtomicJsonFile.WriteAllText(_path, JsonSerializer.Serialize(document, BoardAdmissionJsonContext.Default.BoardAdmissionDocument), _primaryValid);
            Volatile.Write(ref _policies, next);
            _primaryValid = true;
            return true;
        });
    }
    private (Dictionary<string, BoardAdmissionPolicy> Policies, bool PrimaryValid, string? Warning) Load()
    {
        try { return (Read(_path), true, null); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidDataException or ArgumentException)
        {
            try { return (Read(_path + ".bak"), false, "Board admission settings were recovered from their backup."); }
            catch (Exception backup) when (backup is FileNotFoundException or DirectoryNotFoundException or JsonException or InvalidDataException or ArgumentException)
            {
                if (ex is FileNotFoundException or DirectoryNotFoundException && backup is FileNotFoundException or DirectoryNotFoundException)
                    return (new(StringComparer.Ordinal), false, null);
                throw new InvalidDataException("Board admission settings could not be read. Existing files were preserved.", ex);
            }
        }
    }
    private static Dictionary<string, BoardAdmissionPolicy> Read(string path)
    {
        var document = JsonSerializer.Deserialize(File.ReadAllText(path), BoardAdmissionJsonContext.Default.BoardAdmissionDocument)
            ?? throw new InvalidDataException("Empty board admission settings.");
        if (document.SchemaVersion != 1 || document.Boards == null) throw new InvalidDataException("Unsupported board admission settings.");
        Dictionary<string, BoardAdmissionPolicy> result = new(StringComparer.Ordinal);
        foreach (var pair in document.Boards)
        {
            if (!Uri.TryCreate(pair.Key, UriKind.Absolute, out _) || pair.Value == null || pair.Value.Rules == null || pair.Value.Exceptions == null)
                throw new InvalidDataException("Invalid board admission settings.");
            result.Add(pair.Key, new BoardAdmissionPolicy(pair.Value.Rules, pair.Value.Exceptions));
        }
        return result;
    }
}
internal sealed class BoardAdmissionDocument
{
    public int SchemaVersion { get; set; } = 1;
    public Dictionary<string, BoardPolicyDocument> Boards { get; set; } = new(StringComparer.Ordinal);
}
internal sealed class BoardPolicyDocument
{
    public ThreadTextRule[] Rules { get; set; } = Array.Empty<ThreadTextRule>();
    public Dictionary<string, ThreadAdmissionException> Exceptions { get; set; } = new(StringComparer.Ordinal);
}
[JsonSerializable(typeof(BoardAdmissionDocument))]
internal partial class BoardAdmissionJsonContext : JsonSerializerContext { }
