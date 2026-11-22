using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Persistence;
using MeowChan.PluginHost.Taggers;
using MeowChan.PluginSdk;

namespace MeowChan.PluginRuntime.Tests;

public sealed class LocalTaggerDirectoryTests
{
    [Theory]
    [InlineData("profile")]
    [InlineData("store")]
    [InlineData("slot")]
    public async Task SwappedDirectoriesAreNeverUsedForPublicationOrCleanup(string level)
    {
        // On Windows the profile lock can also prevent moving its parent outright.
        if (OperatingSystem.IsWindows()) return;
        string workspace = Directory.CreateTempSubdirectory("meowchan-slot-").FullName;
        try
        {
            string profile = Path.Combine(workspace, "profile");
            using ProfileLease lease = ProfileLease.Acquire(profile);
            string storePath = Path.Combine(profile, "tagger-model");
            string slotPath = Path.Combine(storePath, "b");
            string candidate = Path.Combine(workspace, LocalTaggerModelManifest.FileName);
            byte[] weights = [27];
            LocalTaggerModelManifest manifest = new(LocalTaggerModelManifest.FormatId, 1, "Test publisher", "test", "1",
                weights.Length, Convert.ToHexStringLower(SHA256.HashData(weights)), "MIT", [new("cat", TaggerTagCategory.General)],
                new("input", "scores", "NCHW", 1, 1, "RGB", "stretch", "nearest", null, 1, [0, 0, 0], [1, 1, 1], "identity"));
            await File.WriteAllBytesAsync(candidate, JsonSerializer.SerializeToUtf8Bytes(manifest, LocalTaggerModelManifest.JsonOptions));
            await File.WriteAllBytesAsync(Path.Combine(workspace, LocalTaggerModelManifest.WeightsFileName), weights);
            LocalTaggerModelStore models = new(lease);
            CancellationToken token = CancellationToken.None;
            LocalTaggerModelPreview preview = await models.PreviewAsync(candidate, token);
            await models.ImportAsync(preview, token);
            byte[] active = await File.ReadAllBytesAsync(Path.Combine(storePath, "active"), token);
            string replaced = level switch { "profile" => profile, "store" => storePath, _ => slotPath };
            string moved = Path.Combine(workspace, "moved");
            string replacementWeights = Path.Combine(slotPath, LocalTaggerModelManifest.WeightsFileName);
            string unrelatedPending = Path.Combine(storePath, "active.pending");

            await Assert.ThrowsAnyAsync<IOException>(() => models.ImportAsync(preview, token, beforePublish: () =>
            {
                Directory.Move(replaced, moved);
                Directory.CreateDirectory(slotPath);
                File.WriteAllText(replacementWeights, "keep replacement weights");
                if (level != "slot") File.WriteAllText(unrelatedPending, "keep replacement pointer");
            }));

            Assert.Equal("keep replacement weights", await File.ReadAllTextAsync(replacementWeights, token));
            if (level != "slot")
                Assert.Equal("keep replacement pointer", await File.ReadAllTextAsync(unrelatedPending, token));
            Directory.Delete(replaced, recursive: true);
            Directory.Move(moved, replaced);
            Assert.Equal(active, await File.ReadAllBytesAsync(Path.Combine(storePath, "active"), token));
            Assert.NotNull(await models.VerifyInstalledAsync(token));
        }
        finally { Directory.Delete(workspace, recursive: true); }
    }
}
