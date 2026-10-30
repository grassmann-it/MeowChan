using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginHost.Activation;
using MeowChan.PluginHost.Contracts;
using MeowChan.PluginSdk;

namespace MeowChan.PluginHost.Configuration.Tests;

public sealed class PreV2BackupTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "meowchan-pre-v2-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private PluginProfileStore Store => new(root);
    private static int Version(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("version").GetInt32();
    }

    [Fact]
    public async Task FirstTransitionPreservesExactEnvelopeAndDigestAcrossTwoRestartsAndRestore()
    {
        var store = Store;
        SettingsSchema schema = new("1", []);
        PluginDescription description = new("legacy.plugin", "Legacy", "1.0", SdkContract.Version, "Legacy fixture", [],
            [new("filter", "Filter", "Fixture", FilterDataAccess.OriginalImage, schema, ["image/png"])], schema, []);
        PluginArtifact artifact = new("compilation", "execution", "/fixture/legacy.dll", new string('a', 64), "sdk", "compiler", []);
        PluginCandidateObservation candidate = new("old-candidate", "legacy.plugin", artifact, description);
        var before = await store.ApplyStartupAsync([candidate], 0);
        // Seed historical v1 state without exercising the new writer's backup policy.
        before = await store.SetEnabledAsync("legacy.plugin", false, before.Revision);
        before = await store.SetGrantAsync("legacy.plugin", "filter", new("source", "board"), FilterDataAccess.OriginalImage, before.Revision);
        File.Delete(store.PreV2BackupPath);
        File.Delete(store.PreV2BackupSha256Path);
        byte[] original = await File.ReadAllBytesAsync(store.StorePath);
        Assert.Equal(1, Version(store.StorePath));
        Assert.False(File.Exists(store.PreV2BackupPath));
        var next = await store.ApplyBundledStartupAsync([], [], ["restricted.source"], before.Revision);
        Assert.Equal(2, Version(store.StorePath));
        Assert.Equal(original, await File.ReadAllBytesAsync(store.PreV2BackupPath));
        string sha = Convert.ToHexString(SHA256.HashData(original)) + "\n";
        Assert.Equal(sha, await File.ReadAllTextAsync(store.PreV2BackupSha256Path));
        for (int restart = 0; restart < 2; restart++)
        {
            store = Store;
            next = await store.ApplyBundledStartupAsync([], [], [], next.Revision);
            Assert.Equal(2, Version(store.StorePath)); // never silently downgrade a v2 envelope
            Assert.Equal(original, await File.ReadAllBytesAsync(store.PreV2BackupPath));
            Assert.Equal(sha, await File.ReadAllTextAsync(store.PreV2BackupSha256Path));
        }
        // Explicit restore into a different private profile: never an automatic recovery path.
        string restored = Path.Combine(root, "restored");
        var oldReader = new PluginProfileStore(restored);
        Directory.CreateDirectory(Path.GetDirectoryName(oldReader.StorePath)!);
        File.Copy(store.PreV2BackupPath, oldReader.StorePath);
        Assert.Equal(1, Version(oldReader.StorePath));
        Assert.Equal(SdkJson.Serialize(before), SdkJson.Serialize(await oldReader.ReadAsync()));
        Assert.Equal(original, File.ReadAllBytes(oldReader.StorePath));
    }

    [Fact]
    public async Task StartupObservationCannotReplaceOriginalBeforeLaterV2Transition()
    {
        var store = Store;
        var state = await store.ApplyStartupAsync([], 0);
        byte[] original = File.ReadAllBytes(store.StorePath);
        state = await store.ApplyStartupAsync([], state.Revision);
        Assert.Equal(1, Version(store.StorePath));
        Assert.Equal(original, File.ReadAllBytes(store.PreV2BackupPath));
        store = Store;
        state = await store.ApplyStartupAsync([], state.Revision);
        await store.ApplyBundledStartupAsync([], [], ["restricted.source"], state.Revision);
        Assert.Equal(2, Version(store.StorePath));
        Assert.Equal(original, File.ReadAllBytes(store.PreV2BackupPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)) + "\n", File.ReadAllText(store.PreV2BackupSha256Path));
    }

    [Fact]
    public async Task AlreadyV2WithoutBackupDoesNotClaimToRetainPreV2State()
    {
        var store = Store;
        var state = await store.ApplyBundledStartupAsync([], [], ["restricted.source"], 0);
        store = Store;
        await store.ApplyStartupAsync([], state.Revision);
        Assert.Equal(2, Version(store.StorePath));
        Assert.False(File.Exists(store.PreV2BackupPath));
        Assert.False(File.Exists(store.PreV2BackupSha256Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBackupBlocksEvenV1StartupObservation(bool digest)
    {
        var store = Store;
        var state = await store.ApplyStartupAsync([], 0);
        byte[] original = File.ReadAllBytes(store.StorePath);
        string blocked = digest ? store.PreV2BackupSha256Path : store.PreV2BackupPath;
        Directory.CreateDirectory(blocked);
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => store.ApplyStartupAsync([], state.Revision));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(store.StorePath));
        Assert.True(Directory.Exists(blocked));
    }

    [Fact]
    public async Task V2EnvelopeWithMatchingDigestCannotMasqueradeAsOriginalV1Backup()
    {
        var store = Store;
        var state = await store.ApplyStartupAsync([], 0);
        byte[] original = File.ReadAllBytes(store.StorePath);
        string otherRoot = Path.Combine(root, "other");
        var other = new PluginProfileStore(otherRoot);
        await other.ApplyBundledStartupAsync([], [], ["restricted.source"], 0);
        byte[] v2 = File.ReadAllBytes(other.StorePath);
        File.WriteAllBytes(store.PreV2BackupPath, v2);
        File.WriteAllText(store.PreV2BackupSha256Path, Convert.ToHexString(SHA256.HashData(v2)) + "\n");
        await Assert.ThrowsAsync<IOException>(() => store.ApplyStartupAsync([], state.Revision));
        Assert.Equal(original, File.ReadAllBytes(store.StorePath));
        Assert.Equal(v2, File.ReadAllBytes(store.PreV2BackupPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingBackupOrDigestBlocksPublicationAndPreservesEvidence(bool digest)
    {
        var store = Store;
        var before = await store.ApplyStartupAsync([], 0);
        byte[] original = File.ReadAllBytes(store.StorePath);
        string conflict = digest ? store.PreV2BackupSha256Path : store.PreV2BackupPath;
        await File.WriteAllTextAsync(conflict, "retain this conflicting evidence");
        await Assert.ThrowsAsync<IOException>(() => store.ApplyBundledStartupAsync([], [], ["restricted.source"], before.Revision));
        Assert.Equal(original, File.ReadAllBytes(store.StorePath));
        Assert.Equal("retain this conflicting evidence", File.ReadAllText(conflict));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.StorePath)!, "*.tmp"));
    }

    [Fact]
    public async Task InterruptedBackupBeforeDigestCanResumeWithoutReplacingBackup()
    {
        var store = Store;
        var before = await store.ApplyStartupAsync([], 0);
        byte[] original = File.ReadAllBytes(store.StorePath);
        File.WriteAllBytes(store.PreV2BackupPath, original);
        await store.ApplyBundledStartupAsync([], [], ["restricted.source"], before.Revision);
        Assert.Equal(original, File.ReadAllBytes(store.PreV2BackupPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)) + "\n", File.ReadAllText(store.PreV2BackupSha256Path));
    }

    [Fact]
    public async Task OversizedBackupIsRejectedWithoutReadingOrReplacingIt()
    {
        var store = Store;
        var before = await store.ApplyStartupAsync([], 0);
        byte[] original = File.ReadAllBytes(store.StorePath);
        using (var file = File.Create(store.PreV2BackupPath)) file.SetLength(24 * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<IOException>(() => store.ApplyBundledStartupAsync([], [], ["restricted.source"], before.Revision));
        Assert.Equal(original, File.ReadAllBytes(store.StorePath));
        Assert.Equal(24 * 1024 * 1024 + 1, new FileInfo(store.PreV2BackupPath).Length);
    }

    [Fact]
    public async Task LinkedBackupCannotBecomeAnAliasOfMutableState()
    {
        if (OperatingSystem.IsWindows()) return; // symlink privilege is not assumed
        var store = Store;
        var before = await store.ApplyStartupAsync([], 0);
        byte[] original = File.ReadAllBytes(store.StorePath);
        File.CreateSymbolicLink(store.PreV2BackupPath, store.StorePath);
        await Assert.ThrowsAsync<IOException>(() => store.ApplyBundledStartupAsync([], [], ["restricted.source"], before.Revision));
        Assert.Equal(original, File.ReadAllBytes(store.StorePath));
        Assert.NotNull(new FileInfo(store.PreV2BackupPath).LinkTarget);
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("{\"version\":99,\"sha256\":\"\",\"payload\":\"\"}")]
    public async Task CorruptOrFutureStateNeverBecomesBackupOrDefaults(string content)
    {
        var store = Store;
        Directory.CreateDirectory(Path.GetDirectoryName(store.StorePath)!);
        File.WriteAllText(store.StorePath, content);
        await Assert.ThrowsAsync<PluginProfileCorruptException>(() => store.ApplyBundledStartupAsync([], [], ["restricted.source"], 0));
        Assert.Equal(content, File.ReadAllText(store.StorePath));
        Assert.False(File.Exists(store.PreV2BackupPath));
    }

    [Fact]
    public async Task CancelledTransitionLeavesOriginalAndNoBackup()
    {
        var store = Store;
        var before = await store.ApplyStartupAsync([], 0);
        byte[] original = File.ReadAllBytes(store.StorePath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ApplyBundledStartupAsync([], [], ["restricted.source"], before.Revision, cancelled.Token));
        Assert.Equal(original, File.ReadAllBytes(store.StorePath));
        Assert.False(File.Exists(store.PreV2BackupPath));
    }

    [Fact]
    public async Task NewV2ProfileDoesNotFabricateAnOldBackup()
    {
        var store = Store;
        await store.ApplyBundledStartupAsync([], [], ["restricted.source"], 0);
        Assert.Equal(2, Version(store.StorePath));
        Assert.False(File.Exists(store.PreV2BackupPath));
        Assert.False(File.Exists(store.PreV2BackupSha256Path));
    }

    [Fact]
    public async Task StaleRevisionDoesNotCreateBackup()
    {
        var store = Store;
        await store.ApplyStartupAsync([], 0);
        await Assert.ThrowsAsync<PluginProfileConflictException>(() => store.ApplyBundledStartupAsync([], [], ["restricted.source"], 0));
        Assert.False(File.Exists(store.PreV2BackupPath));
        Assert.False(File.Exists(store.PreV2BackupSha256Path));
    }
}
