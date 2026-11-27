using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using MeowChan.Archive;
using Microsoft.Data.Sqlite;

namespace MeowChan.Persistence;

/// <summary>Inventories legacy write destinations and rebinds them when materializing a snapshot.</summary>
internal sealed class ProfileMediaRelocation(Dictionary<string, string> paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly string[] SettingsDocuments = ["settings.json", "settings.json.bak"];
    private static readonly string[] SessionDocuments = ["saved_urls.json", "saved_urls.json.bak"];
    private static readonly string[] BooruDocuments = ["booru-catalog.json", "booru-catalog.json.bak"];
    private static readonly string[] WorkDatabases = ["work-state.sqlite", "work-state.before-prune.sqlite"];
    private static readonly string[] PathDocuments =
    [
        .. SessionDocuments, .. BooruDocuments, "private-tags.json", "private-tags.json.bak",
        "local-imports.json", "local-imports.json.bak"
    ];

    internal static string[] ManagedRoots(string source, string staged, string legacyDownloads, CancellationToken token)
    {
        HashSet<string> directories = new(PathComparer) { source };
        HashSet<string> files = new(PathComparer);
        foreach (string name in SettingsDocuments)
            if (ReadDocument(Path.Combine(staged, name)) is JsonObject settings && SettingsDownloadRoot(settings, legacyDownloads) is { } root)
                directories.Add(root);
        foreach (string name in SessionDocuments)
        {
            if (ReadDocument(Path.Combine(staged, name)) is JsonObject session
                && (session["DownloadDirectories"] ?? session["downloadDirectories"]) is JsonObject assignments)
                foreach (var assignment in assignments) AddPath(directories, ReadString(assignment.Value));
        }
        foreach (string name in BooruDocuments)
        {
            if (ReadDocument(Path.Combine(staged, name)) is JsonObject catalog && catalog["posts"] is JsonArray posts)
                foreach (JsonNode? item in posts)
                    if (item is JsonObject post) AddPath(files, ReadString(post["localPath"]));
        }
        foreach (string name in WorkDatabases) ReadWorkPaths(Path.Combine(staged, name), directories, files, token);
        ReadArchivePaths(Path.Combine(staged, "archive.db"), directories, files, token);
        foreach (string file in files)
        {
            if (!directories.Contains(file) && !HasParentIn(directories, file) && Path.GetDirectoryName(file) is { } parent)
                directories.Add(parent);
        }
        if (HasParentIn(directories, source) || directories.Any(root => Path.GetDirectoryName(root) is null))
            throw new InvalidDataException("A managed media root contains the source profile or is a filesystem root.");
        return directories.Where(root => !PathComparer.Equals(root, source) && !HasParentIn(directories, root))
            .Order(StringComparer.Ordinal).ToArray();
    }

    internal static void Apply(ProfileSnapshot snapshot, string snapshotDirectory, string staged, string target, CancellationToken token)
    {
        Dictionary<string, string> paths = new(PathComparer) { [snapshot.SourceDirectory] = target };
        string container = "upgrade-downloads";
        string candidate = Path.Combine(staged, container);
        if (Directory.Exists(candidate) || File.Exists(candidate))
            container += "-" + snapshot.Profile.Generation.ToString("N");
        ProfileExternalMedia[] media = snapshot.ManagedMedia!;
        for (int index = 0; index < media.Length; index++)
        {
            string relative = Path.Combine(container, (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            paths.Add(media[index].Root, Path.Combine(target, relative));
            snapshot.CopyMedia(snapshotDirectory, index, Path.Combine(staged, relative), token);
        }
        var relocation = new ProfileMediaRelocation(paths);
        foreach (string name in SettingsDocuments)
            relocation.RewriteDocument(Path.Combine(staged, name), snapshot.LegacyDownloads);
        foreach (string name in PathDocuments)
        {
            token.ThrowIfCancellationRequested();
            relocation.RewriteDocument(Path.Combine(staged, name));
        }
        relocation.RewriteArchive(Path.Combine(staged, "archive.db"));
        foreach (string name in WorkDatabases) relocation.RewriteWork(Path.Combine(staged, name));
    }

    internal static string[] CurrentRoots(string directory, string? downloads, string[] previous)
    {
        var roots = new HashSet<string>(previous, PathComparer);
        if (downloads is not null) roots.Add(downloads);
        roots.UnionWith(FolderRoots(directory));
        return roots.Where(root => RelativeTo(directory, root) is null).Order(StringComparer.Ordinal).ToArray();
    }

    private static bool HasParentIn(HashSet<string> roots, string path)
    {
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (roots.Contains(parent)) return true;
        return false;
    }

    private static string? SettingsDownloadRoot(JsonObject settings, string fallback)
    {
        if (!settings.TryGetPropertyValue("DownloadPath", out JsonNode? value)) return fallback;
        string? path = ReadString(value);
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return ProfileLease.NormalizeDirectory(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? ReadString(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue(out string? path) ? path : null;

    private static void AddPath(HashSet<string> paths, string? value)
    {
        if (value is not null && Path.IsPathFullyQualified(value)) paths.Add(ProfileLease.NormalizeDirectory(value));
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

    private static void ReadWorkPaths(string path, HashSet<string> directories, HashSet<string> files, CancellationToken token)
    {
        if (!File.Exists(path)) return;
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        ReadDatabasePaths(connection, directories, token, """
            SELECT value FROM work_resources, json_each(CASE WHEN json_valid(payload) THEN payload ELSE '{}' END)
                WHERE lower(key)='outputroot';
            """);
        ReadDatabasePaths(connection, files, token, """
            SELECT target FROM work_media;
            SELECT value FROM work_media, json_each(CASE WHEN json_valid(payload) THEN payload ELSE '{}' END)
                WHERE lower(key) IN ('destinationpath','legacypath');
            """);
    }

    private static void ReadArchivePaths(string path, HashSet<string> directories, HashSet<string> files, CancellationToken token)
    {
        if (!File.Exists(path)) return;
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        ArchiveSql.ValidateSchema(connection);
        ReadDatabasePaths(connection, directories, token, """
            SELECT media_directory FROM archive_threads;
            SELECT directory FROM archive_media_directories;
            """);
        ReadDatabasePaths(connection, files, token, """
            SELECT planned_path FROM archive_attachments;
            SELECT destination_path,legacy_path FROM media_decisions;
            """);
        int version = ArchiveSql.GetPragma(connection, "user_version");
        if (version >= 7)
            ReadDatabasePaths(connection, directories, token, "SELECT operations_parent,candidate_directory FROM archive_media_import_operations");
        if (version >= 8)
            ReadDatabasePaths(connection, directories, token, "SELECT operations_parent,full_directory,safe_directory FROM archive_media_merge_operations");
    }

    private static void ReadDatabasePaths(SqliteConnection connection, HashSet<string> paths, CancellationToken token, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        do
        {
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                for (int column = 0; column < reader.FieldCount; column++)
                    if (reader.GetValue(column) is string value) AddPath(paths, value);
            }
        } while (reader.NextResult());
    }

    private void RewriteDocument(string path, string? legacyDownloads = null)
    {
        if (ReadDocument(path) is not JsonObject document) return;
        bool changed = RewriteValues(document);
        if (legacyDownloads is not null && SettingsDownloadRoot(document, legacyDownloads) is { } root)
        {
            string mapped = Map(root);
            if (mapped != ReadString(document["DownloadPath"]))
            {
                document["DownloadPath"] = mapped;
                changed = true;
            }
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
        string full = ProfileLease.NormalizeDirectory(path);
        for (string? root = full; root is not null; root = Path.GetDirectoryName(root))
            if (paths.TryGetValue(root, out string? mapped))
                return PathComparer.Equals(root, full) ? mapped : Path.Combine(mapped, Path.GetRelativePath(root, full));
        return path;
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
        int version = ArchiveSql.GetPragma(connection, "user_version");
        connection.CreateFunction("upgrade_path", (string? value) => value is null ? null : Map(value));
        connection.CreateFunction<string, string>("upgrade_payload", RewritePayload);
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
        if (version >= 7)
        {
            command.CommandText = """
                UPDATE archive_media_import_operations SET operations_parent=upgrade_path(operations_parent),
                    candidate_directory=upgrade_path(candidate_directory)
                    WHERE operations_parent IS NOT upgrade_path(operations_parent)
                    OR candidate_directory IS NOT upgrade_path(candidate_directory);
                """;
            command.ExecuteNonQuery();
        }
        if (version >= 8)
        {
            command.CommandText = """
                UPDATE archive_media_merge_operations SET operations_parent=upgrade_path(operations_parent),
                    full_directory=upgrade_path(full_directory),safe_directory=upgrade_path(safe_directory),roots_json=upgrade_payload(roots_json)
                    WHERE operations_parent IS NOT upgrade_path(operations_parent) OR full_directory IS NOT upgrade_path(full_directory)
                    OR safe_directory IS NOT upgrade_path(safe_directory) OR roots_json IS NOT upgrade_payload(roots_json);
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void RewriteWork(string path)
    {
        if (!File.Exists(path)) return;
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadWrite);
        connection.CreateFunction("upgrade_path", (string value) => Map(value));
        connection.CreateFunction<string, string>("upgrade_payload", RewritePayload);
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

    private string RewritePayload(string value)
    {
        JsonNode? document;
        try { document = JsonNode.Parse(value); }
        catch (JsonException) { return value; } // The owners retain unreadable entries for recovery.
        return document is not null && RewriteValues(document) ? document.ToJsonString() : value;
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
