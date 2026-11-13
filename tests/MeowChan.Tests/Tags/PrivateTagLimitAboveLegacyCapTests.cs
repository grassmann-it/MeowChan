using System.Globalization;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

/// <summary>
/// The own-tag limit is a setting that may go up to the catalog ceiling (20000). The store defines tags through the
/// shared catalog and refuses only at the configured limit, never at the old fixed value of 1024 that the (test-only)
/// in-memory projections still carry; a profile above 1024 tags keeps working and the refusal names the setting.
/// </summary>
public sealed class PrivateTagLimitAboveLegacyCapTests
{
    private const int LegacyCap = 1024;

    [Fact]
    public void TheSettingsMaximumIsTheCatalogCeilingTheStoreReadsAndWritesAgainst()
    {
        Assert.Equal(PrivateTagCatalog.MaximumDefinitions, PrivateTagLimits.MaximumTagLimit);
        Assert.Equal(PrivateTagCatalog.MaximumDefinitions, PrivateTagStore.MaximumTags);
        Assert.True(PrivateTagCatalog.MaximumDefinitions > LegacyCap);
    }

    [Fact]
    public void TheStoreDefinesTagsUpToTheConfiguredLimitAndRefusesOnlyAtTheSetting()
    {
        using TempDirectory temp = new();
        // A profile that already holds more tags than the old fixed value (as a raised setting leaves it).
        File.WriteAllText(Path.Combine(temp.Path, PrivateTagStore.FileName), "{\"Version\":1,\"Revision\":1,\"Tags\":["
            + string.Join(",", Enumerable.Range(0, 1500).Select(index => "{\"Id\":\"" + Id(index + 1).ToString("D")
                + "\",\"Name\":\"tag " + index.ToString(CultureInfo.InvariantCulture) + "\"}"))
            + "],\"Threads\":[],\"Media\":[]}");
        using ProfileLease lease = ProfileLease.Acquire(temp.Path);
        PrivateTagLimits limits = new() { TagLimit = 1502 };
        PrivateTagStore store = new(temp.Path, lease, limits);
        Assert.Equal(1500, store.ReadSnapshot().Tags.Count);

        Assert.True(store.Define("above the old cap").Changed);
        Assert.True(store.Define("still allowed").Changed);
        Assert.Equal(1502, store.ReadSnapshot().Tags.Count);

        // The configured limit, not a hidden in-memory bound, is what refuses the next tag.
        PrivateTagStoreException refused = Assert.Throws<PrivateTagStoreException>(() => store.Define("refused"));
        Assert.Equal(PrivateTagStoreError.TagLimitReached, refused.Error);
        Assert.Equal(1502, store.ReadSnapshot().Tags.Count);

        limits.TagLimit = 1503;
        Assert.True(store.Define("allowed again").Changed);
        Assert.Equal(1503, store.ReadSnapshot().Tags.Count);
    }

    private static Guid Id(int index) => new(index, 0, 0, new byte[8]);
}
