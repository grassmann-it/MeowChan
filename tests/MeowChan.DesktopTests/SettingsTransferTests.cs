using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

public sealed class SettingsTransferTests
{
    private static readonly string[] ExcludedKeys =
    [
        nameof(Config.WindowWidth), nameof(Config.WindowHeight), nameof(Config.WindowPositionX), nameof(Config.WindowPositionY),
        nameof(Config.WindowMaximized), nameof(Config.FirstRunSetupPending), nameof(Config.FirstRunSetupVersion),
        nameof(Config.WorkViewNoticePending), nameof(Config.SourceMediaIntegrityPermissions), nameof(Config.DownloadsPaused),
        nameof(Config.LastExportDirectory), nameof(Config.GalleryFrameDirectory), nameof(Config.GalleryContextOpen),
        nameof(Config.ArchiveBridgeHintSeen),
        nameof(Config.StatusPageSegment),
        nameof(Config.LastTaglineVersion),
    ];

    [Fact]
    public void ExportAddsSchemaAndProducerAndExcludesProfileLocalState()
    {
        Config source = new()
        {
            SchemaVersion = 1,
            WindowWidth = 1910,
            WindowHeight = 1080,
            WindowPositionX = 17,
            WindowPositionY = 29,
            WindowMaximized = true,
            FirstRunSetupPending = true,
            FirstRunSetupVersion = 1,
            WorkViewNoticePending = true,
            SourceMediaIntegrityPermissions = new()
            {
                [SourceMediaIntegrityPermission.CreateKey("owner", "source")] =
                    new SourceMediaIntegrityPermission("owner", "source", true, 4),
            },
            DownloadsPaused = true,
            LastExportDirectory = "/tmp/last-export",
            GalleryFrameDirectory = "/tmp/frames",
            GalleryContextOpen = true,
            ArchiveBridgeHintSeen = true,
            StatusPageSegment = Config.StatusPageSegmentDiagnostics,
        };

        JsonObject export = SettingsTransfer.Export(source, " 1.1.0+build.42 ");

        Assert.Equal(1, export[nameof(Config.SchemaVersion)]!.GetValue<int>());
        Assert.Equal("MeowChan 1.1.0", export["ExportedBy"]!.GetValue<string>());
        foreach (string key in ExcludedKeys)
            Assert.False(export.ContainsKey(key), $"{key} must not be portable");
        Assert.True(export.ContainsKey(nameof(Config.QuickTagSuggestionCount)), "TP5 settings must remain exportable");
        Assert.True(export.ContainsKey(nameof(Config.QuickTagMarkedMediaLimit)), "TP5 settings must remain exportable");
    }

    [Fact]
    public void ExportRoundTripsThroughExistingConfigSerializerWithoutImportFeatures()
    {
        Config source = new()
        {
            DownloadPath = "/tmp/Übertragung/猫",
            RecheckTimer = 173,
            SaveUrlsOnExit = false,
            Theme = "Light",
            Language = "de",
            StatsHeaderGraph = "SideBySide",
            StatsHeaderIcons = true,
            ArchiveTagFilterTagsPerList = 71,
            UrlTextExportLimitMiB = 17,
            SlashTagSuggestionCount = 13,
            ShrinkWindowForBarebones = false,
            VideoScrubCacheLimitMiB = 96,
            QuickTagSuggestionCount = 18,
            QuickTagRecentCount = 0,
            QuickTagUndoDepth = 37,
            QuickTagNeighbourScan = 777,
            QuickTagFilenameTokens = 12,
            QuickTagSourceTagsPerMedium = 91,
            QuickTagMarkedMediaLimit = 3210,
            PluginEditorExecutable = "编辑器/猫.exe",
            PluginEditorArguments = ["{file}", "日本語"],
        };
        source.GetLayoutProfile("Advanced").ReadingFontSize = "Large";

        JsonObject export = SettingsTransfer.Export(source, "1.1.0");
        Config restored = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;

        restored.Validate();
        Assert.Equal(source.DownloadPath, restored.DownloadPath);
        Assert.Equal(source.RecheckTimer, restored.RecheckTimer);
        Assert.Equal(source.SaveUrlsOnExit, restored.SaveUrlsOnExit);
        Assert.Equal(source.Theme, restored.Theme);
        Assert.Equal(source.Language, restored.Language);
        Assert.Equal(source.StatsHeaderGraph, restored.StatsHeaderGraph);
        Assert.Equal(source.StatsHeaderIcons, restored.StatsHeaderIcons);
        Assert.Equal(source.ArchiveTagFilterTagsPerList, restored.ArchiveTagFilterTagsPerList);
        Assert.Equal(source.UrlTextExportLimitMiB, restored.UrlTextExportLimitMiB);
        Assert.Equal(source.SlashTagSuggestionCount, restored.SlashTagSuggestionCount);
        Assert.Equal(source.ShrinkWindowForBarebones, restored.ShrinkWindowForBarebones);
        Assert.Equal(source.VideoScrubCacheLimitMiB, restored.VideoScrubCacheLimitMiB);
        Assert.Equal(source.QuickTagSuggestionCount, restored.QuickTagSuggestionCount);
        Assert.Equal(source.QuickTagRecentCount, restored.QuickTagRecentCount);
        Assert.Equal(source.QuickTagUndoDepth, restored.QuickTagUndoDepth);
        Assert.Equal(source.QuickTagNeighbourScan, restored.QuickTagNeighbourScan);
        Assert.Equal(source.QuickTagFilenameTokens, restored.QuickTagFilenameTokens);
        Assert.Equal(source.QuickTagSourceTagsPerMedium, restored.QuickTagSourceTagsPerMedium);
        Assert.Equal(source.QuickTagMarkedMediaLimit, restored.QuickTagMarkedMediaLimit);
        Assert.Equal(source.PluginEditorExecutable, restored.PluginEditorExecutable);
        Assert.Equal(source.PluginEditorArguments, restored.PluginEditorArguments);
        Assert.Equal("Large", restored.GetLayoutProfile("Advanced").ReadingFontSize);
        Assert.False(restored.GalleryContextOpen);
        Assert.False(restored.ArchiveBridgeHintSeen);
    }

    [Fact]
    public void ExportLeavesSerializedConfigBytesUnchanged()
    {
        Config source = new() { WorkspaceMode = "Advanced", QuickTagUndoDepth = 37 };
        foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            source.LayoutProfiles[mode] = new PresentationProfile { ReadingFontSize = "Large" };
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(source, ConfigJsonContext.Default.Config);

        _ = SettingsTransfer.Export(source, "1.1.0");

        byte[] after = JsonSerializer.SerializeToUtf8Bytes(source, ConfigJsonContext.Default.Config);
        Assert.Equal(before, after);
    }

    [Fact]
    public void NullSourceIsRejectedAndDefaultVersionHasProducerPrefix()
    {
        Assert.Throws<ArgumentNullException>(() => SettingsTransfer.Export(null!));
        JsonObject export = SettingsTransfer.Export(new Config());
        Assert.StartsWith("MeowChan ", export["ExportedBy"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0)] // No profiles; serialization must not create the active entry on source.
    [InlineData(1)] // Only an inactive profile.
    [InlineData(2)] // Active and inactive profiles.
    [InlineData(3)] // Also retain an unfamiliar profile key verbatim.
    public void ExportPreservesLayoutReferencesContentsAndDictionaryVersion(int profileCount)
    {
        Config source = CreateSourceWithLayouts(profileCount);
        Dictionary<string, PresentationProfile> profiles = source.LayoutProfiles;
        KeyValuePair<string, PresentationProfile>[] before = profiles.ToArray();
        var columnsBefore = before.ToDictionary(pair => pair.Key, pair =>
            (pair.Value.ColumnWidths, pair.Value.ColumnVisibility, pair.Value.ColumnOrder));
        using IEnumerator<KeyValuePair<string, PresentationProfile>> enumerator = profiles.GetEnumerator();

        JsonObject first = SettingsTransfer.Export(source, "1.1.0");
        JsonObject second = SettingsTransfer.Export(source, "1.1.0");

        Assert.Same(profiles, source.LayoutProfiles);
        Assert.Equal(before.Select(pair => pair.Key), profiles.Keys);
        foreach ((string name, PresentationProfile profile) in before)
        {
            Assert.Same(profile, profiles[name]);
            Assert.Same(columnsBefore[name].ColumnWidths, profile.ColumnWidths);
            Assert.Same(columnsBefore[name].ColumnVisibility, profile.ColumnVisibility);
            Assert.Same(columnsBefore[name].ColumnOrder, profile.ColumnOrder);
            Assert.Equal("Large", profile.ReadingFontSize);
            Assert.Equal(271, profile.ColumnWidths["Title"]);
            Assert.True(profile.ColumnVisibility["Title"]);
            Assert.Equal(2, profile.ColumnOrder["Title"]);
        }
        // Equal bytes alone miss Clear/reinsert: an already acquired enumerator detects version changes,
        // even when the original dictionary was empty or all entries were restored to the same references.
        foreach (KeyValuePair<string, PresentationProfile> pair in before)
        {
            Assert.True(enumerator.MoveNext());
            Assert.Equal(pair, enumerator.Current);
        }
        Assert.False(enumerator.MoveNext());
        Assert.True(JsonNode.DeepEquals(first, second));
        Assert.True(source.GalleryContextOpen);
        Assert.Equal("Advanced", source.WorkspaceMode);
        Assert.Equal("Large", source.SettingsBaselineReadingFontSize);
        Assert.Null(source.SettingsBaselineProfiles);
        Assert.NotNull(first[nameof(Config.LayoutProfiles)]!["Advanced"]);
        Assert.Equal(profileCount >= 2, profiles.ContainsKey("Advanced"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EmptyAndExistingLayoutProfilesRoundTripWithLegacyAliases(int profileCount)
    {
        Config source = CreateSourceWithLayouts(profileCount);

        JsonObject export = SettingsTransfer.Export(source, "1.1.0-rc.1+build42");
        Config restored = JsonSerializer.Deserialize(export.ToJsonString(), ConfigJsonContext.Default.Config)!;

        restored.Validate();
        Assert.Equal("MeowChan 1.1.0-rc.1", export["ExportedBy"]!.GetValue<string>());
        Assert.Equal("Advanced", restored.WorkspaceMode);
        Assert.Equal(profileCount, source.LayoutProfiles.Count);
        PresentationProfile active = restored.GetLayoutProfile("Advanced");
        Assert.Equal(profileCount >= 2 ? "Large" : "Normal", active.ReadingFontSize);
        Assert.Equal(active.ReadingFontSize, restored.ReadingFontSize);
        Assert.Equal(active.CompactDensity, restored.CompactDensity);
        Assert.Equal(active.ArchiveMediaTiles, restored.ArchiveMediaTiles);
        Assert.Equal(active.ArchiveThreadLayout, restored.ArchiveThreadLayout);
        Assert.Equal(active.ArchiveSplitWidth, restored.ArchiveSplitWidth);
        Assert.Same(active.ColumnWidths, restored.ColumnWidths);
        Assert.Same(active.ColumnVisibility, restored.ColumnVisibility);
        Assert.Same(active.ColumnOrder, restored.ColumnOrder);
        foreach ((string name, PresentationProfile profile) in source.LayoutProfiles)
        {
            PresentationProfile copy = restored.LayoutProfiles[name];
            Assert.NotSame(profile, copy);
            Assert.Equal(profile.ReadingFontSize, copy.ReadingFontSize);
            Assert.Equal(profile.ColumnWidths, copy.ColumnWidths);
            Assert.Equal(profile.ColumnVisibility, copy.ColumnVisibility);
            Assert.Equal(profile.ColumnOrder, copy.ColumnOrder);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SerializationBoundaryUsesDetachedValuesAndNeverMutatesSource(int profileCount)
    {
        Config source = CreateSourceWithLayouts(profileCount);
        source.PluginEditorArguments = ["{file}", "猫"];
        source.ShortcutBindings = new ShortcutBindingsSection().WithSingleKeyShortcuts(false);
        source.SourceMediaIntegrityPermissions["consent-canary"] =
            new SourceMediaIntegrityPermission("private-owner", "private-source", true, 9);
        source.LastExportDirectory = "last-directory-canary";
        string[] arguments = source.PluginEditorArguments;
        ShortcutBindingsSection shortcuts = source.ShortcutBindings;
        Dictionary<string, PresentationProfile> profiles = source.LayoutProfiles;
        KeyValuePair<string, PresentationProfile>[] before = profiles.ToArray();
        using IEnumerator<KeyValuePair<string, PresentationProfile>> enumerator = profiles.GetEnumerator();
        bool visited = false;

        JsonObject export = SettingsTransfer.Export(source, "1.1.0", snapshot =>
        {
            visited = true;
            Assert.NotSame(source, snapshot);
            Assert.NotSame(profiles, snapshot.LayoutProfiles);
            Assert.NotSame(arguments, snapshot.PluginEditorArguments);
            Assert.NotSame(shortcuts, snapshot.ShortcutBindings);
            Assert.Empty(snapshot.SourceMediaIntegrityPermissions);
            Assert.Empty(snapshot.LastExportDirectory);
            Assert.False(snapshot.GalleryContextOpen);
            Assert.Null(snapshot.SettingsBaselineReadingFontSize);
            foreach ((string name, PresentationProfile profile) in before)
            {
                PresentationProfile copy = snapshot.LayoutProfiles[name];
                Assert.NotSame(profile, copy);
                Assert.NotSame(profile.ColumnWidths, copy.ColumnWidths);
                Assert.NotSame(profile.ColumnVisibility, copy.ColumnVisibility);
                Assert.NotSame(profile.ColumnOrder, copy.ColumnOrder);
                copy.ColumnWidths["Title"] = 999;
                copy.ColumnVisibility["Title"] = false;
                copy.ColumnOrder["Title"] = 9;
                copy.ReadingFontSize = "Small";
            }
            snapshot.PluginEditorArguments[1] = "snapshot-only";
            JsonObject result = Serialize(snapshot); // Compatibility getters may now materialize a profile, locally.

            Assert.Equal(before.Select(pair => pair.Key), source.LayoutProfiles.Keys);
            Assert.Equal("猫", source.PluginEditorArguments[1]);
            foreach ((string name, PresentationProfile profile) in before)
            {
                Assert.Same(profile, source.LayoutProfiles[name]);
                Assert.Equal("Large", profile.ReadingFontSize);
                Assert.Equal(271, profile.ColumnWidths["Title"]);
                Assert.True(profile.ColumnVisibility["Title"]);
                Assert.Equal(2, profile.ColumnOrder["Title"]);
            }
            // A reentrant export during serialization also sees only the unmodified original.
            Assert.Equal(profileCount + (profileCount < 2 ? 1 : 0),
                SettingsTransfer.Export(source, "1.1.0")[nameof(Config.LayoutProfiles)]!.AsObject().Count);
            return result;
        });

        Assert.True(visited);
        Assert.Same(profiles, source.LayoutProfiles);
        Assert.Same(arguments, source.PluginEditorArguments);
        Assert.Same(shortcuts, source.ShortcutBindings);
        Assert.False(shortcuts.SingleKeyShortcuts);
        Assert.Single(source.SourceMediaIntegrityPermissions);
        Assert.DoesNotContain("canary", export.ToJsonString(), StringComparison.Ordinal);
        foreach (KeyValuePair<string, PresentationProfile> pair in before)
        {
            Assert.True(enumerator.MoveNext());
            Assert.Equal(pair, enumerator.Current);
        }
        Assert.False(enumerator.MoveNext());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SerializerFailureAfterReadingCompatibilityGettersLeavesOriginalUntouched(int profileCount)
    {
        Config source = CreateSourceWithLayouts(profileCount);
        Dictionary<string, PresentationProfile> profiles = source.LayoutProfiles;
        KeyValuePair<string, PresentationProfile>[] before = profiles.ToArray();
        using IEnumerator<KeyValuePair<string, PresentationProfile>> enumerator = profiles.GetEnumerator();
        JsonException failure = new("synthetic serializer failure");

        JsonException actual = Assert.Throws<JsonException>(() => SettingsTransfer.Export(source, "1.1.0", snapshot =>
        {
            _ = Serialize(snapshot);
            Assert.Equal(before.Select(pair => pair.Key), source.LayoutProfiles.Keys);
            throw failure;
        }));

        Assert.Same(failure, actual);
        Assert.Same(profiles, source.LayoutProfiles);
        Assert.Equal(before.Select(pair => pair.Key), source.LayoutProfiles.Keys);
        foreach ((string name, PresentationProfile profile) in before)
        {
            Assert.Same(profile, source.LayoutProfiles[name]);
            Assert.True(enumerator.MoveNext());
        }
        Assert.False(enumerator.MoveNext());
        Assert.True(source.GalleryContextOpen);
    }

    [Fact]
    public void RealSerializerFailureOnNonFiniteColumnWidthDoesNotReinsertOriginalProfiles()
    {
        Config source = CreateSourceWithLayouts(1); // Active profile is absent when serialization begins.
        PresentationProfile original = source.LayoutProfiles["Simple"];
        Dictionary<string, double> widths = original.ColumnWidths;
        widths["Title"] = double.NaN;
        using IEnumerator<KeyValuePair<string, PresentationProfile>> enumerator = source.LayoutProfiles.GetEnumerator();

        Assert.Throws<ArgumentException>(() => SettingsTransfer.Export(source, "1.1.0"));

        Assert.Single(source.LayoutProfiles);
        Assert.Same(original, source.LayoutProfiles["Simple"]);
        Assert.Same(widths, original.ColumnWidths);
        Assert.True(double.IsNaN(widths["Title"]));
        Assert.False(source.LayoutProfiles.ContainsKey("Advanced"));
        Assert.True(enumerator.MoveNext());
        Assert.Same(original, enumerator.Current.Value);
        Assert.False(enumerator.MoveNext());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1.1.0\ninvalid")]
    [InlineData("1.1.0/private")]
    public void InvalidProductVersionIsRejectedBeforeSnapshotOrSerialization(string version)
    {
        Config source = new();
        using IEnumerator<KeyValuePair<string, PresentationProfile>> enumerator = source.LayoutProfiles.GetEnumerator();

        Assert.Throws<ArgumentException>(() => SettingsTransfer.Export(source, version, _ =>
            throw new InvalidOperationException("Serialization must not be called")));

        Assert.Empty(source.LayoutProfiles);
        Assert.False(enumerator.MoveNext());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportCopiesEveryPortableSerializedValueIncludingAllNumericAndBooleanSettings(bool throughUiSnapshot)
    {
        Config source = CreateSourceWithLayouts(3);
        // Use the existing public serialization metadata in this test only. Non-default values detect
        // omissions in the explicit snapshot when Config gains a new limit or switch.
        foreach (var property in ConfigJsonContext.Default.Config.Properties)
        {
            if (ExcludedKeys.Contains(property.Name) || property.Name == nameof(Config.SchemaVersion) ||
                property.Get is not { } get || property.Set is not { } set) continue;
            object? current = get(source);
            if (current is bool boolean) set(source, !boolean);
            else if (current is int integer) set(source, integer + 1);
            else if (current is long number) set(source, number + 1L);
            else if (current is double width) set(source, width + 1d);
        }
        source.SchemaVersion = 7; // Export preserves the schema marker rather than validating or rewriting it.
        source.Theme = "Light";
        source.ThemeOverrides = MeowChan.Desktop.Theming.ThemeColorOverrides.Create([new("Workspace.Accent", "#1D5E3A")], [new("Workspace.Panel", "#232A3A")]);
        source.Animations = "Off";
        source.Language = "de";
        source.ReaderDateStyle = "Long";
        source.ReaderSpoilers = "Always";
        source.ReaderPostWidth = "Column";
        source.MediaSort = "Name";
        source.MediaSortDirection = "Reversed";
        source.StatsHeaderGraph = "SideBySide";
        source.GalleryPlaybackMode = "Slideshow";
        source.GallerySlideshowVideoRule = "RepeatTimes";
        source.DownloadPath = "/tmp/portable/猫";
        source.PluginEditorExecutable = "editor-canary";
        source.PluginEditorArguments = ["{file}", "--猫"];
        source.ShortcutBindings = new ShortcutBindingsSection().WithSingleKeyShortcuts(false);
        // These aliases must not be coupled by Config.Clone/Validate during export.
        source.GalleryOverlayControls = false;
        source.GalleryAutoHideControls = true;
        source.SourceMediaIntegrityPermissions["excluded-consent"] =
            new SourceMediaIntegrityPermission("sensitive-owner", "sensitive-source", true, 42);
        source.LastExportDirectory = "sensitive-last-directory";
        source.GalleryFrameDirectory = "sensitive-frame-directory";
        JsonObject expected = Serialize(source); // All profiles already present, so this read is non-materializing.
        foreach (string key in ExcludedKeys) expected.Remove(key);
        expected["ExportedBy"] = "MeowChan 1.1.0";

        Config input = throughUiSnapshot ? SettingsExportUi.CaptureSavedSnapshot(source) : source;
        JsonObject export = SettingsTransfer.Export(input, "1.1.0+build42");

        Assert.Equal(expected.Select(pair => pair.Key).Order(), export.Select(pair => pair.Key).Order());
        foreach ((string key, JsonNode? value) in expected)
            Assert.True(JsonNode.DeepEquals(value, export[key]), $"Portable setting {key} was not copied verbatim");
        Assert.DoesNotContain("sensitive-", export.ToJsonString(), StringComparison.Ordinal);
        Assert.False(export[nameof(Config.GalleryOverlayControls)]!.GetValue<bool>());
        Assert.True(export[nameof(Config.GalleryAutoHideControls)]!.GetValue<bool>());
    }

    private static JsonObject Serialize(Config config) =>
        JsonSerializer.SerializeToNode(config, ConfigJsonContext.Default.Config)!.AsObject();

    private static Config CreateSourceWithLayouts(int profileCount)
    {
        Config source = new()
        {
            WorkspaceMode = "Advanced",
            GalleryContextOpen = true,
            SettingsBaselineReadingFontSize = "Large",
        };
        foreach (string mode in new[] { "Simple", "Advanced", "Future" }.Take(profileCount))
            source.LayoutProfiles.Add(mode, new PresentationProfile
            {
                CompactDensity = true,
                ArchiveMediaTiles = false,
                ReadingFontSize = "Large",
                ArchiveThreadLayout = "Table",
                ArchiveSplitWidth = 350,
                ColumnWidths = new() { ["Title"] = 271 },
                ColumnVisibility = new() { ["Title"] = true },
                ColumnOrder = new() { ["Title"] = 2 },
            });
        return source;
    }
}
