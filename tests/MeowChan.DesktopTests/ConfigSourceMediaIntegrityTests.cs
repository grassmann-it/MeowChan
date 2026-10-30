using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ConfigSourceMediaIntegrityTests
{
    [Fact]
    public void ConsentRoundTripsWithDeepCopiesAndDoesNotFollowASettingsDraft()
    {
        using TempDirectory directory = new();
        Config live = Config.LoadConfig(directory.Path);
        Assert.Empty(live.SourceMediaIntegrityPermissions);
        Config settingsDraft = live.Clone();
        string key = SourceMediaIntegrityPermission.CreateKey("owner", "source");
        live.SourceMediaIntegrityPermissions[key] = new("owner", "source", true, 7);
        Config clone = live.Clone();
        Assert.NotSame(live.SourceMediaIntegrityPermissions, clone.SourceMediaIntegrityPermissions);
        Assert.NotSame(live.SourceMediaIntegrityPermissions[key], clone.SourceMediaIntegrityPermissions[key]);
        live.CopySettingsFieldsFrom(settingsDraft);
        Assert.True(live.SourceMediaIntegrityPermissions[key].AllowProviderSizeMd5Mismatch);
        live.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(live.SourceMediaIntegrityPermissions[key], loaded.SourceMediaIntegrityPermissions[key]);
        Config copy = Config.LoadConfig(directory.Path); copy.CopyFrom(loaded);
        Assert.NotSame(loaded.SourceMediaIntegrityPermissions, copy.SourceMediaIntegrityPermissions);
        clone.SourceMediaIntegrityPermissions.Clear();
        Assert.Single(live.SourceMediaIntegrityPermissions);
        Assert.Single(copy.SourceMediaIntegrityPermissions);
    }

    [Fact]
    public void CopyFromCannotMoveConsentBetweenBoundProfiles()
    {
        using TempDirectory first = new(); using TempDirectory second = new();
        Config profileA = Config.LoadConfig(first.Path), profileB = Config.LoadConfig(second.Path);
        string key = SourceMediaIntegrityPermission.CreateKey("owner", "source");
        profileA.SourceMediaIntegrityPermissions[key] = new("owner", "source", true, 2);
        profileB.CopyFrom(profileA.Clone());
        Assert.Empty(profileB.SourceMediaIntegrityPermissions);
        Assert.Single(profileA.SourceMediaIntegrityPermissions);
        Config unbound = new(); unbound.CopyFrom(profileA);
        Assert.Empty(unbound.SourceMediaIntegrityPermissions);
        profileB.SaveConfig(second.Path);
        Assert.Empty(Config.LoadConfig(second.Path).SourceMediaIntegrityPermissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidPrimaryRetainsConsentAndRevisionAcrossReloads(bool allow)
    {
        using TempDirectory directory = new();
        Config config = await CreateRevokedConfigWithGrantedBackup(directory.Path);
        SourceMediaIntegrityPolicyService policy = CreatePolicy(config, directory.Path);
        policy.SetActive(true);
        if (allow)
            await policy.SetPermissionAsync(true, policy.GetSnapshot().Revision, value => Persist(config, value));
        SourceMediaIntegrityPermission expected = config.SourceMediaIntegrityPermissions[PermissionKey];
        Dictionary<string, byte[]> files = SnapshotFiles(directory.Path);

        for (int restart = 0; restart < 2; restart++)
        {
            Config loaded = Config.LoadConfig(directory.Path);
            Assert.Null(loaded.LoadWarning);
            Assert.Equal(expected, loaded.SourceMediaIntegrityPermissions[PermissionKey]);
            SourceMediaIntegrityPolicyService reloaded = CreatePolicy(loaded, directory.Path);
            reloaded.SetActive(true);
            Assert.Equal(allow, reloaded.GetSnapshot().AllowProviderSizeMd5Mismatch);
            Assert.Equal(expected.Revision + 1, reloaded.GetSnapshot().Revision);
        }
        AssertFilesUnchanged(directory.Path, files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackupRecoveryDoesNotRestoreRevokedConsentOrMutateFiles(bool missingPrimary)
    {
        using TempDirectory directory = new();
        Config config = await CreateRevokedConfigWithGrantedBackup(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        // A real grant followed by a persisted revoke leaves the grant in .bak.
        SourceMediaIntegrityPermission revoked = config.SourceMediaIntegrityPermissions[PermissionKey];
        Assert.False(revoked.AllowProviderSizeMd5Mismatch);
        DamagePrimary(path, missingPrimary);
        Dictionary<string, byte[]> files = SnapshotFiles(directory.Path);

        Config recovered = Config.LoadConfig(directory.Path);

        SourceMediaIntegrityPermission permission = recovered.SourceMediaIntegrityPermissions[PermissionKey];
        Assert.False(permission.AllowProviderSizeMd5Mismatch);
        Assert.Equal(revoked.Revision - 1, permission.Revision);
        Assert.Equal("owner", permission.PluginOwnerId);
        Assert.Equal("source", permission.SourceId);
        Assert.False(recovered.SourceMediaIntegrityPermissions[OtherGrantKey].AllowProviderSizeMd5Mismatch);
        Assert.Equal(long.MaxValue, recovered.SourceMediaIntegrityPermissions[OtherGrantKey].Revision);
        Assert.Equal(new SourceMediaIntegrityPermission("other-owner", "denied", false, 19),
            recovered.SourceMediaIntegrityPermissions[DeniedKey]);
        Assert.Equal(3, recovered.SourceMediaIntegrityPermissions.Count);
        Assert.Equal(120, recovered.RecheckTimer);
        Assert.Equal(7, recovered.MaxParallelDownloads);
        Assert.Equal(Path.Combine(directory.Path, "downloads"), recovered.DownloadPath);
        Assert.Contains("Recovered settings", recovered.LoadWarning);
        Assert.Contains("Source media exception permissions", recovered.LoadWarning);
        Assert.Contains("explicit confirmation", recovered.LoadWarning);
        SourceMediaIntegrityPolicyService policy = CreatePolicy(recovered, directory.Path);
        policy.SetActive(true);
        Assert.False(policy.GetView().IsGranted);
        Assert.False(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            policy.AcquireAcceptanceLeaseAsync(policy.GetSnapshot().Revision, default).AsTask());
        recovered.Validate();
        AssertFilesUnchanged(directory.Path, files);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveredConsentCannotBypassRecoveryDecisionThroughOrdinarySaveOrGrant(bool missingPrimary)
    {
        using TempDirectory directory = new();
        await CreateRevokedConfigWithGrantedBackup(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        DamagePrimary(path, missingPrimary);
        Dictionary<string, byte[]> before = SnapshotFiles(directory.Path);
        Config recovered = Config.LoadConfig(directory.Path);
        SourceMediaIntegrityPermission permission = recovered.SourceMediaIntegrityPermissions[PermissionKey];
        Assert.False(permission.AllowProviderSizeMd5Mismatch);
        Assert.Throws<InvalidOperationException>(() => recovered.SaveConfig());
        Assert.NotNull(recovered.LoadWarning);
        AssertFilesUnchanged(directory.Path, before);

        SourceMediaIntegrityPolicyService policy = CreatePolicy(recovered, directory.Path);
        policy.SetActive(true);
        long activeRevision = policy.GetSnapshot().Revision;
        Assert.Equal(permission.Revision + 1, activeRevision);
        await Assert.ThrowsAsync<InvalidDataException>(() => policy.SetPermissionAsync(true, permission.Revision,
            _ => Assert.Fail("Stale recovery revision was persisted")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.SetPermissionAsync(true, activeRevision,
            value => Persist(recovered, value)));
        Assert.Equal(activeRevision, policy.GetSnapshot().Revision);
        Assert.False(policy.GetSnapshot().AllowProviderSizeMd5Mismatch);
        Assert.Equal(permission, recovered.SourceMediaIntegrityPermissions[PermissionKey]);
        await Assert.ThrowsAsync<InvalidDataException>(() => policy.AcquireAcceptanceLeaseAsync(activeRevision, default).AsTask());
        Config reloaded = Config.LoadConfig(directory.Path);
        Assert.NotNull(reloaded.LoadWarning);
        Assert.False(reloaded.SourceMediaIntegrityPermissions[PermissionKey].AllowProviderSizeMd5Mismatch);
        SourceMediaIntegrityPolicyService restarted = CreatePolicy(reloaded, directory.Path);
        restarted.SetActive(true);
        Assert.False(restarted.GetSnapshot().AllowProviderSizeMd5Mismatch);
        AssertFilesUnchanged(directory.Path, before);
    }

    private static readonly string PermissionKey = SourceMediaIntegrityPermission.CreateKey("owner", "source");
    private static readonly string OtherGrantKey = SourceMediaIntegrityPermission.CreateKey("other-owner", "source");
    private static readonly string DeniedKey = SourceMediaIntegrityPermission.CreateKey("other-owner", "denied");
    private const string DamagedPrimary = "{interrupted-write";

    private static async Task<Config> CreateRevokedConfigWithGrantedBackup(string directory)
    {
        Config config = Config.LoadConfig(directory);
        config.DownloadPath = Path.Combine(directory, "downloads");
        config.RecheckTimer = 120;
        config.MaxParallelDownloads = 7;
        config.SourceMediaIntegrityPermissions[OtherGrantKey] = new("other-owner", "source", true, long.MaxValue);
        config.SourceMediaIntegrityPermissions[DeniedKey] = new("other-owner", "denied", false, 19);
        SourceMediaIntegrityPolicyService policy = new(directory, "owner", "source");
        policy.SetActive(true);
        await policy.SetPermissionAsync(true, policy.GetSnapshot().Revision, value => Persist(config, value));
        long grantedRevision = config.SourceMediaIntegrityPermissions[PermissionKey].Revision;
        Assert.True(config.SourceMediaIntegrityPermissions[PermissionKey].AllowProviderSizeMd5Mismatch);
        await policy.SetPermissionAsync(false, policy.GetSnapshot().Revision, value => Persist(config, value));
        Assert.Equal(grantedRevision + 1, config.SourceMediaIntegrityPermissions[PermissionKey].Revision);
        return config;
    }

    private static SourceMediaIntegrityPolicyService CreatePolicy(Config config, string directory) =>
        new(directory, "owner", "source", config.SourceMediaIntegrityPermissions[PermissionKey]);

    private static void Persist(Config config, SourceMediaIntegrityPermission permission)
    {
        Config candidate = config.Clone();
        candidate.SourceMediaIntegrityPermissions[PermissionKey] = permission;
        candidate.SaveConfig();
        config.CopyFrom(candidate);
    }

    private static void DamagePrimary(string path, bool missingPrimary)
    {
        if (missingPrimary) File.Delete(path);
        else File.WriteAllText(path, DamagedPrimary);
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string directory) =>
        Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);

    private static void AssertFilesUnchanged(string directory, Dictionary<string, byte[]> expected)
    {
        Dictionary<string, byte[]> actual = SnapshotFiles(directory);
        Assert.Equal(expected.Keys.OrderBy(key => key), actual.Keys.OrderBy(key => key));
        foreach ((string name, byte[] contents) in expected)
            Assert.Equal(contents, actual[name]);
    }

    [Fact]
    public void CompoundKeyCannotAliasOwnersAndSourcesContainingSeparators()
    {
        Assert.NotEqual(SourceMediaIntegrityPermission.CreateKey("a/b", "c"), SourceMediaIntegrityPermission.CreateKey("a", "b/c"));
        Assert.NotEqual(SourceMediaIntegrityPermission.CreateKey("a%2Fb", "c"), SourceMediaIntegrityPermission.CreateKey("a/b", "c"));
    }

    [Theory]
    [InlineData("owner/source", "other-owner", "source", 1)]
    [InlineData("owner/source", "owner", "source", -1)]
    [InlineData("owner/source", "owner", "source\n", 1)]
    public void InvalidSavedIdentityOrRevisionIsRejected(string key, string owner, string source, long revision)
    {
        Config config = new();
        config.SourceMediaIntegrityPermissions[key] = new(owner, source, true, revision);
        Assert.Throws<InvalidDataException>(() => config.Validate());
    }

    [Fact]
    public void NullPermissionMapOrRecordIsRejected()
    {
        Config config = new() { SourceMediaIntegrityPermissions = null! };
        Assert.Throws<InvalidDataException>(() => config.Validate());
        config.SourceMediaIntegrityPermissions = new() { ["owner/source"] = null! };
        Assert.Throws<InvalidDataException>(() => config.Validate());
    }
}
