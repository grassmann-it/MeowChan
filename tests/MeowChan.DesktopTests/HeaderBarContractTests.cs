using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// Tom's rules (14.11.2026 evening, reversing the right-edge rule of the same day, narrowed by his item 35 and by his decision of 15.11.2026):
/// the way back of a PAGE (back, "Back to …" of the reader, a detail view, the rule assistant) stands at the far left of its header row, before
/// the title, first in the row's tab order and set apart from the title by a gap and a thin separator; the remaining buttons are right-aligned.
/// Overlay cards (export, link confirmation, filter surface, reset question, shell help) look like dialogs, so their × stands at the right edge
/// like the inline tool rows' (find bar, tag row, preview, Downloads search, details panel): without back slot, gap or separator. All through
/// the one shared <see cref="HeaderBar"/>. Layout runs without an Avalonia application (plain borders); the page rows are checked in their
/// XAML. The × of window dialogs (caption) stays at the right.
/// </summary>
public sealed class HeaderBarContractTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Wv = "using:MeowChan.Desktop.WorkViews";

    private static Border Box(double width, double height = 32) => new() { Width = width, Height = height };

    private static void Layout(Control control, double width, double height = 40)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
    }

    [Fact]
    public void TheBackSlotStandsAtTheFarLeftBeforeTheContentAfterAnIsolatingGapWithASeparator()
    {
        Border back = Box(120), close = Box(32), title = Box(300), more = Box(32);
        title.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left; // a fixed width would otherwise be centred in the content area
        HeaderBar bar = HeaderBar.Create(back, title, more);
        HeaderBar.SetIsBack(close, true);
        bar.Children.Insert(1, close);
        Layout(bar, 700);
        Assert.Equal(0, back.Bounds.Left, 3);
        Assert.Equal(back.Bounds.Right + HeaderBar.BackSpacing, close.Bounds.Left, 3);
        // The content starts BackGap (24 dip, the 8 dip grid) after the back slot; the separator stands in the middle of that gap.
        double contentLeft = close.Bounds.Right + HeaderBar.BackGap;
        Assert.True(title.Bounds.Left >= contentLeft - 0.001, $"title {title.Bounds} starts inside the gap after {close.Bounds}");
        Assert.Equal(contentLeft, title.Bounds.Left, 3);
        Assert.Equal(24, HeaderBar.BackGap);
        double separator = bar.SeparatorX!.Value;
        Assert.True(separator > close.Bounds.Right && separator + HeaderBar.SeparatorThickness < title.Bounds.Left,
            $"separator at {separator} is not inside the gap between {close.Bounds} and {title.Bounds}");
        Assert.Equal(Math.Round(close.Bounds.Right + (HeaderBar.BackGap - HeaderBar.SeparatorThickness) / 2), separator, 3);
        Assert.Equal(32, bar.SeparatorHeight, 3);
        // The remaining button is right-aligned.
        Assert.Equal(700, more.Bounds.Right, 3);
    }

    [Fact]
    public void TheBackButtonIsTheFirstChildAndSeparatedFromTheRestOfTheRow()
    {
        Border back = Box(120), title = Box(200), more = Box(32);
        HeaderBar bar = HeaderBar.Create(back, title, more);
        Assert.Same(back, bar.Children[0]);
        Assert.True(HeaderBar.GetIsBack(back));
        Assert.Contains(HeaderBar.BackClass, back.Classes);
        Assert.True(bar.BackSlotIsFirst);
        Assert.True(bar.EndSlotIsLast);
        Assert.Equal([back], bar.BackChildren);
        Assert.Equal([more], bar.EndChildren);
        Layout(bar, 600);
        // Set apart: the content never touches the back slot, and no other control stands left of it.
        Assert.True(title.Bounds.Left - back.Bounds.Right >= HeaderBar.BackGap - 0.001);
        Assert.NotNull(bar.SeparatorX);
        Assert.All(new Control[] { title, more }, control => Assert.True(control.Bounds.Left > back.Bounds.Right));
    }

    [Fact]
    public void AnInlineRowWithOnlyAnEndCloseHasNoBackGapAndNoSeparator()
    {
        Border content = new() { Height = 32 }, close = Box(32);
        HeaderBar bar = HeaderBar.Create(null, content, close);
        Layout(bar, 400);
        Assert.Null(bar.SeparatorX);
        Assert.Equal(0, content.Bounds.Left, 3);
        Assert.Equal(400, close.Bounds.Right, 3);
        Assert.True(content.Bounds.Right <= close.Bounds.Left - HeaderBar.EndGap + 0.001);
        Assert.Empty(bar.BackChildren);
        Assert.True(bar.EndSlotIsLast);
    }

    [Fact]
    public void ABackChildDeclaredAfterTheContentBreaksTheTabOrderCheck()
    {
        HeaderBar wrong = new();
        Border content = Box(100), early = Box(32);
        HeaderBar.SetIsBack(early, true);
        wrong.Children.Add(content);
        wrong.Children.Add(early);
        Assert.False(wrong.BackSlotIsFirst);
        HeaderBar endFirst = new();
        Border end = Box(32);
        HeaderBar.SetIsEnd(end, true);
        endFirst.Children.Add(end);
        endFirst.Children.Add(Box(100));
        Assert.False(endFirst.EndSlotIsLast);
    }

    [Fact]
    public void AHiddenBackSlotTakesNoRoomAndARowWithoutOneHasNoGapAndNoSeparator()
    {
        Border back = Box(120), close = Box(32), title = new() { Height = 32 };
        HeaderBar bar = HeaderBar.Create(back, title);
        HeaderBar.SetIsBack(close, true);
        bar.Children.Insert(1, close);
        back.IsVisible = false;
        Layout(bar, 500);
        Assert.Equal(0, close.Bounds.Left, 3); // the visible back child moves to the edge
        Assert.NotNull(bar.SeparatorX);
        close.IsVisible = false;
        Layout(bar, 500);
        Assert.Null(bar.SeparatorX); // nothing to set apart: the content gets the whole row
        Assert.Equal(0, title.Bounds.Left, 3);
        Assert.Equal(500, title.Bounds.Width, 3);
    }

    [Fact]
    public void ARowWithOnlyABackButtonHasNoSeparator()
    {
        Border back = Box(120);
        HeaderBar bar = new();
        HeaderBar.SetIsBack(back, true);
        bar.Children.Add(back);
        Layout(bar, 400);
        Assert.Null(bar.SeparatorX);
    }

    [Fact]
    public void TheContentShrinksBeforeTheBackSlotAndTheRemainingButtonsGiveWay()
    {
        Border back = Box(100), title = new() { Height = 32 }, more = Box(40);
        HeaderBar bar = HeaderBar.Create(back, title, more);
        Layout(bar, 400);
        Assert.Equal(0, back.Bounds.Left, 3);
        Assert.Equal(100, back.Bounds.Width, 3);
        Assert.Equal(400, more.Bounds.Right, 3);
        Assert.True(title.Bounds.Right <= more.Bounds.Left - HeaderBar.EndGap + 0.001, $"title {title.Bounds} runs into {more.Bounds}");
    }

    [Fact]
    public void ANarrowBarIsCompactAndAWideOneIsNot()
    {
        HeaderBar bar = HeaderBar.Create(Box(120), Box(100));
        Layout(bar, HeaderBar.CompactWidth - 8);
        Assert.True(bar.IsCompact);
        Layout(bar, HeaderBar.CompactWidth + 8);
        Assert.False(bar.IsCompact);
    }

    [Fact]
    public void TheCloseIsTheDrawnCrossOnASquareWithItsNameAndRenamingKeepsTheIcon()
    {
        Button close = new();
        HeaderBar.MakeClose(close, "Close find");
        Assert.Equal(WorkspaceIcons.Close, WorkspaceIcons.KeyOf(close.Content));
        Assert.Contains(WorkspaceIcons.SquareClass, close.Classes);
        Assert.Equal("Close find", AutomationProperties.GetName(close));
        Assert.Equal("Close find", ToolTip.GetTip(close));
        object icon = close.Content!;
        HeaderBar.MakeClose(close, "Suche schließen", "Suche schließen · Esc");
        Assert.Same(icon, close.Content);
        Assert.Equal("Suche schließen", AutomationProperties.GetName(close));
        Assert.Equal("Suche schließen · Esc", ToolTip.GetTip(close));
    }

    // Inventory (15.11.): PAGES have a back slot at the far left; the inline tool rows and the overlay cards keep their × at the right (end slot).
    // The gallery viewer keeps its own overlay placement (GL3), the popovers and the quick-tag surface their own headers; window modals have
    // the caption's × at the right (OS convention).
    public static TheoryData<string, string> WaysBack => new()
    {
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveGalleryReturn" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveMediaReturn" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveBackButton" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveCloseReader" },
        { "Desktop/Views/Sources/SourcesPage.axaml", "SourceBackButton" },
        { "Desktop/Views/Plugins/PluginsPage.axaml", "PluginBack" },
        { "Desktop/Views/Rules/RulesPage.axaml", "RuleWizardCloseButton" },
    };

    // The × of the overlay cards: the right edge of the card's header row, after the title (the export overlay builds its row in code, OverlayFrameTests).
    public static TheoryData<string, string> OverlayCloses => new()
    {
        { "Desktop/Shell/MainWindow.axaml", "ShellHelpClose" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ArchiveFilterSurfaceClose" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ReaderLinkClose" },
        { "Desktop/Views/Statistics/StatisticsPage.axaml", "ResetConfirmClose" },
    };

    public static TheoryData<string, string> InlineCloses => new()
    {
        { "Desktop/Views/Archive/ArchivePage.axaml", "ReaderFindClose" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ReaderTagClose" },
        { "Desktop/Views/Archive/ArchivePage.axaml", "ClosePreviewButton" },
        { "Desktop/Shell/MainWindow.axaml", "DownloadSearchClose" },
        { "Desktop/Shell/MainWindow.axaml", "InspectorCloseButton" },
    };

    [Theory]
    [MemberData(nameof(WaysBack))]
    public void EveryPageOrOverlayWayBackSitsInAHeaderBarBackSlotAsItsFirstChild(string file, string name)
    {
        XElement control = Named(file, name);
        Assert.Equal("True", BackAttribute(control));
        Assert.Equal("HeaderBar", control.Parent!.Name.LocalName);
        Assert.Null(EndAttribute(control));
        Assert.True(control.Parent.Elements().First() == control || BackAttribute(control.Parent.Elements().First()) == "True",
            name + " is not in the leading run of its row.");
        // Text content would come back with the localizer: the closes take their names in code, the returns from their command.
        Assert.DoesNotContain(control.Attributes(), attribute => attribute.Name.LocalName == "Grid.Column");
    }

    [Theory]
    [MemberData(nameof(OverlayCloses))]
    public void EveryOverlayCardHasItsCrossAtTheRightEdgeAfterTheTitleLikeADialogAndNoBackSlot(string file, string name)
    {
        XElement control = Named(file, name);
        Assert.Equal("True", EndAttribute(control));
        Assert.Null(BackAttribute(control));
        XElement row = control.Parent!;
        Assert.Equal("HeaderBar", row.Name.LocalName);
        Assert.DoesNotContain(row.Elements(), child => BackAttribute(child) == "True"); // no back gap, no separator
        Assert.Same(control, row.Elements().Last());
        Assert.Equal(2, row.Elements().Count()); // the title, then the cross
        Assert.Equal("TextBlock", row.Elements().First().Name.LocalName);
        Assert.Equal("Button", control.Name.LocalName);
        Assert.DoesNotContain(control.Attributes(), attribute => attribute.Name.LocalName == "Grid.Column");
    }

    [Theory]
    [MemberData(nameof(InlineCloses))]
    public void EveryInlineToolRowKeepsItsCloseAtTheRightInTheEndSlotWithoutABackSlot(string file, string name)
    {
        XElement control = Named(file, name);
        Assert.Equal("True", EndAttribute(control));
        Assert.Null(BackAttribute(control));
        XElement row = control.Parent!;
        Assert.Equal("HeaderBar", row.Name.LocalName);
        Assert.DoesNotContain(row.Elements(), child => BackAttribute(child) == "True"); // no back gap, no separator
        Assert.Same(control, row.Elements().Last()); // the rightmost control of its row, last in tab order
        Assert.DoesNotContain(control.Attributes(), attribute => attribute.Name.LocalName == "Grid.Column");
    }

    [Fact]
    public void EveryHeaderBarIsEitherAPageRowWithABackSlotFirstOrAnInlineRowWithOnlyEndButtons()
    {
        int bars = 0, withBack = 0, inline = 0;
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.axaml", SearchOption.AllDirectories))
            foreach (XElement bar in XDocument.Load(file).Descendants().Where(element => element.Name.LocalName == "HeaderBar"))
            {
                bars++;
                XElement[] children = bar.Elements().ToArray();
                string where = Path.GetFileName(file) + "/" + ((string?)bar.Attribute(X + "Name") ?? "HeaderBar");
                int lastBack = Array.FindLastIndex(children, child => BackAttribute(child) == "True");
                int firstEnd = Array.FindIndex(children, child => EndAttribute(child) == "True");
                Assert.True(lastBack >= 0 || firstEnd >= 0, where + " has neither a back slot nor an end slot.");
                Assert.DoesNotContain(children, child => BackAttribute(child) == "True" && EndAttribute(child) == "True");
                if (lastBack >= 0)
                {
                    withBack++;
                    Assert.True(children.Length > lastBack + 1, where + " has no content after the back slot.");
                    Assert.All(children.Take(lastBack + 1), child => Assert.True(BackAttribute(child) == "True", where + ": content before the way back."));
                    Assert.All(children.Take(lastBack + 1), child => Assert.Contains(child.Name.LocalName, new[] { "Button", "ToggleButton" }));
                }
                else inline++;
                if (firstEnd >= 0)
                {
                    Assert.True(firstEnd > lastBack + 1, where + " has no content between the back slot and the end buttons.");
                    Assert.All(children.Skip(firstEnd), child => Assert.True(EndAttribute(child) == "True", where + ": content after the end buttons."));
                    Assert.All(children.Skip(firstEnd), child => Assert.Contains(child.Name.LocalName, new[] { "Button", "ToggleButton" }));
                }
            }
        Assert.True(withBack >= 4, "Expected the reader, plugin, source and rule rows: " + withBack);
        // The five inline tool rows and the four XAML overlay cards (filter surface, link, reset, help) have only end buttons.
        Assert.True(inline >= 9, "Expected the find, tag, preview, search and details rows and the overlay cards: " + inline);
        Assert.Equal(bars, withBack + inline);
    }

    // The ways back of the reader are the first controls of its header row (left to right: the state returns, the narrow list return, the large reader's
    // "Back to {origin}"), then the title; the toolbar below keeps no way out.
    [Fact]
    public void TheReaderHeaderStartsWithTheWaysBackAndTheToolbarKeepsNoWayOut()
    {
        XElement page = XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Root!;
        XElement header = page.Descendants().Single(element => (string?)element.Attribute(X + "Name") == "ReaderHeaderBar");
        Assert.Equal(["ArchiveGalleryReturn", "ArchiveMediaReturn", "ArchiveBackButton", "ArchiveCloseReader", "ArchiveReaderTitle"],
            header.Elements().Select(element => (string?)element.Attribute(X + "Name")));
        XElement toolbar = page.Descendants().Single(element => (string?)element.Attribute(X + "Name") == "ReaderToolbar");
        string[] ids = toolbar.Descendants().Select(element => (string?)element.Attribute(XName.Get("WorkViewElement.Id", Wv))).OfType<string>().ToArray();
        foreach (string wayOut in new[] { "reader.toolbar.close", "reader.toolbar.gallery-return", "reader.toolbar.media-return", "reader.header.back" })
            Assert.DoesNotContain(wayOut, ids);
    }

    // The rule assistant's way back to the overview is the first control of its editor header; the remaining More button is right-aligned.
    [Fact]
    public void TheRuleEditorHeaderStartsWithTheWayBackToTheOverviewAndKeepsMoreAtTheRight()
    {
        XElement header = Named("Desktop/Views/Rules/RulesPage.axaml", "RuleWizardHeader");
        Assert.Equal("HeaderBar", header.Name.LocalName);
        string?[] names = header.Elements().Select(element => (string?)element.Attribute(X + "Name")).ToArray();
        Assert.Equal("RuleWizardCloseButton", names.First());
        Assert.Equal("RulesMoreButton", names.Last());
        Assert.Equal("True", EndAttribute(Named("Desktop/Views/Rules/RulesPage.axaml", "RulesMoreButton")));
        // Save and Discard stay in the footer, never in the header.
        Assert.DoesNotContain("SaveRuleButton", names);
        Assert.DoesNotContain("DiscardRuleButton", names);
    }

    // The code-side texts no longer state the old rule.
    [Fact]
    public void TheHeaderBarSourcesDescribeTheLeftRuleNotTheRightEdgeRule()
    {
        string bar = File.ReadAllText(Root("Desktop/Controls/HeaderBar.cs"));
        string overlay = File.ReadAllText(Root("Desktop/Controls/OverlayFrame.cs"));
        Assert.Contains("far left", bar);
        Assert.DoesNotContain("far right edge", bar);
        Assert.DoesNotContain("alone at the top right", overlay);
        // Overlay cards close on the right like dialogs (15.11.): the cross is the end child, never the back slot.
        Assert.Contains("HeaderBar.Create(null, title, close)", overlay);
        Assert.DoesNotContain("HeaderBar.Create(close, title)", overlay);
        Assert.DoesNotContain("back slot, first in the row's tab order", overlay);
        Assert.Contains("right edge", overlay);
    }

    // Work views never reduce a way out: a back-slot control with a work-view id is Required in every view.
    [Fact]
    public void EveryWayOutWithAWorkViewIdIsRequiredInEveryView()
    {
        int checkedIds = 0;
        foreach (string file in Directory.EnumerateFiles(Root("Desktop"), "*.axaml", SearchOption.AllDirectories))
            foreach (XElement end in XDocument.Load(file).Descendants().Where(element => BackAttribute(element) == "True"))
                if ((string?)end.Attribute(XName.Get("WorkViewElement.Id", Wv)) is { } id)
                {
                    checkedIds++;
                    Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view)));
                }
        Assert.Equal(5, checkedIds); // the four reader ways back and the rule editor's way back to the overview
    }

    // The converted closes are named in code (MakeClose / an icon-only command), never given text content again.
    [Fact]
    public void TheConvertedClosesAreNotGivenTextContentAgain()
    {
        string find = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.ThreadFind.cs"));
        string tags = File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.Tags.cs"));
        string presentation = File.ReadAllText(Root("Desktop/Shell/MainWindow.Presentation.cs"));
        string fullscreen = File.ReadAllText(Root("Desktop/Shell/MainWindow.Fullscreen.cs"));
        Assert.DoesNotContain("ReaderFindClose.Content", find);
        Assert.Contains("HeaderBar.MakeClose(ReaderFindClose", find);
        Assert.DoesNotContain("ReaderTagClose.Content", tags);
        Assert.Contains("HeaderBar.MakeClose(ReaderTagClose", tags);
        // DL-07: the close of the Downloads search bar is a labelled "Close" with the chevron, never a second cross beside the field's clear button.
        Assert.DoesNotContain("HeaderBar.MakeClose(DownloadSearchClose", presentation);
        Assert.Contains("LocalizeDownloadSearchClose();", presentation);
        Assert.Contains("CommandBindings.Apply(ShellHelpClose, \"window.close\", iconOnly: true)", fullscreen);
        Assert.Contains("HeaderBar.MakeClose(ClosePreviewButton", File.ReadAllText(Root("Desktop/Views/Archive/ArchivePage.axaml.cs")));
    }

    private static string? BackAttribute(XElement element) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "HeaderBar.IsBack")?.Value;

    private static string? EndAttribute(XElement element) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "HeaderBar.IsEnd")?.Value;

    private static XElement Named(string file, string name) =>
        XDocument.Load(Root(file)).Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
