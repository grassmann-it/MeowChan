using System.Globalization;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class CoreLimitProductionCallerTests : IDisposable
{
    public CoreLimitProductionCallerTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }
    private static Guid Id(int i) => new(i, 0, 0, new byte[8]);

    [Theory]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(128)]
    public void AdmissionUsesTheDestinationLimitAndDoesNotRevalidateTheOtherListAtThatLimit(int limit)
    {
        Guid[] destination = Enumerable.Range(1, limit).Select(Id).ToArray();
        Guid[] existingOther = Enumerable.Range(1000, 128).Select(Id).ToArray();
        Assert.Null(ArchivePage.DraftTagRefusal(destination, existingOther, Id(1), limit));
        Assert.Null(ArchivePage.DraftTagRefusal(destination[..^1], existingOther, Id(limit), limit));
        Assert.Equal(Texts.T("coreLimits.tags.tooMany", limit),
            ArchivePage.DraftTagRefusal(destination, existingOther, Id(500), limit));
        Assert.Equal(Texts.T("tags.filter.error.both"),
            ArchivePage.DraftTagRefusal(destination[..^1], existingOther, Id(1000), limit));
        // This is the actual reconstruction contract used after the admission check, not the lowered budget.
        var rebuilt = new ArchiveTagFilter(destination, existingOther, ArchiveTagMatch.All, 128);
        Assert.Equal(limit, rebuilt.Include.Count);
        Assert.Equal(128, rebuilt.Exclude.Count);
    }

    [Fact]
    public void Applied128TagFiltersCanLoseOneChipOrChangeMatchWithoutLosingTheRest()
    {
        Guid[] include = Enumerable.Range(1, 128).Select(Id).ToArray();
        Guid[] exclude = Enumerable.Range(1000, 128).Select(Id).ToArray();
        var saved = new ArchiveSearchSpec("needle").WithTags(new ArchiveTagFilter(include, exclude, ArchiveTagMatch.Any, 128));
        ArchiveSearchSpec loaded = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(saved));
        Assert.Equal(saved, loaded);
        ArchiveSearchSpec removedInclude = ArchivePage.CopySearch(loaded, "tagInclude", Id(64).ToString("D"));
        Assert.Equal(127, removedInclude.Tags!.Include.Count);
        Assert.DoesNotContain(Id(64), removedInclude.Tags.Include);
        Assert.Equal(loaded.Tags!.Exclude, removedInclude.Tags.Exclude);
        Assert.Equal(ArchiveTagMatch.Any, removedInclude.Tags.Match);
        ArchiveSearchSpec removedExclude = ArchivePage.CopySearch(loaded, "tagExclude", Id(1064).ToString("D"));
        Assert.Equal(127, removedExclude.Tags!.Exclude.Count);
        Assert.Equal(loaded.Tags.Include, removedExclude.Tags.Include);
        ArchiveSearchSpec all = ArchivePage.CopySearch(loaded, "tagMatch");
        Assert.Equal(ArchiveTagMatch.All, all.Tags!.Match);
        Assert.Equal(loaded.Tags.Include, all.Tags.Include);
        Assert.Equal(loaded.Tags.Exclude, all.Tags.Exclude);
        Assert.Equal("needle", all.Text);
        Assert.Equal(all, ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(all)));
        Assert.Same(loaded.Tags, ArchivePage.CopySearch(loaded, "text").Tags);
    }

    private static PrivateTagStoreSnapshot EmptyTags() => new(1, "fixture", Array.Empty<PrivateTagDefinition>(),
        Array.Empty<PrivateThreadTagAssignment>(), Array.Empty<PrivateMediaTagAssignment>());

    [Theory]
    [InlineData(18, 3, 3)]
    [InlineData(18, 20, 18)]
    [InlineData(3, 20, 3)]
    public void SlashDetectionBudgetIsSeparateFromQuickTagDisplayBudget(int quickCount, int slashCount, int expected)
    {
        Config config = new() { QuickTagSuggestionCount = quickCount, SlashTagSuggestionCount = slashCount };
        TagContextOptions options = QuickTagSettings.ContextOptions(config);
        Assert.Equal(quickCount, options.SuggestionCount);
        Assert.Equal(slashCount, options.SlashSuggestionCount);
        string markers = string.Join(" ", Enumerable.Range(1, 20).Select(i => "/board" + i + "/"));
        var ranked = TagContextSuggestions.Rank(EmptyTags(), new TagContext { TitleTexts = [markers] }, options: options);
        Assert.Equal(expected, ranked.Count);
        Assert.All(ranked, item => Assert.Equal(TagSuggestionReason.SlashMarker, item.PrimaryReason));
        // An explicit ranking/display count still cannot make Detect admit more than the slash budget.
        var allCandidates = TagContextSuggestions.Rank(EmptyTags(), new TagContext { TitleTexts = [markers] }, options: options, limit: 40);
        Assert.Equal(slashCount, allCandidates.Count);
        Assert.Equal(quickCount, QuickTagSettings.SurfaceOptions(config).SuggestionCount);
    }

    [Fact]
    public void SlashTransportClampsIndependentlyAndDoesNotLiftThePerTextSafetyCeiling()
    {
        var low = QuickTagSettings.ContextOptions(new Config { QuickTagSuggestionCount = 18, SlashTagSuggestionCount = int.MinValue });
        var high = QuickTagSettings.ContextOptions(new Config { QuickTagSuggestionCount = 3, SlashTagSuggestionCount = int.MaxValue });
        Assert.Equal((18, 3), (low.SuggestionCount, low.SlashSuggestionCount));
        Assert.Equal((3, 20), (high.SuggestionCount, high.SlashSuggestionCount));
        string beyondScan = new(' ', SlashTagSuggestions.MaximumScannedCharacters);
        Assert.Empty(TagContextSuggestions.Rank(EmptyTags(), new TagContext { TitleTexts = [beyondScan + "/hidden/"] }, options: high));
    }

    private static ArchiveUrlTextExportPlan Plan()
    {
        ResourceId id = new("4chan", "b", "123");
        return ArchiveUrlTextExportPlan.TryCreate(id, new ArchiveThreadSummary(id,
            "https://boards.4chan.org/b/thread/123", "offline", "/unused", ThreadSourceState.NotFound,
            false, DateTimeOffset.UnixEpoch, null, 0, 1), out _)!;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(64)]
    public async Task RealWriterPathLimitIsNotReportedAsAnOutputLimitAndConfirmationDoesNotRetry(int limitMiB)
    {
        var writer = new UrlTextExportFileWriter(limitMiB);
        int picks = 0, writes = 0;
        using var operation = new UrlTextExportOperation(Plan(), () =>
        {
            picks++;
            return Task.FromResult<string?>(Path.Combine(Path.GetTempPath(), new string('x', UrlTextExportFileWriter.MaximumPathCharacters + 1)));
        }, (plan, path, token) => { writes++; return writer.WriteAsync(plan, path, token); }, default, limitMiB);
        await operation.ConfirmAsync();
        await operation.ConfirmAsync();
        Assert.Equal((1, 1), (picks, writes));
        Assert.Equal(limitMiB, operation.LimitMiB);
        Assert.Equal(limitMiB * 1024 * 1024, writer.LimitBytes);
        Assert.Equal(UrlTextExportFileWriteError.LimitExceeded, operation.Result!.Error);
        Assert.Equal("urltext.export.error.pathLimit", operation.StatusKey);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void LimitMessageClassificationUsesBytePolicyWithoutForgingOversizedPlans(int limitMiB)
    {
        var writer = new UrlTextExportFileWriter(limitMiB);
        Assert.True(writer.IsWithinByteLimit(writer.LimitBytes));
        Assert.False(writer.IsWithinByteLimit(writer.LimitBytes + 1));
        Assert.Equal("error.pathLimit", UrlTextExportOperation.LimitFailureKey(writer.LimitBytes, limitMiB));
        Assert.Equal("error.outputLimit", UrlTextExportOperation.LimitFailureKey(writer.LimitBytes + 1, limitMiB));
        // Today's real, single-page plan is not manufactured into a multi-MiB format capability.
        Assert.InRange(Plan().ByteCount, 1, 1024 * 1024);
    }

    [Fact]
    public void NewEnglishAndGermanUsageTextsAreDataOnlyAndNameTheActualConfiguredLimit()
    {
        Assert.Equal(4, CoreLimitUsageCatalog.Entries.Length);
        Assert.Equal(4, CoreLimitUsageCatalog.Entries.Select(item => item.Key).Distinct().Count());
        foreach (var entry in CoreLimitUsageCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(string.IsNullOrWhiteSpace(entry.German));
            foreach (string template in new[] { entry.English, entry.German })
            {
                string text = string.Format(CultureInfo.InvariantCulture, template, 17);
                Assert.DoesNotContain("{0}", text);
                if (entry.Key.EndsWith("tooMany", StringComparison.Ordinal) || entry.Key.EndsWith("outputLimit", StringComparison.Ordinal))
                    Assert.Contains("17", text);
            }
        }
    }
}
