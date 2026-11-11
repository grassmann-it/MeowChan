using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ArchiveTagFilterUiTests : IDisposable
{
    private static readonly Guid A = new("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = new("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid Gone = new("00000000-0000-0000-0000-0000000000ff");

    public ArchiveTagFilterUiTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static string? Name(Guid id) => id == A ? "alpha" : id == B ? "beta" : null;

    [Fact]
    public void AppliedChipsNameEachTagTheModeAndDeletedTags()
    {
        ArchiveSearchSpec spec = new ArchiveSearchSpec("needle").WithTags(new ArchiveTagFilter([A, Gone], [B], ArchiveTagMatch.Any));

        var chips = ArchivePage.AppliedFilterLabels(spec, Name);

        Assert.Equal(new (string, string?, string)[]
        {
            ("text", null, Texts.T("archive.textChip", "needle")),
            ("tagInclude", A.ToString("D"), "Tag: alpha"),
            ("tagInclude", Gone.ToString("D"), "Tag: (deleted tag)"),
            ("tagExclude", B.ToString("D"), "Without tag: beta"),
            ("tagMatch", null, "At least one of the tags")
        }, chips);
        // Without a resolver nothing is invented: every tag reads as deleted, none is dropped.
        Assert.Equal(5, ArchivePage.AppliedFilterLabels(spec).Count);
        Assert.Empty(ArchivePage.AppliedFilterLabels(new ArchiveSearchSpec()));
        Assert.True(ArchivePage.HasArchiveConstraints(new ArchiveSearchSpec().WithTags(new ArchiveTagFilter(null, [A]))));
    }

    [Fact]
    public void RemovingOneChipChangesOnlyThatPartOfTheDefinition()
    {
        ArchiveSearchSpec spec = new ArchiveSearchSpec("needle", ["s"]).WithTags(new ArchiveTagFilter([A, Gone], [B], ArchiveTagMatch.Any));

        ArchiveSearchSpec withoutGone = ArchivePage.CopySearch(spec, "tagInclude", Gone.ToString("D"));
        Assert.Equal(new ArchiveTagFilter([A], [B]), withoutGone.Tags);
        Assert.Equal("needle", withoutGone.Text);
        Assert.Equal(new[] { "s" }, withoutGone.Sources);

        Assert.Equal(new ArchiveTagFilter([A, Gone], null, ArchiveTagMatch.Any), ArchivePage.CopySearch(spec, "tagExclude", B.ToString("D")).Tags);
        Assert.Equal(new ArchiveTagFilter([A, Gone], [B]), ArchivePage.CopySearch(spec, "tagMatch").Tags);
        // Removing an unrelated chip keeps the whole tag filter.
        Assert.Equal(spec.Tags, ArchivePage.CopySearch(spec, "text").Tags);
        Assert.Equal(spec.Tags, ArchivePage.CopySearch(spec, "source", "s").Tags);
        // The last tag chip removes the filter and returns to schema 1.
        ArchiveSearchSpec single = new ArchiveSearchSpec().WithTags(new ArchiveTagFilter([A], null));
        ArchiveSearchSpec cleared = ArchivePage.CopySearch(single, "tagInclude", A.ToString("D"));
        Assert.Null(cleared.Tags);
        Assert.Equal(1, cleared.SchemaVersion);
    }

    [Theory]
    [InlineData("en", "Tags (at least one): alpha, (deleted tag)", "Without tags: beta")]
    [InlineData("de", "Tags (mindestens einer): alpha, (gelöschter Tag)", "Ohne Tags: beta")]
    public void SavedSearchDescriptionNamesTagsAndNeverOmitsADeletedOne(string language, string include, string exclude)
    {
        Texts.SetLanguage(language);
        ArchiveSearchSpec spec = new ArchiveSearchSpec().WithTags(new ArchiveTagFilter([A, Gone], [B], ArchiveTagMatch.Any));

        string text = SavedArchiveSearchDescription.Describe(spec, Name);

        Assert.Contains(include, text, StringComparison.Ordinal);
        Assert.Contains(exclude, text, StringComparison.Ordinal);
        Assert.DoesNotContain(A.ToString("D"), text, StringComparison.Ordinal);
        string all = SavedArchiveSearchDescription.Describe(new ArchiveSearchSpec().WithTags(new ArchiveTagFilter([A, B], null)), Name);
        Assert.Contains(Texts.T("tags.filter.describeAll") + ": alpha, beta", all, StringComparison.Ordinal);
        Assert.DoesNotContain(Texts.T("tags.filter.describeAll"), SavedArchiveSearchDescription.Describe(new ArchiveSearchSpec()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedSearchWithTagFilterSurvivesTheStoreAndARestart()
    {
        using TempDirectory temp = new();
        Guid id = Guid.NewGuid();
        ArchiveSearchSpec definition;
        await using (DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync();
            TagId keep = (await host.DefinePrivateTagAsync("keep")).Tag, skip = (await host.DefinePrivateTagAsync("skip")).Tag;
            definition = new ArchiveSearchSpec("needle", sort: ArchiveThreadSort.Title)
                .WithTags(new ArchiveTagFilter([keep.Value], [skip.Value]));
            SavedArchiveSearchSnapshot saved = await host.CreateSavedArchiveSearchAsync(id, "tagged", definition, 0);
            SavedArchiveSearch entry = Assert.Single(saved.Searches);
            Assert.True(entry.CanApply);
            Assert.Equal(definition, entry.Definition);
            Assert.Contains("\"schemaVersion\":2", entry.RawDefinition, StringComparison.Ordinal);
        }
        await using DesktopSessionHost restored = new(temp.Path, Array.Empty<IImageBoard>());
        await restored.StartAsync();
        SavedArchiveSearch reloaded = Assert.Single((await restored.LoadSavedArchiveSearchesAsync()).Searches);
        Assert.Equal(definition, reloaded.Definition);
        Assert.Null(reloaded.UnsupportedReason);
        // The stored definition still needs the tag index to run; without it the request is refused.
        Assert.Throws<ArchiveTagFilterException>(() => ArchiveSearchRequest.Create(reloaded.Definition!));
        Assert.NotNull(ArchiveSearchRequest.Create(reloaded.Definition!, restored.GetPrivateTagSnapshot().Snapshot).TagMembership);
    }
}
