using System.Globalization;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class WorkViewSwitchCatalogTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void SummaryReportsAllSevenDisjointBucketsAndDoesNotClaimObservedLiveState(string language)
    {
        string previous = Texts.Language;
        try
        {
            Texts.Initialize();
            Texts.SetLanguage(language);
            DiffSummary diff = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
            string summary = WorkViewSwitchCatalog.Summary(diff);
            var entry = WorkViewSwitchCatalog.Entries.Single(e => e.Key == "workview.switch.summary");
            Assert.Equal(string.Format(CultureInfo.GetCultureInfo(language), language == "de" ? entry.German : entry.English,
                WorkViewOverview.ViewLabel(diff.From), WorkViewOverview.ViewLabel(diff.To),
                diff.Counts.ToDirect, diff.Counts.ToOverflow, diff.Counts.ToCollapsed, diff.Counts.ToHidden,
                diff.Counts.ToColumnOn, diff.Counts.ToColumnOff, diff.Counts.ToColumnNotOffered), summary);
            Assert.DoesNotContain("workview.", summary);
            Assert.Contains(language == "de" ? "Spaltenvorgaben" : "Column defaults", summary);
            Assert.Contains(language == "de" ? "können abweichen" : "may differ", summary);
            Assert.Equal(diff.Counts.Total, diff.Counts.ToDirect + diff.Counts.ToOverflow + diff.Counts.ToCollapsed
                + diff.Counts.ToHidden + diff.Counts.ToColumnOn + diff.Counts.ToColumnOff + diff.Counts.ToColumnNotOffered);
            // Every view pair, including upgrades, must be formattable without dropped or extra arguments.
            foreach (WorkView from in WorkViewSetting.All)
            foreach (WorkView to in WorkViewSetting.All)
                Assert.Contains(" → ", WorkViewSwitchCatalog.Summary(WorkViewPolicy.Diff(from, to)));
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void EntriesAreUniqueNonemptyAndPublishBothActionsAndSettingsRange()
    {
        var entries = WorkViewSwitchCatalog.Entries;
        Assert.Equal(entries.Length, entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry => { Assert.False(string.IsNullOrWhiteSpace(entry.English)); Assert.False(string.IsNullOrWhiteSpace(entry.German)); });
        Assert.Contains(entries, e => e.Key == "workview.switch.undo" && e.German == "Rückgängig");
        Assert.Contains(entries, e => e.Key == "workview.switch.details" && e.English == "Details");
        Assert.Contains(entries, e => e.Key == "workview.switch.overviewUnavailable");
        Assert.Contains(entries, e => e.Key == "settings.workViewSwitchNoticeSecondsHint"
            && e.English.Contains("0–60", StringComparison.Ordinal) && e.German.Contains("Standard 8", StringComparison.Ordinal));
    }
}
