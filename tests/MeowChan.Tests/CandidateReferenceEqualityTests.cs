using MeowChan.Archive;
using MeowChan.Persistence;
using MeowChan.Rules;
using MeowChan.Storage;

namespace MeowChan.Tests;

public sealed class CandidateReferenceEqualityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilterDecisionSurvivesReferenceRoundTripAndRejectsChangedReferences(bool hasReferences)
    {
        using TempDirectory temp = new();
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(temp.Path, lease);
        MediaRuleStore rules = new(temp.Path, lease);
        await rules.LoadAsync();
        ResourceId resource = new("source", "board", "thread");
        string mediaDirectory = Path.Combine(temp.Path, "media");
        await catalog.RegisterThreadAsync(new(resource, "https://source.test/thread", "title", mediaDirectory, false));
        MediaProviderMetadata provider = new("media", "original", "https://source.test/media", null, 3, null, null);
        CandidateMetadata metadata = new(new(resource, "media"), new(provider.Url), Path.Combine(mediaDirectory, "media.bin"), 3)
            { AcquisitionRevisionKey = MediaRevisionKeys.CreateAcquisitionKey(resource, provider) };
        if (hasReferences)
        {
            List<AttachmentRevisionReference> supplied = [Reference("post")];
            metadata = metadata with { AttachmentReferences = supplied };
            supplied.Clear();
            Assert.Single(metadata.AttachmentReferences);
        }

        CandidateDecision expected = await catalog.GetOrCreateDecisionAsync(metadata, rules);
        CandidateDecision restored = Assert.Single(await catalog.GetDecisionsAsync(resource));
        Assert.Equal(expected.Metadata, restored.Metadata);
        Assert.Equal(expected.Metadata.GetHashCode(), restored.Metadata.GetHashCode());
        CandidateDecision? omitted = await catalog.RecordFilterOmissionAsync(expected, false, "filter unavailable", default);
        Assert.NotNull(omitted);
        Assert.Equal(MediaSelectionOutcome.TechnicalOmission, omitted.Selection.Outcome);
        Assert.Equal(MediaSelectionReason.FilterUnavailable, omitted.Selection.Reason);

        CandidateDecision stale = expected with { Metadata = expected.Metadata with { AttachmentReferences = [Reference("other-post")] } };
        Assert.Null(await catalog.RecordFilterOmissionAsync(stale, true, "must not replace current selection", default));
        CandidateDecision retained = Assert.Single(await catalog.GetDecisionsAsync(resource));
        Assert.Equal(omitted.Selection.Outcome, retained.Selection.Outcome);
        Assert.Equal(omitted.Selection.Reason, retained.Selection.Reason);
        Assert.Equal(omitted.Selection.FilterReason, retained.Selection.FilterReason);
        Assert.Equal(expected.Metadata, retained.Metadata);

        AttachmentRevisionReference Reference(string post) => new(resource, post, 0,
            MediaRevisionKeys.CreateAttachmentKey(resource, post, 0, provider));
    }
}
