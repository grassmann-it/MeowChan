using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>The context menus of the filter chips of the archive search and of the tags in the "Add tag…" popover.</summary>
[Collection("Localization")]
public sealed class TagChipMenuTests : IDisposable
{
    public TagChipMenuTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ContextMenuModel Chip(string text = "Tag: alpha", string? tag = "alpha", bool excluded = false, bool toggle = true, bool copyName = true,
        bool available = true, List<string>? log = null) =>
        FilterChipMenu.Compose(text, tag, excluded, toggle ? () => log?.Add("toggle") : null, () => log?.Add("copy"),
            copyName ? () => log?.Add("name") : null, () => log?.Add("remove"), available).ToModel();

    [Fact]
    public void ADraftTagChipSwitchesCopiesAndRemovesWithTheSwitchSayingWhatItWillDo()
    {
        ContextMenuModel included = Chip();
        Assert.Equal(new[] { "tags.filter-toggle", "archive.copy-filter", "tags.copy-name", "archive.clear-filter" }, included.Entries.Select(entry => entry.CommandId));
        Assert.Equal("Switch to exclude: alpha", included.Entries.First().Label);
        Assert.Equal("Switch to include: alpha", Chip("Without tag: alpha", excluded: true).Entries.First().Label);
        Assert.Equal(3, included.Sections.Count);
        Assert.Equal("archive.clear-filter", included.Sections[^1].Single().CommandId);
        Assert.All(included.Entries, entry => Assert.True(CommandCatalog.TryGet(entry.CommandId!, out _)));
    }

    [Fact]
    public void AnAppliedTagChipHasNoSwitchBecauseItIsChangedByRemovingItAndAPlainChipHasNoTagEntries()
    {
        Assert.Equal(new[] { "archive.copy-filter", "tags.copy-name", "archive.clear-filter" }, Chip(toggle: false).Entries.Select(entry => entry.CommandId));
        Assert.Equal(new[] { "archive.copy-filter", "archive.clear-filter" },
            Chip("Source: 4chan", tag: null, toggle: false, copyName: false).Entries.Select(entry => entry.CommandId));
    }

    [Fact]
    public void WhileASearchRunsTheChangingEntriesSayWhyAndCopyStaysAvailable()
    {
        ContextMenuModel model = Chip(available: false);
        foreach (ContextMenuEntry entry in model.Entries)
        {
            bool changes = entry.CommandId is "tags.filter-toggle" or "archive.clear-filter";
            Assert.Equal(!changes, entry.Available);
            if (changes) Assert.Equal("Currently unavailable for this target.", entry.Reason);
        }
    }

    [Fact]
    public void EachEntryReachesItsOwnAction()
    {
        List<string> log = [];
        foreach (ContextMenuEntry entry in Chip(log: log).Entries) entry.Invoke!();
        Assert.Equal(new[] { "toggle", "copy", "name", "remove" }, log);
    }

    [Fact]
    public void ATagInThePopoverCanBeAddedEitherWayAndItsNameCopied()
    {
        List<string> log = [];
        ContextMenuModel model = TagPickerMenu.Compose("alpha", () => log.Add("include"), () => log.Add("exclude"), () => log.Add("name")).ToModel();
        Assert.Equal(new[] { "tags.filter-include", "tags.filter-exclude", "tags.copy-name" }, model.Entries.Select(entry => entry.CommandId));
        Assert.Equal(new[] { "Include", "Exclude", "Copy tag name" }, model.Entries.Select(entry => entry.Label));
        Assert.Equal(2, model.Sections.Count);
        foreach (ContextMenuEntry entry in model.Entries) entry.Invoke!();
        Assert.Equal(new[] { "include", "exclude", "name" }, log);
    }
}
