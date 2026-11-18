using System.Runtime.CompilerServices;
using System.Text;
using MeowChan.Desktop;
using MeowChan.Desktop.Plugins;
using MeowChan.Desktop.Updates;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginHost.Sources;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

/// <summary>
/// Engine-family chan plugins: per-site switches, the takeover of the replaced single-site plugins' state,
/// and the upgrade from a 1.0.x profile without any source being registered twice.
/// </summary>
public sealed class ChanSiteSwitchesTests
{
    [Fact]
    public void SitesStartAtTheirDefaultAndOnlyFamilySitesHaveASwitch()
    {
        using TempDirectory profile = new();
        ChanSiteSwitches switches = new(profile.Path);
        Assert.True(switches.IsEnabled(ChanSourceFamilies.Vichan, "lainchan"));
        Assert.False(switches.IsEnabled(ChanSourceFamilies.Vichan, "wizchan"));
        Assert.False(switches.IsEnabled(ChanSourceFamilies.Jschan, "zzzchan"));
        Assert.False(switches.IsEnabled(ChanSourceFamilies.Vichan, "added-by-a-local-build"));
        // Any other plugin's source has no site switch.
        Assert.True(switches.IsEnabled("meowchan.source.kohlchan", "kohlchan"));
        Assert.False(switches.SetEnabled("meowchan.source.kohlchan", "kohlchan", false));
        Assert.False(File.Exists(Path.Combine(profile.Path, ChanSiteSwitches.FileName)), "Reading defaults writes nothing.");
    }

    [Fact]
    public void AChoiceIsDurableAndStaysTheUsersEvenWhenItEqualsTheDefault()
    {
        using TempDirectory profile = new();
        Assert.True(new ChanSiteSwitches(profile.Path).SetEnabled(ChanSourceFamilies.Vichan, "wizchan", true));
        Assert.True(new ChanSiteSwitches(profile.Path).IsEnabled(ChanSourceFamilies.Vichan, "wizchan"));
        Assert.True(new ChanSiteSwitches(profile.Path).SetEnabled(ChanSourceFamilies.Vichan, "wizchan", false));
        ChanSiteSwitches reopened = new(profile.Path);
        Assert.False(reopened.IsEnabled(ChanSourceFamilies.Vichan, "wizchan"));
        Assert.False(reopened.IsUnreadable);
    }

    [Fact]
    public void TheReplacedPluginsEnabledStateIsTakenOverOncePerOldPluginAndNeverOverridesAChoice()
    {
        using TempDirectory profile = new();
        PluginProfileSnapshot legacy = Profile(("meowchan.source.wizchan", true), ("meowchan.source.lainchan", false),
            ("meowchan.source.leftypol", false), ("meowchan.source.zzzchan", true), ("meowchan.source.kohlchan", true));
        ChanSiteSwitches switches = new(profile.Path);
        Assert.True(switches.SetEnabled(ChanSourceFamilies.Vichan, "leftypol", true)); // An explicit choice made before.
        switches.MigrateFrom(legacy);
        Assert.True(switches.IsEnabled(ChanSourceFamilies.Vichan, "wizchan"));
        Assert.False(switches.IsEnabled(ChanSourceFamilies.Vichan, "lainchan")); // A disabled default-on site stays off.
        Assert.True(switches.IsEnabled(ChanSourceFamilies.Vichan, "leftypol")); // The existing choice wins.
        Assert.True(switches.IsEnabled(ChanSourceFamilies.Jschan, "zzzchan"));
        Assert.False(switches.IsEnabled(ChanSourceFamilies.Vichan, "sushichan")); // No old record: its default.

        // The user turns Wizchan off again; the old record (kept for older releases) never re-seeds it.
        Assert.True(switches.SetEnabled(ChanSourceFamilies.Vichan, "wizchan", false));
        ChanSiteSwitches restarted = new(profile.Path);
        restarted.MigrateFrom(legacy);
        Assert.False(restarted.IsEnabled(ChanSourceFamilies.Vichan, "wizchan"));
        Assert.False(restarted.IsEnabled(ChanSourceFamilies.Vichan, "lainchan"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"Version\":2,\"Enabled\":{\"wizchan\":true},\"Migrated\":[]}")]
    [InlineData("{\"Version\":1}")]
    public void AnUnusableFileTurnsNoFamilySiteOnAndIsNeverOverwritten(string content)
    {
        using TempDirectory profile = new();
        string file = Path.Combine(profile.Path, ChanSiteSwitches.FileName);
        File.WriteAllText(file, content);
        ChanSiteSwitches switches = new(profile.Path);
        Assert.False(switches.IsEnabled(ChanSourceFamilies.Vichan, "wizchan"));
        // Fail safe: the stored choices are unknown, so even the default-on site stays off (the user may have turned it off).
        Assert.False(switches.IsEnabled(ChanSourceFamilies.Vichan, "lainchan"));
        Assert.True(switches.IsEnabled("meowchan.source.kohlchan", "kohlchan")); // Other plugins have no site switch.
        Assert.True(switches.IsUnreadable);
        Assert.False(switches.SetEnabled(ChanSourceFamilies.Vichan, "wizchan", true));
        switches.MigrateFrom(Profile(("meowchan.source.wizchan", true)));
        Assert.Equal(content, File.ReadAllText(file));
        Assert.False(switches.IsEnabled(ChanSourceFamilies.Vichan, "wizchan"));
    }

    // Durability cannot be unit-tested. The retirement of the old single-site plugin records relies on this file having
    // reached the disk before the profile write, so guard the write path itself: write-through temp file, flush to disk, then rename.
    [Fact]
    public void TheSwitchFileIsFlushedToDiskBeforeItIsRenamedIntoPlace()
    {
        string source = File.ReadAllText(Path.Combine(RepositoryRoot(), "Desktop", "ChanSiteSwitches.cs"));
        int write = source.IndexOf("private bool Write(", StringComparison.Ordinal);
        Assert.True(write >= 0, "ChanSiteSwitches.Write was not found.");
        string body = source[write..];
        int create = body.IndexOf("FileOptions.WriteThrough", StringComparison.Ordinal);
        int flush = body.IndexOf("Flush(flushToDisk: true)", StringComparison.Ordinal);
        int move = body.IndexOf("File.Move(", StringComparison.Ordinal);
        Assert.True(create >= 0, "The temporary file must be opened with FileOptions.WriteThrough.");
        Assert.True(flush > create, "The temporary file must be flushed to disk after it is written.");
        Assert.True(move > flush, "The rename must come after the flush.");
        Assert.DoesNotContain("File.WriteAllBytes(", body);
    }

    private static string RepositoryRoot([CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../.."));

    private static PluginProfileSnapshot Profile(params (string PluginId, bool Enabled)[] plugins) => new(1,
        plugins.Select(plugin => new PluginActivationSnapshot(plugin.PluginId, plugin.Enabled, 1, null, null, null, null, null,
            Array.Empty<PluginCandidateSnapshot>(), null)).ToArray(), Array.Empty<PluginGrantSnapshot>());
}

/// <summary>
/// Which saved size/MD5 exceptions keep a site's changed bundled build in repair review: an exception left behind by a
/// retired 1.0.x single-site plugin restricts nothing (nothing can use it), every other one still does.
/// </summary>
public sealed class ChanFamilyRepairRestrictionTests
{
    private const string OldLainchan = "meowchan.source.lainchan";

    [Fact]
    public void AnExceptionOfARetiredSingleSitePluginNoLongerRestrictsItsSite()
    {
        SourceMediaIntegrityPermission[] permissions = [new(OldLainchan, "lainchan", true, 1)];
        Assert.Empty(PluginSession.RepairRestrictedSourceIds(permissions, Profile(ChanSourceFamilies.Vichan), []));
    }

    [Fact]
    public void AnExceptionOfTheFamilyOrAnyOtherCurrentOwnerStillRestricts()
    {
        SourceMediaIntegrityPermission[] permissions =
        [
            new(ChanSourceFamilies.Vichan, "lainchan", true, 1),
            new("meowchan.source.kohlchan", "kohlchan", true, 1),
            new(OldLainchan, "leftypol", true, 1) // Not the old plugin's own site: hand-edited, kept strict.
        ];
        Assert.Equal(new[] { "lainchan", "kohlchan", "leftypol" },
            PluginSession.RepairRestrictedSourceIds(permissions, Profile(ChanSourceFamilies.Vichan), []));
    }

    [Fact]
    public void AnExceptionOfAReplacedPluginStillRestrictsUntilItsOwnerReallyLeft()
    {
        SourceMediaIntegrityPermission[] permissions = [new(OldLainchan, "lainchan", true, 1)];
        // The profile is not read yet: nothing is known to be retired.
        Assert.Equal(new[] { "lainchan" }, PluginSession.RepairRestrictedSourceIds(permissions, null, []));
        // The old record is still in the profile (not retired, for example a pinned build).
        Assert.Equal(new[] { "lainchan" }, PluginSession.RepairRestrictedSourceIds(permissions, Profile(ChanSourceFamilies.Vichan, OldLainchan), []));
        // A local copy with the old identity is installed: the exception can apply to it again.
        PluginCandidateObservation local = new("local:" + OldLainchan, OldLainchan, null, null);
        Assert.Equal(new[] { "lainchan" }, PluginSession.RepairRestrictedSourceIds(permissions, Profile(ChanSourceFamilies.Vichan), [local]));
    }

    [Fact]
    public void ARevokedExceptionNeverRestricts()
    {
        SourceMediaIntegrityPermission[] permissions = [new(ChanSourceFamilies.Vichan, "lainchan", false, 2), new(OldLainchan, "lainchan", false, 2)];
        Assert.Empty(PluginSession.RepairRestrictedSourceIds(permissions, null, []));
        Assert.Empty(PluginSession.RepairRestrictedSourceIds(permissions, Profile(ChanSourceFamilies.Vichan), []));
    }

    private static PluginProfileSnapshot Profile(params string[] pluginIds) => new(1,
        pluginIds.Select(pluginId => new PluginActivationSnapshot(pluginId, true, 1, null, null, null, null, null,
            Array.Empty<PluginCandidateSnapshot>(), null)).ToArray(), Array.Empty<PluginGrantSnapshot>());
}

[Collection("Delivered plugin hosts")]
public sealed class ChanFamilyUpgradeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly string[] VichanSites = ["lainchan", "leftypol", "sushichan", "uboachan", "wizchan"];

    [Fact]
    public async Task AProfileWithTheSingleSitePluginsUpgradesToTheFamiliesWithoutAnySourceRegisteredTwice()
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        // The 1.0.x state: single-site plugins active, Wizchan and ZZZchan turned on, Lainchan turned off.
        await SeedLegacyAsync(profile.Path, ("meowchan.source.lainchan", "lainchan", false), ("meowchan.source.wizchan", "wizchan", true),
            ("meowchan.source.leftypol", "leftypol", false), ("meowchan.source.zzzchan", "zzzchan", true));
        PluginProfileSnapshot legacy = await new PluginProfileStore(profile.Path).ReadAsync();
        for (int restart = 0; restart < 2; restart++)
        {
            using DesktopPluginFixtures fixture = new() { RejectAll = true };
            await using DesktopSessionHost host = Create(profile.Path, fixture);
            await host.StartAsync().WaitAsync(Deadline);
            Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
            PluginSession plugins = host.Plugins!;
            Assert.Empty(plugins.Warnings);
            PluginProfileSnapshot state = await plugins.ProfileStore.ReadAsync();

            // Exactly one adapter per source ID, owned by its family, with the taken-over site switch.
            Assert.Equal(plugins.Sources.Count, plugins.Sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count());
            foreach (string site in VichanSites.Append("zzzchan"))
                Assert.Single(plugins.Sources, source => source.Name == site);
            Assert.All(state.Plugins.Where(plugin => ChanSourceFamilies.IsFamily(plugin.PluginId)), family =>
            { Assert.True(family.Enabled); Assert.NotNull(family.Active); Assert.Null(family.Problem); });
            Assert.True(plugins.SiteSwitches.IsEnabled(ChanSourceFamilies.Vichan, "wizchan"));
            Assert.True(plugins.SiteSwitches.IsEnabled(ChanSourceFamilies.Jschan, "zzzchan"));
            Assert.False(plugins.SiteSwitches.IsEnabled(ChanSourceFamilies.Vichan, "lainchan"));
            Assert.False(plugins.SiteSwitches.IsEnabled(ChanSourceFamilies.Vichan, "leftypol"));
            Assert.False(plugins.SiteSwitches.IsEnabled(ChanSourceFamilies.Vichan, "sushichan"));
            // Routing guard: a thread of a family site resolves to exactly one adapter, owned by the family, never to
            // a replaced single-site plugin; a site that is off says so instead of a generic "unavailable".
            Uri lainchanThread = new(DesktopPluginFixtures.ThreadUrl("lainchan"));
            PluginSourceImageBoard routed = Assert.Single(plugins.Sources, source => source.CanHandle(lainchanThread));
            Assert.Equal(ChanSourceFamilies.Vichan, plugins.SourceOwner(routed));
            Assert.Equal(PluginSourceUnavailableReason.SiteDisabled, routed.UnavailableReason);
            Assert.All(plugins.Sources.Where(source => VichanSites.Append("zzzchan").Contains(source.Name)),
                source => Assert.True(ChanSourceFamilies.IsFamily(plugins.SourceOwner(source)!), source.Name));
            Assert.Equal(PluginSourceUnavailableReason.None, plugins.Sources.Single(source => source.Name == "wizchan").UnavailableReason);
            Assert.True(plugins.Sources.Single(source => source.Name == "wizchan").Enabled);
            Assert.True(plugins.Sources.Single(source => source.Name == "zzzchan").Enabled);
            Assert.False(plugins.Sources.Single(source => source.Name == "lainchan").Enabled);
            Assert.False(plugins.Sources.Single(source => source.Name == "leftypol").Enabled);
            // The mismatch exception is per owner: a family site starts strict until it is granted again.
            Assert.All(host.GetSourceMediaIntegrityPolicies().Where(policy => policy.Snapshot.SourceId is "wizchan" or "zzzchan"),
                policy => { Assert.Equal(policy.Snapshot.SourceId == "wizchan" ? ChanSourceFamilies.Vichan : ChanSourceFamilies.Jschan, policy.Snapshot.PluginOwnerId); Assert.False(policy.IsGranted); });

            // The replaced records left the profile on the first start: their state lives on in the site switches and in
            // one immutable pre-retirement backup, which holds every old record exactly as it was.
            Assert.DoesNotContain(state.Plugins, plugin => ChanSourceFamilies.Replaced.ContainsKey(plugin.PluginId));
            Assert.Empty(SupersededBundledPlugins.InProfile(state, ChanSourceFamilies.Successors));
            string backup = Assert.Single(Directory.GetFiles(Path.Combine(profile.Path, "plugin-state"), "state.pre-retirement-*.json"));
            PluginProfileSnapshot saved = ReadEnvelope(backup);
            foreach (PluginActivationSnapshot old in legacy.Plugins)
            {
                PluginActivationSnapshot kept = Assert.Single(saved.Plugins, plugin => plugin.PluginId == old.PluginId);
                Assert.Equal(old.Enabled, kept.Enabled);
                Assert.Equal(old.SelectedCandidateId, kept.SelectedCandidateId);
                Assert.Equal(old.Active!.ExecutionRevision, kept.LastGood!.ExecutionRevision);
            }
            PluginCatalogSnapshot catalog = await new PluginManagementServices(plugins.ProfileStore, plugins.CompilationService, plugins.Inventory,
                plugins.PluginDirectory, (_, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException(),
                (_, _, _, _, _) => throw new InvalidOperationException()).ReadCatalogAsync(CancellationToken.None);
            Assert.DoesNotContain(catalog.Entries, entry => entry.PluginId is { } id && ChanSourceFamilies.Replaced.ContainsKey(id));
            Assert.Contains(catalog.Entries, entry => entry.PluginId == ChanSourceFamilies.Vichan);
            Assert.DoesNotContain((await plugins.ProfileStore.PreviewBundledRepairsAsync()).Rows, row => ChanSourceFamilies.Replaced.ContainsKey(row.PluginId));
            Assert.DoesNotContain((await plugins.ProfileStore.PreviewBundledReselectionsAsync()).Rows, row => ChanSourceFamilies.Replaced.ContainsKey(row.PluginId));
            Assert.Empty(fixture.Requests);
        }
    }

    [Fact]
    public async Task AnOldSingleSitePluginStillInstalledBesideTheFamilyNeverDoubleRegistersASource()
    {
        using TempDirectory profile = new();
        Configure(profile.Path);
        await SeedLegacyAsync(profile.Path, ("meowchan.source.wizchan", "wizchan", true));
        // The user's own copy of an old single-site plugin, with the old identity, stays installed.
        string plugins = Path.Combine(profile.Path, "plugins");
        Directory.CreateDirectory(plugins);
        string family = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "plugins", "sources", "VichanFamily.cs"));
        await File.WriteAllTextAsync(Path.Combine(plugins, "OldWizchan.cs"),
            family.Replace("public const string PluginId = \"meowchan.source.vichan\";", "public const string PluginId = \"meowchan.source.wizchan\";", StringComparison.Ordinal),
            new UTF8Encoding(false));
        using DesktopPluginFixtures fixture = new() { RejectAll = true };
        await using DesktopSessionHost host = Create(profile.Path, fixture);
        await host.StartAsync().WaitAsync(Deadline);
        Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
        PluginProfileSnapshot state = await host.Plugins!.ProfileStore.ReadAsync();
        // Not replaced: the explicit conflict rules decide, and no source has two adapters or two active owners.
        Assert.Empty(SupersededBundledPlugins.InProfile(state, ChanSourceFamilies.Successors));
        Assert.Equal(host.Plugins.Sources.Count, host.Plugins.Sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (string site in VichanSites)
            Assert.True(state.Plugins.Count(plugin => plugin.Active?.Description.Sources.Any(source => source.SourceId == site) == true) <= 1, site);
        Assert.Contains("Source identity conflict", state.Plugins.Single(plugin => plugin.PluginId == ChanSourceFamilies.Vichan).Problem);
        Assert.Empty(fixture.Requests);
    }

    [Theory]
    [InlineData("meowchan.source.lainchan", false)] // Left behind by the retired single-site plugin.
    [InlineData(ChanSourceFamilies.Vichan, true)] // Granted to the family itself.
    public async Task OnlyAnExceptionOfAPluginStillInTheProfileKeepsTheFamilySiteInRepairReview(string owner, bool restricts)
    {
        using TempDirectory profile = new();
        Configure(profile.Path, new SourceMediaIntegrityPermission(owner, "lainchan", true, 1));
        await SeedLegacyAsync(profile.Path, ("meowchan.source.lainchan", "lainchan", true));
        for (int restart = 0; restart < 2; restart++)
        {
            using DesktopPluginFixtures fixture = new() { RejectAll = true };
            await using DesktopSessionHost host = Create(profile.Path, fixture);
            await host.StartAsync().WaitAsync(Deadline);
            Assert.True(host.State == DesktopSessionState.Ready, host.StartupError);
            PluginProfileSnapshot state = await host.Plugins!.ProfileStore.ReadAsync();
            Assert.DoesNotContain(state.Plugins, plugin => plugin.PluginId == "meowchan.source.lainchan");
            PluginActivationSnapshot family = Assert.Single(state.Plugins, plugin => plugin.PluginId == ChanSourceFamilies.Vichan);
            Assert.NotEqual(PluginActivationProblems.InheritedGrantsBlocked, family.Problem);
            Assert.NotNull(family.Active);
            // The first start still saw the old record at activation; from the next start on (the record retired) the
            // saved restriction set, which a changed delivered family build is checked against, has only live owners.
            if (restart == 1)
                Assert.Equal(restricts, ReadRestrictedSourceIds(profile.Path).Contains("lainchan"));
            Assert.Empty(fixture.Requests);
        }
    }

    internal static string[] ReadRestrictedSourceIds(string profile)
    {
        using System.Text.Json.JsonDocument envelope = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(profile, "plugin-state", "state.json")));
        byte[] payload = envelope.RootElement.GetProperty("payload").GetBytesFromBase64();
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payload);
        // A v1 payload is the bare profile and carries no restrictions.
        return document.RootElement.TryGetProperty("restrictedSourceIds", out System.Text.Json.JsonElement restricted)
            ? restricted.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
    }

    internal static PluginProfileSnapshot ReadEnvelope(string file)
    {
        using System.Text.Json.JsonDocument envelope = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(file));
        byte[] payload = envelope.RootElement.GetProperty("payload").GetBytesFromBase64();
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payload);
        // The delivered (v2) envelope wraps the profile in a document; a v1 envelope is the profile itself.
        return document.RootElement.TryGetProperty("Profile", out System.Text.Json.JsonElement inner) || document.RootElement.TryGetProperty("profile", out inner)
            ? SdkJson.Deserialize<PluginProfileSnapshot>(System.Text.Encoding.UTF8.GetBytes(inner.GetRawText()))
            : SdkJson.Deserialize<PluginProfileSnapshot>(payload);
    }

    internal static async Task SeedLegacyAsync(string profile, params (string PluginId, string SourceId, bool Enabled)[] plugins)
    {
        // A store-level stand-in for the 1.0.x delivered single-site bundles (same plugin and source IDs).
        PluginProfileStore store = new(profile, ExperimentalSourceActivationPolicy.InitiallyEnabled);
        SettingsSchema empty = new("1", []);
        PluginCandidateObservation[] candidates = plugins.Select(plugin =>
        {
            SourceDescription source = new(plugin.SourceId, plugin.SourceId, new("https://" + plugin.SourceId + ".invalid/"), SourceCapabilities.ThreadRead,
                [new(SourceResourceKind.Thread, plugin.SourceId + ".invalid", "/{board}/res/{thread}.html", IsCanonical: true)],
                [new(plugin.SourceId + ".invalid", HttpHostPurpose.Metadata)], empty, PostOrderKind.DecimalPostId);
            PluginDescription description = new(plugin.PluginId, plugin.SourceId, "1.0.0", SdkContract.Version, "Legacy single-site plugin", [source], [], empty, []);
            PluginArtifact artifact = new("code-" + plugin.SourceId, "execution-" + plugin.SourceId, "/legacy/" + plugin.SourceId + ".dll", new string('a', 64), "sdk", "compiler", []);
            return new PluginCandidateObservation("legacy:" + plugin.PluginId, plugin.PluginId, artifact, description,
                CandidatePath: "/legacy/MeowChan.Plugin." + plugin.SourceId + ".plugin.json");
        }).ToArray();
        PluginProfileSnapshot state = await store.ApplyStartupAsync(candidates, 0);
        foreach (var plugin in plugins)
            state = await store.SetEnabledAsync(plugin.PluginId, plugin.Enabled, state.Revision);
        Assert.All(state.Plugins, plugin => Assert.NotNull(plugin.Active));
    }

    internal static void Configure(string profile, params SourceMediaIntegrityPermission[] permissions) => new Config
    {
        DownloadPath = Path.Combine(profile, "media"), DownloadsPaused = true, SaveUrlsOnExit = true,
        AutomaticUpdateChecks = false, MaxParallelDownloads = 2, RecheckTimer = 3600, MinimumFreeSpaceGiB = 0,
        SourceMediaIntegrityPermissions = permissions.ToDictionary(permission => SourceMediaIntegrityPermission.CreateKey(permission.PluginOwnerId, permission.SourceId),
            permission => permission, StringComparer.Ordinal)
    }.SaveConfig(profile);

    internal static DesktopSessionHost Create(string profile, DesktopPluginFixtures fixture) => new(profile,
        updateSessionFactory: (path, config) => new UpdateSession(path, config, fixture.Client(), disableEnvironment: () => "1"),
        pluginTransportFactory: fixture.Client);
}
