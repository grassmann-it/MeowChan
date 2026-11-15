using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>Plan AP5 §1.4 (WV2): the archive list's header, search scope, filter fields and results bar per work view (pure, no window).</summary>
public sealed class WorkViewArchiveTests
{
    private static Presence P(string id, WorkView view) => WorkViewPolicy.Resolve(id, view);

    // The matrix of §1.4 verbatim: id → (Barebones, Simple, Advanced).
    public static TheoryData<string, Presence, Presence, Presence> Matrix => new()
    {
        { "archive.header.refresh", Presence.Overflow, Presence.Direct, Presence.Direct },
        { "archive.header.cleanup", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.header.transfer-preview", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.header.transfer-export", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.search.field", Presence.Required, Presence.Required, Presence.Required },
        { "archive.search.scope", Presence.Hidden, Presence.Direct, Presence.Direct },
        { "archive.filters.expander", Presence.Collapsed, Presence.Collapsed, Presence.Collapsed },
        { "archive.filters.source", Presence.Direct, Presence.Direct, Presence.Direct },
        { "archive.filters.board", Presence.Direct, Presence.Direct, Presence.Direct },
        { "archive.filters.tags", Presence.Direct, Presence.Direct, Presence.Direct },
        { "archive.filters.sort", Presence.Direct, Presence.Direct, Presence.Direct },
        { "archive.filters.state", Presence.Overflow, Presence.Direct, Presence.Direct },
        { "archive.filters.date", Presence.Overflow, Presence.Direct, Presence.Direct },
        { "archive.filters.tag-mode", Presence.Overflow, Presence.Direct, Presence.Direct },
        { "archive.filters.capture", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.filters.posts", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.filters.work", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.filters.zone", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.filters.applied", Presence.Required, Presence.Required, Presence.Required },
        { "archive.results.bar", Presence.Required, Presence.Required, Presence.Required },
        { "archive.results.layout-list", Presence.Overflow, Presence.Direct, Presence.Direct },
        { "archive.results.layout-table", Presence.Overflow, Presence.Direct, Presence.Direct },
        { "archive.results.gallery", Presence.Direct, Presence.Direct, Presence.Direct },
        { "archive.results.favorites", Presence.Overflow, Presence.Direct, Presence.Direct },
        { "archive.results.columns", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.results.export", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.results.reader-groups", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.results.saved-searches", Presence.Overflow, Presence.Overflow, Presence.Direct },
        { "archive.results.bookmarks", Presence.Overflow, Presence.Overflow, Presence.Direct },
    };

    [Theory, MemberData(nameof(Matrix))]
    public void EachRowFollowsThePlanMatrix(string id, Presence barebones, Presence simple, Presence advanced)
    {
        Assert.Equal(barebones, P(id, WorkView.Barebones));
        Assert.Equal(simple, P(id, WorkView.Simple));
        Assert.Equal(advanced, P(id, WorkView.Advanced));
    }

    [Fact]
    public void FilterFieldsUnderMoreGrowWithTheReduction()
    {
        Assert.Equal(["archive.filters.state", "archive.filters.tag-mode", "archive.filters.capture", "archive.filters.posts",
            "archive.filters.work", "archive.filters.date", "archive.filters.zone"], WorkViewPolicy.ArchiveFiltersUnderMore(WorkView.Barebones));
        Assert.Equal(["archive.filters.capture", "archive.filters.posts", "archive.filters.work", "archive.filters.zone"],
            WorkViewPolicy.ArchiveFiltersUnderMore(WorkView.Simple));
        Assert.Empty(WorkViewPolicy.ArchiveFiltersUnderMore(WorkView.Advanced));
    }

    // R6: a field with a value, an applied filter or an error is never put under More filters, in any view.
    [Fact]
    public void AFieldWithAValueStaysOnTheSurface()
    {
        foreach (WorkView view in WorkViewSetting.All)
        {
            Assert.Empty(WorkViewPolicy.ArchiveFiltersUnderMore(view, _ => true));
            foreach (string id in WorkViewPolicy.ArchiveFilterGroupIds())
                Assert.DoesNotContain(id, WorkViewPolicy.ArchiveFiltersUnderMore(view, held => held == id));
        }
        // Only the field that holds the value moves out; its neighbours stay reduced.
        Assert.Equal(["archive.filters.tag-mode", "archive.filters.capture", "archive.filters.posts", "archive.filters.work",
            "archive.filters.date", "archive.filters.zone"],
            WorkViewPolicy.ArchiveFiltersUnderMore(WorkView.Barebones, id => id == "archive.filters.state"));
    }

    [Fact]
    public void TheScopeChipIsHiddenOnlyForTheTextScopeAndKeepsItsShortcutRoute()
    {
        Assert.False(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("archive.search.scope", WorkView.Barebones)));
        Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("archive.search.scope", WorkView.Barebones, nonDefaultState: true)));
        Assert.All(new[] { WorkView.Simple, WorkView.Advanced },
            view => Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("archive.search.scope", view))));
        // The scope menu opens with Alt+Down / F4 in the field, which every view shows.
        Assert.Equal("archive.search.field", WorkViewPolicy.Get("archive.search.scope").Alternative);
        Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, P("archive.search.field", view)));
    }

    // R4/R5: data and active filters are never reduced.
    [Fact]
    public void AppliedFilterChipsAndTheResultsBarAreRequiredInEveryView() =>
        Assert.All(new[] { "archive.filters.applied", "archive.results.bar", "archive.search.field" },
            id => Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, P(id, view))));

    [Fact]
    public void ResultsBarItemsMoveUnderMoreInTheSameBarAndKeepTheirCommands()
    {
        foreach (WorkViewEntry entry in WorkViewPolicy.Entries.Where(entry => entry.Id.StartsWith("archive.results.", StringComparison.Ordinal)
            && entry.Kind == WorkViewKind.Action))
            Assert.All(WorkViewSetting.All, view => Assert.NotEqual(Presence.Hidden, entry.For(view)));
        // Barebones keeps only "media of the matching threads" on the bar; the rest is under More and still has its shortcut.
        Assert.Equal(OverflowMode.AsNeeded, WorkViewPresenter.ToolBarMode(P("archive.results.gallery", WorkView.Barebones)));
        foreach (string id in new[] { "layout-list", "layout-table", "favorites", "columns", "export", "reader-groups", "saved-searches", "bookmarks", "other" })
            Assert.Equal(OverflowMode.Always, WorkViewPresenter.ToolBarMode(P("archive.results." + id, WorkView.Barebones)));
        foreach (string id in new[] { "layout-list", "layout-table", "favorites", "gallery" })
            Assert.All(new[] { WorkView.Simple, WorkView.Advanced },
                view => Assert.Equal(OverflowMode.AsNeeded, WorkViewPresenter.ToolBarMode(P("archive.results." + id, view))));
        Assert.Equal("favorites.open", WorkViewPolicy.Get("archive.results.favorites").CommandId);
        Assert.Equal("table.choose-columns", WorkViewPolicy.Get("archive.results.columns").CommandId);
    }

    [Fact]
    public void HeaderToolsMoveUnderTheSameBarsMore()
    {
        Assert.Equal(OverflowMode.Always, WorkViewPresenter.ToolBarMode(P("archive.header.refresh", WorkView.Barebones)));
        Assert.Equal(OverflowMode.AsNeeded, WorkViewPresenter.ToolBarMode(P("archive.header.refresh", WorkView.Simple)));
        Assert.Equal(OverflowMode.Always, WorkViewPresenter.ToolBarMode(P("archive.header.cleanup", WorkView.Simple)));
        Assert.Equal(OverflowMode.AsNeeded, WorkViewPresenter.ToolBarMode(P("archive.header.cleanup", WorkView.Advanced)));
        Assert.All(new[] { "refresh", "cleanup", "transfer-preview", "transfer-export" }, name =>
            Assert.NotNull(WorkViewPolicy.Get("archive.header." + name).CommandId));
    }

    // ---- The XAML carries exactly the rows the policy has ----

    [Fact]
    public void EveryArchiveRowOfAControlKindIsRegisteredInTheArchiveXamlOrCode()
    {
        string xaml = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.axaml"));
        string code = string.Join("\n", Directory.EnumerateFiles(Path("Desktop/Views/Archive"), "*.cs").Select(File.ReadAllText));
        string[] missing = WorkViewPolicy.Entries
            .Where(entry => entry.Page == WorkViewPage.Archive && entry.Kind is WorkViewKind.Action or WorkViewKind.Element or WorkViewKind.Section
                && !entry.Id.EndsWith(".other", StringComparison.Ordinal))
            .Select(entry => entry.Id)
            .Where(id => !xaml.Contains($"WorkViewElement.Id=\"{id}\"", StringComparison.Ordinal)
                && !code.Contains($"SetId(manager, \"{id}\")", StringComparison.Ordinal))
            .ToArray();
        Assert.True(missing.Length == 0, "Archive rows without a registered control: " + string.Join(", ", missing));
    }

    [Fact]
    public void FilterGroupsAreListedInSurfaceOrderInTheXaml()
    {
        string xaml = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.axaml"));
        string[] inXaml = Regex.Matches(xaml, "WorkViewElement\\.Id=\"(archive\\.filters\\.[A-Za-z-]+)\"").Select(match => match.Groups[1].Value)
            .Where(id => WorkViewPolicy.ArchiveFilterGroupIds().Contains(id)).ToArray();
        Assert.Equal(WorkViewPolicy.ArchiveFilterGroupIds(), inXaml);
        // The page's field list (code) names the same groups: one x:Name per policy id, each used by ArchivePage.WorkViews.cs.
        string page = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.WorkViews.cs"));
        string[] names = Regex.Matches(xaml, "x:Name=\"(ArchiveFilterGroup\\w+)\"").Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(WorkViewPolicy.ArchiveFilterGroupIds().Count, names.Length);
        Assert.All(names, name => Assert.Contains(name, page, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryActionOfTheArchiveBarsHasAnId()
    {
        string xaml = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.axaml"));
        foreach (string toolbar in new[] { "ArchiveHeaderActions", "ArchiveResultActions" })
        {
            int start = xaml.IndexOf($"x:Name=\"{toolbar}\"", StringComparison.Ordinal);
            int end = xaml.IndexOf("</desktop:ActionToolBar>", start, StringComparison.Ordinal);
            string block = xaml[start..end];
            string[] untagged = Regex.Matches(block, "<(?:Button|ToggleButton|SplitButton) x:Name=\"(\\w+)\"[^>]*>")
                .Where(match => !match.Value.Contains("WorkViewElement.Id=", StringComparison.Ordinal))
                .Select(match => match.Groups[1].Value).ToArray();
            // The bookmarks button is built in code and registered there; nothing else may lack an id.
            Assert.True(untagged.Length == 0, toolbar + " items without a work-view id: " + string.Join(", ", untagged));
        }
    }

    // The header shortcuts keep working when the button sits under More: the explicit cleanup registration asks the page, not the button.
    [Fact]
    public void ExplicitShortcutRegistrationsDoNotDependOnAMovableButtonBeingVisible()
    {
        string cleanup = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.Cleanup.cs"));
        Assert.DoesNotContain("ArchiveCleanupOpen.IsEffectivelyVisible", cleanup, StringComparison.Ordinal);
        string transfer = File.ReadAllText(Path("Desktop/Views/Archive/ArchivePage.TransferPreview.cs"));
        Assert.DoesNotContain("ArchiveTransferPreview.IsEffectivelyVisible", transfer, StringComparison.Ordinal);
    }

    private static string Path(string relative, [CallerFilePath] string source = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, "../..", relative));
}
