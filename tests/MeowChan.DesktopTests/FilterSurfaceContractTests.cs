namespace MeowChan.DesktopTests;

// S1/S2/S7 and the tag popover: what the unit host cannot drive (the pages need the asset loader) is pinned as a source contract;
// the behaviour is driven by the media-page and archive filter smokes.
public sealed class FilterSurfaceContractTests
{
    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    [Fact]
    public void BothPagesUseTheFilterToggleButtonAndKeepTheirControlNames()
    {
        string media = Source("Desktop/Views/Media/MediaPage.FilterSurface.cs"), archive = Source("Desktop/Views/Archive/ArchivePage.axaml");
        Assert.Contains("new() { Name = \"MediaFilters\"", media);
        Assert.Contains("FilterToggleButton _filters", media);
        Assert.Contains("<controls:FilterToggleButton x:Name=\"ArchiveFilters\"", archive);
        Assert.DoesNotContain("<Expander x:Name=\"ArchiveFilters\"", archive);
        Assert.Contains("IsVisible=\"{Binding #ArchiveFilters.IsChecked}\"", archive);
    }

    [Fact]
    public void TheArchiveCountsOneFilterPerAppliedChipAndOffersResetAllBehindTheLastChip()
    {
        string chips = Source("Desktop/Views/Archive/ArchivePage.AppliedFilters.cs"), presentation = Source("Desktop/Views/Archive/ArchivePage.Presentation.cs");
        Assert.Contains("_appliedFilterCount = tags.Count;", chips);
        Assert.Contains("Name = \"ArchiveAppliedResetAll\"", chips);
        Assert.Contains("panel.Children.Add(_appliedResetAll);", chips);
        // The link is added after the chips, and runs the same reset as the empty state.
        Assert.True(chips.IndexOf("panel.Children.Add(CreateAppliedChip(", StringComparison.Ordinal) < chips.IndexOf("panel.Children.Add(_appliedResetAll);", StringComparison.Ordinal));
        Assert.Contains("OnResetArchiveFiltersClick(_appliedResetAll, e)", chips);
        Assert.Contains("ArchiveFilters.Show(", presentation);
        Assert.Contains("Texts.Count(\"archive.filters.countName\", count)", presentation);
    }

    [Fact]
    public void TileTagChipsExcludeOnShiftAndTheMediaPageWiresIt()
    {
        string tile = Source("Desktop/MediaLibraryTileView.cs"), page = Source("Desktop/Views/Media/MediaPage.cs"), filters = Source("Desktop/Views/Media/MediaPage.Filters.cs");
        Assert.Contains("e.KeyModifiers.HasFlag(KeyModifiers.Shift)", tile);
        Assert.Contains("TagExclude(tag.Id.Value)", tile);
        Assert.Contains("TagExclude = ExcludeTag", page);
        Assert.Contains("IncludeOrExclude(id, include: false)", filters);
    }

    [Fact]
    public void MediaRefreshIsAnIconButtonAndTheTagComboAndItsTwoButtonsAreGone()
    {
        string page = Source("Desktop/Views/Media/MediaPage.cs"), filters = Source("Desktop/Views/Media/MediaPage.Filters.cs");
        Assert.Contains("IconButtons.Apply(_refresh, \"refresh-cw\", Texts.T(\"media.refresh\"));", page);
        Assert.Contains("Name = \"MediaRefresh\"", page);
        Assert.Contains("new() { Name = \"MediaTagAdd\" }", filters);
        foreach (string gone in new[] { "MediaTagChoice", "MediaTagInclude", "MediaTagExclude" })
            Assert.DoesNotContain(gone, filters);
    }

    [Fact]
    public void TagChipsInTheMediaHeaderCarryTheToggle()
    {
        string chips = Source("Desktop/Views/Media/MediaPage.FilterChips.cs");
        Assert.Contains("ArchivePage.CreateTagFilterChip(", chips);
        Assert.Contains("ToggleTagFilter(tagId)", chips);
    }

    [Fact]
    public void TheArchiveTagEditorUsesThePopoverAndTheChipTogglesAndKeepsItsPendingValidation()
    {
        string axaml = Source("Desktop/Views/Archive/ArchivePage.axaml"), tags = Source("Desktop/Views/Archive/ArchivePage.TagFilter.cs"),
            editors = Source("Desktop/Views/Archive/ArchivePage.SearchEditors.cs");
        Assert.Contains("<controls:TagFilterPicker x:Name=\"ArchiveTagChoice\"", axaml);
        Assert.Contains("<DataValidationErrors x:Name=\"ArchiveTagChoiceHost\">", axaml);
        foreach (string gone in new[] { "<ComboBox x:Name=\"ArchiveTagChoice\"", "ArchiveAddTagInclude", "ArchiveAddTagExclude", "OnAddTagInclude", "OnAddTagExclude" })
        {
            Assert.DoesNotContain(gone, axaml);
            Assert.DoesNotContain(gone, tags);
        }
        Assert.DoesNotContain("TagChoice(", tags);
        Assert.Contains("ArchiveTagChoice.KeepQueryOnDismiss = true;", tags);
        Assert.Contains("ArchiveTagChoice.Picked = (id, exclude) => AddDraftTag(id, include: !exclude);", tags);
        Assert.Contains("CreateTagFilterChip(", tags);
        Assert.Contains("ToggleDraftTag(", tags);
        Assert.Contains("DataValidationErrors.SetErrors(ArchiveTagChoiceHost, pending", editors);
        Assert.Contains("_invalidSearchEditor = ArchiveTagChoiceHost;", editors);
        Assert.Contains("private bool HasPendingTagChoice => ArchiveTagChoice.HasPending;", tags);
    }

    [Fact]
    public void AppliedTagChipsCarryTheSameToggleAndSearchAgainAtOnce()
    {
        string chips = Source("Desktop/Views/Archive/ArchivePage.AppliedFilters.cs"), toggle = Source("Desktop/Views/Archive/ArchivePage.AppliedFilters.Toggle.cs");
        Assert.Contains("CreateTagFilterChip(item.Label, TagFilterLabel(id), id, item.Field == \"tagExclude\"", toggle);
        Assert.Contains("async () => await ToggleAppliedTag(id)", toggle);
        // The toggle and the removal share one search path, so a refused definition and a late draft edit behave the same.
        Assert.Contains("SearchFromAppliedChip(applied.WithTags(filter), \"tags\")", toggle);
        Assert.Contains("await SearchFromAppliedChip(CopySearch(", chips);
        Assert.Contains("ArchiveTagFilter.MaximumTagsPerList", toggle);
    }
}
