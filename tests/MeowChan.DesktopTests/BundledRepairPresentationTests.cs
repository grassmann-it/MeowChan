using System.Runtime.CompilerServices;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.DesktopTests;

/// <summary>
/// The "Review bundled repairs" cards (16.11.2026): plugin name instead of the ID, one plain status line, the reason, the
/// action, hashes in the collapsed technical details, and a clear restart note. Presentation only: what may be confirmed
/// stays with the row's own flags and the store's rederivation.
/// </summary>
public sealed class BundledRepairPresentationTests
{
    private const string PluginId = "meowchan.source.kohlchan";
    private const string OldPath = "/opt/meowchan/plugins/kohlchan/manifest.json";
    private const string MovedPath = "/home/user/meowchan-copy/plugins/kohlchan/manifest.json";

    private static BundledCandidateRepair Row(string? reason = null, bool buildChanged = false, string? newPath = MovedPath,
        string? oldPath = OldPath, string? newCandidate = "new-candidate") =>
        new(PluginId, "old-candidate", newCandidate, "slot", oldPath, newPath, "old-exec", "new-exec", buildChanged, reason);

    [Fact]
    public void TheSameBuildFromAnotherFolderWaitsForTheUsersOkAsARelocation()
    {
        BundledCandidateRepair row = Row();
        Assert.True(row.CanConfirm);
        Assert.Equal(BundledRepairStatus.Relocated, BundledRepairPresentation.Status(row));
        Assert.Equal("plugins.repair.status.relocated", BundledRepairPresentation.StatusKey(BundledRepairPresentation.Status(row)));
        Assert.Equal("plugins.repair.sameBuild", BundledRepairPresentation.ReasonKey(row, permissionsReadable: false));
    }

    [Fact]
    public void AChangedBuildSaysWhetherItCameFromAnotherFolder()
    {
        BundledCandidateRepair moved = Row(buildChanged: true);
        Assert.Equal(BundledRepairStatus.UpdatedElsewhere, BundledRepairPresentation.Status(moved));
        Assert.Equal("plugins.repair.changed", BundledRepairPresentation.ReasonKey(moved, permissionsReadable: false));
        // Same folder (an in-place update that still needs the OK) or an unknown path is not reported as a move.
        Assert.Equal(BundledRepairStatus.Updated, BundledRepairPresentation.Status(Row(buildChanged: true, newPath: OldPath)));
        Assert.Equal(BundledRepairStatus.Updated, BundledRepairPresentation.Status(Row(buildChanged: true, newPath: null)));
        Assert.Equal(BundledRepairStatus.Updated, BundledRepairPresentation.Status(Row(buildChanged: true, oldPath: null)));
    }

    [Theory]
    [InlineData(OldPath, MovedPath, true)]
    [InlineData(OldPath, OldPath, false)]
    [InlineData(OldPath, "/opt/meowchan/plugins/kohlchan/other.json", false)]
    [InlineData(null, MovedPath, false)]
    [InlineData(OldPath, null, false)]
    [InlineData("", MovedPath, false)]
    public void AMoveIsOnlyReportedForTwoKnownDifferentFolders(string? oldPath, string? newPath, bool moved) =>
        Assert.Equal(moved, BundledRepairPresentation.MovedFolder(Row(oldPath: oldPath, newPath: newPath)));

    [Fact]
    public void RightsAndChangeReviewsKeepTheirSeparateDecisions()
    {
        BundledCandidateRepair rights = Row("inherited-grants", buildChanged: true) with
        { PermissionReviewJson = "{}", PermissionFingerprint = "fingerprint" };
        Assert.True(rights.CanReauthorize);
        Assert.False(rights.CanConfirm);
        Assert.Equal(BundledRepairStatus.NeedsRights, BundledRepairPresentation.Status(rights));
        Assert.Equal("plugins.repair.reauthorize.ready", BundledRepairPresentation.ReasonKey(rights, permissionsReadable: true));
        // Unreadable rights: the decision is not offered, and the card names the blocking reason instead.
        Assert.Equal("plugins.repair.blocked.inherited-grants", BundledRepairPresentation.ReasonKey(rights, permissionsReadable: false));

        BundledCandidateRepair changes = Row("description-changed", buildChanged: true) with
        { ChangeReviewJson = "{}", ChangeFingerprint = "fingerprint" };
        Assert.True(changes.CanReviewChanges);
        Assert.Equal(BundledRepairStatus.NeedsChangeReview, BundledRepairPresentation.Status(changes));
    }

    [Theory]
    [InlineData("pinned")]
    [InlineData("unproven-history")]
    [InlineData("ambiguous")]
    [InlineData("no-proven-target")]
    [InlineData("pending-choice")]
    [InlineData("source-owner-conflict")]
    [InlineData("inherited-grants")] // without an exact permission preview
    [InlineData("description-changed")] // without an exact change review
    [InlineData("configuration-incompatible")]
    public void ABlockedRowSaysItCannotBeConfirmedAndKeepsItsLocalizedReason(string reason)
    {
        BundledCandidateRepair row = Row(reason, buildChanged: true);
        Assert.False(row.CanConfirm || row.CanReauthorize || row.CanReviewChanges);
        Assert.Equal(BundledRepairStatus.Blocked, BundledRepairPresentation.Status(row));
        string key = BundledRepairPresentation.ReasonKey(row, permissionsReadable: false);
        Assert.Equal("plugins.repair.blocked." + reason, key);
        Assert.True(BuiltInCatalog.English.ContainsKey(key), key);
        Assert.True(BuiltInCatalog.German.ContainsKey(key), key);
    }

    [Fact]
    public void ARowWithoutATargetIsBlocked() =>
        Assert.Equal(BundledRepairStatus.Blocked, BundledRepairPresentation.Status(Row(newCandidate: null)));

    [Fact]
    public void EveryStatusAndReceiptLineHasAnEnglishAndGermanText()
    {
        foreach (BundledRepairStatus status in Enum.GetValues<BundledRepairStatus>())
        {
            string key = BundledRepairPresentation.StatusKey(status);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.English[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.German[key]), key);
        }
        foreach (PluginApprovalState state in Enum.GetValues<PluginApprovalState>())
        {
            string key = BundledRepairPresentation.ReceiptStatusKey(state);
            Assert.True(BuiltInCatalog.English.ContainsKey(key), key);
            Assert.True(BuiltInCatalog.German.ContainsKey(key), key);
        }
        Assert.Equal("plugins.repair.status.confirmed", BundledRepairPresentation.ReceiptStatusKey(PluginApprovalState.Saved));
        Assert.Equal("plugins.approval.state.CheckStore", BundledRepairPresentation.ReceiptStatusKey(PluginApprovalState.CheckStore));
        foreach (string key in new[] { "plugins.repair.intro", "plugins.repair.restartNote", "plugins.repair.reviewOpen", "plugins.repair.pluginId" })
        {
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.English[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(BuiltInCatalog.German[key]), key);
        }
        Assert.Contains("{0}", BuiltInCatalog.English["plugins.repair.pluginId"]);
        Assert.Contains("{0}", BuiltInCatalog.German["plugins.repair.pluginId"]);
    }

    [Fact]
    public void TheRestartNoteFollowsEverySavedOrPossiblySavedApproval()
    {
        Assert.False(BundledRepairPresentation.NeedsRestartNote(Array.Empty<PluginApprovalState>()));
        Assert.False(BundledRepairPresentation.NeedsRestartNote(new[] { PluginApprovalState.NotExecuted, PluginApprovalState.NeedsReview, PluginApprovalState.Failed }));
        Assert.True(BundledRepairPresentation.NeedsRestartNote(new[] { PluginApprovalState.Failed, PluginApprovalState.Saved }));
        Assert.True(BundledRepairPresentation.NeedsRestartNote(new[] { PluginApprovalState.CheckStore }));
    }

    [Fact]
    public void TheCardHeadingIsThePluginsDeclaredNameNotItsId()
    {
        PluginActivationSnapshot plugin = new(PluginId, true, 1, "old-candidate", null, null, Release("Kohlchan (retained)"), null,
            [Candidate("old-candidate", "Kohlchan (old)"), Candidate("new-candidate", "Kohlchan")], null);
        PluginProfileSnapshot profile = new(1, [plugin], []);
        Assert.Equal("Kohlchan", BundledRepairPresentation.DisplayName(PluginId, profile, "new-candidate", "old-candidate"));
        Assert.Equal("Kohlchan (old)", BundledRepairPresentation.DisplayName(PluginId, profile, null, "old-candidate"));
        Assert.Equal("Kohlchan (retained)", BundledRepairPresentation.DisplayName(PluginId, profile, "missing"));
        PluginActivationSnapshot bare = plugin with { LastGood = null, Candidates = [] };
        Assert.Equal(PluginId, BundledRepairPresentation.DisplayName(PluginId, new(1, [bare], []), "new-candidate"));
        Assert.Equal(PluginId, BundledRepairPresentation.DisplayName(PluginId, null, "new-candidate"));
        Assert.Equal("other.plugin", BundledRepairPresentation.DisplayName("other.plugin", profile, "new-candidate"));
    }

    [Fact]
    public void TheDialogShowsCardsAndARestartNoteButNeverActivatesAnything()
    {
        string migration = Read("Desktop/Views/Plugins/PluginsPage.Migration.cs");
        string review = Read("Desktop/Views/Plugins/PluginsPage.ChangeReview.cs");
        // The heading is the display name; the ID moves into the collapsed technical details.
        Assert.DoesNotContain("new TextBlock { Text = row.PluginId", migration);
        Assert.Contains("BundledRepairPresentation.DisplayName(row.PluginId, _catalog?.Profile", migration);
        Assert.Contains("Texts.T(\"plugins.repair.pluginId\", pluginId)", migration);
        Assert.Contains("IsExpanded = false", migration);
        Assert.Contains("\"BundledRepairStatus:\" + row.PluginId", migration);
        Assert.Contains("\"BundledRepairStatus:\" + row.PluginId", review);
        // The exact receipt line (ID, execution, state) is kept, in the receipt card's technical details.
        Assert.Contains("Texts.T(\"plugins.approval.receipt\", choice.PluginId,", migration);
        Assert.Contains("Texts.T(\"plugins.repair.restartNote\")", migration);
        // Confirmation stays bound to the row's own flags; no activation or restart path is used from the dialog.
        Assert.Contains("if (!saving && coherent && approval.MayOffer(row.PluginId) && row.CanConfirm)", migration);
        foreach (string forbidden in new[] { "ApplyBundledStartupAsync", "ApplyStartupAsync", "StartAsync(", "ProfileLauncher", "Process.Start" })
        {
            Assert.DoesNotContain(forbidden, migration);
            Assert.DoesNotContain(forbidden, review);
        }
        // Cancelling the page operation closes the review instead of leaving it open without a live operation.
        Assert.Contains("token.Register(", migration);
        Assert.Contains("approval.Stop();", migration);
    }

    private static PluginDescription Description(string name) =>
        new(PluginId, name, "1.0.0", SdkContract.Version, "Reads a supported source.", [], [], new("1", []), []);

    private static PluginArtifact Artifact(string execution) =>
        new("compile-" + execution, execution, "/fixture/plugin.dll", "assembly-hash", "sdk-hash", "compiler-hash", []);

    private static PluginCandidateSnapshot Candidate(string candidateId, string name) =>
        new(candidateId, PluginId, Artifact(candidateId + "-exec"), Description(name), true, null, "/fixture/" + candidateId + "/manifest.json");

    private static PluginReleaseSnapshot Release(string name) =>
        new("retained-candidate", Artifact("retained-exec"), Description(name),
            new("config", new("1", []), new Dictionary<string, SettingsSnapshot>()));

    private static string Read(string path, [CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../..", path)));
}
