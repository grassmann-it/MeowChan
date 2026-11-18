using System.Reflection;
using Ursa.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using MeowChan.Desktop.Controls;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Tags;
using MeowChan.Gallery;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// UX-LEDGER-28 package 1: real controls and design resources, without a native window.
[Collection("Gallery viewer construction")]
public sealed class TaggingLayoutRhythmTests
{
    private static void WithPage(Action<TaggingPage> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        Texts.Initialize();
        Texts.SetLanguage("en");
        using TempDirectory temp = new();
        DesktopSessionHost host = new(temp.Path, Array.Empty<IImageBoard>());
        TaggingPage page = new(host);
        // A detached UserControl has no application resource parent. Give this fixture the product's actual resource scope.
        page.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://MeowChan/"))
            { Source = new Uri("avares://MeowChan/Desktop/WorkspaceStyles.axaml") });
        try { test(page); }
        finally { page.End(); host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    });

    private static T Find<T>(TaggingPage page, string name) where T : Control =>
        ((ILogical)page).GetLogicalDescendants().OfType<T>().Single(c => c.Name == name);

    private static double Token(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, out object? value), key);
        return Assert.IsType<double>(value);
    }

    [Fact]
    public void MajorSectionsAndRelatedRowsUseDifferentTokenRhythms() => WithPage(page =>
    {
        StackPanel sections = Find<StackPanel>(page, "TaggingSections");
        StackPanel queue = Find<StackPanel>(page, "TaggingQueueControls");
        Assert.Equal(Token(sections, "Workspace.Space.16"), sections.Spacing);
        Assert.Equal(Token(sections, "Workspace.Space.16"), sections.Margin.Bottom);
        Assert.Equal(PageHeader.PageTopOffset, sections.Margin.Top);
        Assert.Equal(Token(sections, "Workspace.Space.12"), sections.Margin.Left);
        Assert.Equal(Token(sections, "Workspace.Space.12"), sections.Margin.Right);
        Assert.Equal(Token(queue, "Workspace.Space.8"), queue.Spacing);
        Assert.Equal(new[] { "TaggingQueueActionRow", "TaggingClearConfirm" }, queue.Children.Select(c => c.Name));
        StackPanel heading = Find<StackPanel>(page, "TaggingHeading");
        Assert.Equal(Token(sections, "Workspace.Space.8"), heading.Spacing);
        Assert.Equal(Avalonia.Layout.Orientation.Vertical, heading.Orientation);
        Assert.Equal(new[] { "TaggingTitle", "TaggingSubtitle" }, heading.Children.Select(c => c.Name));
        StackPanel error = Assert.IsType<StackPanel>(page.ErrorPanel.Child);
        Assert.Equal(Token(error, "Workspace.Space.8"), error.Spacing);
        Grid stamp = Find<Grid>(page, "TaggingStamp");
        Assert.Equal(Token(stamp, "Workspace.Space.12"), stamp.ColumnSpacing);
        Assert.Equal(GridUnitType.Star, stamp.ColumnDefinitions[1].Width.GridUnitType);
        // Hidden feedback/secondary rows must not add phantom spacing.
        Assert.False(page.ErrorPanel.IsVisible);
        Assert.Equal(new[] { "TaggingGroupPanel", "TaggingFilterPanel", "TaggingListPresentation" }, page.ListControls.Children.Select(c => c.Name));
    });
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void RenderSnapshot(TaggingPage page, TagQueueSnapshot snapshot, TaggingQueueFilter filter = TaggingQueueFilter.All,
        string? group = null, bool ungrouped = false, bool loading = false, Exception? error = null)
    {
        // Feed immutable queue states into the actual presenter, not a parallel role model.
        object work = typeof(TaggingPage).GetField("_queueWork", Private)!.GetValue(page)!;
        foreach ((string name, object? value) in new (string, object?)[]
        {
            ("Queue", snapshot), ("Loaded", !loading), ("Loading", loading), ("LoadError", error)
        }) typeof(TaggingQueueController).GetProperty(name, Private)!.SetValue(work, value);
        typeof(TaggingPage).GetField("_selectedGroupId", Private)!.SetValue(page, group);
        typeof(TaggingPage).GetField("_ungroupedSelected", Private)!.SetValue(page, ungrouped);
        page.Filter = filter;
        typeof(TaggingPage).GetMethod("Show", Private)!.Invoke(page, null);
    }

    private static TagQueueItem Item(TagQueueState state = TagQueueState.Open) =>
        new(new ResourceId("4chan", "g", "123"), "/media/123", "asset-1", new string('a', 64), 101, "media-1", "original",
            "p1", null, null, null, false, null, "file.png", SavedMediaKind.Image, state, DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void EveryEmptyStateOwnsAtMostOneEnabledPrimaryAndStartRegainsItOnReturn(string language) => WithPage(page =>
    {
        Texts.SetLanguage(language);
        page.AddRequested = () => { };
        TagQueueGroup group = new("empty", "Empty", 0, Array.Empty<string>());
        TagQueueSnapshot open = new(1, 0, Array.Empty<string>(), new[] { Item() }, TagQueueLoadState.Ready, new[] { group });
        TagQueueSnapshot done = new(1, 0, Array.Empty<string>(), new[] { Item(TagQueueState.Done) }, TagQueueLoadState.Ready);
        TagQueueSnapshot skipped = new(1, 0, Array.Empty<string>(), new[] { Item(TagQueueState.Skipped) }, TagQueueLoadState.Ready);
        foreach (WorkView view in WorkViewSetting.All)
        {
            page.ApplyWorkView(view);
            Check(TagQueueSnapshot.Empty, TaggingQueueFilter.All, null, false, TaggingEmptyKind.QueueEmpty, page.EmptyMediaButton);
            Check(done, TaggingQueueFilter.Open, null, false, TaggingEmptyKind.AllDone, page.EmptyMediaButton);
            Check(skipped, TaggingQueueFilter.Open, null, false, TaggingEmptyKind.AllDone, page.EmptyMediaButton);
            Check(open, TaggingQueueFilter.Done, null, false, TaggingEmptyKind.FilterNoHits, page.EmptyShowAllButton);
            Check(open, TaggingQueueFilter.All, group.Id, false, TaggingEmptyKind.GroupEmpty, page.EmptyShowAllButton);
            // An all-grouped queue has an empty "Without group" scope, so it uses the group-empty recommendation.
            Check(new TagQueueSnapshot(1, 0, Array.Empty<string>(), new[] { Item() with { GroupId = group.Id } }, TagQueueLoadState.Ready, new[] { group }), TaggingQueueFilter.All, null, true,
                TaggingEmptyKind.GroupEmpty, page.EmptyShowAllButton);
            RenderSnapshot(page, open);
            Assert.True(page.StartButton.Classes.Contains("primary"));
            Assert.False(page.EmptyMediaButton.Classes.Contains("primary"));
            Assert.False(page.EmptyShowAllButton.Classes.Contains("primary"));
            RenderSnapshot(page, open, loading: true);
            Assert.Equal(TaggingEmptyKind.None, page.EmptyKind);
            Assert.False(page.StartButton.IsEnabled);
            RenderSnapshot(page, open, error: new IOException("unreadable"));
            Assert.Equal(TaggingEmptyKind.None, page.EmptyKind);
            Assert.False(page.StartButton.IsEnabled);
        }

        void Check(TagQueueSnapshot snapshot, TaggingQueueFilter filter, string? groupId, bool ungrouped, TaggingEmptyKind kind, Button primary)
        {
            RenderSnapshot(page, snapshot, filter, groupId, ungrouped);
            Assert.Equal(kind, page.EmptyKind);
            Button[] recommendations = { page.StartButton, page.EmptyMediaButton, page.EmptyShowAllButton };
            Assert.Same(primary, Assert.Single(recommendations.Where(b => b.Classes.Contains("primary"))));
            Assert.True(primary.IsEnabled);
            Assert.True(primary.IsVisible);
            Assert.True(recommendations.Count(b => b.IsEnabled && b.IsVisible && b.Classes.Contains("primary")) <= 1);
            Assert.DoesNotContain("primary", page.StartButton.Classes);
        }
    });
    [Fact]
    public void HelpNeverChangesRoleAndGroupDeletionHasItsOwnDangerButton() => WithPage(page =>
    {
        TagQueueGroup group = new("empty", "Empty", 0, Array.Empty<string>());
        TagQueueSnapshot queue = new(1, 0, Array.Empty<string>(), new[] { Item() }, TagQueueLoadState.Ready, new[] { group });
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            typeof(TaggingPage).GetMethod("Localize", Private)!.Invoke(page, null);
            RenderSnapshot(page, queue, group: group.Id);
            Assert.Equal(TaggingEmptyKind.GroupEmpty, page.EmptyKind);
            Assert.False(page.EmptyHowButton.IsVisible);
            Assert.Equal(Texts.T("tagging.page.empty.how"), page.EmptyHowButton.Content);
            Assert.Contains("link", page.EmptyHowButton.Classes);
            Assert.True(page.EmptyDeleteGroupButton.IsVisible);
            Assert.NotSame(page.EmptyHowButton, page.EmptyDeleteGroupButton);
            Assert.Contains("danger", page.EmptyDeleteGroupButton.Classes);
            Assert.DoesNotContain("link", page.EmptyDeleteGroupButton.Classes);
            Assert.Equal(Texts.T("tagging.page.empty.group.delete"), page.EmptyDeleteGroupButton.Content);
            Assert.True(DeadAffordanceGuard.Handles(page.EmptyDeleteGroupButton, Button.ClickEvent));
            // An ungrouped scope is not a deletable group.
            RenderSnapshot(page, new TagQueueSnapshot(1, 0, Array.Empty<string>(), new[] { Item() with { GroupId = group.Id } }, TagQueueLoadState.Ready, new[] { group }), ungrouped: true);
            Assert.False(page.EmptyDeleteGroupButton.IsVisible);
            RenderSnapshot(page, TagQueueSnapshot.Empty);
            Assert.True(page.EmptyHowButton.IsVisible);
            Assert.Equal(Texts.T("tagging.page.empty.how"), page.EmptyHowButton.Content);
            Assert.False(page.EmptyDeleteGroupButton.IsVisible);
        }
    });
    [Fact]
    public void IconOnlyControlsUseTheSquareTokenButLabelledPresentationsDoNot() => WithPage(page =>
    {
        // Include returns to earlier policies: switching back must restore, not just add, the class.
        foreach (WorkView view in WorkViewSetting.All.Concat(WorkViewSetting.All.Reverse()))
        {
            page.ApplyWorkView(view);
            foreach (Button button in new[] { page.ClearButton, page.ClearDoneButton })
            {
                bool iconOnly = ToolBar.GetOverflowMode(button) != OverflowMode.Always;
                Assert.Equal(iconOnly, button.Classes.Contains(WorkspaceIcons.SquareClass));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.NotNull(ToolTip.GetTip(button));
            }
            Assert.Contains(WorkspaceIcons.SquareClass, page.SelectionClearButton.Classes);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(page.SelectionClearButton)));
            Assert.Contains("Esc", ToolTip.GetTip(page.SelectionClearButton)!.ToString());
            Assert.DoesNotContain(WorkspaceIcons.SquareClass, page.AddButton.Classes);
            Assert.DoesNotContain(WorkspaceIcons.SquareClass, page.StartButton.Classes);
        }
        // Pin the shared class to its actual geometry tokens, not a duplicate hard-coded size.
        System.Xml.Linq.XNamespace x = "https://github.com/avaloniaui";
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, "../.."));
        var square = System.Xml.Linq.XDocument.Load(Path.Combine(root, "Desktop/WorkspaceStyles.axaml"))
            .Descendants(x + "Style").Single(e => (string?)e.Attribute("Selector") == "Button.icon-square");
        var setters = square.Elements(x + "Setter").ToDictionary(e => (string)e.Attribute("Property")!, e => (string)e.Attribute("Value")!);
        Assert.Equal("{StaticResource Workspace.Size.Input}", setters["Width"]);
        Assert.Equal(setters["Width"], setters["Height"]);
    });

    private static string Source([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    [Fact]
    public void StampTitleChangesWeightOnlyAtTheBodyTextSize() => WithPage(page =>
    {
        TextBlock heading = Find<TextBlock>(page, "TaggingStampHeading");
        Assert.Equal(Token(heading, "Workspace.Text.Body"), heading.FontSize);
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, heading.FontWeight);
        Assert.DoesNotContain("section-title", heading.Classes);
    });
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void StampBodyKeepsTheEmptyHintBesideTheInputAndAddButton(string language) => WithPage(page =>
    {
        Texts.SetLanguage(language);
        typeof(TaggingPage).GetMethod("Localize", Private)!.Invoke(page, null);
        RenderSnapshot(page, TagQueueSnapshot.Empty);
        Grid body = Find<Grid>(page, "TaggingStampBody");
        Grid add = Find<Grid>(page, "TaggingStampAddTags");
        Assert.Equal(Token(body, "Workspace.Space.16"), body.ColumnSpacing);
        Assert.Equal(GridUnitType.Star, body.ColumnDefinitions[1].Width.GridUnitType);
        Assert.Equal(new[] { "TaggingStampChips", "TaggingStampAddTags" }, body.Children.Select(c => c.Name));
        Assert.Equal(Token(add, "Workspace.Space.8"), add.ColumnSpacing);
        Assert.Equal(Token(page.StampChips, "Workspace.Space.8"), page.StampChips.ItemSpacing);
        Assert.Equal(Token(page.StampChips, "Workspace.Space.8"), page.StampChips.LineSpacing);
        Assert.Same(add, page.StampInput.Parent);
        Assert.Same(add, page.StampEmptyText.Parent);
        Assert.Equal(Grid.GetRow(page.StampInput), Grid.GetRow(page.StampEmptyText));
        Assert.Equal(Grid.GetColumn(page.StampInput) + 2, Grid.GetColumn(page.StampEmptyText));
        Assert.Equal(page.StampInput.Width, add.ColumnDefinitions[0].Width.Value);
        Assert.Equal(GridUnitType.Star, add.ColumnDefinitions[2].Width.GridUnitType);
        Assert.Equal(1, Grid.GetColumn(body));
        Assert.Equal(1, Grid.GetColumn(add));
        Assert.True(page.StampEmptyText.IsVisible);
        Assert.False(page.StampChips.IsVisible);
        RenderSnapshot(page, new TagQueueSnapshot(1, 0, new[] { "review", "later" }, Array.Empty<TagQueueItem>(), TagQueueLoadState.Ready));
        Assert.False(page.StampEmptyText.IsVisible);
        Assert.True(page.StampChips.IsVisible);
        Assert.Equal(2, page.StampChips.Children.Count);
        page.RestoreStampBlock(TaggingStampBlockState.Closed);
        Assert.False(page.StampOpen);
        page.RestoreStampBlock(TaggingStampBlockState.Open);
        Assert.True(page.StampOpen);
    });
    [Fact]
    public void StateColumnUsesTheSameSingleGapAsEveryOtherColumnInRowsAndHeader() => WithPage(page =>
    {
        foreach (bool preview in new[] { true, false })
        foreach (bool compact in new[] { true, false })
        {
            TaggingQueueRowView row = new(showPreview: preview, compactTable: compact);
            Assert.Equal(Token(page, "Workspace.Space.12"), row.ColumnSpacing);
            // Isolate column geometry from native text shaping: keep the real State container and use cell-sized probes for text cells.
            foreach (TextBlock text in ((ILogical)row).GetLogicalDescendants().OfType<TextBlock>()) text.IsVisible = false;
            foreach (int column in new[] { 5, 6 })
            {
                Control text = row.Children.Single(c => Grid.GetColumn(c) == column);
                Border probe = new() { Margin = text.Margin, HorizontalAlignment = text.HorizontalAlignment };
                Grid.SetColumn(probe, column);
                row.Children.Remove(text);
                row.Children.Add(probe);
            }
            row.Measure(new Size(1440, double.PositiveInfinity));
            row.Arrange(new Rect(0, 0, 1440, 44));
            Control tags = row.Children.Single(c => Grid.GetColumn(c) == 6);
            Control state = row.Children.Single(c => Grid.GetColumn(c) == 7);
            Control group = row.Children.Single(c => Grid.GetColumn(c) == 5);
            Assert.Equal(default(Thickness), state.Margin);
            Assert.Equal(row.ColumnSpacing, state.Bounds.Left - tags.Bounds.Right, 6);
            Assert.Equal(tags.Bounds.Left - group.Bounds.Right, state.Bounds.Left - tags.Bounds.Right, 6);
            page.RestorePreviewColumn(preview);
            Assert.Equal(row.ColumnSpacing, page.ColumnHeader.ColumnSpacing);
            Assert.Equal(default(Thickness), page.ColumnHeader.Children.Single(c => Grid.GetColumn(c) == 7).Margin);
        }
    });
    [Fact]
    public void EnabledStartYieldsToTheEmptyRecommendationAndReclaimsPrimaryWithRows() => WithPage(page =>
    {
        DesktopSessionHost host = (DesktopSessionHost)typeof(TaggingPage).GetField("_host", Private)!.GetValue(page)!;
        Task.Run(() => host.StartAsync()).GetAwaiter().GetResult();
        page.StartTagging = () => Task.CompletedTask;
        page.AddRequested = () => { };
        TagQueueSnapshot open = new(1, 0, Array.Empty<string>(), new[] { Item() }, TagQueueLoadState.Ready);
        foreach (WorkView view in WorkViewSetting.All)
        {
            page.ApplyWorkView(view);
            RenderSnapshot(page, open, TaggingQueueFilter.Done);
            Assert.Equal(TaggingEmptyKind.FilterNoHits, page.EmptyKind);
            Assert.True(page.StartButton.IsEnabled); // A state filter does not prevent continuing the queue.
            Assert.DoesNotContain("primary", page.StartButton.Classes);
            Assert.Same(page.EmptyShowAllButton, Assert.Single(EnabledPrimaries()));
            RenderSnapshot(page, open);
            Assert.True(page.StartButton.IsEnabled);
            Assert.Same(page.StartButton, Assert.Single(EnabledPrimaries()));
        }
        IEnumerable<Button> EnabledPrimaries() => ((ILogical)page).GetLogicalDescendants().OfType<Button>()
            .Concat(page.Actions.Items.OfType<Button>()).Distinct()
            .Where(b => b.IsEffectivelyVisible && b.IsEnabled && b.Classes.Contains("primary"));
    });
}
