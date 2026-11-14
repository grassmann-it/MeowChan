using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowChan.Desktop;

/// <summary>Builds the portable, non-destructive settings export. File I/O belongs to the UI caller.</summary>
public static class SettingsTransfer
{
    private const string ExportedByPrefix = "MeowChan ";

    /// <summary>
    /// Returns the current settings in the same JSON shape as <see cref="Config"/> serialization, with
    /// profile-local, runtime, consent and window-state fields removed. The input is not changed.
    /// </summary>
    public static JsonObject Export(Config source) => Export(source, ProductVersion());

    /// <summary>
    /// Returns an export using an explicit product version. Build metadata after '+' is not part of the displayed version.
    /// </summary>
    public static JsonObject Export(Config source, string productVersion)
    {
        ArgumentNullException.ThrowIfNull(source);
        string version = NormalizeProductVersion(productVersion);

        JsonObject document = SerializeWithoutMaterializingLayoutProfile(source);
        foreach (string key in ExcludedKeys)
            document.Remove(key);

        // SchemaVersion remains the Config schema marker so the document can be read by the existing settings serializer.
        document["ExportedBy"] = ExportedByPrefix + version;
        return document;
    }

    private static readonly string[] ExcludedKeys =
    [
        nameof(Config.WindowWidth),
        nameof(Config.WindowHeight),
        nameof(Config.WindowPositionX),
        nameof(Config.WindowPositionY),
        nameof(Config.WindowMaximized),
        nameof(Config.FirstRunSetupPending),
        nameof(Config.FirstRunSetupVersion),
        nameof(Config.WorkViewNoticePending),
        nameof(Config.SourceMediaIntegrityPermissions),
        nameof(Config.DownloadsPaused),
        nameof(Config.LastExportDirectory),
        nameof(Config.GalleryFrameDirectory),
        // These are persisted view/notice state rather than user settings.
        nameof(Config.GalleryContextOpen),
        nameof(Config.ArchiveBridgeHintSeen),
    ];

    private static JsonObject SerializeWithoutMaterializingLayoutProfile(Config source)
    {
        // Config's compatibility properties are backed by the active LayoutProfiles entry. Reading one of them
        // during normal source-generated serialization creates that entry when a profile has none. Serialize is
        // otherwise read-only, so restore the dictionary exactly to keep Export pure even for a fresh Config.
        KeyValuePair<string, PresentationProfile>[] originalProfiles = source.LayoutProfiles.ToArray();
        try
        {
            JsonNode? node = JsonSerializer.SerializeToNode(source, ConfigJsonContext.Default.Config);
            return node?.AsObject() ?? throw new InvalidOperationException("Config serialization returned no object.");
        }
        finally
        {
            source.LayoutProfiles.Clear();
            foreach ((string name, PresentationProfile profile) in originalProfiles)
                source.LayoutProfiles.Add(name, profile);
        }
    }

    private static string ProductVersion()
    {
        Assembly assembly = typeof(SettingsTransfer).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return NormalizeProductVersion(informational);

        Version? assemblyVersion = assembly.GetName().Version;
        return assemblyVersion == null
            ? "0.0.0"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{Math.Max(assemblyVersion.Build, 0)}");
    }

    private static string NormalizeProductVersion(string productVersion)
    {
        ArgumentNullException.ThrowIfNull(productVersion);
        string version = productVersion.Split('+', 2)[0].Trim();
        if (version.Length is 0 or > 64 || version.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
            throw new ArgumentException("A non-empty product version without control characters is required.", nameof(productVersion));
        return version;
    }
}
