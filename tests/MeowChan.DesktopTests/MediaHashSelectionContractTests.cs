using MeowChan;
using MeowChan.Storage;
using Op = MeowChan.MediaHashSearchOperation;

namespace MeowChan.DesktopTests;

// Operation contract, not native modal/navigation or present-day file verification evidence.
public sealed class MediaHashSelectionContractTests
{
    private static readonly string Hash = new('b', 64);

    [Fact]
    public async Task EqualHashSelectionsPreserveExactAssetsAndAllCapturedAttachmentBindings()
    {
        var (snapshot, assets, bindings) = Fixture();
        MediaAssetReference[] before = snapshot.GetPage().ToArray();
        var operation = new Op(snapshot); // Real production query, no injected result.
        try
        {
            await operation.SearchAsync(Hash.ToUpperInvariant());
            var view = operation.Current;
            Assert.Equal(Op.Status.Available, view.Status);
            Assert.Equal("captured-not-live", view.Generation);
            Assert.Equal(assets.Length, view.Count);
            for (int index = 0; index < view.Page.Length; index++)
            {
                MediaAssetReference selected = Assert.IsType<MediaAssetReference>(operation.Select(view.Request, view.Offset, index));
                Assert.Same(before[index], selected);
                Assert.Same(selected, snapshot.FindById(selected.AssetId));
                // Association stays in the supplied snapshot, not invented on the returned asset.
                MediaAttachmentBinding[] associations = snapshot.AttachmentBindings.Values
                    .Where(binding => binding.AssetId == selected.AssetId).ToArray();
                Assert.NotEmpty(associations);
                foreach (MediaAttachmentBinding binding in associations)
                {
                    Assert.Same(bindings.Single(expected => expected.Reference.RevisionKey == binding.Reference.RevisionKey), binding);
                    Assert.Same(binding, snapshot.FindAttachmentBinding(binding.Reference.RevisionKey));
                    Assert.Equal(selected.MediaIdentifier, binding.Provider.MediaId);
                    Assert.Equal(selected.Variant, binding.Provider.Variant);
                    Assert.Equal(Hash, binding.Actual.Sha256);
                }
            }
            Assert.Collection(bindings,
                binding => Assert.Equal((new ResourceId("fixture", "board", "100"), "101", 0, "original"),
                    (binding.Reference.Resource, binding.Reference.PostId, binding.Reference.Position, binding.Provider.Variant)),
                binding => Assert.Equal((new ResourceId("fixture", "board", "200"), "201", 2, "original"),
                    (binding.Reference.Resource, binding.Reference.PostId, binding.Reference.Position, binding.Provider.Variant)),
                binding => Assert.Equal((new ResourceId("fixture", "board", "100"), "102", 3, "preview"),
                    (binding.Reference.Resource, binding.Reference.PostId, binding.Reference.Position, binding.Provider.Variant)));
            Assert.Equal(2, bindings.Count(binding => binding.AssetId == assets[0].AssetId));
            Assert.Equal(2, bindings.Select(binding => binding.Reference.Resource).Distinct().Count());
            Assert.Equal(3, bindings.Select(binding => binding.Reference.PostId).Distinct().Count());
            Assert.Contains(view.Page, asset => !asset.IsCurrent && asset.Variant == "preview");
            AssertCapturedUnchanged(snapshot, before, bindings);
        }
        finally { await operation.TerminateAsync(); }
        AssertCapturedUnchanged(snapshot, before, bindings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelOrCloseRevokesSelectionWithoutChangingCapturedMeasurementOrAssociation(bool close)
    {
        var (snapshot, _, bindings) = Fixture();
        MediaAssetReference[] before = snapshot.GetPage().ToArray();
        using var parent = new CancellationTokenSource();
        var operation = new Op(snapshot, parent.Token);
        try
        {
            await operation.SearchAsync(Hash);
            var published = operation.Current;
            Assert.Equal(Op.Status.Available, published.Status);
            Assert.Same(before[1], operation.Select(published.Request, published.Offset, 1));
            if (close) await operation.TerminateAsync();
            else parent.Cancel();
            Assert.Equal(close ? Op.Status.Closed : Op.Status.Canceled, operation.Current.Status);
            Assert.Empty(operation.Current.Page);
            for (int index = 0; index < published.Page.Length; index++)
                Assert.Null(operation.Select(published.Request, published.Offset, index));
            Assert.Null(operation.Select(operation.Current.Request, operation.Current.Offset, 0));
            AssertCapturedUnchanged(snapshot, before, bindings);
        }
        finally { await operation.TerminateAsync(); }
        AssertCapturedUnchanged(snapshot, before, bindings);
    }

    private static void AssertCapturedUnchanged(MediaAssetSnapshot snapshot, MediaAssetReference[] before,
        MediaAttachmentBinding[] bindings)
    {
        Assert.Equal("captured-not-live", snapshot.Generation);
        Assert.Equal(MediaManifestStatus.Available, snapshot.Status);
        Assert.Equal(before.Length, snapshot.Count);
        Assert.Equal(bindings.Length, snapshot.AttachmentBindings.Count);
        var after = snapshot.GetPage();
        for (int index = 0; index < before.Length; index++)
        {
            Assert.Same(before[index], after[index]);
            Assert.Equal(Hash, after[index].Sha256);
            Assert.Equal(17, after[index].Length);
            Assert.Equal(index == 0, after[index].IsCurrent);
        }
        foreach (var binding in bindings)
            Assert.Same(binding, snapshot.FindAttachmentBinding(binding.Reference.RevisionKey));
    }

    private static (MediaAssetSnapshot Snapshot, MediaAssetReference[] Assets, MediaAttachmentBinding[] Bindings) Fixture()
    {
        // Inert strings only; no temp directory, File.Exists, reader, verifier or acquisition.
        const string directory = "/synthetic-hash-contract/not-opened";
        string md5 = Convert.ToBase64String(new byte[16]);
        var actual = new MediaActualIntegrity(17, md5, Hash);
        var assets = new[]
        {
            new MediaAssetReference(new string('1', 64), "media-a", "original", "same.bin", directory + "/a",
                17, Hash, "https://offline.invalid/a", "same.bin", true) { Md5 = md5 },
            new MediaAssetReference(new string('2', 64), "media-b", "preview", "same.bin", directory + "/b",
                17, Hash, "https://offline.invalid/b", "same.bin", false) { Md5 = md5 }
        };
        var firstThread = new ResourceId("fixture", "board", "100");
        var otherThread = new ResourceId("fixture", "board", "200");
        MediaAttachmentBinding Bind(MediaAssetReference asset, ResourceId resource, string post, int position)
        {
            var provider = new MediaProviderMetadata(asset.MediaIdentifier, asset.Variant, asset.SourceUrl,
                asset.OriginalFileName, 17, md5, Hash);
            string revision = MediaRevisionKeys.CreateAttachmentKey(resource, post, position, provider);
            return new(new(resource, post, position, revision), MediaRevisionKeys.CreateAcquisitionKey(resource, provider),
                asset.AssetId, provider, actual, new(MediaAcceptanceKind.Strict, false, false,
                    DateTimeOffset.UnixEpoch, "fixture", "owner", resource.Source, 1));
        }
        var bindings = new[] { Bind(assets[0], firstThread, "101", 0),
            Bind(assets[0], otherThread, "201", 2), Bind(assets[1], firstThread, "102", 3) };
        var snapshot = new MediaAssetSnapshot(directory, MediaManifestStatus.Available, 4, "captured-not-live", assets,
            attachmentBindings: bindings.ToDictionary(binding => binding.Reference.RevisionKey, StringComparer.Ordinal));
        return (snapshot, assets, bindings);
    }
}
