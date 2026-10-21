using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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
        private string _readingFontSize = "Normal";
        private string _archiveThreadLayout = "List";
        private int _videoVolume = 70;
        private double _archiveSplitWidth = 270;
        private double _windowWidth = 1380;
        private double _windowHeight = 850;
        private double? _windowPositionX;
        private double? _windowPositionY;
        private Dictionary<string, double> _columnWidths = new(StringComparer.Ordinal);
        private string _lastExportDirectory = string.Empty;

        public int SchemaVersion { get; set; } = 1;
        public string DownloadPath { get; set; } = GetDefaultDownloadPath();
        public int RecheckTimer { get; set; } = 60;
        public bool SaveUrlsOnExit { get; set; } = true;
        public bool ShowBoardAdmissionWizard { get; set; } = true;
        public bool DownloadsPaused { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }
        public long BandwidthLimiter { get; set; } = 1024 * 1024;
        public int MaxParallelDownloads { get; set; } = 5;

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string Theme
        {
            get => _theme;
            set => _theme = string.Equals(value?.Trim(), "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
                : string.Equals(value?.Trim(), "System", StringComparison.OrdinalIgnoreCase) ? "System" : "Dark";
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

        [JsonIgnore]
        internal bool? SettingsBaselineCompactDensity { get; set; }

        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool ArchiveMediaTiles { get; set; } = true;

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReadingFontSize
        {
            get => _readingFontSize;
            set => _readingFontSize = string.Equals(value?.Trim(), "Small", StringComparison.OrdinalIgnoreCase) ? "Small"
                : string.Equals(value?.Trim(), "Large", StringComparison.OrdinalIgnoreCase) ? "Large" : "Normal";
        }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ArchiveThreadLayout
        {
            get => _archiveThreadLayout;
            set => _archiveThreadLayout = string.Equals(value?.Trim(), "Tiles", StringComparison.OrdinalIgnoreCase) ? "Tiles"
                : string.Equals(value?.Trim(), "Table", StringComparison.OrdinalIgnoreCase) ? "Table" : "List";
        }

        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool VideoAutoplay { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool VideoStartMuted { get; set; }
        [JsonConverter(typeof(DesktopPreferenceVolumeConverter))]
        public int VideoVolume { get => _videoVolume; set => _videoVolume = Math.Clamp(value, 0, 100); }
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowShortcutHelp { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryContextOpen { get; set; }
        [JsonConverter(typeof(DesktopPreferenceSplitWidthConverter))]
        public double ArchiveSplitWidth
        {
            get => _archiveSplitWidth;
            set => _archiveSplitWidth = double.IsFinite(value) ? Math.Clamp(value, 180, 1200) : 270;
        }

        [JsonConverter(typeof(DesktopPreferenceWindowWidthConverter))]
        public double WindowWidth { get => _windowWidth; set => _windowWidth = double.IsFinite(value) ? Math.Clamp(value, 320, 10000) : 1380; }
        [JsonConverter(typeof(DesktopPreferenceWindowHeightConverter))]
        public double WindowHeight { get => _windowHeight; set => _windowHeight = double.IsFinite(value) ? Math.Clamp(value, 240, 10000) : 850; }
        [JsonConverter(typeof(DesktopPreferenceWindowPositionConverter))]
        public double? WindowPositionX { get => _windowPositionX; set => _windowPositionX = value.HasValue && double.IsFinite(value.Value) && Math.Abs(value.Value) <= 100000 ? value : null; }
        [JsonConverter(typeof(DesktopPreferenceWindowPositionConverter))]
        public double? WindowPositionY { get => _windowPositionY; set => _windowPositionY = value.HasValue && double.IsFinite(value.Value) && Math.Abs(value.Value) <= 100000 ? value : null; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool WindowMaximized { get; set; }
        [JsonConverter(typeof(DesktopPreferenceColumnWidthsConverter))]
        public Dictionary<string, double> ColumnWidths
        {
            get => _columnWidths;
            set => _columnWidths = value == null ? new(StringComparer.Ordinal) : new(value, StringComparer.Ordinal);
        }
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string LastExportDirectory { get => _lastExportDirectory; set => _lastExportDirectory = value ?? string.Empty; }

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
            if (LastExportDirectory.Length > 4096 || LastExportDirectory.Any(char.IsControl))
                throw new ArgumentException("The last export directory is invalid.", nameof(LastExportDirectory));
            if (ColumnWidths.Count > 64 || ColumnWidths.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 100 ||
                entry.Key.Any(char.IsControl) || !double.IsFinite(entry.Value) || entry.Value < 24 || entry.Value > 2000))
                throw new ArgumentException("Saved column widths are invalid.", nameof(ColumnWidths));
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
                CloseToTray = CloseToTray,
                BandwidthLimiter = BandwidthLimiter,
                MaxParallelDownloads = MaxParallelDownloads,
                Theme = Theme,
                Language = Language,
                WorkspaceMode = WorkspaceMode,
                CompactDensity = CompactDensity,
                SettingsBaselineCompactDensity = CompactDensity,
                ArchiveMediaTiles = ArchiveMediaTiles,
                ArchiveThreadLayout = ArchiveThreadLayout,
                ReadingFontSize = ReadingFontSize,
                VideoAutoplay = VideoAutoplay,
                VideoStartMuted = VideoStartMuted,
                VideoVolume = VideoVolume,
                ShowShortcutHelp = ShowShortcutHelp,
                GalleryContextOpen = GalleryContextOpen,
                ArchiveSplitWidth = ArchiveSplitWidth,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowPositionX = WindowPositionX,
                WindowPositionY = WindowPositionY,
                WindowMaximized = WindowMaximized,
                ColumnWidths = ColumnWidths,
                LastExportDirectory = LastExportDirectory,
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
            CloseToTray = source.CloseToTray;
            BandwidthLimiter = source.BandwidthLimiter;
            MaxParallelDownloads = source.MaxParallelDownloads;
            Theme = source.Theme;
            Language = source.Language;
            WorkspaceMode = source.WorkspaceMode;
            CompactDensity = source.CompactDensity;
            ArchiveMediaTiles = source.ArchiveMediaTiles;
            ArchiveThreadLayout = source.ArchiveThreadLayout;
            ReadingFontSize = source.ReadingFontSize;
            VideoAutoplay = source.VideoAutoplay;
            VideoStartMuted = source.VideoStartMuted;
            VideoVolume = source.VideoVolume;
            ShowShortcutHelp = source.ShowShortcutHelp;
            GalleryContextOpen = source.GalleryContextOpen;
            ArchiveSplitWidth = source.ArchiveSplitWidth;
            WindowWidth = source.WindowWidth;
            WindowHeight = source.WindowHeight;
            WindowPositionX = source.WindowPositionX;
            WindowPositionY = source.WindowPositionY;
            WindowMaximized = source.WindowMaximized;
            ColumnWidths = source.ColumnWidths;
            LastExportDirectory = source.LastExportDirectory;
            LoadWarning = source.LoadWarning;
        }

        /// <summary>Copies only fields edited by Settings, preserving direct view preferences.</summary>
        internal void CopySettingsFieldsFrom(Config source)
        {
            ArgumentNullException.ThrowIfNull(source);
            source.Validate();
            DownloadPath = Path.GetFullPath(source.DownloadPath, source._relativePathBaseDirectory);
            RecheckTimer = source.RecheckTimer;
            SaveUrlsOnExit = source.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = source.ShowBoardAdmissionWizard;
            MinimizeToTray = source.MinimizeToTray;
            CloseToTray = source.CloseToTray;
            BandwidthLimiter = source.BandwidthLimiter;
            MaxParallelDownloads = source.MaxParallelDownloads;
            Theme = source.Theme;
            Language = source.Language;
            WorkspaceMode = source.WorkspaceMode;
            ReadingFontSize = source.ReadingFontSize;
            VideoAutoplay = source.VideoAutoplay;
            VideoStartMuted = source.VideoStartMuted;
            VideoVolume = source.VideoVolume;
            ShowShortcutHelp = source.ShowShortcutHelp;
            if (source.SettingsBaselineCompactDensity.HasValue &&
                source.SettingsBaselineCompactDensity.Value != source.CompactDensity)
                CompactDensity = source.CompactDensity;
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
            Log.Information("Minimize to Tray: {MinimizeToTray}; Close to Tray: {CloseToTray}", MinimizeToTray, CloseToTray);
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
                    // The old MinimizeToTray preference controlled close only.
                    // An explicit CloseToTray field marks the independent preferences.
                    if (!document.RootElement.TryGetProperty(nameof(CloseToTray), out _))
                    {
                        config.CloseToTray = config.MinimizeToTray;
                        config.MinimizeToTray = false;
                    }
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

    internal sealed class DesktopPreferenceTrueBooleanConverter : JsonConverter<bool>
    {
        public DesktopPreferenceTrueBooleanConverter() { }
        public override bool HandleNull => true;
        public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
                return reader.GetBoolean();
            reader.Skip();
            return true;
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }

    internal sealed class DesktopPreferenceVolumeConverter : JsonConverter<int>
    {
        public DesktopPreferenceVolumeConverter() { }
        public override bool HandleNull => true;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
                return Math.Clamp(value, 0, 100);
            reader.Skip();
            return 70;
        }
        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceSplitWidthConverter : JsonConverter<double>
    {
        public DesktopPreferenceSplitWidthConverter() { }
        public override bool HandleNull => true;
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) && double.IsFinite(value))
                return Math.Clamp(value, 180, 1200);
            reader.Skip();
            return 270;
        }
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceWindowWidthConverter : JsonConverter<double>
    {
        public DesktopPreferenceWindowWidthConverter() { }
        public override bool HandleNull => true;
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) && double.IsFinite(value))
                return Math.Clamp(value, 320, 10000);
            reader.Skip();
            return 1380;
        }
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceWindowHeightConverter : JsonConverter<double>
    {
        public DesktopPreferenceWindowHeightConverter() { }
        public override bool HandleNull => true;
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) && double.IsFinite(value))
                return Math.Clamp(value, 240, 10000);
            reader.Skip();
            return 850;
        }
        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
    }

    internal sealed class DesktopPreferenceWindowPositionConverter : JsonConverter<double?>
    {
        public DesktopPreferenceWindowPositionConverter() { }
        public override bool HandleNull => true;
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) &&
                double.IsFinite(value) && Math.Abs(value) <= 100000)
                return value;
            reader.Skip();
            return null;
        }
        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value.HasValue) writer.WriteNumberValue(value.Value);
            else writer.WriteNullValue();
        }
    }

    internal sealed class DesktopPreferenceColumnWidthsConverter : JsonConverter<Dictionary<string, double>>
    {
        public DesktopPreferenceColumnWidthsConverter() { }
        public override bool HandleNull => true;
        public override Dictionary<string, double> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Dictionary<string, double> result = new(StringComparer.Ordinal);
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                return result;
            }
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (result.Count >= 64) break;
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 100 || property.Name.Any(char.IsControl) ||
                    property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetDouble(out double width) ||
                    !double.IsFinite(width)) continue;
                result[property.Name] = Math.Clamp(width, 24, 2000);
            }
            return result;
        }
        public override void Write(Utf8JsonWriter writer, Dictionary<string, double> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, double> entry in value.OrderBy(item => item.Key, StringComparer.Ordinal))
                writer.WriteNumber(entry.Key, entry.Value);
            writer.WriteEndObject();
        }
    }

    [JsonSerializable(typeof(Config))]
    [JsonSerializable(typeof(SavedUrls))]
    public partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}
