using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Sources;

namespace MeowChan.DesktopTests;

/// <summary>
/// End to end: a 1.0.x profile with the old single-site chan plugins (Lainchan, Leftypol, SushiChan, Uboachan, Wizchan,
/// ZZZchan) opened by 1.1 hands every site to its vichan or jschan family plugin. Pins the whole startup path through
/// <see cref="DesktopSessionHost"/>: the site switch takeover into <c>chan-sources.json</c>, the retirement of the old
/// records with the immutable pre-retirement backup, the families' activation without a repair review, URL routing,
/// idempotent later starts, the user's 1.0.x choice over the family default, and the fail-safe path when the switch
/// file cannot be read. Fixtures only (store-level 1.0.x stand-ins, in-memory transport that rejects every request).
/// </summary>
[Collection("Delivered plugin hosts")]
public sealed class ChanFamilyMigrationEndToEndTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private const string OldLainchan = "meowchan.source.lainchan";
    private static readonly string[] FamilySites = ["lainchan", "leftypol", "sushichan", "uboachan", "wizchan", "zzzchan"];
    private static readonly Uri LainchanThread = new(DesktopPluginFixtures.ThreadUrl("lainchan"));
    private static readonly Uri ZzzchanThread = new("https://zzzchan.xyz/demo/thread/123.html");

    // The typical 1.0.x profile: Lainchan (on by default) on, every experimental site off, and a size/MD5 exception
    // that the old Lainchan plugin owned.
    private static readonly (string PluginId, string SourceId, bool Enabled)[] TypicalLegacy =
    [
        (OldLainchan, "lainchan", true), ("meowchan.source.leftypol", "leftypol", false),
        ("meowchan.source.sushichan", "sushichan", false), ("meowchan.source.uboachan", "uboachan", false),
        ("meowchan.source.wizchan", "wizchan", false), ("meowchan.source.zzzchan", "zzzchan", false)
    ];

    [Fact]
    public async Task TheFirstStartRetiresTheSingleSitePluginsAndHandsEverySiteToItsActiveFamily()
    {
        using TempDirectory profile = new();
        await SeedTypicalAsync(profile.Path);
        PluginProfileSnapshot legacy = await new PluginProfileStore(profile.Path).ReadAsync();

        using DesktopPluginFixtures fixture = new() { RejectAll = true };
        await using DesktopSessionHost host = ChanFamilyUpgradeTests.Create(profile.Path, fixture);
        await host.StartAsync().WaitAsync(Deadline);
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        PluginSession plugins = host.Plugins!;
        Assert.Empty(plugins.Warnings);
        PluginProfileSnapshot state = await plugins.ProfileStore.ReadAsync();

        // The old records are retired; the exact state before that is kept in one immutable backup.
        Assert.DoesNotContain(state.Plugins, plugin => ChanSourceFamilies.Replaced.ContainsKey(plugin.PluginId));
        string backup = Assert.Single(Backups(profile.Path));
        PluginProfileSnapshot saved = ChanFamilyUpgradeTests.ReadEnvelope(backup);
        foreach (PluginActivationSnapshot old in legacy.Plugins)
        {
            PluginActivationSnapshot kept = Assert.Single(saved.Plugins, plugin => plugin.PluginId == old.PluginId);
            Assert.Equal(old.Enabled, kept.Enabled);
            Assert.Equal(old.Active!.ExecutionRevision, kept.LastGood!.ExecutionRevision);
        }

        // chan-sources.json took each site's on/off state over from its old plugin, once per old plugin.
        (Dictionary<string, bool> enabled, string[] migrated) = ReadSwitchFile(profile.Path);
        Assert.Equal(TypicalLegacy.ToDictionary(item => item.SourceId, item => item.Enabled), enabled);
        Assert.Equal(ChanSourceFamilies.Replaced.Keys.Order(StringComparer.Ordinal), migrated);
        Assert.True(plugins.SiteSwitches.IsEnabled(ChanSourceFamilies.Vichan, "lainchan"));
        foreach (string site in FamilySites.Where(site => site != "lainchan"))
            Assert.False(plugins.SiteSwitches.IsEnabled(site == "zzzchan" ? ChanSourceFamilies.Jschan : ChanSourceFamilies.Vichan, site), site);

        // Both families run with no repair review, although the old Lainchan plugin had a size/MD5 exception.
        AssertFamiliesActive(state);
        Assert.DoesNotContain((await plugins.ProfileStore.PreviewBundledRepairsAsync()).Rows, row => ChanSourceFamilies.IsFamily(row.PluginId));
        Assert.All(plugins.Sources.Where(source => FamilySites.Contains(source.Name)),
            source => Assert.NotEqual(PluginSourceUnavailableReason.NeedsRepairReview, source.UnavailableReason));
        // The exception is per owner: it stays with the retired plugin and the family's Lainchan starts strict.
        Assert.All(host.GetSourceMediaIntegrityPolicies().Where(policy => policy.Snapshot.SourceId == "lainchan"), policy =>
        { Assert.Equal(ChanSourceFamilies.Vichan, policy.Snapshot.PluginOwnerId); Assert.False(policy.IsGranted); });

        // Exactly one adapter per site, owned by its family; a thread link of each family routes there.
        AssertOneFamilyAdapterPerSite(plugins);
        PluginSourceImageBoard lainchan = AssertRoutesToFamily(plugins, LainchanThread, ChanSourceFamilies.Vichan);
        Assert.True(lainchan.Enabled);
        Assert.Equal(PluginSourceUnavailableReason.None, lainchan.UnavailableReason);
        PluginSourceImageBoard zzzchan = AssertRoutesToFamily(plugins, ZzzchanThread, ChanSourceFamilies.Jschan);
        Assert.False(zzzchan.Enabled);
        Assert.Equal(PluginSourceUnavailableReason.SiteDisabled, zzzchan.UnavailableReason);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task ASecondStartRewritesNothingOfTheMigrationAndTheFamiliesStayActive()
    {
        using TempDirectory profile = new();
        await SeedTypicalAsync(profile.Path);
        string switchFile = Path.Combine(profile.Path, ChanSiteSwitches.FileName);
        byte[] switchBytes = [], backupBytes = [];
        DateTime switchWritten = default;
        string backup = string.Empty;
        Dictionary<string, string> familyBuilds = new(StringComparer.Ordinal);
        for (int start = 0; start < 2; start++)
        {
            using DesktopPluginFixtures fixture = new() { RejectAll = true };
            await using DesktopSessionHost host = ChanFamilyUpgradeTests.Create(profile.Path, fixture);
            await host.StartAsync().WaitAsync(Deadline);
            Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
            PluginSession plugins = host.Plugins!;
            PluginProfileSnapshot state = await plugins.ProfileStore.ReadAsync();
            Assert.DoesNotContain(state.Plugins, plugin => ChanSourceFamilies.Replaced.ContainsKey(plugin.PluginId));
            AssertFamiliesActive(state);
            if (start == 0)
            {
                switchBytes = await File.ReadAllBytesAsync(switchFile);
                switchWritten = File.GetLastWriteTimeUtc(switchFile);
                backup = Assert.Single(Backups(profile.Path));
                backupBytes = await File.ReadAllBytesAsync(backup);
                foreach (PluginActivationSnapshot family in state.Plugins.Where(plugin => ChanSourceFamilies.IsFamily(plugin.PluginId)))
                    familyBuilds.Add(family.PluginId, family.Active!.ExecutionRevision);
                Assert.Equal(2, familyBuilds.Count);
            }
            else
            {
                // Nothing is taken over or retired again: the switch file, the one backup and the family builds are unchanged.
                Assert.Equal(switchBytes, await File.ReadAllBytesAsync(switchFile));
                Assert.Equal(switchWritten, File.GetLastWriteTimeUtc(switchFile));
                Assert.Equal(backup, Assert.Single(Backups(profile.Path)));
                Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
                Assert.Empty(Directory.GetFiles(profile.Path, ChanSiteSwitches.FileName + ".*.tmp"));
                foreach (PluginActivationSnapshot family in state.Plugins.Where(plugin => ChanSourceFamilies.IsFamily(plugin.PluginId)))
                    Assert.Equal(familyBuilds[family.PluginId], family.Active!.ExecutionRevision);
                // With the old record gone, its exception no longer keeps the family's Lainchan in the repair restriction set.
                Assert.DoesNotContain("lainchan", ChanFamilyUpgradeTests.ReadRestrictedSourceIds(profile.Path));
            }
            Assert.DoesNotContain((await plugins.ProfileStore.PreviewBundledRepairsAsync()).Rows, row => ChanSourceFamilies.IsFamily(row.PluginId));
            AssertOneFamilyAdapterPerSite(plugins);
            Assert.True(AssertRoutesToFamily(plugins, LainchanThread, ChanSourceFamilies.Vichan).Enabled);
            Assert.False(AssertRoutesToFamily(plugins, ZzzchanThread, ChanSourceFamilies.Jschan).Enabled);
            Assert.Empty(fixture.Requests);
        }
    }

    [Fact]
    public async Task TheUsers1xChoiceWinsOverTheFamilyDefaultInBothDirections()
    {
        using TempDirectory profile = new();
        ChanFamilyUpgradeTests.Configure(profile.Path);
        // Lainchan (on by default) turned off; Wizchan and ZZZchan (off by default) turned on; Uboachan left off.
        // Leftypol and SushiChan have no old record at all.
        await ChanFamilyUpgradeTests.SeedLegacyAsync(profile.Path, (OldLainchan, "lainchan", false),
            ("meowchan.source.wizchan", "wizchan", true), ("meowchan.source.zzzchan", "zzzchan", true),
            ("meowchan.source.uboachan", "uboachan", false));
        Dictionary<string, bool> expected = new(StringComparer.Ordinal)
        { ["lainchan"] = false, ["wizchan"] = true, ["zzzchan"] = true, ["uboachan"] = false, ["leftypol"] = false, ["sushichan"] = false };
        // The second start runs after the old records retired: only chan-sources.json still holds the choice, and the
        // default must not come back.
        for (int start = 0; start < 2; start++)
        {
            using DesktopPluginFixtures fixture = new() { RejectAll = true };
            await using DesktopSessionHost host = ChanFamilyUpgradeTests.Create(profile.Path, fixture);
            await host.StartAsync().WaitAsync(Deadline);
            Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
            PluginSession plugins = host.Plugins!;
            PluginProfileSnapshot state = await plugins.ProfileStore.ReadAsync();
            Assert.DoesNotContain(state.Plugins, plugin => ChanSourceFamilies.Replaced.ContainsKey(plugin.PluginId));
            AssertFamiliesActive(state);
            AssertOneFamilyAdapterPerSite(plugins);
            foreach ((string site, bool on) in expected)
            {
                PluginSourceImageBoard source = Assert.Single(plugins.Sources, item => item.Name == site);
                Assert.True(on == source.Enabled, site);
                Assert.Equal(on ? PluginSourceUnavailableReason.None : PluginSourceUnavailableReason.SiteDisabled, source.UnavailableReason);
            }
            (Dictionary<string, bool> stored, _) = ReadSwitchFile(profile.Path);
            // Only the sites with an old record were taken over; the others keep their (off) default without an entry.
            Assert.Equal(new Dictionary<string, bool>(StringComparer.Ordinal)
                { ["lainchan"] = false, ["wizchan"] = true, ["zzzchan"] = true, ["uboachan"] = false }, stored);
            Assert.False(AssertRoutesToFamily(plugins, LainchanThread, ChanSourceFamilies.Vichan).Enabled);
            Assert.True(AssertRoutesToFamily(plugins, ZzzchanThread, ChanSourceFamilies.Jschan).Enabled);
            Assert.Empty(fixture.Requests);
        }
    }

    [Fact]
    public async Task AnUnreadableSwitchFileRetiresNothingAndTurnsNoSiteOn()
    {
        using TempDirectory profile = new();
        ChanFamilyUpgradeTests.Configure(profile.Path);
        // Lainchan turned off (the one default-on site) and Wizchan turned on: neither may change silently.
        await ChanFamilyUpgradeTests.SeedLegacyAsync(profile.Path, (OldLainchan, "lainchan", false), ("meowchan.source.wizchan", "wizchan", true),
            ("meowchan.source.zzzchan", "zzzchan", false));
        PluginProfileSnapshot legacy = await new PluginProfileStore(profile.Path).ReadAsync();
        string switchFile = Path.Combine(profile.Path, ChanSiteSwitches.FileName);
        const string Damaged = "{ not json";
        await File.WriteAllTextAsync(switchFile, Damaged);
        for (int start = 0; start < 2; start++)
        {
            using DesktopPluginFixtures fixture = new() { RejectAll = true };
            await using DesktopSessionHost host = ChanFamilyUpgradeTests.Create(profile.Path, fixture);
            await host.StartAsync().WaitAsync(Deadline);
            Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
            PluginSession plugins = host.Plugins!;
            Assert.True(plugins.SiteSwitches.IsUnreadable);
            PluginProfileSnapshot state = await plugins.ProfileStore.ReadAsync();

            // Nothing was taken over, so nothing is retired: every old record stays (inactive, it owns no source) and
            // no pre-retirement backup is written. The damaged file is never overwritten.
            Assert.Equal(Damaged, await File.ReadAllTextAsync(switchFile));
            Assert.Empty(Backups(profile.Path));
            foreach (PluginActivationSnapshot old in legacy.Plugins)
            {
                PluginActivationSnapshot kept = Assert.Single(state.Plugins, plugin => plugin.PluginId == old.PluginId);
                Assert.Equal(old.Enabled, kept.Enabled);
                Assert.Null(kept.Active);
            }
            Assert.Equal(legacy.Plugins.Select(plugin => plugin.PluginId).Order(StringComparer.Ordinal),
                SupersededBundledPlugins.InProfile(state, ChanSourceFamilies.Successors).Order(StringComparer.Ordinal));

            // Fail safe: no family site is used while its stored choice is unknown, not even the default-on Lainchan
            // that the user had turned off. Still one adapter per site, owned by its family.
            AssertFamiliesActive(state);
            AssertOneFamilyAdapterPerSite(plugins);
            Assert.All(plugins.Sources.Where(source => FamilySites.Contains(source.Name)), source =>
            {
                Assert.False(source.Enabled, source.Name);
                Assert.Equal(PluginSourceUnavailableReason.SiteDisabled, source.UnavailableReason);
            });
            Assert.False(AssertRoutesToFamily(plugins, LainchanThread, ChanSourceFamilies.Vichan).Enabled);
            Assert.False(AssertRoutesToFamily(plugins, ZzzchanThread, ChanSourceFamilies.Jschan).Enabled);
            // No switch can change until the file is fixed or removed.
            Assert.False(plugins.SetChanSiteEnabled("lainchan", true));
            Assert.Equal(Damaged, await File.ReadAllTextAsync(switchFile));
            Assert.Empty(fixture.Requests);
        }
    }

    private static async Task SeedTypicalAsync(string profile)
    {
        ChanFamilyUpgradeTests.Configure(profile, new SourceMediaIntegrityPermission(OldLainchan, "lainchan", true, 1));
        await ChanFamilyUpgradeTests.SeedLegacyAsync(profile, TypicalLegacy);
    }

    private static void AssertFamiliesActive(PluginProfileSnapshot state)
    {
        PluginActivationSnapshot[] families = state.Plugins.Where(plugin => ChanSourceFamilies.IsFamily(plugin.PluginId)).ToArray();
        Assert.Equal(new[] { ChanSourceFamilies.Jschan, ChanSourceFamilies.Vichan }, families.Select(family => family.PluginId).Order(StringComparer.Ordinal));
        Assert.All(families, family =>
        {
            Assert.True(family.Enabled, family.PluginId);
            Assert.NotNull(family.Active);
            Assert.Null(family.Problem);
            Assert.False(PluginActivationProblems.RequiresRepairReview(family.Problem));
        });
    }

    private static void AssertOneFamilyAdapterPerSite(PluginSession plugins)
    {
        Assert.Equal(plugins.Sources.Count, plugins.Sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (string site in FamilySites)
        {
            PluginSourceImageBoard source = Assert.Single(plugins.Sources, item => item.Name == site);
            Assert.Equal(site == "zzzchan" ? ChanSourceFamilies.Jschan : ChanSourceFamilies.Vichan, plugins.SourceOwner(source));
        }
    }

    private static PluginSourceImageBoard AssertRoutesToFamily(PluginSession plugins, Uri thread, string family)
    {
        PluginSourceImageBoard routed = Assert.Single(plugins.Sources, source => source.CanHandle(thread));
        Assert.Equal(family, plugins.SourceOwner(routed));
        return routed;
    }

    private static string[] Backups(string profile) =>
        Directory.GetFiles(Path.Combine(profile, "plugin-state"), "state.pre-retirement-*.json");

    private static (Dictionary<string, bool> Enabled, string[] Migrated) ReadSwitchFile(string profile)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(profile, ChanSiteSwitches.FileName)));
        Assert.Equal(1, document.RootElement.GetProperty("Version").GetInt32());
        Dictionary<string, bool> enabled = document.RootElement.GetProperty("Enabled").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetBoolean(), StringComparer.Ordinal);
        string[] migrated = document.RootElement.GetProperty("Migrated").EnumerateArray().Select(item => item.GetString()!).ToArray();
        return (enabled, migrated);
    }
}
