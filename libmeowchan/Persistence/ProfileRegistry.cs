using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MeowChan.Persistence;

/// <summary>One known profile folder. <see cref="Id"/> is the profile's own <see cref="ProfileInstanceIdentity"/> once
/// the profile has been opened; <see cref="LastOpenedUtc"/> is null for a profile created but never opened.</summary>
internal sealed record ProfileRegistryEntry(string Name, string Path, Guid? Id, DateTimeOffset? LastOpenedUtc);

internal enum ProfileRegistryState
{
    /// <summary>No registry file yet: an empty list.</summary>
    Missing,
    Valid,
    /// <summary>The file was damaged; its backup was read. The next write keeps a copy of the damaged file.</summary>
    RecoveredFromBackup,
    /// <summary>Neither the file nor its backup could be read: an empty list. The next write keeps a copy of both.</summary>
    Damaged,
    /// <summary>A newer app version wrote the file: shown as far as readable, never written.</summary>
    Unsupported
}

internal sealed record ProfileRegistrySnapshot(IReadOnlyList<ProfileRegistryEntry> Entries, ProfileRegistryState State);

internal enum ProfileRegistryChange { Added, Updated, LimitReached, Refused }

/// <summary>
/// The list of known profiles (plan PLAN-POWER-TAGGEN E5): one small file outside every profile, so each profile stays
/// self-contained and a profile folder never names another one. The list only points at folders: it never creates,
/// moves or deletes a profile, and an entry whose folder is gone stays listed (shown as missing) until the person
/// removes it. Several windows (one per profile) share the file: every change is a read-modify-write under a
/// cross-process lock file and an atomic replacement with a <c>.bak</c> of the previous good state.
/// </summary>
internal sealed class ProfileRegistry
{
    internal const string FileName = "profiles.json";
    internal const int SchemaVersion = 1;
    /// <summary>Most entries; a setting (ProfileRegistryLimit). Lowering it only stops new entries.</summary>
    internal const int DefaultLimit = 32, MinimumLimit = 1, LimitCeiling = 256;
    /// <summary>Fixed ceilings of one entry (documented, not settings): display name and folder path length.</summary>
    internal const int MaximumNameLength = 64, MaximumPathLength = 1024;
    private const int MaximumBytes = 1024 * 1024;
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(3);

    internal string FilePath { get; }

    internal ProfileRegistry(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!System.IO.Path.IsPathFullyQualified(filePath)) throw new ArgumentException("The profile registry needs an absolute path.", nameof(filePath));
        FilePath = System.IO.Path.GetFullPath(filePath);
    }

    /// <summary>The per-user location: <c>%APPDATA%\MeowChan</c> on Windows, <c>~/Library/Application Support/MeowChan</c>
    /// on macOS, <c>$XDG_CONFIG_HOME/MeowChan</c> (else <c>~/.config/MeowChan</c>) elsewhere. Null when the user folder
    /// cannot be resolved. Never inside a profile: the default profiles are <c>.MeowChan</c>/<c>.oChan</c>.</summary>
    internal static string? DefaultFilePath()
    {
        if (OperatingSystem.IsWindows())
            return Rooted(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeowChan", FileName);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Rooted(home, "Library", "Application Support", "MeowChan", FileName);
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        // The XDG base directory specification ignores relative values.
        return !string.IsNullOrWhiteSpace(config) && System.IO.Path.IsPathFullyQualified(config)
            ? System.IO.Path.Combine(config, "MeowChan", FileName) : Rooted(home, ".config", "MeowChan", FileName);
    }

    private static string? Rooted(string root, params string[] parts) =>
        string.IsNullOrWhiteSpace(root) || !System.IO.Path.IsPathFullyQualified(root) ? null : System.IO.Path.Combine([root, .. parts]);

    internal static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static bool SamePath(string left, string right) =>
        string.Equals(ProfileLease.NormalizeDirectory(left), ProfileLease.NormalizeDirectory(right), PathComparison);

    /// <summary>The display name for <paramref name="name"/>: trimmed, control characters removed, at most
    /// <see cref="MaximumNameLength"/> characters; the folder name when nothing is left.</summary>
    internal static string NormalizeName(string? name, string path)
    {
        string text = new string((name ?? string.Empty).Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (text.Length == 0) text = System.IO.Path.GetFileName(ProfileLease.NormalizeDirectory(path)).TrimStart('.');
        if (text.Length == 0) text = ProfileLease.NormalizeDirectory(path);
        if (text.Length > MaximumNameLength)
        {
            int cut = MaximumNameLength;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut].TrimEnd();
        }
        return text;
    }

    /// <summary>The current list. Never writes and never throws for a damaged file (see <see cref="ProfileRegistryState"/>).</summary>
    internal ProfileRegistrySnapshot Read()
    {
        Loaded loaded = Load();
        return new(loaded.Entries, loaded.State);
    }

    /// <summary>
    /// Adds <paramref name="path"/> or updates its entry. A null <paramref name="name"/>, <paramref name="id"/> or
    /// <paramref name="openedUtc"/> keeps what the entry already has. A new entry beyond <paramref name="limit"/> is not
    /// added (<see cref="ProfileRegistryChange.LimitReached"/>); nothing is ever dropped to make room. A file written by
    /// a newer version is never replaced (<see cref="ProfileRegistryChange.Refused"/>).
    /// </summary>
    /// <exception cref="IOException">The registry is locked by another window for too long, or cannot be written.</exception>
    internal ProfileRegistryChange Register(string path, string? name, Guid? id, DateTimeOffset? openedUtc, int limit)
    {
        string normalized = NormalizeEntryPath(path);
        int effectiveLimit = Math.Clamp(limit, MinimumLimit, LimitCeiling);
        return WithLock(() =>
        {
            Loaded loaded = Load();
            if (loaded.State == ProfileRegistryState.Unsupported) return ProfileRegistryChange.Refused;
            List<ProfileRegistryEntry> entries = loaded.Entries.ToList();
            int index = entries.FindIndex(entry => string.Equals(entry.Path, normalized, PathComparison));
            ProfileRegistryChange change;
            if (index >= 0)
            {
                ProfileRegistryEntry current = entries[index];
                entries[index] = current with
                {
                    Name = name == null ? current.Name : NormalizeName(name, normalized),
                    Id = id ?? current.Id,
                    LastOpenedUtc = openedUtc?.ToUniversalTime() ?? current.LastOpenedUtc
                };
                if (entries[index] == current) return ProfileRegistryChange.Updated;
                change = ProfileRegistryChange.Updated;
            }
            else
            {
                if (entries.Count >= effectiveLimit) return ProfileRegistryChange.LimitReached;
                entries.Add(new(NormalizeName(name, normalized), normalized, id, openedUtc?.ToUniversalTime()));
                change = ProfileRegistryChange.Added;
            }
            Write(entries, loaded.State);
            return change;
        });
    }

    /// <summary>Removes the entry of <paramref name="path"/> from the list only; the folder itself is never touched.</summary>
    internal bool Forget(string path)
    {
        string normalized = NormalizeEntryPath(path);
        return WithLock(() =>
        {
            Loaded loaded = Load();
            if (loaded.State == ProfileRegistryState.Unsupported) return false;
            List<ProfileRegistryEntry> entries = loaded.Entries.ToList();
            if (entries.RemoveAll(entry => string.Equals(entry.Path, normalized, PathComparison)) == 0) return false;
            Write(entries, loaded.State);
            return true;
        });
    }

    private static string NormalizeEntryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathFullyQualified(path)) throw new ArgumentException("A profile folder must be an absolute path.", nameof(path));
        string normalized = ProfileLease.NormalizeDirectory(path);
        if (normalized.Length > MaximumPathLength) throw new ArgumentException("The profile folder path is too long.", nameof(path));
        return normalized;
    }

    private sealed record Loaded(IReadOnlyList<ProfileRegistryEntry> Entries, ProfileRegistryState State);

    private Loaded Load()
    {
        ReadResult primary = TryReadFile(FilePath);
        if (primary.Entries != null) return new(primary.Entries, primary.Unsupported ? ProfileRegistryState.Unsupported : ProfileRegistryState.Valid);
        ReadResult backup = TryReadFile(FilePath + ".bak");
        if (primary.Missing && backup.Missing) return new(Array.Empty<ProfileRegistryEntry>(), ProfileRegistryState.Missing);
        if (backup.Entries != null && !backup.Unsupported) return new(backup.Entries, ProfileRegistryState.RecoveredFromBackup);
        if (backup.Unsupported) return new(backup.Entries!, ProfileRegistryState.Unsupported);
        return new(Array.Empty<ProfileRegistryEntry>(), ProfileRegistryState.Damaged);
    }

    private readonly record struct ReadResult(IReadOnlyList<ProfileRegistryEntry>? Entries, bool Missing, bool Unsupported);

    private static ReadResult TryReadFile(string path)
    {
        byte[] bytes;
        try
        {
            FileInfo info = new(path);
            if (!info.Exists) return new(null, Missing: !Directory.Exists(path), false);
            if (info.LinkTarget != null || info.Length > MaximumBytes) return new(null, false, false);
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(null, false, false); }
        try { return Parse(bytes); }
        catch (InvalidDataException) { return new(null, false, false); }
    }

    private static ReadResult Parse(byte[] bytes)
    {
        try
        {
            if (JsonDuplicateProperties.Any(bytes)) throw new InvalidDataException("The profile registry repeats a property.");
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out JsonElement version)
                || !version.TryGetInt32(out int schema) || schema < 1)
                throw new InvalidDataException("The profile registry has no valid version.");
            bool newer = schema > SchemaVersion;
            if (!root.TryGetProperty("profiles", out JsonElement profiles) || profiles.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The profile registry has no list.");
            if (!newer && root.EnumerateObject().Count() != 2) throw new InvalidDataException("The profile registry has unknown properties.");
            List<ProfileRegistryEntry> entries = new();
            foreach (JsonElement item in profiles.EnumerateArray())
            {
                if (entries.Count >= LimitCeiling) throw new InvalidDataException("The profile registry has too many entries.");
                ProfileRegistryEntry entry = ParseEntry(item, newer);
                if (entries.Any(existing => string.Equals(existing.Path, entry.Path, PathComparison)))
                    throw new InvalidDataException("The profile registry lists a folder twice.");
                entries.Add(entry);
            }
            return new(entries, false, newer);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { throw new InvalidDataException("The profile registry is not valid.", error); }
    }

    private static ProfileRegistryEntry ParseEntry(JsonElement item, bool newer)
    {
        if (item.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A profile entry is not an object.");
        if (!newer && item.EnumerateObject().Any(property => property.Name is not ("name" or "path" or "id" or "lastOpenedUtc")))
            throw new InvalidDataException("A profile entry has unknown properties.");
        string path = item.GetProperty("path").GetString() ?? throw new InvalidDataException("A profile entry has no folder.");
        if (!System.IO.Path.IsPathFullyQualified(path) || path.Length > MaximumPathLength || path.Any(char.IsControl))
            throw new InvalidDataException("A profile entry has an invalid folder.");
        path = ProfileLease.NormalizeDirectory(path);
        string name = item.GetProperty("name").GetString() ?? throw new InvalidDataException("A profile entry has no name.");
        if (name.Length == 0 || name.Length > MaximumNameLength || name.Any(char.IsControl))
            throw new InvalidDataException("A profile entry has an invalid name.");
        Guid? id = null;
        if (item.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind != JsonValueKind.Null)
        {
            if (!Guid.TryParseExact(idElement.GetString(), "D", out Guid parsed) || parsed == Guid.Empty)
                throw new InvalidDataException("A profile entry has an invalid id.");
            id = parsed;
        }
        DateTimeOffset? opened = null;
        if (item.TryGetProperty("lastOpenedUtc", out JsonElement openedElement) && openedElement.ValueKind != JsonValueKind.Null)
        {
            if (!DateTimeOffset.TryParseExact(openedElement.GetString(), "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
                throw new InvalidDataException("A profile entry has an invalid time.");
            opened = parsed.ToUniversalTime();
        }
        return new(name, path, id, opened);
    }

    internal static byte[] Serialize(IEnumerable<ProfileRegistryEntry> entries)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteStartArray("profiles");
            foreach (ProfileRegistryEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("name", entry.Name);
                writer.WriteString("path", entry.Path);
                if (entry.Id is { } id) writer.WriteString("id", id.ToString("D")); else writer.WriteNull("id");
                if (entry.LastOpenedUtc is { } opened) writer.WriteString("lastOpenedUtc", opened.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                else writer.WriteNull("lastOpenedUtc");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private void Write(IReadOnlyList<ProfileRegistryEntry> entries, ProfileRegistryState state)
    {
        // Only a good primary becomes the backup; after a damaged read the damaged file is kept as a copy instead.
        AtomicJsonFile.WriteAllText(FilePath, Encoding.UTF8.GetString(Serialize(entries)), backupCurrentFile: state == ProfileRegistryState.Valid);
    }

    private T WithLock<T>(Func<T> operation)
    {
        string directory = System.IO.Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        string lockPath = FilePath + ".lock";
        DateTime deadline = DateTime.UtcNow + LockWait;
        while (true)
        {
            FileStream? stream = null;
            try { stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); continue; }
            catch (IOException error) { throw new IOException("The profile list is in use by another window. Try again.", error); }
            // The lock file stays: deleting it could let two windows lock different files.
            using (stream) return operation();
        }
    }
}
