using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class NativePrivateConsentDecisionTests
{
    private static readonly ResourceId Thread = new("4chan", "b", "9001");
    private static readonly Guid PackageId = Guid.Parse("6e6c7c17-94a2-4d9d-bf1d-df117d5ad7e2");
    private static readonly DateTimeOffset At = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Media = "synthetic-consent-media"u8.ToArray();
    private const string Note = "private test note";

    [Fact]
    public async Task PublicOnlyIsDefaultOffEvenWhenPackageCarriesPrivateComponent()
    {
        NativeTransferPackage package = await PackageAsync(withPrivate: true);
        NativePrivateConsentProvenance provenance = NativePrivateConsentProvenance.FromPackage(package);

        NativePrivateConsentDecision decision = NativePrivateConsentDecision.Create(package, provenance);

        Assert.True(decision.IsPublicOnly);
        Assert.False(decision.HasExplicitPrivateConsent);
        Assert.Equal(default, decision.Selection);
        Assert.True(decision.PackageHasPrivateMetadata);
        Assert.Equal(64, decision.DecisionIdentity.Length);
        Assert.Equal(64, decision.RequestIdentity.Length);
    }

    [Fact]
    public async Task ExplicitConsentRequiresNonEmptyAvailableSelection()
    {
        NativeTransferPackage package = await PackageAsync(withPrivate: true);
        NativePrivateConsentProvenance provenance = NativePrivateConsentProvenance.FromPackage(package);

        NativePrivateConsentDecision decision = NativePrivateConsentDecision.Create(package, provenance,
            NativePrivateConsentScope.ExplicitPrivateMetadata,
            new NativePrivateMetadataSelection(ThreadFavorites: true));

        Assert.True(decision.HasExplicitPrivateConsent);
        Assert.Equal(new NativePrivateMetadataSelection(ThreadFavorites: true), decision.Selection);
        Assert.NotEqual(decision.DecisionIdentity, decision.RequestIdentity);
    }

    [Fact]
    public async Task ExplicitConsentRejectsUnavailableCategoryAndPublicSelectionRejectsPrivateBits()
    {
        NativeTransferPackage package = await PackageAsync(withPrivate: true);
        NativePrivateConsentProvenance provenance = NativePrivateConsentProvenance.FromPackage(package);

        Assert.Throws<InvalidDataException>(() => NativePrivateConsentDecision.Create(package, provenance,
            NativePrivateConsentScope.ExplicitPrivateMetadata,
            new NativePrivateMetadataSelection(MediaFavorites: true)));
        Assert.Throws<InvalidDataException>(() => NativePrivateConsentDecision.Create(package, provenance,
            NativePrivateConsentScope.PublicOnly,
            new NativePrivateMetadataSelection(ThreadFavorites: true)));
    }

    [Fact]
    public async Task PublicPackageCannotReceivePrivateConsent()
    {
        NativeTransferPackage package = await PackageAsync(withPrivate: false);
        NativePrivateConsentProvenance provenance = NativePrivateConsentProvenance.FromPackage(package);

        Assert.Throws<InvalidDataException>(() => NativePrivateConsentDecision.Create(package, provenance,
            NativePrivateConsentScope.ExplicitPrivateMetadata,
            new NativePrivateMetadataSelection(ThreadFavorites: true)));
    }

    [Fact]
    public async Task ProvenanceMustMatchExactPackageDescriptors()
    {
        NativeTransferPackage package = await PackageAsync(withPrivate: true);
        NativePrivateConsentProvenance provenance = NativePrivateConsentProvenance.FromPackage(package);
        NativePrivateConsentProvenance altered = provenance with { CatalogSha256 = new string('a', 64) };

        Assert.Throws<InvalidDataException>(() => NativePrivateConsentDecision.Create(package, altered));
        Assert.Throws<InvalidDataException>(() => NativePrivateConsentDecision.Create(package, provenance with { ContentSchema = null! }));
    }

    [Fact]
    public async Task IdentityIsDeterministicAndChangesWithScopeSelectionOrProvenance()
    {
        NativeTransferPackage package = await PackageAsync(withPrivate: true);
        NativePrivateConsentProvenance provenance = NativePrivateConsentProvenance.FromPackage(package);
        NativePrivateConsentDecision first = NativePrivateConsentDecision.Create(package, provenance,
            NativePrivateConsentScope.ExplicitPrivateMetadata,
            new NativePrivateMetadataSelection(ThreadFavorites: true));
        NativePrivateConsentDecision second = NativePrivateConsentDecision.Create(package, provenance,
            NativePrivateConsentScope.ExplicitPrivateMetadata,
            new NativePrivateMetadataSelection(ThreadFavorites: true));
        NativePrivateConsentDecision otherSelection = NativePrivateConsentDecision.Create(package, provenance,
            NativePrivateConsentScope.ExplicitPrivateMetadata,
            new NativePrivateMetadataSelection(PostBookmarksAndNotes: true));

        Assert.Equal(first.DecisionIdentity, second.DecisionIdentity);
        Assert.Equal(first.RequestIdentity, second.RequestIdentity);
        Assert.NotEqual(first.DecisionIdentity, otherSelection.DecisionIdentity);
        Assert.NotEqual(first.RequestIdentity, otherSelection.RequestIdentity);
    }

    [Fact]
    public async Task DecisionDoesNotExposePrivatePayloadValues()
    {
        NativeTransferPackage package = await PackageAsync(withPrivate: true);
        NativePrivateConsentDecision decision = NativePrivateConsentDecision.Create(package,
            NativePrivateConsentProvenance.FromPackage(package));
        string text = decision.ToString() + decision.Provenance.ToString();

        Assert.DoesNotContain(Note, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(Media), text, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<NativeTransferPackage> PackageAsync(bool withPrivate)
    {
        CapturedAttachment attachment = new(0, "9001001", "original",
            "https://i.4cdn.org/b/9001001.jpg", "synthetic.jpg");
        CapturedPost post = new("1", 1, PostFieldPresence.All, At, "", null,
            "public post", false, [attachment]);
        NativeTransferCatalog catalog = new(At, [new(Thread, "https://boards.4chan.org/b/thread/9001", "synthetic",
            ThreadSourceState.NotFound, At, At, 1, true,
            [new(post, [new(attachment, TransferMediaRole.Original,
                TransferContentStatus.Contained, Hash(Media), null)])])]);
        TransferPayloadSource source = new(new(Media.Length, Hash(Media)),
            _ => Task.FromResult<Stream>(new MemoryStream(Media, writable: false)));
        using MemoryStream output = new();
        NativePrivateMetadataProjection? privateMetadata = withPrivate
            ? new NativePrivateMetadataProjection([Thread], new(ThreadFavorites: true, PostBookmarksAndNotes: true), [Thread], [], [new PostBookmark(new(Thread, "1"), Note)])
            : null;
        TransferWriteResult written = await NativeTransferPackageWriter.WriteAsync(output, catalog, [source], PackageId,
            privateMetadata: privateMetadata);
        Assert.True(written.IsComplete, written.ToString());
        output.Position = 0;
        TransferPackageInspection inspected = await NativeTransferPackageValidator.InspectAsync(output);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        return inspected.Package!;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
