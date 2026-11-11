using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ArchiveThreadRowTagTests : IDisposable
{
    public ArchiveThreadRowTagTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static ArchiveThreadRow Row() => new(new ArchiveThreadSummary(new ResourceId("synthetic", "b", "1"), "", "", "",
        ThreadSourceState.NotFound, true, DateTimeOffset.UnixEpoch, null, 1, 1));

    [Fact]
    public void TagLineIsHiddenWhenEmptyAndNotifiesOnlyOnRealChanges()
    {
        ArchiveThreadRow row = Row();
        List<string?> changed = new();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.SetTags(null);
        row.SetTags(Array.Empty<string>());
        Assert.False(row.HasTags);
        Assert.Equal(string.Empty, row.TagsText);
        Assert.Empty(changed);

        row.SetTags(["/rpt/", "my_tag"]);
        Assert.True(row.HasTags);
        Assert.Equal("Tags: /rpt/, my_tag", row.TagsText);
        // The list line and the table column notify together, once per real change.
        Assert.Equal(new[] { nameof(ArchiveThreadRow.TagsText), nameof(ArchiveThreadRow.HasTags),
            nameof(ArchiveThreadRow.TagColumnText), nameof(ArchiveThreadRow.TagColumnTip) }, changed);

        row.SetTags(["/rpt/", "my_tag"]);
        Assert.Equal(4, changed.Count);
        row.SetTags(null);
        Assert.False(row.HasTags);
        Assert.Equal(8, changed.Count);
    }

    [Theory]
    [InlineData("de", "Tags: a")]
    [InlineData("es", "Etiquetas: a")]
    [InlineData("fr", "Étiquettes : a")]
    [InlineData("ru", "Теги: a")]
    public void TagLineUsesTheActiveLanguage(string language, string expected)
    {
        Texts.SetLanguage(language);
        ArchiveThreadRow row = Row();
        row.SetTags(["a"]);
        Assert.Equal(expected, row.TagsText);
    }

    [Fact]
    public void ReaderWiresTheTagBarWithoutAGlobalShortcutOrModalState()
    {
        string xaml = Source("Desktop/Views/Archive/ArchivePage.axaml");
        foreach (string name in new[] { "ReaderTagsOpen", "ReaderTagBar", "ReaderTagChips", "ReaderTagSuggestions",
                     "ReaderTagInput", "ReaderTagAdd", "ReaderTagClose", "ReaderTagStatus" })
            Assert.Contains("x:Name=\"" + name + "\"", xaml);
        Assert.Contains("Text=\"{Binding TagsText}\"", xaml);
        string reader = Source("Desktop/Views/Archive/ArchivePage.Reader.cs");
        // Escape is scoped to a source inside the bar, exactly like reader find.
        Assert.Contains("ReaderTagBar.IsVisible && IsTagBarSource(source)", reader);
        string tags = Source("Desktop/Views/Archive/ArchivePage.Tags.cs");
        Assert.Contains("ResourceId thread = _thread!.Summary.Id; // Frozen target", tags);
        Assert.DoesNotContain("ShowDialog", tags);
        Assert.DoesNotContain("CommandCatalog", tags);
    }

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", relative)));
}
