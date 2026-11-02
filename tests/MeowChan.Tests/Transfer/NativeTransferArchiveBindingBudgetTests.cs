using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Tests.GoalFixtures;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Archive integration, not a parser/resolver double. Only disposable synthetic profiles.
public sealed class NativeTransferArchiveBindingBudgetTests
{
    private const int BindingLimit = 100_000;
    private const int PerRoot = 20_000;
    private const int ManifestByteLimit = 32 * 1024 * 1024;
    private static readonly ResourceId Selected = new("4chan", "b", "7001");
    private static readonly ResourceId Foreign = new("4chan", "b", "9001");
    private static readonly DateTimeOffset At = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("b5364880-77eb-499b-a46c-f62468ca8b65");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestVisitGateCountsForeignEntriesAndCrossRootDuplicatesBeforeFiltering(bool duplicateForeignKeys)
    {
        using BudgetProfile profile = await BudgetProfile.CreateAsync();
        string[] roots = await profile.RegisterRootsAsync(5);
        int offset = 0;
        for (int i = 0; i < roots.Length; i++)
        {
            // One selected CURRENT binding, 99,999 foreign entries. A dictionary cannot
            // contain duplicate keys, so duplicate mode repeats valid keys ACROSS roots.
            int foreignCount = PerRoot - (i == 0 ? 1 : 0);
            await profile.WriteManifestAsync(roots[i], Foreign, duplicateForeignKeys ? 0 : offset,
                foreignCount, includeCurrent: i == 0);
            offset += foreignCount;
        }
        Inventory boundary = await profile.ReadInventoryAsync(roots);
        Assert.Equal(BindingLimit, boundary.Visits);
        Assert.Equal(1, boundary.SelectedEntries);
        Assert.Equal(1, boundary.DistinctSelectedKeys);
        Assert.Equal(duplicateForeignKeys ? PerRoot : BindingLimit - 1, boundary.DistinctForeignKeys);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        await AssertValidCurrentMetadataAsync(reader, roots);
        // Real bytes are valid here. All selected observations resolve, but the
        // other roots' foreign-only origins MUST remain unrepresented. In particular
        // exactly 100,000 visits must not be rejected by a >= count-gate off-by-one.
        TransferArchiveFreezeResult allowedCount = await NativeTransferArchiveExporter.FreezeAsync(reader, Selected, At, PackageId);
        AssertRejected(allowedCount, TransferArchiveError.UnrepresentedAssets, profile);
        await profile.AssertManifestHashesAsync(roots, boundary.ManifestHashes);
        Assert.Equal(profile.Bytes, await File.ReadAllBytesAsync(profile.AssetPath(roots[0])));

        // Change only the last root's binding inventory by ONE foreign entry. In
        // duplicate mode all its existing foreign keys also occur in earlier roots.
        int lastStart = duplicateForeignKeys ? 0 : (BindingLimit - 1) - PerRoot;
        await profile.WriteManifestAsync(roots[^1], Foreign, lastStart, PerRoot + 1, includeCurrent: false);
        // Poison a selected current original in the FIRST root: verification before
        // the global visit reservation would fail InvalidArchive, not SelectionLimit.
        await profile.PoisonAssetsAsync([roots[0]]);
        Inventory excess = await profile.ReadInventoryAsync(roots);
        Assert.Equal(BindingLimit + 1, excess.Visits);
        Assert.Equal(1, excess.SelectedEntries);
        Assert.Equal(1, excess.DistinctSelectedKeys);
        Assert.Equal(duplicateForeignKeys ? PerRoot + 1 : BindingLimit, excess.DistinctForeignKeys);

        TransferArchiveFreezeResult rejected = await NativeTransferArchiveExporter.FreezeAsync(reader, Selected, At, PackageId);
        AssertRejected(rejected, TransferArchiveError.SelectionLimit, profile);
        await profile.AssertManifestHashesAsync(roots, excess.ManifestHashes);
        await profile.AssertPoisonedAsync([roots[0]]);
        await AssertValidCurrentMetadataAsync(reader, roots);
    }

    [Fact]
    public async Task OneCurrentPlus100000DistinctRetainedBindingsHitsCombinedGateWithinVisitBudget()
    {
        using BudgetProfile profile = await BudgetProfile.CreateAsync();
        string[] roots = await profile.RegisterRootsAsync(5);
        for (int i = 0; i < roots.Length; i++)
            await profile.WriteManifestAsync(roots[i], Selected, i * PerRoot, PerRoot, includeCurrent: false);
        await profile.PoisonAssetsAsync(roots);
        Inventory inventory = await profile.ReadInventoryAsync(roots);
        Assert.Equal(BindingLimit, inventory.Visits); // visit gate cannot explain SelectionLimit
        Assert.Equal(BindingLimit, inventory.SelectedEntries);
        Assert.Equal(BindingLimit, inventory.DistinctSelectedKeys);
        Assert.Equal(0, inventory.DistinctForeignKeys);

        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        await AssertValidCurrentMetadataAsync(reader, roots);
        ArchivedThreadView view = (await reader.ReadThreadAsync(Selected))!;
        string currentKey = view.Posts.Single().Attachments!.Single().AttachmentRevisionKey!;
        Assert.Equal(profile.Current.Reference.RevisionKey, currentKey);
        // The current observation is valid but deliberately not in any manifest.
        // Hence every selected manifest key is an ADDITIONAL retained occurrence,
        // not a duplicate/current suppression case. This uses one SQL attachment,
        // not 100,000 capture rows or legacy per-asset resolver scans.
        Assert.Equal(0, inventory.CurrentKeyEntries);
        Assert.Equal(BindingLimit + 1, 1 + inventory.DistinctSelectedKeys);

        TransferArchiveFreezeResult rejected = await NativeTransferArchiveExporter.FreezeAsync(reader, Selected, At, PackageId);
        // Removing/delaying the combined check reaches the separate 16 MiB catalog
        // serializer cap (InvalidArchive), not this typed SelectionLimit. No large
        // catalog is serialized by the intended path, and no poisoned bytes opened.
        AssertRejected(rejected, TransferArchiveError.SelectionLimit, profile);
        await profile.AssertManifestHashesAsync(roots, inventory.ManifestHashes);
        await profile.AssertPoisonedAsync(roots);
    }

    [Fact]
    public async Task SmallValidCurrentAndRetainedControlFreezesWritesAndReinspectsRealArchive()
    {
        using BudgetProfile profile = await BudgetProfile.CreateAsync();
        string[] roots = await profile.RegisterRootsAsync(1);
        await profile.WriteManifestAsync(roots[0], Selected, 0, 1, includeCurrent: true);
        Inventory inventory = await profile.ReadInventoryAsync(roots);
        Assert.Equal(2, inventory.Visits);
        Assert.Equal(2, inventory.DistinctSelectedKeys);
        await using ArchiveReader reader = await ArchiveReader.OpenAsync(profile.Path);
        TransferArchiveFreezeResult frozen = await NativeTransferArchiveExporter.FreezeAsync(reader, Selected, At, PackageId);
        Assert.True(frozen.IsFrozen, frozen.Error.ToString());
        using MemoryStream stage = new();
        TransferArchiveWriteResult written = await NativeTransferArchiveExporter.WriteAsync(frozen.Snapshot!, stage);
        Assert.True(written.IsComplete, written.Error + "/" + written.WriteError);
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(stage);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        NativeTransferPackage package = inspected.Package!;
        Assert.Equal("chan-v2", package.Envelope.ContentSchema);
        TransferThread thread = Assert.Single(package.Catalog.Threads);
        TransferPost post = Assert.Single(thread.Posts);
        Assert.Equal("7001", post.Metadata.PostId);
        Assert.Equal(TransferContentStatus.Contained, Assert.Single(post.Attachments).Status);
        TransferRetainedBinding retained = Assert.Single(thread.RetainedBindings);
        Assert.Equal("7999", retained.PostId); // not a fabricated captured post
        Assert.Equal(TransferContentStatus.Contained, retained.Attachment.Status);
        Assert.Equal("strict", retained.Attachment.History!.Kind);
        Assert.Equal(profile.Current.Acceptance.AcceptedAtUtc, retained.Attachment.History.AcceptedAtUtc);
        Assert.Equal(profile.Binding(Selected, 0).Reference.RevisionKey, retained.Attachment.Metadata.AttachmentRevisionKey);
        Assert.Single(package.Envelope.Payloads); // shared real asset, independent binding keys
        await profile.AssertManifestHashesAsync(roots, inventory.ManifestHashes);
        Assert.Equal(profile.Bytes, await File.ReadAllBytesAsync(profile.AssetPath(roots[0])));

        // Validate the tripwire on the same admitted small current+retained fixture:
        // unchanged-length corruption is not a missing-file/unavailable success.
        await profile.PoisonAssetsAsync(roots);
        TransferArchiveFreezeResult poisoned = await NativeTransferArchiveExporter.FreezeAsync(reader, Selected, At, PackageId);
        AssertRejected(poisoned, TransferArchiveError.InvalidArchive, profile);
        await profile.AssertManifestHashesAsync(roots, inventory.ManifestHashes);
        await profile.AssertPoisonedAsync(roots);
    }

    private static async Task AssertValidCurrentMetadataAsync(ArchiveReader reader, string[] roots)
    {
        ArchivedThreadView view = (await reader.ReadThreadAsync(Selected))!;
        Assert.Equal(roots, view.MediaDirectories);
        Assert.InRange(view.MediaDirectories.Count, 1, 32);
        Assert.Single(view.Posts);
        Assert.Single(view.Posts.Single().Attachments!);
        TransferArchiveFreezeResult metadata = await NativeTransferArchiveExporter.FreezeAsync(
            reader, Selected, At, PackageId, includeOriginals: false);
        Assert.True(metadata.IsFrozen, metadata.Error.ToString());
        TransferThread thread = Assert.Single(metadata.Snapshot!.Catalog.Threads);
        Assert.Equal(TransferContentStatus.Omitted, Assert.Single(Assert.Single(thread.Posts).Attachments).Status);
        Assert.Empty(thread.RetainedBindings);
        Assert.Empty(metadata.Snapshot.Payloads);
    }

    private static void AssertRejected(TransferArchiveFreezeResult result, TransferArchiveError expected, BudgetProfile profile)
    {
        Assert.Equal(expected, result.Error);
        Assert.False(result.IsFrozen);
        Assert.Null(result.Snapshot);
        Assert.DoesNotContain(profile.Path, result.ToString());
    }

    private sealed record Inventory(int Visits, int SelectedEntries, int DistinctSelectedKeys,
        int DistinctForeignKeys, int CurrentKeyEntries, string[] ManifestHashes);

    private sealed class BudgetProfile : IDisposable
    {
        private const string ManifestName = ".meowchan-manifest.json";
        private readonly TempDirectory _profile = new();
        private readonly JsonElement _asset;
        private readonly JsonElement _entry;
        private readonly string _requestedFileName;
        private readonly CapturedAttachment _attachment;
        private readonly byte[] _poison;
        public string Path => _profile.Path;
        public byte[] Bytes { get; }
        public MediaAttachmentBinding Current { get; }

        private BudgetProfile(MediaAttachmentBinding current, CapturedAttachment attachment,
            byte[] bytes, JsonElement asset)
        {
            Current = current;
            _attachment = attachment with { PlannedPath = null };
            Bytes = bytes;
            _poison = bytes.ToArray();
            _poison[0] ^= 0xff; // same length; must fail exact SHA-256 verification
            _asset = asset.Clone();
            _entry = asset.GetProperty("Entry").Clone();
            _requestedFileName = asset.GetProperty("RequestedFileName").GetString()!;
        }

        public static async Task<BudgetProfile> CreateAsync()
        {
            // Reuse a genuine MediaStore commit as the immutable asset/evidence seed.
            // Large inventories are streamed as synthetic wire manifests, then admitted
            // by the real MediaAssetReader before ArchiveReader -> FreezeAsync is called.
            await using NativeTransferArchiveFixture seed = await NativeTransferArchiveFixture.CreateAsync();
            using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(
                System.IO.Path.Combine(seed.MediaDirectory, ManifestName)));
            Assert.Equal(0, seed.UnexpectedProviderCalls);
            return new(seed.StrictBinding, seed.StrictAttachment, seed.StrictBytes.ToArray(),
                manifest.RootElement.GetProperty("Assets").GetProperty(seed.StrictAssetId));
        }

        public string AssetPath(string root) => System.IO.Path.Combine(root, _entry.GetProperty("FileName").GetString()!);

        public async Task<string[]> RegisterRootsAsync(int count)
        {
            Assert.InRange(count, 1, 32);
            string[] roots = Enumerable.Range(0, count)
                .Select(i => System.IO.Path.Combine(Path, "media-" + i.ToString("D2", System.Globalization.CultureInfo.InvariantCulture))).ToArray();
            using ProfileLease lease = ProfileLease.Acquire(Path);
            await using ArchiveCatalog archive = await ArchiveCatalog.OpenAsync(Path, lease);
            foreach (string root in roots)
            {
                Directory.CreateDirectory(root);
                await File.WriteAllBytesAsync(AssetPath(root), Bytes);
                await archive.CapturePreservingRevisionsAsync(new(Selected, NativeTransferArchiveFixture.ThreadUrl,
                    "Synthetic retained budget", ThreadSourceState.NotFound, At, root,
                    [new CapturedPost("7001", 0, PostFieldPresence.Attachments, Attachments: [_attachment])]));
            }
            return roots;
        }

        public MediaAttachmentBinding Binding(ResourceId resource, int position)
        {
            const string post = "7999";
            string key = MediaRevisionKeys.CreateAttachmentKey(resource, post, position, Current.Provider);
            return Current with
            {
                Reference = new(resource, post, position, key),
                AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(resource, Current.Provider)
            };
        }

        public async Task WriteManifestAsync(string root, ResourceId resource, int start, int count, bool includeCurrent)
        {
            string path = System.IO.Path.Combine(root, ManifestName);
            // Do not construct a giant JsonNode graph, repeatedly commit a growing
            // manifest, or forge MediaAssetSnapshot. O(N) fixture construction.
            await using (FileStream file = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using Utf8JsonWriter writer = new(file);
                writer.WriteStartObject(); writer.WriteNumber("Version", 4);
                writer.WriteStartObject("Entries"); writer.WritePropertyName(_requestedFileName); _entry.WriteTo(writer); writer.WriteEndObject();
                writer.WriteStartObject("Assets"); writer.WritePropertyName(Current.AssetId); _asset.WriteTo(writer); writer.WriteEndObject();
                writer.WriteStartObject("AttachmentBindings");
                if (includeCurrent) WriteBinding(writer, Current);
                for (int i = start; i < start + count; i++) WriteBinding(writer, Binding(resource, i));
                writer.WriteEndObject(); writer.WriteEndObject();
                await writer.FlushAsync();
                Assert.InRange(file.Length, 1L, (long)ManifestByteLimit);
            }
        }

        private static void WriteBinding(Utf8JsonWriter writer, MediaAttachmentBinding binding)
        {
            writer.WritePropertyName(binding.Reference.RevisionKey);
            JsonSerializer.Serialize(writer, binding);
        }

        public async Task<Inventory> ReadInventoryAsync(string[] roots)
        {
            int visits = 0, selectedEntries = 0, currentKeyEntries = 0;
            HashSet<string> selected = new(StringComparer.Ordinal), foreign = new(StringComparer.Ordinal);
            string[] hashes = new string[roots.Length];
            for (int i = 0; i < roots.Length; i++)
            {
                string path = System.IO.Path.Combine(roots[i], ManifestName);
                Assert.InRange(new FileInfo(path).Length, 1L, (long)ManifestByteLimit);
                MediaAssetSnapshot snapshot = await MediaAssetReader.OpenAsync(roots[i]);
                Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
                Assert.Equal(4, snapshot.ManifestVersion);
                Assert.Equal(1, snapshot.Count); // never the asset-count gate
                hashes[i] = snapshot.Generation!;
                visits += snapshot.AttachmentBindings.Count;
                foreach (MediaAttachmentBinding binding in snapshot.AttachmentBindings.Values)
                {
                    Assert.Equal(Current.AssetId, binding.AssetId);
                    if (binding.Reference.RevisionKey == Current.Reference.RevisionKey) currentKeyEntries++;
                    if (binding.Reference.Resource == Selected)
                    {
                        selectedEntries++;
                        selected.Add(binding.Reference.RevisionKey);
                    }
                    else
                    {
                        Assert.Equal(Foreign, binding.Reference.Resource);
                        foreign.Add(binding.Reference.RevisionKey);
                    }
                }
            }
            return new(visits, selectedEntries, selected.Count, foreign.Count, currentKeyEntries, hashes);
        }

        public async Task AssertManifestHashesAsync(string[] roots, string[] hashes)
        {
            for (int i = 0; i < roots.Length; i++)
            {
                await using FileStream file = File.OpenRead(System.IO.Path.Combine(roots[i], ManifestName));
                Assert.Equal(hashes[i], Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant());
            }
        }

        public async Task PoisonAssetsAsync(string[] roots)
        {
            foreach (string root in roots) await File.WriteAllBytesAsync(AssetPath(root), _poison);
        }

        public async Task AssertPoisonedAsync(string[] roots)
        {
            foreach (string root in roots) Assert.Equal(_poison, await File.ReadAllBytesAsync(AssetPath(root)));
        }

        public void Dispose() => _profile.Dispose();
    }
}
