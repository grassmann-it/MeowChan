using System.Text.Json;
using MeowChan.Archive;

namespace MeowChan.Tests;

public sealed class ArchiveTagFilterOptionsTests
{
    private static Guid Id(int index) => new(index, 0, 0, new byte[8]);
    private static Guid[] Ids(int count, int offset = 0) => Enumerable.Range(1 + offset, count).Select(Id).ToArray();

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(128)]
    public void EachListHasItsOwnInclusiveAdmissionLimit(int limit)
    {
        Guid[] include = Ids(limit), exclude = Ids(limit, 1000);
        ArchiveTagFilter filter = new(include, exclude, ArchiveTagMatch.Any, limit);
        Assert.Equal(limit, filter.Include.Count);
        Assert.Equal(limit, filter.Exclude.Count);
        Assert.Equal(filter, ArchiveTagFilter.OrNull(include, exclude, ArchiveTagMatch.Any, limit));
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter(Ids(limit + 1), exclude, ArchiveTagMatch.All, limit));
        Assert.Throws<ArgumentException>(() => ArchiveTagFilter.OrNull(include, Ids(limit + 1, 1000), ArchiveTagMatch.All, limit));
        Assert.Null(ArchiveTagFilter.OrNull([], null, ArchiveTagMatch.All, limit));
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter([Id(1)], [Id(1)], ArchiveTagMatch.All, limit));
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter([Guid.Empty], [], ArchiveTagMatch.All, limit));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ArchiveTagFilter([Id(1)], [], (ArchiveTagMatch)99, limit));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(7)]
    [InlineData(129)]
    [InlineData(int.MaxValue)]
    public void InvalidLimitsAreRejectedBeforeEnumerationOrArithmetic(int limit)
    {
        IEnumerable<Guid> NotInspected()
        {
            yield return ThrowIfInspected();
        }
        Guid ThrowIfInspected() => throw new InvalidOperationException("Must validate the limit first.");
        Assert.Equal("tagsPerList", Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ArchiveTagFilter(NotInspected(), null, ArchiveTagMatch.All, limit)).ParamName);
        Assert.Equal("tagsPerList", Assert.Throws<ArgumentOutOfRangeException>(() =>
            ArchiveTagFilter.OrNull(NotInspected(), null, ArchiveTagMatch.All, limit)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveTagFilter.OrNull(null, null, ArchiveTagMatch.All, limit));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedEnumerationStopsAtLimitPlusOneEvenForOrNull(bool orNull)
    {
        int inspected = 0;
        IEnumerable<Guid> Endless()
        {
            while (true) { inspected++; yield return Id(1); }
        }
        // Duplicate inputs still consume the raw-input budget, as with the old fixed limit.
        if (orNull)
            Assert.Throws<ArgumentException>(() => ArchiveTagFilter.OrNull(Endless(), [], ArchiveTagMatch.All, 8));
        else
            Assert.Throws<ArgumentException>(() => new ArchiveTagFilter(Endless(), [], ArchiveTagMatch.All, 8));
        Assert.Equal(9, inspected);
    }

    [Fact]
    public void AdmissionBudgetDoesNotChangeFilterIdentityOrLegacySavedDefinitions()
    {
        ArchiveTagFilter small = new([Id(2), Id(1), Id(2)], null, ArchiveTagMatch.Any, 8);
        ArchiveTagFilter large = new([Id(1), Id(2)], null, ArchiveTagMatch.Any, 128);
        Assert.Equal(small, large);
        Assert.Equal(small.GetHashCode(), large.GetHashCode());
        Assert.Equal(ArchiveTagFilter.DefaultTagsPerList, new ArchiveTagFilter(Ids(32), null).Include.Count);
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter(Ids(33), null));

        ArchiveSearchSpec saved = new ArchiveSearchSpec().WithTags(new ArchiveTagFilter(Ids(32), null));
        string json = ArchiveSearchSpecJson.Serialize(saved);
        // No user admission preference is involved in loading an old definition, even when a UI now admits only eight.
        ArchiveSearchSpec restored = ArchiveSearchSpecJson.Deserialize(json);
        Assert.Equal(saved, restored);
        Assert.Equal(json, ArchiveSearchSpecJson.Serialize(restored));
    }

    [Theory]
    [InlineData(33)]
    [InlineData(128)]
    public void SavedFilterJsonReaderRoundTripsBeyondTheHistoricalDefault(int count)
    {
        ArchiveTagFilter original = new(Ids(count), Ids(count, 1000), ArchiveTagMatch.Any,
            ArchiveTagFilter.MaximumConfigurableTagsPerList);
        ArchiveSearchSpec saved = new ArchiveSearchSpec().WithTags(original);
        string json = ArchiveSearchSpecJson.Serialize(saved);

        ArchiveSearchSpec restored = ArchiveSearchSpecJson.Deserialize(json);
        Assert.Equal(saved, restored);
        Assert.Equal(original, restored.Tags);
        Assert.Equal(json, ArchiveSearchSpecJson.Serialize(restored));
    }

    [Fact]
    public void LoweringAdmissionToEightDoesNotDiscardAStored128TagFilter()
    {
        ArchiveSearchSpec saved = new ArchiveSearchSpec().WithTags(new ArchiveTagFilter(Ids(128), null, ArchiveTagMatch.Any,
            ArchiveTagFilter.MaximumConfigurableTagsPerList));
        ArchiveSearchSpec restored = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(saved));

        // Eight is an admission limit for new growth only; reconstructing the stored definition uses the technical cap.
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter(restored.Tags!.Include, restored.Tags.Exclude,
            restored.Tags.Match, ArchiveTagFilter.MinimumTagsPerList));
        ArchiveTagFilter reopened = new(restored.Tags.Include, restored.Tags.Exclude, restored.Tags.Match,
            ArchiveTagFilter.MaximumConfigurableTagsPerList);
        Assert.Equal(saved.Tags, reopened);
    }

    [Fact]
    public void SavedFilterJsonReaderRejectsThe129thReferenceBeforeParsingIt()
    {
        ArchiveSearchSpec saved = new ArchiveSearchSpec().WithTags(new ArchiveTagFilter(Ids(128), null, ArchiveTagMatch.All,
            ArchiveTagFilter.MaximumConfigurableTagsPerList));
        System.Text.Json.Nodes.JsonNode json = System.Text.Json.Nodes.JsonNode.Parse(ArchiveSearchSpecJson.Serialize(saved))!;
        json["tags"]!["include"]!.AsArray().Add("not-a-guid");

        JsonException error = Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(json.ToJsonString()));
        Assert.Contains("exceeds", error.Message, StringComparison.Ordinal);
    }
}
