using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>Pure WV11 projection tests; no controls, database, network, window or native decoder.</summary>
[Collection("Localization")]
public sealed class WorkViewOverviewTests : IDisposable
{
    public WorkViewOverviewTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void ProjectionIncludesCurrentPolicyExactlyOnceWithoutAReleaseRowCount()
    {
        var rows = WorkViewOverview.Build();
        Assert.Equal(WorkViewPolicy.Entries.Count, rows.Count);
        Assert.Equal(WorkViewPolicy.Entries.Select(e => e.Id).Order(), rows.Select(row => row.Id).Order());
        Assert.Equal(rows.Count, rows.Select(row => row.Id).Distinct().Count());
        Assert.Equal(rows.Select(row => row.Entry.Page).Order(), rows.Select(row => row.Entry.Page));
        foreach (var row in rows)
        {
            Assert.Same(WorkViewPolicy.Get(row.Id), row.Entry);
            Assert.False(string.IsNullOrWhiteSpace(row.Label), row.Id);
            Assert.False(string.IsNullOrWhiteSpace(row.PageLabel), row.Id);
            Assert.All(WorkViewSetting.All, view => Assert.False(string.IsNullOrWhiteSpace(row.Cell(view)), row.Id));
        }
    }

    [Fact]
    public void CommandLabelWinsWithoutReadingAnyRegisteredControl()
    {
        foreach (var entry in WorkViewPolicy.Entries.Where(e => e.CommandId != null))
        {
            var label = WorkViewOverview.Label(entry, _ => throw new InvalidOperationException("Command labels must not inspect controls"));
            Assert.Equal(WorkViewOverview.LabelSource.Command, label.Source);
            Assert.Equal(CommandCatalog.Label(entry.CommandId!), label.Text);
        }
    }

    [Fact]
    public void SuppliedAccessibleNameWinsOverHumanizedIdAndMissingOrBlankNamesFallBack()
    {
        var entry = WorkViewPolicy.Get("archive.filters.applied");
        var named = WorkViewOverview.Project(entry, id => id == entry.Id ? "  Active filters  " : null);
        Assert.Equal("Active filters", named.Label);
        Assert.Equal(WorkViewOverview.LabelSource.RegisteredControl, named.Source);
        var fallback = WorkViewOverview.Project(entry, _ => " \t");
        Assert.Equal(WorkViewOverview.LabelSource.HumanizedId, fallback.Source);
        Assert.Equal("Filters / Applied", fallback.Label);
        Assert.Equal(fallback.Label, WorkViewOverview.Project(entry).Label);
    }

    [Fact]
    public void NoCurrentPolicyLabelFallsBackToARawDottedId()
    {
        // Zero raw ids is the shrinking baseline; no pinned count of humanized fallback labels.
        Assert.All(WorkViewOverview.Build(), row => Assert.NotEqual(row.Id, row.Label));
        Assert.Equal("Columns / Archive Thread Table / Last Capture", WorkViewOverview.Humanize("columns.ArchiveThreadTable.lastCapture"));
        Assert.Equal("More filters / HTTP Budget / Other columns", WorkViewOverview.Humanize("more-filters.HTTPBudget.*"));
    }

    [Fact]
    public void HiddenFunctionsNameTheActualPolicyAlternativeInsteadOfInventingAMenu()
    {
        var rows = WorkViewOverview.Build();
        foreach (var row in rows.Where(r => r.Entry.Kind != WorkViewKind.Column))
        foreach (var view in WorkViewSetting.All.Where(v => row.Entry.For(v) == Presence.Hidden))
        {
            string alternativeId = Assert.IsType<string>(row.Entry.Alternative);
            string expected;
            if (WorkViewPolicy.Find(alternativeId) is { } other)
            {
                expected = WorkViewOverview.Label(other).Text;
                Assert.NotEqual(Presence.Hidden, other.For(view));
            }
            else expected = CommandCatalog.Label(alternativeId);
            Assert.Equal(expected, row.AlternativeLabel);
            Assert.Equal(WorkViewOverviewCatalog.T("workview.presence.hidden", expected), row.Cell(view));
        }
    }

    [Fact]
    public void UnresolvedAlternativeIsNotAdvertisedAsAWorkingRoute()
    {
        var entry = new WorkViewEntry("archive.synthetic", WorkViewPage.Archive, WorkViewKind.Element,
            Presence.Hidden, Presence.Direct, Presence.Direct, Alternative: "not.a.real.route");
        var row = WorkViewOverview.Project(entry);
        Assert.Null(row.AlternativeLabel);
        Assert.Equal(WorkViewOverviewCatalog.T("workview.overview.noAlternative"), row.Cell(WorkView.Barebones));
    }

    [Fact]
    public void RequiredAndSafetyAreClearlyProtectedAndColumnsHaveTheirOwnVocabulary()
    {
        foreach (var row in WorkViewOverview.Build())
        {
            Assert.Equal(row.Entry.Safety || WorkViewSetting.All.Any(v => row.Entry.For(v) == Presence.Required), row.Protected);
            if (row.Protected)
            {
                Assert.Contains(WorkViewOverviewCatalog.T(row.Entry.Safety ? "workview.overview.safety" : "workview.overview.required"), row.ElementText);
                Assert.Contains(row.ElementText, row.AccessibleName);
            }
            foreach (var view in WorkViewSetting.All)
            {
                var presence = row.Entry.For(view);
                if (row.Entry.Kind == WorkViewKind.Column)
                {
                    string key = presence switch
                    {
                        Presence.Hidden => "workview.column.notOffered",
                        Presence.Overflow or Presence.Collapsed => "workview.column.off",
                        _ => "workview.column.on"
                    };
                    Assert.Equal(WorkViewOverviewCatalog.T(key), row.Cell(view));
                }
                else if (presence == Presence.Required)
                    Assert.Equal(WorkViewOverviewCatalog.T("workview.presence.required"), row.Cell(view));
            }
        }
    }

    [Theory]
    [InlineData(Presence.Direct, "workview.presence.direct")]
    [InlineData(Presence.Overflow, "workview.presence.overflow")]
    [InlineData(Presence.Collapsed, "workview.presence.collapsed")]
    [InlineData(Presence.Required, "workview.presence.required")]
    public void NonColumnCellsProjectEveryPresenceWithoutChangingThePolicy(Presence presence, string key)
    {
        var entry = new WorkViewEntry("archive.synthetic", WorkViewPage.Archive, WorkViewKind.Element,
            presence, presence, presence);
        var row = WorkViewOverview.Project(entry);
        Assert.All(WorkViewSetting.All, view => Assert.Equal(WorkViewOverviewCatalog.T(key), row.Cell(view)));
        Assert.Same(entry, row.Entry);
        Assert.Equal(presence, entry.Barebones);
    }

    [Fact]
    public void UnofferedColumnDoesNotPretendToHaveAnAlternativeAction()
    {
        var entry = new WorkViewEntry("archive.columns.synthetic", WorkViewPage.Archive, WorkViewKind.Column,
            Presence.Hidden, Presence.Overflow, Presence.Direct);
        var row = WorkViewOverview.Project(entry);
        Assert.Null(row.AlternativeLabel);
        Assert.Equal("Not offered", row.Cell(WorkView.Barebones));
        Assert.Equal("Off (selectable)", row.Cell(WorkView.Simple));
        Assert.Equal("On", row.Cell(WorkView.Advanced));
    }

    [Fact]
    public void FilteringIsLiteralAllWordCaseInsensitiveAndCombinesWithPage()
    {
        var rows = WorkViewOverview.Build();
        var row = rows.Single(r => r.Id == "archive.filters.applied");
        Assert.All(rows, r => Assert.True(WorkViewOverview.Matches(r, null, " \t")));
        Assert.True(WorkViewOverview.Matches(row, WorkViewPage.Archive, "APPLIED archive"));
        Assert.False(WorkViewOverview.Matches(row, WorkViewPage.Media, "applied"));
        Assert.False(WorkViewOverview.Matches(row, null, "applied impossible-word"));
        Assert.False(WorkViewOverview.Matches(row, null, ".*")); // not regex syntax
        var named = WorkViewOverview.Project(row.Entry, _ => "Needle label");
        Assert.True(WorkViewOverview.Matches(named, null, "needle"));
        foreach (var page in Enum.GetValues<WorkViewPage>())
            Assert.Equal(WorkViewPolicy.Entries.Count(e => e.Page == page), rows.Count(r => WorkViewOverview.Matches(r, page, null)));
        var hidden = rows.First(r => r.Entry.Kind != WorkViewKind.Column && r.Entry.Barebones == Presence.Hidden);
        Assert.True(WorkViewOverview.Matches(hidden, null, hidden.Entry.Alternative));
    }

    [Theory]
    [InlineData("en", "Under More", "Off (selectable)", "Not offered", "Always present")]
    [InlineData("de", "Unter Mehr", "Aus (wählbar)", "Nicht angeboten", "Immer vorhanden")]
    public void LocaleReprojectionKeepsIdsAndLocalizesCellsPagesAndProtection(string language, string overflow, string off, string notOffered, string required)
    {
        var before = WorkViewOverview.Build();
        Texts.SetLanguage(language);
        var after = WorkViewOverview.Build();
        Assert.Equal(before.Select(row => row.Id), after.Select(row => row.Id));
        var action = after.Single(r => r.Id == "archive.header.cleanup");
        Assert.Equal(overflow, action.Cell(WorkView.Barebones));
        var column = after.Single(r => r.Id == "archive.columns.ArchiveThreadTable.tags");
        Assert.Equal(off, column.Cell(WorkView.Barebones));
        var synthetic = WorkViewOverview.Project(column.Entry with { Barebones = Presence.Hidden });
        Assert.Equal(notOffered, synthetic.Cell(WorkView.Barebones));
        var protectedRow = after.Single(r => r.Id == "archive.filters.applied");
        Assert.Equal(required, protectedRow.Cell(WorkView.Barebones));
        Assert.Contains(required, protectedRow.ElementText);
        Assert.Equal(language == "de" ? "Archiv" : "Archive", action.PageLabel);
        Assert.Equal(CommandCatalog.Label("archive.cleanup"), action.Label);
        Assert.Contains(WorkViewOverview.ViewLabel(WorkView.Simple, true), action.CellName(WorkView.Simple, true));
        Assert.Contains(action.AccessibleName, action.CellName(WorkView.Simple, true));
        Assert.NotEqual(action.CellName(WorkView.Simple, true), action.CellName(WorkView.Simple, false));
    }

    [Fact]
    public void CatalogKeysAreUniqueTranslatedAndHaveMatchingFormatArguments()
    {
        var entries = WorkViewOverviewCatalog.Entries;
        Assert.Equal(entries.Length, entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English), entry.Key);
            Assert.False(string.IsNullOrWhiteSpace(entry.German), entry.Key);
            Assert.DoesNotContain('\u2192', entry.English); // no arrow/lock glyph texticons
            Assert.DoesNotContain('\u2192', entry.German);
            var en = System.Text.RegularExpressions.Regex.Matches(entry.English, @"\{\d+\}").Select(m => m.Value).Order();
            var de = System.Text.RegularExpressions.Regex.Matches(entry.German, @"\{\d+\}").Select(m => m.Value).Order();
            Assert.Equal(en, de);
        }
    }
}
