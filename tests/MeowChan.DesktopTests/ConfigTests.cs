using System.Text.Json;

namespace MeowChan.DesktopTests;

public sealed class ConfigTests
{
    [Fact]
    public void UnchangedVideoVolumeDraftPreservesDirectPreferenceButExplicitEditWins()
    {
        Config live = new() { VideoVolume = 70 };
        Config draft = live.Clone();
        live.VideoVolume = 29;
        live.CopySettingsFieldsFrom(draft);
        Assert.Equal(29, live.VideoVolume);

        draft.VideoVolume = 0;
        live.CopySettingsFieldsFrom(draft);
        Assert.Equal(0, live.VideoVolume);
        live.CopySettingsFieldsFrom(new Config { VideoVolume = 43 });
        Assert.Equal(43, live.VideoVolume);
        Assert.DoesNotContain(nameof(Config.SettingsBaselineVideoVolume), JsonSerializer.Serialize(draft));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyTrayPreferenceRetainsCloseBehaviorOnly(bool enabled)
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"MinimizeToTray\":" + enabled.ToString().ToLowerInvariant() + "}");
        Config config = Config.LoadConfig(directory.Path);
        Assert.Equal(enabled, config.CloseToTray);
        Assert.False(config.MinimizeToTray);
        config.SaveConfig(directory.Path);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(enabled, saved.RootElement.GetProperty("CloseToTray").GetBoolean());
        Assert.Equal(enabled, Config.LoadConfig(directory.Path).CloseToTray);
        Assert.False(Config.LoadConfig(directory.Path).MinimizeToTray);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TrayPreferencesAreIndependentThroughCloneCopyAndReload(bool close, bool minimize)
    {
        using TempDirectory directory = new();
        Config original = new() { CloseToTray = close, MinimizeToTray = minimize };
        Config copy = new();
        copy.CopyFrom(original.Clone());
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(close, loaded.CloseToTray);
        Assert.Equal(minimize, loaded.MinimizeToTray);
        Assert.False(new Config().CloseToTray);
        Assert.False(new Config().MinimizeToTray);
    }

    [Fact]
    public void BoardWizardPreferenceDefaultsOnAndSurvivesCloneCopyAndPersistence()
    {
        using TempDirectory directory = new();
        Assert.True(Config.LoadConfig(directory.Path).ShowBoardAdmissionWizard);
        Config config = new();
        Config draft = config.Clone(); draft.ShowBoardAdmissionWizard = false;
        Assert.True(config.ShowBoardAdmissionWizard);
        config.CopyFrom(draft); config.SaveConfig(directory.Path);
        Assert.False(Config.LoadConfig(directory.Path).ShowBoardAdmissionWizard);
        Assert.False(config.Clone().ShowBoardAdmissionWizard);
    }

    [Fact]
    public void LoadingMissingProfileReturnsDefaultsWithoutCreatingDirectories()
    {
        using TempDirectory directory = new TempDirectory();
        string profile = Path.Combine(directory.Path, "not-created");

        Config config = Config.LoadConfig(profile);

        Assert.False(Directory.Exists(profile));
        Assert.Null(config.LoadWarning);
        Assert.Equal("Barebones", config.WorkspaceMode);
        Assert.Equal(60, config.RecheckTimer);
        Assert.Equal(5, config.MaxParallelDownloads);
        Assert.Equal(1024 * 1024, config.BandwidthLimiter);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MeowChan"), config.DownloadPath);
        Assert.True(Path.IsPathFullyQualified(config.DownloadPath));
        config.Validate();
    }

    [Theory]
    [InlineData(false, false, ".MeowChan")]
    [InlineData(false, true, ".oChan")]
    [InlineData(true, false, ".MeowChan")]
    public void DefaultProfileDiscoveryChoosesTheOnlyExistingProfileWithoutWriting(
        bool newProfileExists, bool oldProfileExists, string expectedName)
    {
        using TempDirectory home = new TempDirectory();
        string newProfile = Path.Combine(home.Path, ".MeowChan");
        string oldProfile = Path.Combine(home.Path, ".oChan");
        if (newProfileExists) Directory.CreateDirectory(newProfile);
        if (oldProfileExists) Directory.CreateDirectory(oldProfile);

        Assert.Equal(Path.Combine(home.Path, expectedName), Config.ResolveDefaultProfileDirectory(home.Path));
        Assert.Equal(newProfileExists, Directory.Exists(newProfile));
        Assert.Equal(oldProfileExists, Directory.Exists(oldProfile));
    }

    [Fact]
    public void BothDefaultProfilesRequireExplicitSelection()
    {
        using TempDirectory home = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(home.Path, ".MeowChan"));
        Directory.CreateDirectory(Path.Combine(home.Path, ".oChan"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Config.ResolveDefaultProfileDirectory(home.Path));

        Assert.Contains("MEOWCHAN_PROFILE", error.Message);
        Assert.Empty(Directory.GetFiles(home.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(".MeowChan")]
    [InlineData(".oChan")]
    public void FileAtDefaultProfilePathIsAnError(string profileName)
    {
        using TempDirectory home = new TempDirectory();
        string path = Path.Combine(home.Path, profileName);
        File.WriteAllText(path, "keep-me");

        Assert.ThrowsAny<IOException>(() => Config.ResolveDefaultProfileDirectory(home.Path));

        Assert.Equal("keep-me", File.ReadAllText(path));
    }

    [Fact]
    public void InaccessibleDefaultProfileIsNotTreatedAsMissingWherePermissionsApply()
    {
        if (OperatingSystem.IsWindows()) return;

        using TempDirectory directory = new TempDirectory();
        string home = Path.Combine(directory.Path, "restricted-home");
        string profile = Path.Combine(home, ".MeowChan");
        Directory.CreateDirectory(profile);
        UnixFileMode originalMode = File.GetUnixFileMode(home);
        try
        {
            File.SetUnixFileMode(home, UnixFileMode.None);
            try
            {
                _ = File.GetAttributes(profile);
                return; // A privileged test process can still traverse this directory.
            }
            catch (UnauthorizedAccessException)
            {
                Assert.Throws<UnauthorizedAccessException>(() => Config.ResolveDefaultProfileDirectory(home));
            }
        }
        finally
        {
            File.SetUnixFileMode(home, originalMode);
        }
    }

    [Fact]
    public void OverridesAreNormalizedBeforeComparisonAndConflictFailsBeforeProfileAccess()
    {
        using TempDirectory directory = new TempDirectory();
        string profile = Path.Combine(directory.Path, "profile");
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, profile);
        string other = Path.Combine(directory.Path, "other");
        File.WriteAllText(other, "not-a-profile");

        Assert.Equal(profile, Config.ResolveProfileOverrides(relative, profile));
        Assert.Equal(profile, Config.ResolveProfileOverrides(profile + Path.DirectorySeparatorChar, profile));
        Assert.Equal(profile, Config.ResolveProfileOverrides(profile, profile + Path.DirectorySeparatorChar));
        Assert.Equal(profile, Config.ResolveProfileOverrides(null, relative));
        Assert.Equal(profile, Config.ResolveProfileOverrides(profile, null));
        Assert.Null(Config.ResolveProfileOverrides(null, null));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => Config.ResolveProfileOverrides(profile, other));
        Assert.Contains("MEOWCHAN_PROFILE", error.Message);
        Assert.Contains("OCHAN_PROFILE", error.Message);
        Assert.Equal("not-a-profile", File.ReadAllText(other));
        Assert.False(Directory.Exists(profile));
    }

    [Fact]
    public void LegacyProfileWithMissingSettingsRetainsOldDownloadDefaultWithoutWriting()
    {
        using TempDirectory home = new TempDirectory();
        string profile = Path.Combine(home.Path, ".oChan");
        Directory.CreateDirectory(profile);

        Config config = Config.LoadConfig(profile);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "oChan"), config.DownloadPath);
        Assert.Null(config.LoadWarning);
        Assert.Empty(Directory.GetFiles(profile));
    }

    [Fact]
    public void LegacySettingsWithoutDownloadPathRetainOldDefaultAndOriginalDocument()
    {
        using TempDirectory directory = new TempDirectory();
        const string original = "{\"RecheckTimer\":120}";
        File.WriteAllText(SettingsPath(directory), original);

        Config config = Config.LoadConfig(directory.Path);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "oChan"), config.DownloadPath);
        Assert.Null(config.LoadWarning);
        Assert.Equal(original, File.ReadAllText(SettingsPath(directory)));
    }

    [Theory]
    [InlineData("saved_urls.json")]
    [InlineData(".ochan-profile.lock")]
    public void CustomLegacyProfileWithoutSettingsRetainsOldDownloadDefaultAfterLeaseAdoption(string evidenceName)
    {
        using TempDirectory directory = new TempDirectory();
        string evidencePath = Path.Combine(directory.Path, evidenceName);
        File.WriteAllText(evidencePath, "existing-data");
        File.WriteAllText(Path.Combine(directory.Path, ".meowchan-profile.lock"), "new-lock");
        File.WriteAllText(Path.Combine(directory.Path, ".meowchan-profile.protocol"), "marker");

        Config config = Config.LoadConfig(directory.Path);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "oChan"), config.DownloadPath);
        Assert.Null(config.LoadWarning);
        Assert.Equal("existing-data", File.ReadAllText(evidencePath));
        Assert.False(File.Exists(SettingsPath(directory)));
    }

    [Fact]
    public void NewProfileWithoutLegacyEvidenceUsesNewDownloadDefault()
    {
        using TempDirectory directory = new TempDirectory();
        File.WriteAllText(Path.Combine(directory.Path, ".meowchan-profile.lock"), "new-lock");
        File.WriteAllText(Path.Combine(directory.Path, ".meowchan-profile.protocol"), "marker");

        Config config = Config.LoadConfig(directory.Path);

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MeowChan"), config.DownloadPath);
        Assert.False(File.Exists(SettingsPath(directory)));
    }

    [Fact]
    public void DirectoryAtLegacyEvidencePathDoesNotSilentlyActivateNewDefaults()
    {
        using TempDirectory directory = new TempDirectory();
        Directory.CreateDirectory(Path.Combine(directory.Path, "saved_urls.json"));

        Assert.ThrowsAny<IOException>(() => Config.LoadConfig(directory.Path));
    }

    [Fact]
    public void SaveRoundTripsAndKeepsThePreviousValidSettingsAsBackup()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.DownloadPath = Path.Combine(directory.Path, "downloads");
        config.RecheckTimer = 120;
        config.SaveConfig();
        string original = File.ReadAllText(SettingsPath(directory));

        config.RecheckTimer = 180;
        config.MaxParallelDownloads = 7;
        config.SaveConfig();

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(180, loaded.RecheckTimer);
        Assert.Equal(7, loaded.MaxParallelDownloads);
        Assert.Equal(config.DownloadPath, loaded.DownloadPath);
        Assert.Equal(original, File.ReadAllText(SettingsPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"RecheckTimer\":0}")]
    [InlineData("{\"RecheckTimer\":9}")]
    [InlineData("{\"RecheckTimer\":4294968}")]
    [InlineData("{\"BandwidthLimiter\":0}")]
    [InlineData("{\"MaxParallelDownloads\":0}")]
    [InlineData("{\"DownloadPath\":null}")]
    [InlineData("{\"DownloadPath\":\" \"}")]
    [InlineData("{\"SchemaVersion\":2}")]
    public void InvalidSettingsUseDefaultsAndPreserveOriginal(string invalidJson)
    {
        using TempDirectory directory = new TempDirectory();
        File.WriteAllText(SettingsPath(directory), invalidJson);

        Config config = Config.LoadConfig(directory.Path);

        Assert.NotNull(config.LoadWarning);
        Assert.Contains("Default settings", config.LoadWarning);
        Assert.Equal(60, config.RecheckTimer);
        Assert.Equal(5, config.MaxParallelDownloads);
        Assert.Equal(invalidJson, File.ReadAllText(SettingsPath(directory)));
        Assert.Single(Directory.GetFiles(directory.Path));
        config.Validate();
    }

    [Fact]
    public void CorruptPrimaryRecoversBackupButOrdinarySaveCannotAuthorizeRecovery()
    {
        using TempDirectory directory = new TempDirectory();
        Config original = Config.LoadConfig(directory.Path);
        original.RecheckTimer = 120;
        original.SaveConfig();
        original.RecheckTimer = 180;
        original.SaveConfig();
        string backup = File.ReadAllText(SettingsPath(directory) + ".bak");
        const string damaged = "{interrupted-write";
        File.WriteAllText(SettingsPath(directory), damaged);

        Config recovered = Config.LoadConfig(directory.Path);

        Assert.Equal(120, recovered.RecheckTimer);
        Assert.Contains("Recovered settings", recovered.LoadWarning);
        Assert.Equal(damaged, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));

        recovered.RecheckTimer = 240;
        Assert.Throws<InvalidOperationException>(() => recovered.SaveConfig());

        Assert.NotNull(recovered.LoadWarning);
        Assert.Equal(damaged, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(120, Config.LoadConfig(directory.Path).RecheckTimer);
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "settings.json.corrupt-*"));
        Assert.DoesNotContain("LoadWarning", File.ReadAllText(SettingsPath(directory)));
    }

    [Fact]
    public void MissingPrimaryCanRecoverBackupWithoutWritingThePrimary()
    {
        using TempDirectory directory = new TempDirectory();
        File.WriteAllText(SettingsPath(directory) + ".bak", "{\"RecheckTimer\":120}");

        Config recovered = Config.LoadConfig(directory.Path);

        Assert.Equal(120, recovered.RecheckTimer);
        Assert.NotNull(recovered.LoadWarning);
        Assert.False(File.Exists(SettingsPath(directory)));
    }

    [Fact]
    public void LockedPrimaryFailsLoadingInsteadOfRecoveringAnOlderBackup()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.RecheckTimer = 120;
        config.SaveConfig();
        config.RecheckTimer = 180;
        config.SaveConfig();
        string primary = File.ReadAllText(SettingsPath(directory));
        string backup = File.ReadAllText(SettingsPath(directory) + ".bak");

        using (FileStream locked = new FileStream(SettingsPath(directory), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Config.LoadConfig(directory.Path));
        }

        Assert.Equal(180, Config.LoadConfig(directory.Path).RecheckTimer);
        Assert.Equal(primary, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
    }

    [Fact]
    public void LockedBackupFailsRecoveryInsteadOfActivatingDefaults()
    {
        using TempDirectory directory = new TempDirectory();
        const string damaged = "{invalid";
        const string backup = "{\"RecheckTimer\":120}";
        File.WriteAllText(SettingsPath(directory), damaged);
        File.WriteAllText(SettingsPath(directory) + ".bak", backup);

        using (FileStream locked = new FileStream(SettingsPath(directory) + ".bak", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => Config.LoadConfig(directory.Path));
        }

        Assert.Equal(120, Config.LoadConfig(directory.Path).RecheckTimer);
        Assert.Equal(damaged, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LockedPrimaryOrBackupCannotBeReplacedOrQuarantinedBySave(bool lockBackup)
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.RecheckTimer = 120;
        config.SaveConfig();
        config.RecheckTimer = 180;
        config.SaveConfig();
        string primary = File.ReadAllText(SettingsPath(directory));
        string backup = File.ReadAllText(SettingsPath(directory) + ".bak");
        string lockedPath = SettingsPath(directory) + (lockBackup ? ".bak" : string.Empty);
        config.RecheckTimer = 240;

        using (FileStream locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => config.SaveConfig());
            Assert.Empty(Directory.GetFiles(directory.Path, "*.corrupt-*"));
            Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        }

        Assert.Equal(primary, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(backup, File.ReadAllText(SettingsPath(directory) + ".bak"));
        Assert.Equal(180, Config.LoadConfig(directory.Path).RecheckTimer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableSettingsPathsPropagateAccessErrorsOnLoadAndSave(bool inaccessibleBackup)
    {
        using TempDirectory directory = new TempDirectory();
        string unreadablePath = SettingsPath(directory) + (inaccessibleBackup ? ".bak" : string.Empty);
        Directory.CreateDirectory(unreadablePath);
        Config config = new Config();

        Assert.Throws<UnauthorizedAccessException>(() => Config.LoadConfig(directory.Path));
        Assert.Throws<UnauthorizedAccessException>(() => config.SaveConfig(directory.Path));

        Assert.True(Directory.Exists(unreadablePath));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void SavingDefaultsAfterInvalidPrimaryIsDeniedAndPreservesTheOriginal()
    {
        using TempDirectory directory = new TempDirectory();
        const string damaged = "null";
        File.WriteAllText(SettingsPath(directory), damaged);
        Config defaults = Config.LoadConfig(directory.Path);

        Assert.Throws<InvalidOperationException>(() => defaults.SaveConfig());

        Assert.NotNull(Config.LoadConfig(directory.Path).LoadWarning);
        Assert.Equal(damaged, File.ReadAllText(SettingsPath(directory)));
        Assert.Empty(Directory.GetFiles(directory.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public void ReplacingAnInvalidBackupPreservesItsOriginalContents()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.SaveConfig();
        string valid = File.ReadAllText(SettingsPath(directory));
        const string damaged = "broken-backup";
        File.WriteAllText(SettingsPath(directory) + ".bak", damaged);

        config.RecheckTimer = 120;
        config.SaveConfig();

        Assert.Equal(valid, File.ReadAllText(SettingsPath(directory) + ".bak"));
        string preserved = Assert.Single(Directory.GetFiles(directory.Path, "settings.json.bak.corrupt-*"));
        Assert.Equal(damaged, File.ReadAllText(preserved));
    }

    [Fact]
    public void InvalidEditedValuesCannotOverwriteValidSettings()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.SaveConfig();
        string original = File.ReadAllText(SettingsPath(directory));
        config.MaxParallelDownloads = 0;

        Assert.Throws<ArgumentOutOfRangeException>(() => config.SaveConfig());

        Assert.Equal(original, File.ReadAllText(SettingsPath(directory)));
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(4294967)]
    public void SupportedRecheckTimerBoundariesRoundTrip(int seconds)
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.RecheckTimer = seconds;

        config.Validate();
        config.SaveConfig();

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(seconds, loaded.RecheckTimer);
    }

    [Theory]
    [InlineData(4294968)]
    [InlineData(int.MaxValue)]
    public void RecheckTimerAboveRuntimeMaximumCannotReplaceValidSettings(int seconds)
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        config.SaveConfig();
        string original = File.ReadAllText(SettingsPath(directory));
        config.RecheckTimer = seconds;

        Assert.Throws<ArgumentOutOfRangeException>(() => config.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => config.SaveConfig());

        Assert.Equal(original, File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(60, Config.LoadConfig(directory.Path).RecheckTimer);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void SaveFailureIsObservableAndDoesNotReplaceAnExistingPath()
    {
        using TempDirectory directory = new TempDirectory();
        string blockedProfile = Path.Combine(directory.Path, "profile-is-a-file");
        File.WriteAllText(blockedProfile, "keep-me");
        Config config = new Config();

        Assert.ThrowsAny<IOException>(() => config.SaveConfig(blockedProfile));

        Assert.Equal("keep-me", File.ReadAllText(blockedProfile));
    }

    [Fact]
    public void LegacyRelativeDownloadPathKeepsItsEffectiveLocationAndSavesAsAbsolute()
    {
        using TempDirectory directory = new TempDirectory();
        File.WriteAllText(SettingsPath(directory), "{\"DownloadPath\":\"Downloads\"}");
        string expected = Path.GetFullPath("Downloads", Environment.CurrentDirectory);

        Config config = Config.LoadConfig(directory.Path);

        Assert.Equal(expected, config.DownloadPath);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(5, config.MaxParallelDownloads);
        config.SaveConfig();
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(SettingsPath(directory)));
        Assert.Equal(expected, saved.RootElement.GetProperty("DownloadPath").GetString());
    }

    [Fact]
    public void CandidateCloneKeepsProfileBindingAndOnlyCopiesValidatedValues()
    {
        using TempDirectory directory = new TempDirectory();
        Config config = Config.LoadConfig(directory.Path);
        Config candidate = config.Clone();
        candidate.RecheckTimer = 120;

        candidate.SaveConfig();

        Assert.Equal(60, config.RecheckTimer);
        Assert.Equal(120, Config.LoadConfig(directory.Path).RecheckTimer);
        config.CopyFrom(candidate);
        Assert.Equal(120, config.RecheckTimer);

        candidate.RecheckTimer = 0;
        candidate.DownloadPath = Path.Combine(directory.Path, "must-not-apply");
        Assert.Throws<ArgumentOutOfRangeException>(() => config.CopyFrom(candidate));
        Assert.Equal(120, config.RecheckTimer);
        Assert.NotEqual(candidate.DownloadPath, config.DownloadPath);
    }

    private static string SettingsPath(TempDirectory directory) => Path.Combine(directory.Path, "settings.json");

    [Fact]
    public void LegacySettingsReceivePresentationDefaultsWithoutRecoveryOrSchemaChange()
    {
        using TempDirectory directory = new TempDirectory();
        const string original = "{\"RecheckTimer\":120,\"MaxParallelDownloads\":7}";
        File.WriteAllText(SettingsPath(directory), original);

        Config config = Config.LoadConfig(directory.Path);

        Assert.Null(config.LoadWarning);
        Assert.Equal(1, config.SchemaVersion);
        Assert.Equal(120, config.RecheckTimer);
        Assert.Equal(7, config.MaxParallelDownloads);
        Assert.Equal("Dark", config.Theme);
        Assert.Equal("en", config.Language);
        Assert.Equal("Barebones", config.WorkspaceMode);
        Assert.False(config.CompactDensity);
        Assert.Equal(original, File.ReadAllText(SettingsPath(directory)));
    }

    [Fact]
    public void PresentationPreferencesCloneCopyAndRoundTripThroughTheBoundProfile()
    {
        using TempDirectory directory = new TempDirectory();
        Config original = Config.LoadConfig(directory.Path);
        Config candidate = original.Clone();
        candidate.Theme = "Light";
        candidate.Language = "de";
        candidate.WorkspaceMode = "Simple";
        candidate.CompactDensity = true;
        candidate.SaveConfig();

        Assert.Equal("Dark", original.Theme);
        Assert.Equal("en", original.Language);
        Assert.Equal("Barebones", original.WorkspaceMode);
        Assert.False(original.CompactDensity);

        original.CopyFrom(candidate);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal("Light", loaded.Theme);
        Assert.Equal("de", loaded.Language);
        Assert.Equal("Simple", loaded.WorkspaceMode);
        Assert.True(loaded.CompactDensity);
        Assert.Equal(loaded.Theme, original.Theme);
        Assert.Equal(loaded.Language, original.Language);
        Assert.Equal(loaded.WorkspaceMode, original.WorkspaceMode);
        Assert.Equal(loaded.CompactDensity, original.CompactDensity);
    }

    [Theory]
    [InlineData("{\"Theme\":\"future-theme\",\"Language\":\"../bad\",\"WorkspaceMode\":\"future-mode\"}")]
    [InlineData("{\"Theme\":null,\"Language\":null,\"WorkspaceMode\":null,\"CompactDensity\":null}")]
    [InlineData("{\"Theme\":42,\"Language\":false,\"WorkspaceMode\":{},\"CompactDensity\":\"true\"}")]
    [InlineData("{\"Theme\":[],\"Language\":{},\"WorkspaceMode\":[],\"CompactDensity\":[true]}")]
    public void UnknownOrMalformedPresentationPreferencesKeepValidDownloadSettings(string preferences)
    {
        using TempDirectory directory = new TempDirectory();
        string json = "{\"RecheckTimer\":120,\"MaxParallelDownloads\":7," + preferences[1..];
        File.WriteAllText(SettingsPath(directory), json);

        Config config = Config.LoadConfig(directory.Path);

        Assert.Null(config.LoadWarning);
        Assert.Equal(120, config.RecheckTimer);
        Assert.Equal(7, config.MaxParallelDownloads);
        Assert.Equal("Dark", config.Theme);
        Assert.Equal("en", config.Language);
        Assert.Equal("Barebones", config.WorkspaceMode);
        Assert.False(config.CompactDensity);
        Assert.Equal(json, File.ReadAllText(SettingsPath(directory)));
    }

    [Theory]
    [InlineData(" light ", " advanced ", " DE-de ", "Light", "Advanced", "de-DE")]
    [InlineData("DARK", "bareBONES", "en-US", "Dark", "Barebones", "en-US")]
    [InlineData("DARK", " simple ", "en-US", "Dark", "Simple", "en-US")]
    [InlineData("system", "unknown", "not_a_language", "System", "Barebones", "en")]
    public void PresentationPreferenceValuesNormalizeConservatively(string theme, string mode, string language,
        string expectedTheme, string expectedMode, string expectedLanguage)
    {
        Config config = new Config { Theme = theme, WorkspaceMode = mode, Language = language };

        config.Validate();

        Assert.Equal(expectedTheme, config.Theme);
        Assert.Equal(expectedMode, config.WorkspaceMode);
        Assert.Equal(expectedLanguage, config.Language);
    }

    [Theory]
    [InlineData("Barebones")]
    [InlineData("Simple")]
    [InlineData("Advanced")]
    public void ExplicitSavedWorkspaceModesSurviveLoadCloneCopyAndSave(string mode)
    {
        using TempDirectory directory = new();
        File.WriteAllText(SettingsPath(directory), JsonSerializer.Serialize(new { WorkspaceMode = mode }));

        Config loaded = Config.LoadConfig(directory.Path);
        Config copied = new();
        copied.CopyFrom(loaded.Clone());
        copied.SaveConfig(directory.Path);

        Assert.Null(loaded.LoadWarning);
        Assert.Equal(mode, loaded.WorkspaceMode);
        Assert.Equal(mode, copied.WorkspaceMode);
        Assert.Equal(mode, Config.LoadConfig(directory.Path).WorkspaceMode);
    }

    [Fact]
    public void ReadingAndMediaPreferencesUseMigrationDefaultsAndRoundTrip()
    {
        using TempDirectory directory = new();
        File.WriteAllText(SettingsPath(directory), "{\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal("Normal", loaded.ReadingFontSize);
        Assert.True(loaded.VideoAutoplay);
        Assert.False(loaded.VideoStartMuted);
        Assert.Equal(70, loaded.VideoVolume);
        Assert.True(loaded.ShowShortcutHelp);
        Assert.False(loaded.GalleryContextOpen);
        Assert.Equal("List", loaded.ArchiveThreadLayout);
        Assert.Equal(270, loaded.ArchiveSplitWidth);

        Config changed = loaded.Clone();
        changed.Theme = "System";
        changed.ReadingFontSize = "Large";
        changed.VideoAutoplay = false;
        changed.VideoStartMuted = true;
        changed.VideoVolume = 42;
        changed.ShowShortcutHelp = false;
        changed.GalleryContextOpen = true;
        changed.ArchiveThreadLayout = "Table";
        changed.ArchiveSplitWidth = 360;
        changed.SaveConfig();
        Config roundTrip = Config.LoadConfig(directory.Path);
        Assert.Equal("System", roundTrip.Theme);
        Assert.Equal("Large", roundTrip.ReadingFontSize);
        Assert.False(roundTrip.VideoAutoplay);
        Assert.True(roundTrip.VideoStartMuted);
        Assert.Equal(42, roundTrip.VideoVolume);
        Assert.False(roundTrip.ShowShortcutHelp);
        Assert.True(roundTrip.GalleryContextOpen);
        Assert.Equal("Table", roundTrip.ArchiveThreadLayout);
        Assert.Equal(360, roundTrip.ArchiveSplitWidth);
    }

    [Fact]
    public void MalformedNewPresentationPreferencesDoNotDiscardDownloadSettings()
    {
        using TempDirectory directory = new();
        File.WriteAllText(SettingsPath(directory),
            "{\"RecheckTimer\":120,\"ReadingFontSize\":{},\"ArchiveThreadLayout\":null," +
            "\"VideoAutoplay\":null,\"ShowShortcutHelp\":[],\"VideoVolume\":\"loud\",\"ArchiveSplitWidth\":false}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.Equal("Normal", loaded.ReadingFontSize);
        Assert.Equal("List", loaded.ArchiveThreadLayout);
        Assert.True(loaded.VideoAutoplay);
        Assert.True(loaded.ShowShortcutHelp);
        Assert.Equal(70, loaded.VideoVolume);
        Assert.Equal(270, loaded.ArchiveSplitWidth);
    }

    [Fact]
    public void DirectViewGeometryColumnsAndExportFolderRoundTripWithoutEnteringSettingsDraft()
    {
        using TempDirectory directory = new();
        Config config = Config.LoadConfig(directory.Path);
        config.WindowWidth = 1100;
        config.WindowHeight = 720;
        config.WindowPositionX = -200;
        config.WindowPositionY = 80;
        config.WindowMaximized = true;
        config.ColumnWidths["archive.title"] = 320;
        config.LastExportDirectory = Path.Combine(directory.Path, "exports");
        Config draft = config.Clone();
        draft.WindowWidth = 900;
        draft.ColumnWidths["archive.title"] = 240;
        Assert.Equal(320, config.ColumnWidths["archive.title"]);
        config.SaveConfig();

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(1100, loaded.WindowWidth);
        Assert.Equal(720, loaded.WindowHeight);
        Assert.Equal(-200, loaded.WindowPositionX);
        Assert.Equal(80, loaded.WindowPositionY);
        Assert.True(loaded.WindowMaximized);
        Assert.Equal(320, loaded.ColumnWidths["archive.title"]);
        Assert.Equal(config.LastExportDirectory, loaded.LastExportDirectory);
        Assert.False(Directory.Exists(config.LastExportDirectory));
    }

    [Theory]
    [InlineData("Tiles", "List")]
    [InlineData("Table", "Table")]
    [InlineData("List", "List")]
    public void ArchivedThreadLayoutMigratesToSupportedViews(string stored, string expected)
    {
        using TempDirectory directory = new();
        File.WriteAllText(SettingsPath(directory), "{\"ArchiveThreadLayout\":\"" + stored + "\"}");
        Assert.Equal(expected, Config.LoadConfig(directory.Path).ArchiveThreadLayout);
    }

    [Fact]
    public void PresentationProfilesRemainIndependentAcrossModeSwitchAndRestart()
    {
        using TempDirectory directory = new();
        Config config = Config.LoadConfig(directory.Path);
        config.WorkspaceMode = "Barebones";
        config.ColumnWidths["ThreadsDataGrid.Title"] = 260;
        config.ColumnVisibility["ThreadsDataGrid.Status"] = false;
        config.ColumnOrder["ThreadsDataGrid.Title"] = 2;
        config.ArchiveThreadLayout = "Table";
        config.ArchiveSplitWidth = 380;
        config.ArchiveMediaTiles = false;
        config.CompactDensity = true;
        config.ReadingFontSize = "Large";
        config.WorkspaceMode = "Simple";
        Assert.Empty(config.ColumnWidths);
        Assert.Empty(config.ColumnVisibility);
        Assert.Empty(config.ColumnOrder);
        Assert.Equal("List", config.ArchiveThreadLayout);
        Assert.Equal(270, config.ArchiveSplitWidth);
        Assert.True(config.ArchiveMediaTiles);
        Assert.False(config.CompactDensity);
        Assert.Equal("Normal", config.ReadingFontSize);
        config.ColumnWidths["ThreadsDataGrid.Title"] = 420;
        config.SaveConfig();

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal("Simple", loaded.WorkspaceMode);
        Assert.Equal(420, loaded.ColumnWidths["ThreadsDataGrid.Title"]);
        loaded.WorkspaceMode = "Barebones";
        Assert.Equal(260, loaded.ColumnWidths["ThreadsDataGrid.Title"]);
        Assert.False(loaded.ColumnVisibility["ThreadsDataGrid.Status"]);
        Assert.Equal(2, loaded.ColumnOrder["ThreadsDataGrid.Title"]);
        Assert.Equal("Table", loaded.ArchiveThreadLayout);
        Assert.Equal(380, loaded.ArchiveSplitWidth);
        Assert.False(loaded.ArchiveMediaTiles);
        Assert.True(loaded.CompactDensity);
        Assert.Equal("Large", loaded.ReadingFontSize);
        loaded.WorkspaceMode = "Advanced";
        Assert.Empty(loaded.ColumnWidths);
    }

    [Fact]
    public void OldPresentationSettingsMigrateOnlyIntoSelectedMode()
    {
        using TempDirectory directory = new();
        File.WriteAllText(SettingsPath(directory),
            "{\"WorkspaceMode\":\"Advanced\",\"CompactDensity\":true,\"ArchiveThreadLayout\":\"Table\"," +
            "\"ColumnWidths\":{\"ThreadsDataGrid.Title\":333}}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.True(loaded.CompactDensity);
        Assert.Equal("Table", loaded.ArchiveThreadLayout);
        Assert.Equal(333, loaded.ColumnWidths["ThreadsDataGrid.Title"]);
        loaded.WorkspaceMode = "Simple";
        Assert.False(loaded.CompactDensity);
        Assert.Equal("List", loaded.ArchiveThreadLayout);
        Assert.Empty(loaded.ColumnWidths);
    }

    [Fact]
    public void SettingsMergePreservesDirectProfileChangesInSelectedAndOtherModes()
    {
        Config current = new();
        Config draft = current.Clone();
        current.ColumnWidths["ThreadsDataGrid.Title"] = 320;
        current.WorkspaceMode = "Advanced";
        current.ColumnWidths["ThreadsDataGrid.Title"] = 600;
        draft.WorkspaceMode = "Simple";
        current.CopySettingsFieldsFrom(draft);
        Assert.Equal("Simple", current.WorkspaceMode);
        current.WorkspaceMode = "Barebones";
        Assert.Equal(320, current.ColumnWidths["ThreadsDataGrid.Title"]);
        current.WorkspaceMode = "Advanced";
        Assert.Equal(600, current.ColumnWidths["ThreadsDataGrid.Title"]);
    }

    [Fact]
    public void ProfileDataWinsOverLegacyFieldsRegardlessOfJsonOrderAndNullMapsRecover()
    {
        using TempDirectory directory = new();
        File.WriteAllText(SettingsPath(directory),
            "{\"WorkspaceMode\":\"Simple\",\"LayoutProfiles\":{\"Simple\":{" +
            "\"ColumnWidths\":null,\"ColumnVisibility\":null,\"ColumnOrder\":null," +
            "\"ArchiveThreadLayout\":\"Table\",\"CompactDensity\":true}}," +
            "\"ArchiveThreadLayout\":\"List\",\"CompactDensity\":false}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal("Table", loaded.ArchiveThreadLayout);
        Assert.True(loaded.CompactDensity);
        Assert.Empty(loaded.ColumnWidths);
        Assert.Empty(loaded.ColumnVisibility);
        Assert.Empty(loaded.ColumnOrder);
    }

    [Fact]
    public void SettingsDraftAppliesPresentationEditsToTheModeActuallyEdited()
    {
        Config current = new();
        Config draft = current.Clone();
        draft.WorkspaceMode = "Advanced";
        draft.CompactDensity = true;
        draft.ReadingFontSize = "Large";
        current.CopySettingsFieldsFrom(draft);
        Assert.Equal("Advanced", current.WorkspaceMode);
        Assert.True(current.CompactDensity);
        Assert.Equal("Large", current.ReadingFontSize);
        current.WorkspaceMode = "Barebones";
        Assert.False(current.CompactDensity);
        Assert.Equal("Normal", current.ReadingFontSize);
    }
}
