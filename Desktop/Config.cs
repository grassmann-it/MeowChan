using System;
using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeowChan.Models;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan
{
    public class Config
    {
        private string? _profileDirectory;
        private string _relativePathBaseDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        private string _theme = "Dark";
        private string _language = "en";
        private string _workspaceMode = "Barebones";

        public int SchemaVersion { get; set; } = 1;
        public string DownloadPath { get; set; } = GetDefaultDownloadPath();
        public int RecheckTimer { get; set; } = 60;
        public bool SaveUrlsOnExit { get; set; } = true;
        public bool ShowBoardAdmissionWizard { get; set; } = true;
        public bool DownloadsPaused { get; set; }
        public bool MinimizeToTray { get; set; }
        public long BandwidthLimiter { get; set; } = 1024 * 1024;
        public int MaxParallelDownloads { get; set; } = 5;

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string Theme
        {
            get => _theme;
            set => _theme = string.Equals(value?.Trim(), "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string Language
        {
            get => _language;
            set => _language = NormalizeLanguage(value);
        }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string WorkspaceMode
        {
            get => _workspaceMode;
            set
            {
                string? mode = value?.Trim();
                _workspaceMode = string.Equals(mode, "Advanced", StringComparison.OrdinalIgnoreCase) ? "Advanced"
                    : string.Equals(mode, "Simple", StringComparison.OrdinalIgnoreCase) ? "Simple" : "Barebones";
            }
        }

        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool CompactDensity { get; set; }

        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool ArchiveMediaTiles { get; set; } = true;

        [JsonIgnore]
        public string? LoadWarning { get; private set; }

        /// <summary>Resolves the profile without creating files or directories.</summary>
        public static string GetConfigDirectory()
        {
            string? overrideDirectory = ResolveProfileOverrides(
                Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE"),
                Environment.GetEnvironmentVariable("OCHAN_PROFILE"));
            if (overrideDirectory != null)
                return overrideDirectory;

            string homePath = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.IsNullOrWhiteSpace(homePath))
                throw new InvalidOperationException("The user configuration directory could not be resolved.");

            return ResolveDefaultProfileDirectory(homePath);
        }

        internal static string? ResolveProfileOverrides(string? meowChanProfile, string? oChanProfile)
        {
            string? current = string.IsNullOrWhiteSpace(meowChanProfile) ? null :
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(meowChanProfile));
            string? legacy = string.IsNullOrWhiteSpace(oChanProfile) ? null :
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(oChanProfile));
            if (current != null && legacy != null && !string.Equals(current, legacy,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "MEOWCHAN_PROFILE and OCHAN_PROFILE select different profiles. Set only one, or set both to the same directory.");

            return current ?? legacy;
        }

        internal static string ResolveDefaultProfileDirectory(string homePath)
        {
            string current = Path.GetFullPath(Path.Combine(homePath, ".MeowChan"));
            string legacy = Path.GetFullPath(Path.Combine(homePath, ".oChan"));
            bool hasCurrent = ProfileDirectoryExists(current);
            bool hasLegacy = ProfileDirectoryExists(legacy);
            if (hasCurrent && hasLegacy)
                throw new InvalidOperationException(
                    $"Both MeowChan profiles exist ('{current}' and '{legacy}'). Select one explicitly with MEOWCHAN_PROFILE.");

            return hasLegacy ? legacy : current;
        }

        private static bool ProfileDirectoryExists(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) == 0)
                    throw new IOException($"The profile path '{path}' is a file, not a directory.");
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static string GetDefaultDownloadPath() => GetDefaultDownloadPath("MeowChan");

        private static string GetDefaultDownloadPath(string productDirectory)
        {
            string homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(homePath))
                throw new InvalidOperationException("The user home directory could not be resolved.");

            return Path.GetFullPath(Path.Combine(homePath, "Downloads", productDirectory));
        }

        public static Config LoadConfig() => LoadConfig(GetConfigDirectory());

        /// <summary>Loads an isolated profile. Loading never changes existing files.</summary>
        public static Config LoadConfig(string profileDirectory)
        {
            string directory = ResolveProfileDirectory(profileDirectory);
            string filePath = Path.Combine(directory, "settings.json");
            string relativePathBaseDirectory = Path.GetFullPath(Environment.CurrentDirectory);
            LoadStatus status = TryRead(filePath, relativePathBaseDirectory, out Config? config, out string? error);
            if (status == LoadStatus.Valid)
                return BindProfile(config!, directory, relativePathBaseDirectory);

            string backupPath = filePath + ".bak";
            LoadStatus backupStatus = TryRead(backupPath, relativePathBaseDirectory, out Config? backup, out string? backupError);
            if (backupStatus == LoadStatus.Valid)
            {
                Config recovered = BindProfile(backup!, directory, relativePathBaseDirectory);
                recovered.LoadWarning = $"Settings could not be loaded from '{filePath}' ({error ?? "file missing"}). " +
                    $"Recovered settings from backup '{backupPath}'. The original files were preserved.";
                Log.Warning("{LoadWarning}", recovered.LoadWarning);
                return recovered;
            }

            Config defaults = BindProfile(new Config(), directory, relativePathBaseDirectory);
            if (IsLegacyProfileWithoutSettings(directory))
                defaults.DownloadPath = GetDefaultDownloadPath("oChan");
            if (status != LoadStatus.Missing || backupStatus != LoadStatus.Missing)
            {
                defaults.LoadWarning = $"Settings could not be loaded from '{filePath}' ({error ?? "file missing"}); " +
                    $"no valid backup was available ({backupError ?? "file missing"}). " +
                    "Default settings are in use. The original files were preserved.";
                Log.Warning("{LoadWarning}", defaults.LoadWarning);
            }

            return defaults;
        }

        public void Validate()
        {
            if (SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported settings schema version: {SchemaVersion}.");
            if (RecheckTimer < 10 || TimeSpan.FromSeconds(RecheckTimer).TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(RecheckTimer), "The recheck interval must be between 10 seconds and the supported timer maximum.");
            if (BandwidthLimiter <= 0)
                throw new ArgumentOutOfRangeException(nameof(BandwidthLimiter), "The bandwidth limit must be greater than zero.");
            if (MaxParallelDownloads <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxParallelDownloads), "Parallel downloads must be greater than zero.");
            if (string.IsNullOrWhiteSpace(DownloadPath))
                throw new ArgumentException("A download directory is required.", nameof(DownloadPath));

            _ = Path.GetFullPath(DownloadPath, _relativePathBaseDirectory);
        }

        public Config Clone()
        {
            return new Config
            {
                SchemaVersion = SchemaVersion,
                DownloadPath = DownloadPath,
                RecheckTimer = RecheckTimer,
                SaveUrlsOnExit = SaveUrlsOnExit,
                ShowBoardAdmissionWizard = ShowBoardAdmissionWizard,
                DownloadsPaused = DownloadsPaused,
                MinimizeToTray = MinimizeToTray,
                BandwidthLimiter = BandwidthLimiter,
                MaxParallelDownloads = MaxParallelDownloads,
                Theme = Theme,
                Language = Language,
                WorkspaceMode = WorkspaceMode,
                CompactDensity = CompactDensity,
                ArchiveMediaTiles = ArchiveMediaTiles,
                LoadWarning = LoadWarning,
                _profileDirectory = _profileDirectory,
                _relativePathBaseDirectory = _relativePathBaseDirectory
            };
        }

        /// <summary>Applies validated values without changing this instance's profile.</summary>
        public void CopyFrom(Config source)
        {
            ArgumentNullException.ThrowIfNull(source);
            source.Validate();
            string absoluteDownloadPath = Path.GetFullPath(source.DownloadPath, source._relativePathBaseDirectory);

            SchemaVersion = source.SchemaVersion;
            DownloadPath = absoluteDownloadPath;
            RecheckTimer = source.RecheckTimer;
            SaveUrlsOnExit = source.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = source.ShowBoardAdmissionWizard;
            DownloadsPaused = source.DownloadsPaused;
            MinimizeToTray = source.MinimizeToTray;
            BandwidthLimiter = source.BandwidthLimiter;
            MaxParallelDownloads = source.MaxParallelDownloads;
            Theme = source.Theme;
            Language = source.Language;
            WorkspaceMode = source.WorkspaceMode;
            CompactDensity = source.CompactDensity;
            ArchiveMediaTiles = source.ArchiveMediaTiles;
            LoadWarning = source.LoadWarning;
        }

        public void SaveConfig() => SaveConfig(_profileDirectory ?? GetConfigDirectory());

        /// <summary>Atomically saves settings; validation and write errors reach the caller.</summary>
        public void SaveConfig(string profileDirectory)
        {
            Validate();
            string directory = ResolveProfileDirectory(profileDirectory);
            string filePath = Path.Combine(directory, "settings.json");
            DownloadPath = Path.GetFullPath(DownloadPath, _relativePathBaseDirectory);
            string json = JsonSerializer.Serialize(this, ConfigJsonContext.Default.Config);

            LoadStatus currentStatus = TryRead(filePath, _relativePathBaseDirectory, out _, out _);
            string backupPath = filePath + ".bak";
            LoadStatus backupStatus = TryRead(backupPath, _relativePathBaseDirectory, out _, out _);
            if (currentStatus == LoadStatus.Valid && backupStatus == LoadStatus.Invalid)
                AtomicJsonFile.PreserveFile(backupPath);

            AtomicJsonFile.WriteAllText(filePath, json, backupCurrentFile: currentStatus == LoadStatus.Valid);
            _profileDirectory = directory;
            LoadWarning = null;
            Log.Information("Configuration saved to {ConfigFilePath}", filePath);
        }

        public void PrintConfig()
        {
            Log.Information("Download Path: {DownloadPath}", DownloadPath);
            Log.Information("Recheck Timer: {RecheckTimer} seconds", RecheckTimer);
            Log.Information("Save URLs on Exit: {SaveUrlsOnExit}", SaveUrlsOnExit);
            Log.Information("Minimize to Tray: {MinimizeToTray}", MinimizeToTray);
            Log.Information("Bandwidth Limiter: {BandwidthLimiter} bytes per second", BandwidthLimiter);
            Log.Information("Parallel downloads: {MaxParallelDownloads}", MaxParallelDownloads);
        }

        private static Config BindProfile(Config config, string directory, string relativePathBaseDirectory)
        {
            config._profileDirectory = directory;
            config._relativePathBaseDirectory = relativePathBaseDirectory;
            return config;
        }

        private static string ResolveProfileDirectory(string profileDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profileDirectory);
            return Path.GetFullPath(profileDirectory);
        }

        private static bool IsLegacyProfileWithoutSettings(string directory)
        {
            if (string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), ".oChan",
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return true;

            // The owner can have published the new protocol marker and lock before
            // settings are loaded. These original data files still identify a legacy
            // profile whose implicit download directory must remain unchanged.
            return ProfileFileExists(Path.Combine(directory, "saved_urls.json")) ||
                ProfileFileExists(Path.Combine(directory, ".ochan-profile.lock"));
        }

        private static bool ProfileFileExists(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Directory) != 0)
                    throw new IOException($"The profile file path '{path}' is a directory, not a file.");
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }

        private static string NormalizeLanguage(string? value)
        {
            string code = value?.Trim() ?? string.Empty;
            if (code.Length > 35 || !Regex.IsMatch(code, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant))
                return "en";

            try
            {
                return CultureInfo.GetCultureInfo(code).Name;
            }
            catch (CultureNotFoundException)
            {
                return "en";
            }
        }

        private static LoadStatus TryRead(string filePath, string relativePathBaseDirectory, out Config? config, out string? error)
        {
            config = null;
            error = null;
            string json;
            try
            {
                json = File.ReadAllText(filePath);
            }
            catch (FileNotFoundException)
            {
                return LoadStatus.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return LoadStatus.Missing;
            }

            // An inaccessible document is not a damaged document. Propagate read
            // failures so temporary locks or permissions cannot activate defaults
            // or allow SaveConfig to replace a file it could not inspect.
            try
            {
                config = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config);
                if (config == null)
                    throw new InvalidDataException("The settings document contains null.");

                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    if (!document.RootElement.TryGetProperty(nameof(DownloadPath), out _))
                        config.DownloadPath = GetDefaultDownloadPath("oChan");
                }

                config._relativePathBaseDirectory = relativePathBaseDirectory;
                config.Validate();
                // Existing relative settings keep the location used by the old application.
                // Resolve once on load so later working-directory changes cannot move jobs.
                config.DownloadPath = Path.GetFullPath(config.DownloadPath, relativePathBaseDirectory);
                return LoadStatus.Valid;
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                config = null;
                error = ex.Message;
                return LoadStatus.Invalid;
            }
        }

        private enum LoadStatus { Missing, Valid, Invalid }
    }

    // Invalid presentation preferences must not discard valid download/session settings.
    internal sealed class DesktopPreferenceStringConverter : JsonConverter<string>
    {
        public DesktopPreferenceStringConverter() { }
        public override bool HandleNull => true;

        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
                return reader.GetString() ?? string.Empty;
            reader.Skip();
            return string.Empty;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }

    internal sealed class DesktopPreferenceBooleanConverter : JsonConverter<bool>
    {
        public DesktopPreferenceBooleanConverter() { }
        public override bool HandleNull => true;

        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                return reader.GetBoolean();
            reader.Skip();
            return false;
        }

        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    [JsonSerializable(typeof(Config))]
    [JsonSerializable(typeof(SavedUrls))]
    public partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}
