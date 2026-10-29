using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MeowChan.Models;
using MeowChan.Desktop;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan
{
    public sealed class PresentationProfile
    {
        public bool CompactDensity { get; set; }
        public bool ArchiveMediaTiles { get; set; } = true;
        public string ReadingFontSize { get; set; } = "Normal";
        public string ArchiveThreadLayout { get; set; } = "List";
        public double ArchiveSplitWidth { get; set; } = 270;
        [JsonConverter(typeof(DesktopPreferenceColumnWidthsConverter))]
        public Dictionary<string, double> ColumnWidths { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> ColumnVisibility { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ColumnOrder { get; set; } = new(StringComparer.Ordinal);

        public PresentationProfile Clone() => new()
        {
            CompactDensity = CompactDensity, ArchiveMediaTiles = ArchiveMediaTiles,
            ReadingFontSize = ReadingFontSize, ArchiveThreadLayout = ArchiveThreadLayout,
            ArchiveSplitWidth = ArchiveSplitWidth,
            ColumnWidths = ColumnWidths == null ? new(StringComparer.Ordinal) : new(ColumnWidths, StringComparer.Ordinal),
            ColumnVisibility = ColumnVisibility == null ? new(StringComparer.Ordinal) : new(ColumnVisibility, StringComparer.Ordinal),
            ColumnOrder = ColumnOrder == null ? new(StringComparer.Ordinal) : new(ColumnOrder, StringComparer.Ordinal)
        };
    }

    /// <summary>Host-owned consent. Plugin settings and metadata cannot grant it.</summary>
    public sealed record SourceMediaIntegrityPermission(string PluginOwnerId, string SourceId,
        bool AllowProviderSizeMd5Mismatch, long Revision)
    {
        public static string CreateKey(string pluginOwnerId, string sourceId)
        {
            ValidateIdentity(pluginOwnerId, nameof(pluginOwnerId));
            ValidateIdentity(sourceId, nameof(sourceId));
            return Uri.EscapeDataString(pluginOwnerId) + "/" + Uri.EscapeDataString(sourceId);
        }

        internal static void ValidateIdentity(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
                throw new ArgumentException("A bounded source/owner identity is required.", name);
        }
    }

    public class Config : IJsonOnDeserialized
    {
        private string? _profileDirectory;
        private string _relativePathBaseDirectory = Path.GetFullPath(Environment.CurrentDirectory);
        private string _theme = "Dark";
        private string _language = "en";
        private string _workspaceMode = "Barebones";
        private int _videoVolume = 70;
        private string _galleryPlaybackMode = "Manual";
        private int _gallerySlideshowSeconds = 5;
        private double _windowWidth = 1380;
        private double _windowHeight = 850;
        private double? _windowPositionX;
        private double? _windowPositionY;
        private Dictionary<string, PresentationProfile> _layoutProfiles = new(StringComparer.Ordinal);
        private Dictionary<string, PresentationProfile>? _deserializedLayoutProfiles;
        private string _lastExportDirectory = string.Empty;

        public Desktop.Commands.ShortcutBindingsSection ShortcutBindings { get; set; } = new();
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, SourceMediaIntegrityPermission> SourceMediaIntegrityPermissions { get; set; } = new(StringComparer.Ordinal);
        public string DownloadPath { get; set; } = GetDefaultDownloadPath();
        public int RecheckTimer { get; set; } = 60;
        public bool SaveUrlsOnExit { get; set; } = true;
        public bool ShowBoardAdmissionWizard { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool RememberReadingPosition { get; set; }
        public bool DownloadsPaused { get; set; }
        public bool MinimizeToTray { get; set; }
        public bool CloseToTray { get; set; }
        public long BandwidthLimiter { get; set; } = 1024 * 1024;
        public int MaxParallelDownloads { get; set; } = 5;
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool AutomaticUpdateChecks { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool IncludePrereleaseUpdates { get; set; }
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowInformationNotifications { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowWarningNotifications { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowErrorNotifications { get; set; } = true;
        public int MinimumFreeSpaceGiB { get; set; } = 1;
        public int PluginMaximumProductionWorkers { get; set; } = 4;
        public int PluginWorkerIdleMinutes { get; set; } = 5;
        public int PluginSourceHeapMiB { get; set; } = 256;
        public int PluginFilterHeapMiB { get; set; } = 512;

        public string? PluginEditorExecutable { get; set; }
        public string[] PluginEditorArguments { get; set; } = ["{file}"];


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

        [JsonPropertyOrder(100)]
        public Dictionary<string, PresentationProfile> LayoutProfiles
        {
            get => _layoutProfiles;
            set
            {
                if (value == null) return;
                _deserializedLayoutProfiles = value
                    .Where(pair => pair.Value != null)
                    .ToDictionary(pair => pair.Key, pair => NormalizeProfile(pair.Value), StringComparer.Ordinal);
                foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
                    if (value.TryGetValue(mode, out PresentationProfile? profile) && profile != null)
                        _layoutProfiles[mode] = NormalizeProfile(profile);
            }
        }

        void IJsonOnDeserialized.OnDeserialized()
        {
            // Couple only after all legacy keys have been read, independent of JSON order.
            NormalizeGalleryControls();
            // The new profiles win even if a hand-edited file places legacy active
            // preference fields after LayoutProfiles.
            if (_deserializedLayoutProfiles == null) return;
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
                if (_deserializedLayoutProfiles.TryGetValue(mode, out PresentationProfile? profile))
                    _layoutProfiles[mode] = profile;
            _deserializedLayoutProfiles = null;
        }

        public PresentationProfile GetLayoutProfile(string mode)
        {
            mode = NormalizeWorkspaceMode(mode);
            if (!_layoutProfiles.TryGetValue(mode, out PresentationProfile? profile))
                _layoutProfiles[mode] = profile = new PresentationProfile();
            return profile;
        }

        private PresentationProfile ActiveProfile => GetLayoutProfile(WorkspaceMode);

        private static string NormalizeWorkspaceMode(string? mode) =>
            string.Equals(mode?.Trim(), "Advanced", StringComparison.OrdinalIgnoreCase) ? "Advanced" :
            string.Equals(mode?.Trim(), "Simple", StringComparison.OrdinalIgnoreCase) ? "Simple" : "Barebones";

        private static PresentationProfile NormalizeProfile(PresentationProfile profile)
        {
            PresentationProfile result = profile.Clone();
            result.ReadingFontSize = string.Equals(profile.ReadingFontSize?.Trim(), "Small", StringComparison.OrdinalIgnoreCase) ? "Small"
                : string.Equals(profile.ReadingFontSize?.Trim(), "Large", StringComparison.OrdinalIgnoreCase) ? "Large" : "Normal";
            result.ArchiveThreadLayout = string.Equals(profile.ArchiveThreadLayout?.Trim(), "Table", StringComparison.OrdinalIgnoreCase) ? "Table" : "List";
            result.ArchiveSplitWidth = double.IsFinite(profile.ArchiveSplitWidth) ? Math.Clamp(profile.ArchiveSplitWidth, 180, 1200) : 270;
            return result;
        }

        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool CompactDensity { get => ActiveProfile.CompactDensity; set => ActiveProfile.CompactDensity = value; }

        [JsonIgnore]
        internal bool? SettingsBaselineCompactDensity { get; set; }
        [JsonIgnore]
        internal string? SettingsBaselineReadingFontSize { get; set; }
        [JsonIgnore]
        internal string? SettingsBaselineWorkspaceMode { get; set; }
        [JsonIgnore]
        internal Dictionary<string, PresentationProfile>? SettingsBaselineProfiles { get; set; }
        [JsonIgnore]
        internal int? SettingsBaselineVideoVolume { get; set; }
        [JsonIgnore]
        internal GalleryPlaybackPreferences? SettingsBaselineGalleryPlayback { get; set; }

        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool ArchiveMediaTiles { get => ActiveProfile.ArchiveMediaTiles; set => ActiveProfile.ArchiveMediaTiles = value; }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ReadingFontSize
        {
            get => ActiveProfile.ReadingFontSize;
            set => ActiveProfile.ReadingFontSize = string.Equals(value?.Trim(), "Small", StringComparison.OrdinalIgnoreCase) ? "Small"
                : string.Equals(value?.Trim(), "Large", StringComparison.OrdinalIgnoreCase) ? "Large" : "Normal";
        }

        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string ArchiveThreadLayout
        {
            get => ActiveProfile.ArchiveThreadLayout;
            set => ActiveProfile.ArchiveThreadLayout = string.Equals(value?.Trim(), "Table", StringComparison.OrdinalIgnoreCase) ? "Table" : "List";
        }

        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool VideoAutoplay { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool VideoStartMuted { get; set; }
        [JsonConverter(typeof(DesktopPreferenceVolumeConverter))]
        public int VideoVolume { get => _videoVolume; set => _videoVolume = Math.Clamp(value, 0, 100); }
        [JsonConverter(typeof(DesktopPreferenceStringConverter))]
        public string GalleryPlaybackMode
        {
            get => _galleryPlaybackMode;
            set => _galleryPlaybackMode = string.Equals(value?.Trim(), "Repeat", StringComparison.OrdinalIgnoreCase) ? "Repeat"
                : string.Equals(value?.Trim(), "Slideshow", StringComparison.OrdinalIgnoreCase) ? "Slideshow" : "Manual";
        }
        [JsonConverter(typeof(DesktopPreferenceSlideshowSecondsConverter))]
        public int GallerySlideshowSeconds { get => _gallerySlideshowSeconds; set => _gallerySlideshowSeconds = Math.Clamp(value, 1, 300); }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryMediaFocus { get; set; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryOverlayControls { get; set; }
        // Keep both serialized keys for older profiles; explicit actions use
        // GalleryPlaybackPreferences.WithControlsOverlay rather than coupled setters.
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryAutoHideControls { get; set; }
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryOverlayPostContext { get; set; }

        [JsonConverter(typeof(DesktopPreferenceTrueBooleanConverter))]
        public bool ShowShortcutHelp { get; set; } = true;
        [JsonConverter(typeof(DesktopPreferenceBooleanConverter))]
        public bool GalleryContextOpen { get; set; }
        [JsonConverter(typeof(DesktopPreferenceSplitWidthConverter))]
        public double ArchiveSplitWidth
        {
            get => ActiveProfile.ArchiveSplitWidth;
            set => ActiveProfile.ArchiveSplitWidth = double.IsFinite(value) ? Math.Clamp(value, 180, 1200) : 270;
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
            get => ActiveProfile.ColumnWidths;
            set => ActiveProfile.ColumnWidths = value == null ? new(StringComparer.Ordinal) : new(value, StringComparer.Ordinal);
        }
        public Dictionary<string, bool> ColumnVisibility
        {
            get => ActiveProfile.ColumnVisibility;
            set => ActiveProfile.ColumnVisibility = value == null ? new(StringComparer.Ordinal) : new(value, StringComparer.Ordinal);
        }
        public Dictionary<string, int> ColumnOrder
        {
            get => ActiveProfile.ColumnOrder;
            set => ActiveProfile.ColumnOrder = value == null ? new(StringComparer.Ordinal) : new(value, StringComparer.Ordinal);
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
                // A backup can predate an explicit revoke. Recovery restores settings, but
                // source exceptions require fresh consent rather than reviving an old grant.
                bool disabledSourceGrants = recovered.SourceMediaIntegrityPermissions.Values
                    .Any(permission => permission.AllowProviderSizeMd5Mismatch);
                if (disabledSourceGrants)
                    recovered.SourceMediaIntegrityPermissions = recovered.SourceMediaIntegrityPermissions.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.AllowProviderSizeMd5Mismatch
                            ? pair.Value with { AllowProviderSizeMd5Mismatch = false } : pair.Value,
                        StringComparer.Ordinal);
                recovered.LoadWarning = $"Settings could not be loaded from '{filePath}' ({error ?? "file missing"}). " +
                    $"Recovered settings from backup '{backupPath}'. The original files were preserved." +
                    (disabledSourceGrants ? " Source media exception permissions from the backup were disabled and require explicit confirmation in Sources." : string.Empty);
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

        private void NormalizeGalleryControls()
        {
            GalleryAutoHideControls = GalleryOverlayControls;
            SettingsBaselineGalleryPlayback = SettingsBaselineGalleryPlayback?.Normalize();
        }

        public void Validate()
        {
            NormalizeGalleryControls();
            if (SourceMediaIntegrityPermissions == null || SourceMediaIntegrityPermissions.Count > 1024)
                throw new InvalidDataException("Saved source media permissions are invalid.");
            foreach ((string key, SourceMediaIntegrityPermission permission) in SourceMediaIntegrityPermissions)
            {
                if (permission == null || permission.Revision < 0)
                    throw new InvalidDataException("Saved source media permission identity is invalid.");
                string expectedKey;
                try { expectedKey = SourceMediaIntegrityPermission.CreateKey(permission.PluginOwnerId, permission.SourceId); }
                catch (ArgumentException error) { throw new InvalidDataException("Saved source media permission identity is invalid.", error); }
                if (!string.Equals(key, expectedKey, StringComparison.Ordinal))
                    throw new InvalidDataException("Saved source media permission identity is invalid.");
            }
            if (SchemaVersion != 1)
                throw new InvalidDataException($"Unsupported settings schema version: {SchemaVersion}.");
            if (RecheckTimer < 10 || TimeSpan.FromSeconds(RecheckTimer).TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(RecheckTimer), "The recheck interval must be between 10 seconds and the supported timer maximum.");
            if (BandwidthLimiter <= 0)
                throw new ArgumentOutOfRangeException(nameof(BandwidthLimiter), "The bandwidth limit must be greater than zero.");
            if (MaxParallelDownloads <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxParallelDownloads), "Parallel downloads must be greater than zero.");
            if (PluginMaximumProductionWorkers is < 1 or > 8)
                throw new ArgumentOutOfRangeException(nameof(PluginMaximumProductionWorkers), "The plugin limit must be between 1 and 8.");
            if (PluginWorkerIdleMinutes is < 1 or > 30)
                throw new ArgumentOutOfRangeException(nameof(PluginWorkerIdleMinutes), "The plugin limit must be between 1 and 30.");
            if (PluginSourceHeapMiB is < 128 or > 1024)
                throw new ArgumentOutOfRangeException(nameof(PluginSourceHeapMiB), "The plugin limit must be between 128 and 1024.");
            if (PluginFilterHeapMiB is < 256 or > 2048)
                throw new ArgumentOutOfRangeException(nameof(PluginFilterHeapMiB), "The plugin limit must be between 256 and 2048.");
            if (MinimumFreeSpaceGiB < 0)
                throw new ArgumentOutOfRangeException(nameof(MinimumFreeSpaceGiB), "Free-space reserve must not be negative.");
            if (PluginEditorExecutable is { Length: > 4096 } || PluginEditorExecutable?.Any(char.IsControl) == true
                || PluginEditorArguments == null || PluginEditorArguments.Length > 32
                || PluginEditorArguments.Any(value => value == null || value.Length > 4096 || value.Any(char.IsControl)))
                throw new ArgumentException("The plugin editor command is invalid.", nameof(PluginEditorExecutable));
            if (string.IsNullOrWhiteSpace(DownloadPath))
                throw new ArgumentException("A download directory is required.", nameof(DownloadPath));

            _ = Path.GetFullPath(DownloadPath, _relativePathBaseDirectory);
            if (LastExportDirectory.Length > 4096 || LastExportDirectory.Any(char.IsControl))
                throw new ArgumentException("The last export directory is invalid.", nameof(LastExportDirectory));
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                PresentationProfile profile = GetLayoutProfile(mode);
                if (profile.ColumnWidths.Count > 64 || profile.ColumnWidths.Any(entry => string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 100 ||
                    entry.Key.Any(char.IsControl) || !double.IsFinite(entry.Value) || entry.Value < 24 || entry.Value > 2000))
                    throw new ArgumentException("Saved column widths are invalid.", nameof(ColumnWidths));
                if (profile.ColumnVisibility.Count > 64 || profile.ColumnVisibility.Keys.Any(key =>
                    string.IsNullOrWhiteSpace(key) || key.Length > 100 || key.Any(char.IsControl)))
                    throw new ArgumentException("Saved column visibility is invalid.", nameof(ColumnVisibility));
                if (profile.ColumnOrder.Count > 64 || profile.ColumnOrder.Any(entry =>
                    string.IsNullOrWhiteSpace(entry.Key) || entry.Key.Length > 100 || entry.Key.Any(char.IsControl)
                    || entry.Value < 0 || entry.Value > 63))
                    throw new ArgumentException("Saved column order is invalid.", nameof(ColumnOrder));
            }
        }

        public Config Clone()
        {
            return new Config
            {
                SchemaVersion = SchemaVersion,
                SourceMediaIntegrityPermissions = SourceMediaIntegrityPermissions.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.Ordinal),
                DownloadPath = DownloadPath,
                RecheckTimer = RecheckTimer,
                SaveUrlsOnExit = SaveUrlsOnExit,
                ShowBoardAdmissionWizard = ShowBoardAdmissionWizard,
                RememberReadingPosition = RememberReadingPosition,
                DownloadsPaused = DownloadsPaused,
                MinimizeToTray = MinimizeToTray,
                CloseToTray = CloseToTray,
                BandwidthLimiter = BandwidthLimiter,
                MaxParallelDownloads = MaxParallelDownloads,
                AutomaticUpdateChecks = AutomaticUpdateChecks,
                ShowInformationNotifications = ShowInformationNotifications,
                ShowWarningNotifications = ShowWarningNotifications,
                ShowErrorNotifications = ShowErrorNotifications,
                IncludePrereleaseUpdates = IncludePrereleaseUpdates,
                MinimumFreeSpaceGiB = MinimumFreeSpaceGiB,
                PluginMaximumProductionWorkers = PluginMaximumProductionWorkers,
                PluginWorkerIdleMinutes = PluginWorkerIdleMinutes,
                PluginSourceHeapMiB = PluginSourceHeapMiB,
                PluginFilterHeapMiB = PluginFilterHeapMiB,

                ShortcutBindings = ShortcutBindings.Clone(),
                PluginEditorExecutable = PluginEditorExecutable,
                PluginEditorArguments = (string[])PluginEditorArguments.Clone(),
                Theme = Theme,
                Language = Language,
                WorkspaceMode = WorkspaceMode,
                CompactDensity = CompactDensity,
                SettingsBaselineCompactDensity = CompactDensity,
                SettingsBaselineReadingFontSize = ReadingFontSize,
                SettingsBaselineWorkspaceMode = WorkspaceMode,
                SettingsBaselineVideoVolume = VideoVolume,
                // A fresh draft captures the current normalized state. Session admission
                // restores the editor's original baseline after freezing via Clone().
                SettingsBaselineGalleryPlayback = GalleryPlaybackPreferences.FromConfig(this),
                SettingsBaselineProfiles = new[] { "Barebones", "Simple", "Advanced" }
                    .ToDictionary(mode => mode, mode => GetLayoutProfile(mode).Clone(), StringComparer.Ordinal),
                ArchiveMediaTiles = ArchiveMediaTiles,
                ArchiveThreadLayout = ArchiveThreadLayout,
                ReadingFontSize = ReadingFontSize,
                VideoAutoplay = VideoAutoplay,
                VideoStartMuted = VideoStartMuted,
                VideoVolume = VideoVolume,
                GalleryPlaybackMode = GalleryPlaybackMode,
                GallerySlideshowSeconds = GallerySlideshowSeconds,
                GalleryMediaFocus = GalleryMediaFocus,
                GalleryOverlayControls = GalleryOverlayControls,
                GalleryAutoHideControls = GalleryOverlayControls,
                GalleryOverlayPostContext = GalleryOverlayPostContext,
                ShowShortcutHelp = ShowShortcutHelp,
                GalleryContextOpen = GalleryContextOpen,
                ArchiveSplitWidth = ArchiveSplitWidth,
                WindowWidth = WindowWidth,
                WindowHeight = WindowHeight,
                WindowPositionX = WindowPositionX,
                WindowPositionY = WindowPositionY,
                WindowMaximized = WindowMaximized,
                ColumnWidths = ColumnWidths,
                LayoutProfiles = _layoutProfiles.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
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
            // Consent is portable within this profile's clones, never across profile-bound instances.
            if (string.Equals(_profileDirectory, source._profileDirectory,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                SourceMediaIntegrityPermissions = source.SourceMediaIntegrityPermissions.ToDictionary(pair => pair.Key, pair => pair.Value with { }, StringComparer.Ordinal);
            DownloadPath = absoluteDownloadPath;
            RecheckTimer = source.RecheckTimer;
            SaveUrlsOnExit = source.SaveUrlsOnExit;
            ShowBoardAdmissionWizard = source.ShowBoardAdmissionWizard;
            RememberReadingPosition = source.RememberReadingPosition;
            DownloadsPaused = source.DownloadsPaused;
            MinimizeToTray = source.MinimizeToTray;
            CloseToTray = source.CloseToTray;
            BandwidthLimiter = source.BandwidthLimiter;
            MaxParallelDownloads = source.MaxParallelDownloads;
            AutomaticUpdateChecks = source.AutomaticUpdateChecks;
            ShowInformationNotifications = source.ShowInformationNotifications;
            ShowWarningNotifications = source.ShowWarningNotifications;
            ShowErrorNotifications = source.ShowErrorNotifications;
            IncludePrereleaseUpdates = source.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = source.MinimumFreeSpaceGiB;
            PluginMaximumProductionWorkers = source.PluginMaximumProductionWorkers;
            PluginWorkerIdleMinutes = source.PluginWorkerIdleMinutes;
            PluginSourceHeapMiB = source.PluginSourceHeapMiB;
            PluginFilterHeapMiB = source.PluginFilterHeapMiB;

            ShortcutBindings = source.ShortcutBindings.Clone();
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
            GalleryPlaybackPreferences.FromConfig(source).ApplyTo(this);
            ShowShortcutHelp = source.ShowShortcutHelp;
            GalleryContextOpen = source.GalleryContextOpen;
            ArchiveSplitWidth = source.ArchiveSplitWidth;
            WindowWidth = source.WindowWidth;
            WindowHeight = source.WindowHeight;
            WindowPositionX = source.WindowPositionX;
            WindowPositionY = source.WindowPositionY;
            WindowMaximized = source.WindowMaximized;
            ColumnWidths = source.ColumnWidths;
            LayoutProfiles = source._layoutProfiles.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            LastExportDirectory = source.LastExportDirectory;
            PluginEditorExecutable = source.PluginEditorExecutable;
            PluginEditorArguments = (string[])source.PluginEditorArguments.Clone();
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
            RememberReadingPosition = source.RememberReadingPosition;
            MinimizeToTray = source.MinimizeToTray;
            CloseToTray = source.CloseToTray;
            BandwidthLimiter = source.BandwidthLimiter;
            MaxParallelDownloads = source.MaxParallelDownloads;
            AutomaticUpdateChecks = source.AutomaticUpdateChecks;
            ShowInformationNotifications = source.ShowInformationNotifications;
            ShowWarningNotifications = source.ShowWarningNotifications;
            ShowErrorNotifications = source.ShowErrorNotifications;
            IncludePrereleaseUpdates = source.IncludePrereleaseUpdates;
            MinimumFreeSpaceGiB = source.MinimumFreeSpaceGiB;
            PluginMaximumProductionWorkers = source.PluginMaximumProductionWorkers;
            PluginWorkerIdleMinutes = source.PluginWorkerIdleMinutes;
            PluginSourceHeapMiB = source.PluginSourceHeapMiB;
            PluginFilterHeapMiB = source.PluginFilterHeapMiB;

            ShortcutBindings = source.ShortcutBindings.Clone();
            Theme = source.Theme;
            Language = source.Language;
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                PresentationProfile edited = source.GetLayoutProfile(mode);
                PresentationProfile baseline = source.SettingsBaselineProfiles != null &&
                    source.SettingsBaselineProfiles.TryGetValue(mode, out PresentationProfile? captured)
                    ? captured : new PresentationProfile();
                PresentationProfile liveEdited = GetLayoutProfile(mode);
                if (baseline.ReadingFontSize != edited.ReadingFontSize)
                    liveEdited.ReadingFontSize = edited.ReadingFontSize;
                if (baseline.CompactDensity != edited.CompactDensity)
                    liveEdited.CompactDensity = edited.CompactDensity;
            }
            WorkspaceMode = source.WorkspaceMode;
            VideoAutoplay = source.VideoAutoplay;
            VideoStartMuted = source.VideoStartMuted;
            // Gallery volume is also a direct preference. An unchanged settings
            // draft must not roll back a newer gallery adjustment.
            if (!source.SettingsBaselineVideoVolume.HasValue || source.VideoVolume != source.SettingsBaselineVideoVolume.Value)
                VideoVolume = source.VideoVolume;
            GalleryPlaybackPreferences galleryDraft = GalleryPlaybackPreferences.FromConfig(source);
            if (source.SettingsBaselineGalleryPlayback is { } galleryBaseline)
                galleryDraft.ApplyChangesTo(this, galleryBaseline);
            else
                galleryDraft.ApplyTo(this);
            ShowShortcutHelp = source.ShowShortcutHelp;
        }

        // Compatibility settings host uses its existing isolated config owner.
        internal void ForgetReadingPositionFile() => File.Delete(Path.Combine(_profileDirectory ?? GetConfigDirectory(), "reading-position.json"));

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

    internal sealed class DesktopPreferenceSlideshowSecondsConverter : JsonConverter<int>
    {
        public override bool HandleNull => true;
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int value))
                return Math.Clamp(value, 1, 300);
            reader.Skip();
            return 5;
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

    [JsonSerializable(typeof(Desktop.Commands.ShortcutBindingsSection))]
    [JsonSerializable(typeof(Config))]
    [JsonSerializable(typeof(SavedUrls))]
    public partial class ConfigJsonContext : JsonSerializerContext
    {
    }
}
