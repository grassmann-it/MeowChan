using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The archive tag filter keeps its fixed limit of 32 tags per list; adding a 33rd tag, or a tag that is already in the
/// other list, is refused with a localized sentence instead of the English exception text of the filter type.
/// </summary>
[Collection("Localization")]
public sealed class ArchiveTagFilterLimitTests : IDisposable
{
    // Load the bundled packs too: an earlier test of the collection may have left only the built-in languages.
    public ArchiveTagFilterLimitTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    private static Guid Id(int index) => new(index, 0, 0, new byte[8]);

    [Fact]
    public void A33rdTagAndATagFromTheOtherListAreRefusedBeforeTheFilterTypeThrows()
    {
        Assert.Equal(32, ArchiveTagFilter.MaximumTagsPerList);
        List<Guid> full = Enumerable.Range(1, ArchiveTagFilter.MaximumTagsPerList).Select(Id).ToList();
        List<Guid> other = [Id(100)];
        string? tooMany = ArchivePage.DraftTagRefusal(full, other, Id(99));
        Assert.Equal(Texts.T("tags.filter.error.tooMany", 32), tooMany);
        Assert.Throws<ArgumentException>(() => new ArchiveTagFilter(full.Append(Id(99)), other));
        // A tag already in the list changes nothing; the 32nd tag still fits.
        Assert.Null(ArchivePage.DraftTagRefusal(full, other, Id(1)));
        Assert.Null(ArchivePage.DraftTagRefusal(full.Take(31).ToList(), other, Id(32)));
        Assert.Equal(Texts.T("tags.filter.error.both"), ArchivePage.DraftTagRefusal(full.Take(31).ToList(), other, Id(100)));
        Assert.Equal(Texts.T("tags.filter.error.both"), ArchivePage.DraftTagRefusal(other, full, Id(5)));
    }

    [Fact]
    public void TheRefusalsAreTranslatedAndNameTheLimitInEveryLanguage()
    {
        string previous = Texts.Language;
        try
        {
            foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
            {
                Texts.SetLanguage(language);
                string tooMany = Texts.T("tags.filter.error.tooMany", ArchiveTagFilter.MaximumTagsPerList);
                Assert.Contains("32", tooMany);
                Assert.DoesNotContain("{0}", tooMany);
                Assert.NotEqual("tags.filter.error.tooMany", tooMany);
                Assert.NotEqual("tags.filter.error.both", Texts.T("tags.filter.error.both"));
                if (language != "en")
                    Assert.NotEqual(Texts.T("tags.filter.error.both"), English("tags.filter.error.both"));
            }
            Texts.SetLanguage("de");
            Assert.DoesNotContain("Sie ", Texts.T("tags.filter.error.tooMany", 32));
        }
        finally { Texts.SetLanguage(previous); }
    }

    private static string English(string key)
    {
        string language = Texts.Language;
        Texts.SetLanguage("en");
        try { return Texts.T(key); }
        finally { Texts.SetLanguage(language); }
    }
}
