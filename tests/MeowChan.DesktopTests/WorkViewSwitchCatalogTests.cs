using System.Globalization;
using System.Text.RegularExpressions;
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

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void CompactSummaryUsesOnePolicyDifferenceCountIncludingColumnsAndKeepsViewLabels(string language)
    {
        string previous = Texts.Language;
        try
        {
            Texts.Initialize();
            Texts.SetLanguage(language);
            DiffSummary diff = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
            var entry = WorkViewSwitchCatalog.Entries.Single(e => e.Key == "workview.switch.compactSummary");
            string template = language == "de" ? entry.German : entry.English;
            Assert.Equal(new[] { "0", "1", "2" }, PlaceholderIndexes(template));

            int columnChanges = diff.Changes.Count(change => change.Entry.Kind == WorkViewKind.Column);
            Assert.True(columnChanges > 0, "The regression pair must include column policy rows.");
            Assert.Equal(diff.Changes.Count, diff.Counts.Total);
            Assert.Equal(diff.Changes.Count, diff.Changes.Select(change => change.Entry.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(diff.Counts.Total, diff.Counts.ToDirect + diff.Counts.ToOverflow + diff.Counts.ToCollapsed
                + diff.Counts.ToHidden + diff.Counts.ToColumnOn + diff.Counts.ToColumnOff + diff.Counts.ToColumnNotOffered);

            string compact = WorkViewSwitchCatalog.CompactSummary(diff);
            string expected = string.Format(CultureInfo.GetCultureInfo(language), template,
                WorkViewOverview.ViewLabel(diff.From), WorkViewOverview.ViewLabel(diff.To), diff.Counts.Total);
            Assert.Equal(expected, compact);
            Assert.Contains(language == "de" ? "Minimal" : "Barebones", compact, StringComparison.Ordinal);
            Assert.Equal(1, Regex.Matches(compact, $"(?<!\\d){diff.Counts.Total}(?!\\d)").Count);
            Assert.DoesNotContain("active", compact, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("saved", compact, StringComparison.OrdinalIgnoreCase);
            Assert.True(compact.Length < WorkViewSwitchCatalog.Summary(diff).Length);
        }
        finally { Texts.SetLanguage(previous); }
    }

    [Fact]
    public void ExistingSummaryKeepsItsNineParameterContract()
    {
        var entry = WorkViewSwitchCatalog.Entries.Single(e => e.Key == "workview.switch.summary");
        Assert.Equal(new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8" }, PlaceholderIndexes(entry.English));
        Assert.Equal(new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8" }, PlaceholderIndexes(entry.German));
    }

    private static string[] PlaceholderIndexes(string template) =>
        Regex.Matches(template, @"\{(\d+)\}").Select(match => match.Groups[1].Value).ToArray();

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
