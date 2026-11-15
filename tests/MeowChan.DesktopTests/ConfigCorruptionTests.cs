using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Tests.PersistenceCorruption;

namespace MeowChan.DesktopTests;

/// <summary>
/// settings.json (the file behind the 1.1.0 limit settings and the "Animations" setting): a damaged, newer or out-of-range file never
/// crashes the start, never replaces a good copy, falls back to the good <c>.bak</c> or to defaults (original files preserved),
/// and a write that follows a failed read is refused.
/// </summary>
public sealed class ConfigCorruptionTests
{
    private const string VersionProperty = "SchemaVersion";

    // Property, minimum, maximum of the limit settings added or changed in 1.1.0, taken from the settings' own
    // bounds so the table cannot drift away from what Config.Validate enforces.
    private static readonly (string Property, long Minimum, long Maximum)[] Limits =
    {
        (nameof(Config.BooruCatalogLimitMiB), Config.BooruCatalogMinimumLimitMiB, Config.BooruCatalogMaximumLimitMiB),
        (nameof(Config.PrivateTagLimit), Config.PrivateTagMinimumLimit, Config.PrivateTagMaximumLimit),
        (nameof(Config.PrivateTagFileLimitMiB), Config.PrivateTagFileMinimumLimitMiB, Config.PrivateTagFileMaximumLimitMiB),
        (nameof(Config.MediaIndexCacheLimitMiB), Config.MediaIndexCacheMinimumLimitMiB, Config.MediaIndexCacheMaximumLimitMiB),
        (nameof(Config.FavoriteEntryLimit), Config.FavoriteEntryLimitMinimum, Config.FavoriteEntryLimitMaximum),
        (nameof(Config.FavoriteFileLimitMiB), Config.FavoriteFileLimitMiBMinimum, Config.FavoriteFileLimitMiBMaximum),
        (nameof(Config.PostBookmarkLimit), Config.PostBookmarkLimitMinimum, Config.PostBookmarkLimitMaximum),
        (nameof(Config.SavedSearchLimit), Config.SavedSearchLimitMinimum, Config.SavedSearchLimitMaximum),
        (nameof(Config.ReaderGroupLimit), Config.ReaderGroupLimitMinimum, Config.ReaderGroupLimitMaximum),
        (nameof(Config.ProfileRegistryLimit), Config.ProfileRegistryLimitMinimum, Config.ProfileRegistryLimitMaximum),
        (nameof(Config.ExportConfirmFiles), Config.ExportConfirmFilesMinimum, Config.ExportConfirmFilesMaximum),
        (nameof(Config.ExportConfirmGiB), Config.ExportConfirmGiBMinimum, Config.ExportConfirmGiBMaximum),
        (nameof(Config.PosterCacheLimitMiB), Config.PosterCacheLimitMiBMinimum, Config.PosterCacheLimitMiBMaximum),
        (nameof(Config.PosterCacheFileLimit), Config.PosterCacheFileLimitMinimum, Config.PosterCacheFileLimitMaximum),
        (nameof(Config.AudioProbeCacheLimitMiB), Config.AudioProbeCacheLimitMiBMinimum, Config.AudioProbeCacheLimitMiBMaximum),
        (nameof(Config.AudioProbeCacheEntries), Config.AudioProbeCacheEntriesMinimum, Config.AudioProbeCacheEntriesMaximum),
        (nameof(Config.ImportMaxFilesPerBatch), Config.ImportMaxFilesPerBatchMinimum, Config.ImportMaxFilesPerBatchMaximum),
        (nameof(Config.ImportMaxBatchGiB), Config.ImportMaxBatchGiBMinimum, Config.ImportMaxBatchGiBMaximum),
        (nameof(Config.ImportMaxFileMiB), Config.ImportMaxFileMiBMinimum, Config.ImportMaxFileMiBMaximum),
        (nameof(Config.ImportMaxTotalFiles), Config.ImportMaxTotalFilesMinimum, Config.ImportMaxTotalFilesMaximum),
        (nameof(Config.ImportCatalogLimitMiB), Config.ImportCatalogLimitMiBMinimum, Config.ImportCatalogLimitMiBMaximum),
        (nameof(Config.ImportMaxTagsPerBatch), Config.ImportMaxTagsPerBatchMinimum, Config.ImportMaxTagsPerBatchMaximum),
        (nameof(Config.ImportMaxBatches), Config.ImportMaxBatchesMinimum, Config.ImportMaxBatchesMaximum),
        (nameof(Config.MediaIndexMaxEntries), Config.MediaIndexMaxEntriesMinimum, Config.MediaIndexMaxEntriesMaximum),
        (nameof(Config.MediaIndexMaxThreads), Config.MediaIndexMaxThreadsMinimum, Config.MediaIndexMaxThreadsMaximum),
        (nameof(Config.TagQueueLimit), Config.TagQueueLimitMinimum, Config.TagQueueLimitMaximum),
        (nameof(Config.TagQueueFileLimitMiB), Config.TagQueueFileLimitMiBMinimum, Config.TagQueueFileLimitMiBMaximum),
        (nameof(Config.TagQueueHistory), Config.TagQueueHistoryMinimum, Config.TagQueueHistoryMaximum),
        (nameof(Config.TagQueueConfirmAbove), Config.TagQueueConfirmAboveMinimum, Config.TagQueueConfirmAboveMaximum),
        (nameof(Config.MediaTagListTop), Config.MediaTagListTopMinimum, Config.MediaTagListTopMaximum),
        (nameof(Config.MediaPanelMinimumPageWidth), Config.MediaPanelMinimumPageWidthMinimum, Config.MediaPanelMinimumPageWidthMaximum),
        (nameof(Config.FolderSourceMaxFiles), Config.FolderSourceMaxFilesMinimum, Config.FolderSourceMaxFilesMaximum),
        (nameof(Config.FolderSourceMaxRoots), Config.FolderSourceMaxRootsMinimum, Config.FolderSourceMaxRootsMaximum),
        (nameof(Config.FolderSourceHashParallelism), Config.FolderSourceHashParallelismMinimum, Config.FolderSourceHashParallelismMaximum),
        (nameof(Config.FolderSourceHashMaxFileGiB), Config.FolderSourceHashMaxFileGiBMinimum, Config.FolderSourceHashMaxFileGiBMaximum),
        (nameof(Config.FolderSourceRescanMinutes), Config.FolderSourceRescanMinutesMinimum, Config.FolderSourceRescanMinutesMaximum),
        (nameof(Config.FolderSourceCatalogLimitMiB), Config.FolderSourceCatalogLimitMiBMinimum, Config.FolderSourceCatalogLimitMiBMaximum),
        (nameof(Config.FolderSourceGalleryHashWindow), Config.FolderSourceGalleryHashWindowMinimum, Config.FolderSourceGalleryHashWindowMaximum),
        (nameof(Config.TagQueueWalkWindow), Config.TagQueueWalkWindowMinimum, Config.TagQueueWalkWindowMaximum),
        (nameof(Config.TagQueueUndoDepth), Config.TagQueueUndoDepthMinimum, Config.TagQueueUndoDepthMaximum),
        (nameof(Config.TagStampSize), Config.TagStampSizeMinimum, Config.TagStampSizeMaximum)
    };

    public static IEnumerable<object[]> LimitCases() =>
        Limits.SelectMany(limit => new[] { limit.Minimum - 1, limit.Maximum + 1, 0, -1, int.MinValue, int.MaxValue }
            .Distinct().Where(value => value < limit.Minimum || value > limit.Maximum)
            .Select(value => new object[] { limit.Property, value }));

    private sealed class Profile : IDisposable
    {
        internal readonly TempDirectory Temp = new();
        internal string Directory => Temp.Path;
        internal string Primary => Path.Combine(Directory, "settings.json");
        internal string Backup => Primary + ".bak";
        internal byte[] GoodPrimary = [];
        internal byte[] GoodBackup = [];

        /// <summary>Two good generations: the backup holds 16 MiB for the Danbooru limit, the primary 17 MiB.</summary>
        internal Profile()
        {
            string downloads = Path.Combine(Directory, "downloads");
            new Config { DownloadPath = downloads, BooruCatalogLimitMiB = 16, Animations = "Off", AutomaticUpdateChecks = false }.SaveConfig(Directory);
            new Config { DownloadPath = downloads, BooruCatalogLimitMiB = 17, Animations = "Off", AutomaticUpdateChecks = false }.SaveConfig(Directory);
            GoodPrimary = File.ReadAllBytes(Primary);
            GoodBackup = File.ReadAllBytes(Backup);
            Assert.NotEqual(GoodPrimary, GoodBackup);
        }

        internal string[] Names() => System.IO.Directory.EnumerateFileSystemEntries(Directory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        public void Dispose() => Temp.Dispose();
    }

    /// <summary>Whether these bytes are a settings document the loader accepts (checked in a throw-away profile of their own).</summary>
    private static bool ReadsAsAValidDocument(byte[] bytes)
    {
        using TempDirectory probe = new();
        File.WriteAllBytes(Path.Combine(probe.Path, "settings.json"), bytes);
        return Config.LoadConfig(probe.Path).Origin == ProfileOrigin.ExistingValid;
    }

    private static Config Load(string directory)
    {
        Config? loaded = null;
        Exception? error = Record.Exception(() => loaded = Config.LoadConfig(directory));
        Assert.True(error == null, $"LoadConfig threw {error?.GetType().Name}: {error?.Message}");
        return loaded!;
    }

    private static void AssertUntouched(Profile profile, string[] names, byte[]? primary, byte[]? backup, string context)
    {
        Assert.True(names.SequenceEqual(profile.Names()), context + ": the directory changed.");
        Assert.True(primary == null ? !File.Exists(profile.Primary) : primary.AsSpan().SequenceEqual(File.ReadAllBytes(profile.Primary)), context + ": settings.json changed.");
        Assert.True(backup == null ? !File.Exists(profile.Backup) : backup.AsSpan().SequenceEqual(File.ReadAllBytes(profile.Backup)), context + ": settings.json.bak changed.");
    }

    [Fact]
    public void TheSeededProfileLoadsCleanly()
    {
        using Profile profile = new();
        Config config = Load(profile.Directory);
        Assert.Null(config.LoadWarning);
        Assert.Equal(17, config.BooruCatalogLimitMiB);
        Assert.Equal("Off", config.Animations);
    }

    [Theory]
    [MemberData(nameof(Damage.Primary), MemberType = typeof(Damage))]
    public void ADamagedPrimaryFallsBackToTheGoodBackupAndRefusesWrites(string variant)
    {
        using Profile profile = new();
        byte[] damaged = Damage.Make(variant, profile.GoodPrimary, VersionProperty);
        File.WriteAllBytes(profile.Primary, damaged);
        string[] names = profile.Names();

        Config config = Load(profile.Directory);
        AssertUntouched(profile, names, damaged, profile.GoodBackup, variant + " load");
        if (config.Origin == ProfileOrigin.ExistingValid)
        {
            // A document that still parses must be the good one, value for value.
            Assert.Equal(17, config.BooruCatalogLimitMiB);
            Assert.Equal("Off", config.Animations);
            return;
        }
        // A newer document is "unsupported" rather than "damaged"; both recover the settings from the good backup.
        Assert.Equal(variant == "future-version" ? ProfileOrigin.Unsupported : ProfileOrigin.BackupRecovery, config.Origin);
        Assert.NotNull(config.LoadWarning);
        Assert.Equal(16, config.BooruCatalogLimitMiB);
        Assert.Equal("Off", config.Animations);
        // The warning text itself is checked by TheLoadWarningShownToTheUserComesFromTheLocalizedTexts.
        Assert.Throws<InvalidOperationException>(() => config.SaveConfig(profile.Directory));
        AssertUntouched(profile, names, damaged, profile.GoodBackup, variant + " save");
    }

    [Theory]
    [MemberData(nameof(Damage.Primary), MemberType = typeof(Damage))]
    public void BothFilesDamagedFallBackToDefaultsAndKeepTheOriginals(string variant)
    {
        using Profile profile = new();
        byte[] damaged = Damage.Make(variant, profile.GoodPrimary, VersionProperty);
        byte[] damagedBackup = Damage.Make("truncated-half", profile.GoodBackup, VersionProperty);
        File.WriteAllBytes(profile.Primary, damaged);
        File.WriteAllBytes(profile.Backup, damagedBackup);
        string[] names = profile.Names();

        Config config = Load(profile.Directory);
        AssertUntouched(profile, names, damaged, damagedBackup, variant + " load");
        if (config.Origin == ProfileOrigin.ExistingValid) { Assert.Equal(17, config.BooruCatalogLimitMiB); return; }
        Assert.Contains(config.Origin, new[] { ProfileOrigin.Damaged, ProfileOrigin.Unsupported });
        Assert.NotNull(config.LoadWarning);
        Assert.Equal(new Config().BooruCatalogLimitMiB, config.BooruCatalogLimitMiB);
        Assert.Throws<InvalidOperationException>(() => config.SaveConfig(profile.Directory));
        Assert.Throws<InvalidOperationException>(() => new Config { BooruCatalogLimitMiB = 40 }.SaveConfig(profile.Directory));
        AssertUntouched(profile, names, damaged, damagedBackup, variant + " save");
    }

    [Theory]
    [MemberData(nameof(Damage.Backup), MemberType = typeof(Damage))]
    public void ADamagedBackupNextToAGoodPrimaryKeepsTheDamagedCopyAside(string variant)
    {
        using Profile profile = new();
        byte[] damaged = Damage.Make(variant, profile.GoodBackup, VersionProperty);
        File.WriteAllBytes(profile.Backup, damaged);

        Config config = Load(profile.Directory);
        Assert.Null(config.LoadWarning);
        Assert.Equal(17, config.BooruCatalogLimitMiB);
        string[] names = profile.Names();

        bool parses = ReadsAsAValidDocument(damaged);
        Exception? write = Record.Exception(() => config.SaveConfig(profile.Directory));
        if (variant == "future-version")
        {
            // A newer backup is never replaced.
            Assert.IsType<InvalidOperationException>(write);
            AssertUntouched(profile, names, profile.GoodPrimary, damaged, variant + " refused");
            return;
        }
        Assert.Null(write);
        // Either way the rotation keeps the previous good primary as the new backup.
        Assert.Equal(profile.GoodPrimary, File.ReadAllBytes(profile.Backup));
        string[] preserved = Directory.EnumerateFiles(profile.Directory, "settings.json.bak.corrupt-*").ToArray();
        if (parses)
        {
            // The bytes still read as a valid settings document (unknown field, duplicate, leniently decoded text):
            // there is nothing damaged to keep aside, so the backup is rotated like any other.
            Assert.Empty(preserved);
            return;
        }
        // The damaged bytes were copied aside before the backup was replaced.
        Assert.Contains(preserved, file => File.ReadAllBytes(file).AsSpan().SequenceEqual(damaged));
    }

    [Theory]
    [MemberData(nameof(LimitCases))]
    public void AnOutOfRangeLimitFallsBackLikeAnyOtherDamageAndNeverCrashes(string property, long value)
    {
        using Profile profile = new();
        JsonObject root = JsonNode.Parse(profile.GoodPrimary)!.AsObject();
        root[property] = value;
        byte[] edited = Encoding.UTF8.GetBytes(root.ToJsonString());
        File.WriteAllBytes(profile.Primary, edited);
        string[] names = profile.Names();

        Config config = Load(profile.Directory);

        // The good backup is used instead; nothing is clamped silently into a file, and nothing on disk changes.
        Assert.Equal(ProfileOrigin.BackupRecovery, config.Origin);
        Assert.NotNull(config.LoadWarning);
        Assert.Equal(16, config.BooruCatalogLimitMiB);
        AssertUntouched(profile, names, edited, profile.GoodBackup, property + "=" + value);
        Assert.Throws<InvalidOperationException>(() => config.SaveConfig(profile.Directory));
        AssertUntouched(profile, names, edited, profile.GoodBackup, property + "=" + value + " save");
    }

    [Theory]
    [InlineData("\"Sometimes\"")]
    [InlineData("\"ON\"")]
    [InlineData("\" off \"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void AnInvalidAnimationsValueNormalizesToTheDefaultAndKeepsTheOtherSettings(string literal)
    {
        using Profile profile = new();
        JsonObject root = JsonNode.Parse(profile.GoodPrimary)!.AsObject();
        root["Animations"] = JsonNode.Parse(literal);
        File.WriteAllBytes(profile.Primary, Encoding.UTF8.GetBytes(root.ToJsonString()));

        Config config = Load(profile.Directory);

        Assert.Null(config.LoadWarning);
        Assert.Equal(ProfileOrigin.ExistingValid, config.Origin);
        Assert.Equal(17, config.BooruCatalogLimitMiB);
        Assert.Contains(config.Animations, new[] { "System", "On", "Off" });
        if (literal is "\" off \"") Assert.Equal("Off", config.Animations);
        else if (literal == "\"ON\"") Assert.Equal("On", config.Animations);
        else Assert.Equal("System", config.Animations);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("symlink-to-good-copy")]
    [InlineData("dangling-symlink")]
    public void ADirectoryOrLinkInPlaceOfTheSettingsFileIsHandled(string kind)
    {
        using Profile profile = new();
        string outside = Path.Combine(profile.Directory, "..", "outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            string target = Path.Combine(outside, "target.json");
            File.Delete(profile.Primary);
            switch (kind)
            {
                case "directory": Directory.CreateDirectory(profile.Primary); File.WriteAllText(Path.Combine(profile.Primary, "marker.txt"), "keep me"); break;
                case "symlink-to-good-copy": File.WriteAllBytes(target, profile.GoodPrimary); File.CreateSymbolicLink(profile.Primary, target); break;
                default: File.CreateSymbolicLink(profile.Primary, Path.Combine(outside, "missing.json")); break;
            }
            string[] names = profile.Names();

            Config? config = null;
            Exception? load = Record.Exception(() => config = Config.LoadConfig(profile.Directory));
            // Either the documented fallback, or a clear I/O failure that the start reports; never a crash with a stranger type.
            if (load != null) Assert.True(load is IOException or UnauthorizedAccessException, $"LoadConfig threw {load.GetType().FullName}: {load.Message}");
            else if (config!.Origin != ProfileOrigin.ExistingValid)
                Assert.Throws<InvalidOperationException>(() => config.SaveConfig(profile.Directory));

            Assert.True(names.SequenceEqual(profile.Names()), "The directory changed.");
            Assert.Equal(profile.GoodBackup, File.ReadAllBytes(profile.Backup));
            if (kind == "directory") Assert.Equal("keep me", File.ReadAllText(Path.Combine(profile.Primary, "marker.txt")));
            if (kind == "symlink-to-good-copy") Assert.Equal(profile.GoodPrimary, File.ReadAllBytes(target));
        }
        finally { Directory.Delete(outside, recursive: true); }
    }

    // The read ceiling is inclusive (a file of exactly 4 MiB is read, one byte more is damaged) and has a write side.
    [Fact]
    public void ASettingsFileOfExactlyTheCeilingIsReadAndOneByteMoreIsDamage()
    {
        using Profile profile = new();
        string good = Encoding.UTF8.GetString(profile.GoodPrimary).TrimEnd();
        string exact = good[..^1] + new string(' ', Config.SettingsFileCeilingBytes - good.Length) + "}";
        File.WriteAllText(profile.Primary, exact);
        Assert.Equal(Config.SettingsFileCeilingBytes, new FileInfo(profile.Primary).Length);
        Assert.Equal(ProfileOrigin.ExistingValid, Load(profile.Directory).Origin);

        File.WriteAllText(profile.Primary, exact[..^1] + " }");
        Assert.Equal(Config.SettingsFileCeilingBytes + 1L, new FileInfo(profile.Primary).Length);
        Config oversized = Load(profile.Directory);
        Assert.Equal(ProfileOrigin.BackupRecovery, oversized.Origin);
        Assert.Equal(16, oversized.BooruCatalogLimitMiB);
    }

    [Fact]
    public void SettingsLargerThanTheReadCeilingAreRefusedBeforeAnythingIsWritten()
    {
        using Profile profile = new();
        Config config = Load(profile.Directory);
        string[] names = profile.Names();
        using System.Text.Json.JsonDocument huge = System.Text.Json.JsonDocument.Parse("{\"SchemaVersion\":1,\"Overrides\":{},\"Note\":\"" + new string('x', Config.SettingsFileCeilingBytes) + "\"}");
        config.ShortcutBindings = new Desktop.Commands.ShortcutBindingsSection(huge.RootElement);

        Assert.Throws<InvalidDataException>(() => config.SaveConfig(profile.Directory));

        Assert.Equal(names, profile.Names());
        Assert.Equal(profile.GoodPrimary, File.ReadAllBytes(profile.Primary));
        Assert.Equal(profile.GoodBackup, File.ReadAllBytes(profile.Backup));
        // The profile still starts: what was refused was never written.
        Assert.Equal(ProfileOrigin.ExistingValid, Load(profile.Directory).Origin);
    }

    [Fact]
    public void ASettingsFileJustBelowTheCeilingIsStillRead()
    {
        using Profile profile = new();
        string padded = Encoding.UTF8.GetString(profile.GoodPrimary).TrimEnd();
        padded = padded[..^1] + new string(' ', Config.SettingsFileCeilingBytes - padded.Length - 64) + "}";
        File.WriteAllText(profile.Primary, padded);
        Assert.True(new FileInfo(profile.Primary).Length <= Config.SettingsFileCeilingBytes);
        Config config = Load(profile.Directory);
        Assert.Equal(ProfileOrigin.ExistingValid, config.Origin);
        Assert.Equal(17, config.BooruCatalogLimitMiB);
    }
}

/// <summary>The settings load warning the window shows: a localized sentence in every language, naming file names only.</summary>
[Collection("Localization")]
public sealed class ConfigLoadWarningLocalizationTests : IDisposable
{
    private readonly string _previous = Texts.Language;
    public ConfigLoadWarningLocalizationTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    private static Config LoadDamaged(TempDirectory temp, bool damageBackupToo)
    {
        string downloads = Path.Combine(temp.Path, "downloads");
        new Config { DownloadPath = downloads, AutomaticUpdateChecks = false }.SaveConfig(temp.Path);
        new Config { DownloadPath = downloads, AutomaticUpdateChecks = false, BooruCatalogLimitMiB = 17 }.SaveConfig(temp.Path);
        string primary = Path.Combine(temp.Path, "settings.json");
        File.WriteAllBytes(primary, Damage.Make("truncated-half", File.ReadAllBytes(primary), "SchemaVersion"));
        if (damageBackupToo) File.WriteAllText(primary + ".bak", "{");
        return Config.LoadConfig(temp.Path);
    }

    [Theory]
    [InlineData(false, "settings.load.recovered")]
    [InlineData(true, "settings.load.defaults")]
    public void TheLoadWarningShownToTheUserComesFromTheLocalizedTexts(bool damageBackupToo, string key)
    {
        using TempDirectory temp = new();
        Config config = LoadDamaged(temp, damageBackupToo);
        Assert.NotEqual(SettingsLoadProblem.None, config.LoadProblem);
        // The log text keeps the technical detail but never the profile path either.
        Assert.DoesNotContain(temp.Path, config.LoadWarning ?? "", StringComparison.Ordinal);
        string? english = null;
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            string? shown = config.LocalizedLoadWarning();
            Assert.NotNull(shown);
            Assert.Equal(Texts.T(key, "settings.json", "settings.json.bak"), shown);
            Assert.DoesNotContain(key, shown, StringComparison.Ordinal);
            Assert.Contains("settings.json", shown, StringComparison.Ordinal);
            Assert.DoesNotContain(temp.Path, shown, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), shown, StringComparison.Ordinal);
            if (language == "en") english = shown;
            else Assert.NotEqual(english, shown);
        }
    }

    [Fact]
    public void ACleanLoadAndASuccessfulSaveShowNoWarning()
    {
        using TempDirectory temp = new();
        new Config { DownloadPath = Path.Combine(temp.Path, "downloads"), AutomaticUpdateChecks = false }.SaveConfig(temp.Path);
        Config config = Config.LoadConfig(temp.Path);
        Assert.Null(config.LocalizedLoadWarning());
        Assert.Equal(SettingsLoadProblem.None, config.LoadProblem);
    }
}

/// <summary>Runs alone: the allocation counter is process-wide, so parallel tests would be counted as reading the file.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsAllocationCollection
{
    public const string Name = "Settings allocation measurement";
}

[Collection(SettingsAllocationCollection.Name)]
public sealed class OversizedSettingsFileTests
{
    // Far above the fixed ceiling (sparse, so it takes no disk space): judged from its length, never read.
    [Theory]
    [InlineData(256L * 1024 * 1024)]
    [InlineData(Config.SettingsFileCeilingBytes + 1L)]
    public void AnOversizedSettingsFileIsNeverReadIntoMemory(long size)
    {
        using TempDirectory temp = new();
        string downloads = Path.Combine(temp.Path, "downloads");
        new Config { DownloadPath = downloads, BooruCatalogLimitMiB = 16, AutomaticUpdateChecks = false }.SaveConfig(temp.Path);
        new Config { DownloadPath = downloads, BooruCatalogLimitMiB = 17, AutomaticUpdateChecks = false }.SaveConfig(temp.Path);
        string primary = Path.Combine(temp.Path, "settings.json");
        byte[] goodBackup = File.ReadAllBytes(primary + ".bak");
        Damage.WriteSparse(primary, size);
        string[] names = Directory.EnumerateFileSystemEntries(temp.Path).Order(StringComparer.Ordinal).ToArray();

        long before = GC.GetTotalAllocatedBytes(precise: false);
        Config config = Config.LoadConfig(temp.Path);
        long used = GC.GetTotalAllocatedBytes(precise: false) - before;

        Assert.True(used < Config.SettingsFileCeilingBytes, $"Loading allocated {used} bytes for a file of {size}.");
        Assert.Equal(ProfileOrigin.BackupRecovery, config.Origin);
        Assert.Equal(16, config.BooruCatalogLimitMiB);
        Assert.Throws<InvalidOperationException>(() => config.SaveConfig(temp.Path));
        Assert.Equal(names, Directory.EnumerateFileSystemEntries(temp.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(size, new FileInfo(primary).Length);
        Assert.Equal(goodBackup, File.ReadAllBytes(primary + ".bak"));
    }
}
