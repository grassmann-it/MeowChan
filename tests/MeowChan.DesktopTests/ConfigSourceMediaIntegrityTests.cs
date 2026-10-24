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
