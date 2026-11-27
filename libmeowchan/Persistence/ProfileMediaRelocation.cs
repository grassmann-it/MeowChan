using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using MeowChan.Archive;
using Microsoft.Data.Sqlite;

namespace MeowChan.Persistence;

/// <summary>Rebinds managed paths on the staged working copy. The snapshot keeps the historical paths.</summary>
internal sealed class ProfileMediaRelocation(string source, string downloads, string target, string newDownloads)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] PathDocuments =
    [
        "settings.json", "settings.json.bak", "saved_urls.json", "saved_urls.json.bak",
        "booru-catalog.json", "booru-catalog.json.bak", "private-tags.json", "private-tags.json.bak",
        "local-imports.json", "local-imports.json.bak"
    ];

    internal static (string[] Roots, ProfileExternalMedia? Media) Prepare(string source, string staged, string target, string legacyDownloads, CancellationToken token)
    {
        string downloads = ReadDownloads(staged, legacyDownloads);
        string? local = RelativeTo(source, downloads);
        if (local is null && RelativeTo(downloads, source) is not null)
            throw new InvalidDataException("The download root contains the source profile. It cannot be copied safely.");
        string managed = local is null ? Path.Combine(target, "upgrade-downloads") : Path.Combine(target, local);
        var relocation = new ProfileMediaRelocation(source, downloads, target, managed);

        // An external managed directory is copied to a private destination for the new generation.
        // Read-only folder sources keep their locations in both generations.
        ProfileExternalMedia? media = local is null
            ? CopyExternal(downloads, Path.Combine(staged, "upgrade-downloads"), token)
            : null;
        foreach (string name in PathDocuments)
        {
            token.ThrowIfCancellationRequested();
            relocation.RewriteDocument(Path.Combine(staged, name), name is "settings.json" or "settings.json.bak");
        }
        relocation.RewriteArchive(Path.Combine(staged, "archive.db"));
        relocation.RewriteWork(Path.Combine(staged, "work-state.sqlite"));
        relocation.RewriteWork(Path.Combine(staged, "work-state.before-prune.sqlite"));
        var roots = new HashSet<string>(StringComparer.Ordinal) { downloads };
        roots.UnionWith(FolderRoots(staged));
        return (roots.Where(root => RelativeTo(source, root) is null).Order(StringComparer.Ordinal).ToArray(), media);
    }

    internal static string[] CurrentRoots(string directory, string downloads, string[] previous)
    {
        var roots = new HashSet<string>(previous, StringComparer.Ordinal) { downloads };
        roots.UnionWith(FolderRoots(directory));
        return roots.Where(root => RelativeTo(directory, root) is null).Order(StringComparer.Ordinal).ToArray();
    }

    private static string ReadDownloads(string directory, string fallback)
    {
        foreach (string name in new[] { "settings.json", "settings.json.bak" })
        {
            if (ReadDocument(Path.Combine(directory, name)) is not JsonObject settings) continue;
            if (settings["DownloadPath"] is JsonValue value && value.TryGetValue(out string? path)
                && !string.IsNullOrWhiteSpace(path))
                return ProfileLease.NormalizeDirectory(path);
            return fallback;
        }
        return fallback;
    }

    private static JsonNode? ReadDocument(string path)
    {
        try
        {
            using FileStream file = RegularProfileFile.OpenRead(path);
            return JsonNode.Parse(file);
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }

    private static ProfileExternalMedia? CopyExternal(string from, string to, CancellationToken token)
    {
        ArchiveExportMetadataFile.RejectLinks(from);
        if (!Directory.Exists(from)) return null;
        if (Directory.Exists(to) || File.Exists(to))
            throw new InvalidDataException("The staged profile already contains the designated download directory.");
        var (directories, files) = ProfileSnapshot.Inventory(from);
        long total = files.Aggregate(0L, (bytes, file) => checked(bytes + file.Length));
        if (ProfileSnapshot.FreeSpace(Path.GetDirectoryName(to)!) < total)
            throw new IOException("There is insufficient space for a separate copy of the profile downloads.");
        Directory.CreateDirectory(to);
        foreach (string directory in directories) Directory.CreateDirectory(ProfileSnapshot.Child(to, directory));
        var copied = new List<ProfileSnapshotFile>(files.Count);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            string original = ProfileSnapshot.Child(from, file.Name);
            string hash = ProfileSnapshot.CopyFile(original, ProfileSnapshot.Child(to, file.Name), file.Length);
            using FileStream input = RegularProfileFile.OpenRead(original);
            if (!string.Equals(hash, Convert.ToHexString(SHA256.HashData(input)), StringComparison.Ordinal))
                throw new InvalidDataException("A download changed while it was being copied.");
            copied.Add(new(file.Name, file.Length, hash));
        }
        var closing = ProfileSnapshot.Inventory(from);
        if (!directories.SequenceEqual(closing.Directories) || !files.SequenceEqual(closing.Files))
            throw new InvalidDataException("The original download inventory changed during the profile upgrade.");
        return new(from, copied.ToArray());
    }

    private void RewriteDocument(string path, bool settings)
    {
        if (ReadDocument(path) is not JsonObject document) return;
        bool changed = RewriteValues(document);
        if (settings)
        {
            document["DownloadPath"] = newDownloads;
            changed = true;
        }
        if (changed) ProfileSnapshot.PublishJson(path, document, Json);
    }

    private bool RewriteValues(JsonNode node)
    {
        bool changed = false;
        if (node is JsonObject map)
        {
            foreach (var pair in map.ToArray())
            {
                if (TryMap(pair.Value, out string? mapped))
                {
                    map[pair.Key] = mapped;
                    changed = true;
                }
                else if (pair.Value is not null) changed |= RewriteValues(pair.Value);
            }
        }
        else if (node is JsonArray array)
        {
            for (int index = 0; index < array.Count; index++)
            {
                if (TryMap(array[index], out string? mapped))
                {
                    array[index] = mapped;
                    changed = true;
                }
                else if (array[index] is { } child) changed |= RewriteValues(child);
            }
        }
        return changed;
    }

    private bool TryMap(JsonNode? node, out string? mapped)
    {
        mapped = null;
        if (node is not JsonValue value || !value.TryGetValue(out string? path) || path is null) return false;
        mapped = Map(path);
        return !string.Equals(mapped, path, StringComparison.Ordinal);
    }

    private string Map(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        if (RelativeTo(downloads, path) is { } download) return Path.Combine(newDownloads, download);
        return RelativeTo(source, path) is { } profile ? Path.Combine(target, profile) : path;
    }

    private static string? RelativeTo(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return null;
        return relative == "." ? "" : relative;
    }

    private void RewriteArchive(string path)
    {
        if (!File.Exists(path)) return;
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);
        ArchiveSql.ValidateSchema(connection);
        connection.CreateFunction("upgrade_path", (string? value) => value is null ? null : Map(value));
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE archive_threads SET media_directory=upgrade_path(media_directory)
                WHERE media_directory IS NOT upgrade_path(media_directory);
            UPDATE archive_media_directories SET directory=upgrade_path(directory)
                WHERE directory IS NOT upgrade_path(directory);
            UPDATE archive_attachments SET planned_path=upgrade_path(planned_path)
                WHERE planned_path IS NOT upgrade_path(planned_path);
            UPDATE media_decisions SET destination_path=upgrade_path(destination_path),legacy_path=upgrade_path(legacy_path)
                WHERE destination_path IS NOT upgrade_path(destination_path) OR legacy_path IS NOT upgrade_path(legacy_path);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private void RewriteWork(string path)
    {
        if (!File.Exists(path)) return;
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);
        connection.CreateFunction("upgrade_path", (string value) => Map(value));
        connection.CreateFunction("upgrade_payload", (string value) =>
            JsonNode.Parse(value) is { } document && RewriteValues(document) ? document.ToJsonString() : value);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE work_resources SET payload=upgrade_payload(payload) WHERE payload IS NOT upgrade_payload(payload);
            UPDATE work_media SET target=upgrade_path(target),payload=upgrade_payload(payload)
                WHERE target IS NOT upgrade_path(target) OR payload IS NOT upgrade_payload(payload);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string[] FolderRoots(string directory)
    {
        string path = Path.Combine(directory, "folder-sources.db");
        if (!File.Exists(path)) return [];
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM roots";
        using SqliteDataReader rows = command.ExecuteReader();
        var roots = new List<string>();
        while (rows.Read()) roots.Add(ProfileLease.NormalizeDirectory(rows.GetString(0)));
        return roots.ToArray();
    }
}
