using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeowChan.Archive;
using Microsoft.Data.Sqlite;

namespace MeowChan.Persistence;

internal sealed record ProfilePartFormat(int Version, string MinimumReader, string MinimumWriter);

internal sealed record ProfileManifest(int Version, Guid ProfileId, Guid Generation, string State,
    Dictionary<string, ProfilePartFormat> Parts, string[] ExternalMediaRoots)
{
    internal const string FileName = "profile.json";
    internal const string Preparing = "preparing";
    internal const string Ready = "ready";
    private const string Legacy = "1.1.3";
    private const string Current = "1.2.0";
    private const int ManifestVersion = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false };

    private static readonly Dictionary<string, (int Minimum, int Maximum, int LegacyMaximum)> Formats =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["settings.json"] = (1, 1, 1),
            [ProfileInstanceIdentity.FileName] = (1, 1, 1),
            ["booru-search-tabs.json"] = (1, 2, 1),
            ["booru-catalog.json"] = (1, 2, 1),
            ["archive.db"] = (1, ArchiveSql.SchemaVersion, 13),
            ["folder-sources.db"] = (1, 1, 1),
            ["work-state.sqlite"] = (0, 1, 1),
            ["work-state.before-prune.sqlite"] = (0, 1, 1),
            ["saved_urls.json"] = (1, 1, 1),
            ["private-tags.json"] = (1, 4, 3),
            ["tag-queue.json"] = (1, 3, 3),
            ["tag-suggestions.json"] = (1, 2, 2),
            ["post-bookmarks.json"] = (1, 2, 2),
            ["post-filters.json"] = (1, 1, 1),
            ["reader-groups.json"] = (1, 1, 1),
            ["archive-searches.json"] = (1, 1, 1),
            ["media_rules.json"] = (1, 1, 1),
            ["export-history.json"] = (1, 1, 1),
            ["collection-export-history.json"] = (1, 1, 1),
        };

    internal static ProfileManifest New(Guid id, string state, Dictionary<string, ProfilePartFormat> parts,
        string[] externalMediaRoots) => new(ManifestVersion, id, Guid.NewGuid(), state, parts, externalMediaRoots);

    internal static ProfileManifest? Read(string directory)
    {
        string? contents = RegularProfileFile.ReadBoundedText(Path.Combine(directory, FileName), 1024 * 1024);
        if (contents is null) return null;
        ProfileManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<ProfileManifest>(contents, Json); }
        catch (JsonException error) { throw new InvalidDataException("The profile manifest is damaged.", error); }
        if (manifest is null || manifest.Version != ManifestVersion || manifest.ProfileId == Guid.Empty
            || manifest.Generation == Guid.Empty || manifest.State is not (Preparing or Ready)
            || manifest.Parts is null || manifest.ExternalMediaRoots is null
            || manifest.ExternalMediaRoots.Any(root => root is null || !Path.IsPathFullyQualified(root)))
            throw new InvalidDataException("The profile manifest has an unsupported format.");
        foreach ((string name, ProfilePartFormat format) in manifest.Parts)
        {
            if (format is null || !TryLimits(name, out var limits)
                || format.Version < limits.Minimum || format.Version > limits.Maximum
                || !Supports(format.MinimumReader) || !Supports(format.MinimumWriter))
                throw new InvalidDataException($"The profile requires a newer reader or writer for '{name}'.");
        }
        return manifest;
    }

    internal void Write(string directory) =>
        ProfileSnapshot.PublishJson(Path.Combine(directory, FileName), this, Json);

    internal static Dictionary<string, ProfilePartFormat> Inspect(string directory)
    {
        Dictionary<string, ProfilePartFormat> parts = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            string name = Path.GetFileName(path);
            if (!TryLimits(name, out var limits)) continue;
            int? version = Database(name) ? InspectDatabase(path) : InspectDocument(path);
            // A malformed file has no declared format. The existing owner on the
            // working copy still decides whether its backup or recovery is usable.
            if (version is null) continue;
            if (version < limits.Minimum || version > limits.Maximum)
                throw new InvalidDataException($"The profile file '{name}' uses unsupported format {version}.");
            string minimum = version <= limits.LegacyMaximum ? Legacy : Current;
            parts.Add(name, new(version.Value, minimum, minimum));
        }
        return parts;
    }

    private static bool Supports(string minimum) =>
        System.Version.TryParse(minimum, out Version? requirement) && requirement <= new Version(1, 2, 0);

    private static bool TryLimits(string name, out (int Minimum, int Maximum, int LegacyMaximum) limits)
    {
        string primary = name.EndsWith(".bak", StringComparison.Ordinal) ? name[..^4] : name;
        return Formats.TryGetValue(primary, out limits);
    }

    private static bool Database(string name) =>
        name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase);

    private static int? InspectDocument(string path)
    {
        try
        {
            using FileStream stream = RegularProfileFile.OpenRead(path);
            using JsonDocument contents = JsonDocument.Parse(stream);
            JsonElement root = contents.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            int? result = null;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!property.Name.Equals("version", StringComparison.OrdinalIgnoreCase)
                    && !property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase)) continue;
                if (result is not null || !property.Value.TryGetInt32(out int value))
                    throw new InvalidDataException($"The profile file '{Path.GetFileName(path)}' has an invalid format marker.");
                result = value;
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    private static int InspectDatabase(string path)
    {
        // This is used on verified copies for upgrades. Read-only access also
        // lets the current profile reject a newer database before its owner opens.
        using (RegularProfileFile.OpenRead(path)) { }
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return checked((int)(long)command.ExecuteScalar()!);
    }
}
