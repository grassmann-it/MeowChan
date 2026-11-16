using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>
/// The on/off switch of each site of an engine-family chan plugin, in <c>chan-sources.json</c> next to the plugin
/// profile. A site is used only while its family plugin (the master switch) and its own switch are both on. A site
/// nobody chose keeps <see cref="ChanSourceFamilies.DefaultEnabled"/>. The first start after the single-site plugins
/// were replaced takes each site's state over from its old plugin's enabled state, once per old plugin.
/// A file that exists but cannot be used keeps the defaults in force, is reported, and is never overwritten.
/// </summary>
internal sealed class ChanSiteSwitches
{
    internal const string FileName = "chan-sources.json";
    private const int Version = 1;
    // Technical ceiling of the stored format (a few dozen short entries); not a product limit and not a setting.
    private const int MaximumBytes = 64 * 1024;
    private readonly object _sync = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private Dictionary<string, bool> _enabled = new(StringComparer.Ordinal);
    private HashSet<string> _migrated = new(StringComparer.Ordinal);
    private bool _loaded;

    private sealed record Document(int Version, Dictionary<string, bool> Enabled, List<string> Migrated);

    internal ChanSiteSwitches(string profileDirectory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
        _path = Path.Combine(profileDirectory, FileName);
        _logger = logger ?? Serilog.Core.Logger.None;
    }

    /// <summary>True when the stored file exists but cannot be read; switches then keep their defaults and cannot change.</summary>
    internal bool IsUnreadable { get; private set; }

    /// <summary>The site's own state. Sources of other plugins have no site switch and are always on here.</summary>
    internal bool IsEnabled(string pluginId, string sourceId)
    {
        if (!ChanSourceFamilies.IsFamily(pluginId)) return true;
        lock (_sync)
        {
            Load();
            return _enabled.TryGetValue(sourceId, out bool enabled) ? enabled : ChanSourceFamilies.DefaultEnabled(pluginId, sourceId);
        }
    }

    /// <summary>Stores the user's choice for one site of a family plugin. False for other plugins, an unreadable file or a write failure.</summary>
    internal bool SetEnabled(string pluginId, string sourceId, bool enabled)
    {
        if (!ChanSourceFamilies.IsFamily(pluginId)) return false;
        lock (_sync)
        {
            Load();
            if (IsUnreadable) return false;
            Dictionary<string, bool> next = new(_enabled, StringComparer.Ordinal) { [sourceId] = enabled };
            if (!Write(next, _migrated)) return false;
            _enabled = next;
            return true;
        }
    }

    /// <summary>
    /// Takes each replaced single-site plugin's enabled state over as its site's switch, once per old plugin and only
    /// for a site nobody chose yet. Reads the saved profile only; it never changes the plugin profile.
    /// </summary>
    internal void MigrateFrom(PluginProfileSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_sync)
        {
            Load();
            if (IsUnreadable) return;
            Dictionary<string, bool> enabled = new(_enabled, StringComparer.Ordinal);
            HashSet<string> migrated = new(_migrated, StringComparer.Ordinal);
            foreach (PluginActivationSnapshot old in profile.Plugins)
            {
                if (!ChanSourceFamilies.Replaced.TryGetValue(old.PluginId, out var site) || !migrated.Add(old.PluginId)) continue;
                enabled.TryAdd(site.SourceId, old.Enabled);
            }
            int taken = migrated.Count - _migrated.Count;
            if (taken == 0 || !Write(enabled, migrated)) return;
            _enabled = enabled;
            _migrated = migrated;
            _logger.Information("Site switches taken over from {Count} replaced single-site chan plugins", taken);
        }
    }

    /// <summary>
    /// The replaced single-site plugins whose enabled state is safely stored as their site's switch (taken over by
    /// <see cref="MigrateFrom"/>). Only these may leave the plugin profile; with an unusable file nothing has been taken
    /// over, so nothing is returned and every old record stays.
    /// </summary>
    internal IReadOnlySet<string> TakenOver()
    {
        lock (_sync)
        {
            Load();
            return IsUnreadable ? new HashSet<string>(StringComparer.Ordinal) : new HashSet<string>(_migrated, StringComparer.Ordinal);
        }
    }

    // Caller holds _sync. Read once; an unusable file is reported and retried on the next session.
    private void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            FileInfo file = new(_path);
            if (!file.Exists) return;
            if (file.Length > MaximumBytes) throw new InvalidDataException("The site switch file is too large.");
            Document document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(_path))
                ?? throw new InvalidDataException("The site switch file is empty.");
            if (document.Version != Version || document.Enabled is null || document.Migrated is null)
                throw new InvalidDataException("The site switch file has an unknown version or shape.");
            foreach ((string sourceId, bool value) in document.Enabled.Where(pair => pair.Key is { Length: > 0 and <= 128 }))
                _enabled[sourceId] = value;
            foreach (string pluginId in document.Migrated.Where(id => id is { Length: > 0 and <= 512 })) _migrated.Add(pluginId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            IsUnreadable = true;
            _enabled.Clear();
            _migrated.Clear();
            _logger.Warning(exception, "The chan site switches in {File} cannot be used; every site keeps its default and no switch can change until the file is fixed or removed", FileName);
        }
    }

    private bool Write(Dictionary<string, bool> enabled, HashSet<string> migrated)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Document(Version,
                enabled.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                migrated.Order(StringComparer.Ordinal).ToList()), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(exception, "The chan site switches could not be saved");
            try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }
}
