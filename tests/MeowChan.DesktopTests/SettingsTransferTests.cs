using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Desktop;

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
        Assert.Equal(source.Theme, restored.Theme);
        Assert.Equal(source.Language, restored.Language);
        Assert.Equal(source.StatsHeaderGraph, restored.StatsHeaderGraph);
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

    [Fact]
    public void ExportDoesNotChangeConfigOrMaterializeAnActiveLayoutProfile()
    {
        Config source = new() { WorkspaceMode = "Advanced", GalleryContextOpen = true };
        source.LayoutProfiles["Simple"] = new PresentationProfile { ReadingFontSize = "Small" };
        KeyValuePair<string, PresentationProfile>[] profilesBefore = source.LayoutProfiles.ToArray();
        bool contextBefore = source.GalleryContextOpen;

        _ = SettingsTransfer.Export(source, "1.1.0");

        Assert.Equal(contextBefore, source.GalleryContextOpen);
        Assert.Equal(profilesBefore.Select(pair => pair.Key), source.LayoutProfiles.Select(pair => pair.Key));
        Assert.Same(profilesBefore[0].Value, source.LayoutProfiles["Simple"]);
        Assert.False(source.LayoutProfiles.ContainsKey("Advanced"));
    }
}
