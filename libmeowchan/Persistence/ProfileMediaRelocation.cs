using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Tags;
using Microsoft.Data.Sqlite;

namespace MeowChan.Persistence;

/// <summary>Inventories legacy write destinations and rebinds them when materializing a snapshot.</summary>
internal sealed class ProfileMediaRelocation(Dictionary<string, string> paths)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly string[] Documents =
        ["settings.json", "saved_urls.json", "booru-catalog.json", "private-tags.json", "local-imports.json", TagQueueStore.FileName];
    private static readonly string[] Suffixes = ["", ".bak"];
    private static readonly string[] WorkDatabases = ["work-state.sqlite", "work-state.before-prune.sqlite"];

    internal static ProfileMediaSelection[] ManagedRoots(string source, string legacyDownloads, CancellationToken token)
    {
        HashSet<string> directories = new(PathComparer) { source };
        HashSet<string> files = new(PathComparer);
        VisitDocuments(source, (name, document) =>
        {
            if (name == "settings.json" && SettingsDownloadRoot(document, legacyDownloads) is { } root)
                directories.Add(root);
            foreach (var (row, field) in PathFields(name, document))
            {
                token.ThrowIfCancellationRequested();
                string? value = ReadString(row[field]);
                if (name == "booru-catalog.json" && value is not null && ReadString(row["localAssetId"]) is not null)
                {
                    if (!Path.IsPathFullyQualified(value) || !File.Exists(value))
                        throw new InvalidDataException($"The linked original '{value}' is unavailable.");
                }
                if (name == "private-tags.json" && value is not null && ReadString(row["RequestedFile"]) is { } filename)
                {
                    if (Path.GetFileName(filename) != filename) throw new InvalidDataException("Invalid occurrence filename.");
                    AddPath(files, Path.Combine(value, filename));
                }
                else AddPath(name == "booru-catalog.json" ? files : directories, value);
            }
            return false;
        });
        foreach (string name in WorkDatabases) ReadWorkPaths(Path.Combine(source, name), directories, files, token);
        ReadArchivePaths(Path.Combine(source, "archive.db"), directories, files, token);
        if (HasParentIn(directories, source) || directories.Any(root => Path.GetDirectoryName(root) is null))
            throw new InvalidDataException("A managed media root contains the source profile or is a filesystem root.");

        var trees = directories.Where(root => !PathComparer.Equals(root, source) && !HasParentIn(directories, root))
            .Select(root => new ProfileMediaSelection(root));
        var individual = files.Where(file => !HasParentIn(directories, file))
            .GroupBy(file => Path.GetDirectoryName(file)!, PathComparer)
            .Select(group => new ProfileMediaSelection(group.Key, group.Select(file => Path.GetFileName(file))
                .Append(".meowchan-manifest.json").Append(".ochan-manifest.json")
                .Distinct(PathComparer).Order(StringComparer.Ordinal).ToArray()));
        return trees.Concat(individual).OrderBy(selection => selection.Root, StringComparer.Ordinal).ToArray();
    }

    internal static void Apply(ProfileSnapshot snapshot, string snapshotDirectory, string staged, string target, ProfileCopyOperation operation,
        bool restoring = false)
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
            snapshot.CopyMedia(snapshotDirectory, index, Path.Combine(staged, relative), operation);
        }
        var relocation = new ProfileMediaRelocation(paths);
        VisitDocuments(staged, (name, document) =>
        {
            operation.Token.ThrowIfCancellationRequested();
            if (name == "settings.json" && SettingsDownloadRoot(document, snapshot.LegacyDownloads!) is { } root)
                return SetPath(document, "DownloadPath", relocation.Map(root));
            bool changed = false;
            foreach (var (row, field) in PathFields(name, document))
            {
                operation.Token.ThrowIfCancellationRequested();
                if (ReadString(row[field]) is not { } path) continue;
                // Earlier snapshots may not have captured directories referenced only by their tagging queue.
                string mapped = relocation.Map(path, allowUncaptured: restoring && name == TagQueueStore.FileName);
                if (name == "booru-catalog.json" && ReadString(row["localAssetId"]) is not null)
                {
                    string? relative = RelativeTo(target, mapped);
                    if (relative is null || !File.Exists(ProfileSnapshot.Child(staged, relative)))
                        throw new InvalidDataException($"The linked original '{path}' is missing from the profile snapshot.");
                }
                changed |= SetPath(row, field, mapped);
            }
            return changed;
        });
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

    private static JsonNode? ReadDocument(string path, bool detectEncoding)
    {
        try
        {
            using FileStream file = RegularProfileFile.OpenRead(path);
            if (!detectEncoding) return JsonNode.Parse(file);
            using StreamReader reader = new(file, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonNode.Parse(reader.ReadToEnd());
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException) { return null; }
    }

    private static void ReadWorkPaths(string path, HashSet<string> directories, HashSet<string> files, CancellationToken token)
    {
        if (!File.Exists(path)) return;
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        ReadDatabasePaths(connection, directories, """
            SELECT value FROM work_resources, json_each(CASE WHEN json_valid(payload) THEN payload ELSE '{}' END)
                WHERE lower(key)='outputroot';
            """, token);
        ReadDatabasePaths(connection, files, """
            SELECT target FROM work_media;
            SELECT value FROM work_media, json_each(CASE WHEN json_valid(payload) THEN payload ELSE '{}' END)
                WHERE lower(key) IN ('destinationpath','legacypath');
            """, token);
    }

    private static void ReadArchivePaths(string path, HashSet<string> directories, HashSet<string> files, CancellationToken token)
    {
        if (!File.Exists(path)) return;
        using SqliteConnection connection = ArchiveSql.OpenConnection(path, SqliteOpenMode.ReadOnly);
        ArchiveSql.ValidateSchema(connection);
        ReadDatabasePaths(connection, directories, """
            SELECT media_directory FROM archive_threads;
            SELECT directory FROM archive_media_directories;
            """, token);
        ReadDatabasePaths(connection, files, """
            SELECT planned_path FROM archive_attachments;
            SELECT destination_path,legacy_path FROM media_decisions;
            """, token);
        int version = ArchiveSql.GetPragma(connection, "user_version");
        if (version >= 7)
            ReadDatabasePaths(connection, directories, "SELECT operations_parent,candidate_directory FROM archive_media_import_operations", token);
        if (version >= 8)
            ReadDatabasePaths(connection, directories, """
                SELECT operations_parent,full_directory,safe_directory FROM archive_media_merge_operations;
                SELECT json_extract(value,'$.Directory') FROM archive_media_merge_operations,json_each(roots_json);
                """, token);
    }

    private static void ReadDatabasePaths(SqliteConnection connection, HashSet<string> paths, string sql, CancellationToken token)
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

    private static void VisitDocuments(string directory, Func<string, JsonObject, bool> visit)
    {
        foreach (string name in Documents)
            foreach (string suffix in Suffixes)
            {
                string path = Path.Combine(directory, name + suffix);
                if (ReadDocument(path, name is "settings.json" or "saved_urls.json") is JsonObject document && visit(name, document))
                    ProfileSnapshot.PublishJson(path, document, Json);
            }
    }

    private static IEnumerable<(JsonObject Row, string Field)> PathFields(string name, JsonObject document)
    {
        if (name == "saved_urls.json" && (document["DownloadDirectories"] ?? document["downloadDirectories"]) is JsonObject assignments)
        {
            foreach (string key in assignments.Select(pair => pair.Key).ToArray()) yield return (assignments, key);
        }
        var (collection, field) = name switch
        {
            "booru-catalog.json" => ("posts", "localPath"),
            "private-tags.json" => ("LocalOccurrences", "Directory"),
            "local-imports.json" => ("Batches", "Directory"),
            TagQueueStore.FileName => ("Items", "Directory"),
            _ => (null, null)
        };
        if (collection is not null && document[collection] is JsonArray rows)
            foreach (JsonObject row in rows.OfType<JsonObject>()) yield return (row, field!);
    }

    private static bool SetPath(JsonObject row, string field, string path)
    {
        if (ReadString(row[field]) == path) return false;
        row[field] = path;
        return true;
    }

    private string Map(string path, bool allowUncaptured = false)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        string full = ProfileLease.NormalizeDirectory(path);
        for (string? root = full; root is not null; root = Path.GetDirectoryName(root))
            if (paths.TryGetValue(root, out string? mapped))
                return PathComparer.Equals(root, full) ? mapped : Path.Combine(mapped, Path.GetRelativePath(root, full));
        if (allowUncaptured) return path;
        throw new InvalidDataException("A profile media path was not captured for relocation.");
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
        connection.CreateFunction<string, string>("upgrade_payload", value => RewritePayload(value, "roots"));
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
        connection.CreateFunction<string, string>("upgrade_resource", value => RewritePayload(value, "resource"));
        connection.CreateFunction<string, string>("upgrade_media", value => RewritePayload(value, "media"));
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE work_resources SET payload=upgrade_resource(payload) WHERE payload IS NOT upgrade_resource(payload);
            UPDATE work_media SET target=upgrade_path(target),payload=upgrade_media(payload)
                WHERE target IS NOT upgrade_path(target) OR payload IS NOT upgrade_media(payload);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private string RewritePayload(string value, string kind)
    {
        JsonNode? document;
        try { document = JsonNode.Parse(value); }
        catch (JsonException) { return value; } // The owners retain unreadable entries for recovery.
        bool changed = false;
        if (kind == "roots" && document is JsonArray roots)
        {
            foreach (JsonObject row in roots.OfType<JsonObject>())
                if (ReadString(row["Directory"]) is { } path) changed |= SetPath(row, "Directory", Map(path));
        }
        else if (document is JsonObject payload)
        {
            foreach (string key in payload.Select(pair => pair.Key).ToArray())
            {
                bool isPath = kind == "resource" ? key.Equals("outputRoot", StringComparison.OrdinalIgnoreCase)
                    : key.Equals("destinationPath", StringComparison.OrdinalIgnoreCase) || key.Equals("legacyPath", StringComparison.OrdinalIgnoreCase);
                if (isPath && ReadString(payload[key]) is { } path) changed |= SetPath(payload, key, Map(path));
            }
        }
        return changed ? document!.ToJsonString() : value;
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
