using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SettingsConditionalCollection { public const string Name = "Settings conditional rows"; }

/// <summary>
/// SP1: a conditional row is shown only while its condition holds, announces itself when it appears, and is shown with its parent when the settings
/// search jumps to it (and taken back when the visit ends). Real controls, no window: the logic is attached-property based and runs without an app.
/// </summary>
[Collection(SettingsConditionalCollection.Name)]
public sealed class SettingsConditionalRowTests
{
    private static (StackPanel Page, Border Row, TextBox Input) Page(bool when)
    {
        var input = new TextBox { Name = "TaglineInput" };
        AutomationProperties.SetName(input, "Tagline for 'Always the same'");
        var grid = new Grid();
        grid.Children.Add(input);
        var row = new Border { Child = grid };
        row.Classes.Add(SettingsConditional.RowClass);
        SettingsConditional.SetWhen(row, when);
        var parent = new TextBox { Name = "ParentInput" };
        var page = new StackPanel();
        page.Children.Add(parent);
        page.Children.Add(row);
        return (page, row, input);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void TheRowIsShownWhileTheConditionHoldsOrTheSearchRevealedIt(bool when, bool revealed, bool shown) =>
        Assert.Equal(shown, SettingsConditional.IsShown(when, revealed));

    [Fact]
    public void TheRowFollowsItsCondition()
    {
        OwnedUiDispatcher.Run(() =>
        {
            (_, Border row, _) = Page(when: false);
            Assert.False(row.IsVisible);
            SettingsConditional.SetWhen(row, true);
            Assert.True(row.IsVisible);
            SettingsConditional.SetWhen(row, false);
            Assert.False(row.IsVisible);
            Assert.True(SettingsConditional.IsConditionalRow(row));
        });
    }

    [Fact]
    public void ARowWithoutAConditionIsAlwaysShown()
    {
        OwnedUiDispatcher.Run(() =>
        {
            var row = new Border();
            Assert.True(SettingsConditional.GetWhen(row));
            Assert.True(row.IsVisible);
        });
    }

    [Fact]
    public void ARowThatAppearsBecauseOfItsParentIsAnnouncedButARevealIsNot()
    {
        OwnedUiDispatcher.Run(() =>
        {
            List<Control> appeared = [];
            void Seen(Control row) => appeared.Add(row);
            SettingsConditional.RowAppeared += Seen;
            try
            {
                (_, Border row, _) = Page(when: false);
                SettingsConditional.SetWhen(row, true);
                Assert.Same(row, Assert.Single(appeared));
                SettingsConditional.SetWhen(row, false);
                appeared.Clear();
                SettingsConditional.SetRevealed(row, true);
                Assert.True(row.IsVisible);
                Assert.Empty(appeared);
            }
            finally { SettingsConditional.RowAppeared -= Seen; }
        });
    }

    [Fact]
    public void TheAnnouncementIsTheNameOfTheFirstInputElseTheFirstText()
    {
        OwnedUiDispatcher.Run(() =>
        {
            (_, Border row, _) = Page(when: true);
            Assert.Equal("Tagline for 'Always the same'", SettingsConditional.LabelOf(row));
            var textOnly = new Border { Child = new TextBlock { Text = "A plain line" } };
            Assert.Equal("A plain line", SettingsConditional.LabelOf(textOnly));
        });
    }

    [Fact]
    public void ASearchTargetInAHiddenRowRevealsExactlyTheHiddenRowsAroundItAndTheVisitTakesThemBack()
    {
        OwnedUiDispatcher.Run(() =>
        {
            (StackPanel page, Border row, TextBox input) = Page(when: false);
            (_, Border shownRow, TextBox otherInput) = Page(when: true);
            var reveal = new SettingsConditionalReveal();

            Assert.Same(row, Assert.Single(SettingsConditional.HiddenRowsAround(input)));
            Assert.Empty(SettingsConditional.HiddenRowsAround(otherInput));

            reveal.Reveal(input);
            Assert.True(row.IsVisible);
            Assert.Equal(1, reveal.Count);
            // A jump elsewhere gives the first one back.
            reveal.Reveal(otherInput);
            Assert.False(row.IsVisible);
            Assert.Equal(0, reveal.Count);
            reveal.Reveal(input);
            reveal.Release();
            Assert.False(row.IsVisible);
        });
    }

    [Fact]
    public void ARevealedRowTheSearchedUserEditedStaysUntilThePageCloses()
    {
        OwnedUiDispatcher.Run(() =>
        {
            (_, Border row, TextBox input) = Page(when: false);
            var reveal = new SettingsConditionalReveal();
            reveal.Reveal(input);
            reveal.NoteEdited(input);
            reveal.Release();
            Assert.True(row.IsVisible);
            // The condition catching up later does not hide the value the person just typed.
            SettingsConditional.SetWhen(row, true);
            Assert.True(row.IsVisible);
        });
    }

    [Fact]
    public void NestedConditionalRowsAreAllRevealedForATargetInTheInnerOne()
    {
        OwnedUiDispatcher.Run(() =>
        {
            var input = new TextBox();
            var inner = new Border { Child = input };
            inner.Classes.Add(SettingsConditional.RowClass);
            SettingsConditional.SetWhen(inner, false);
            var outer = new Border { Child = new StackPanel { Children = { inner } } };
            outer.Classes.Add(SettingsConditional.RowClass);
            SettingsConditional.SetWhen(outer, false);
            var reveal = new SettingsConditionalReveal();
            reveal.Reveal(input);
            Assert.True(inner.IsVisible && outer.IsVisible);
            Assert.Equal(2, reveal.Count);
            reveal.Release();
            Assert.False(inner.IsVisible || outer.IsVisible);
        });
    }
}

/// <summary>SP1: the pure parts of the header and the results: the label panel arithmetic, the section chip, "/", the header layout.</summary>
public sealed class SettingsSearchPresentationTests
{
    [Theory]
    [InlineData(null, "", null)]
    [InlineData("", "", null)]
    [InlineData("Downloads", "Downloads", null)]
    [InlineData("Downloads › Limits", "Downloads", "Limits")]
    [InlineData("Reading and media › Cards › Tags", "Reading and media", "Cards › Tags")]
    [InlineData("Updates › ", "Updates", null)]
    public void AHitNamesItsSectionAsAChipAndKeepsTheGroupInsideIt(string? group, string section, string? inner) =>
        Assert.Equal((section, inner), SettingsSearchChip.Split(group));

    [Theory]
    [InlineData("/", KeyModifiers.None, false, true)]
    [InlineData("/", KeyModifiers.Shift, false, true)]
    [InlineData("/", KeyModifiers.None, true, false)]
    [InlineData("/", KeyModifiers.Control, false, false)]
    [InlineData("/", KeyModifiers.Alt, false, false)]
    [InlineData("/", KeyModifiers.Meta, false, false)]
    [InlineData("a", KeyModifiers.None, false, false)]
    [InlineData(null, KeyModifiers.None, false, false)]
    public void SlashFocusesTheSearchOnlyWhileNoTextIsBeingTyped(string? symbol, KeyModifiers modifiers, bool typing, bool expected) =>
        Assert.Equal(expected, SettingsSearchShortcut.IsSlash(symbol, modifiers, typing));

    [Theory]
    [InlineData(1100, false)]
    [InlineData(640, false)]
    [InlineData(639.5, true)]
    [InlineData(360, true)]
    public void TheSearchMovesBelowTheTitleOnlyInANarrowWindow(double width, bool stacked) =>
        Assert.Equal(stacked, SettingsHeaderLayout.IsStacked(width));

    [Theory]
    [InlineData(300, 100, 24, 8, 108, 100)]
    [InlineData(100, 200, 24, 8, 76, 68)]
    [InlineData(double.PositiveInfinity, 150, 24, 8, 158, 150)]
    [InlineData(20, 200, 24, 8, 8, 0)]
    public void TheHelpButtonFollowsTheLastCharacterOfTheLabelAndNeverLeavesTheRow(double available, double label, double button, double gap, double buttonX, double labelWidth)
    {
        (double x, double width) = LabelHelpPanel.Place(available, label, button, gap);
        Assert.Equal(buttonX, x);
        Assert.Equal(labelWidth, width);
    }

    [Fact]
    public void ThePanelPlacesTheButtonDirectlyAfterTheLabel()
    {
        OwnedUiDispatcher.Run(() =>
        {
            var label = new Border { Width = 100, Height = 20 };
            var help = new Border { Width = 24, Height = 24 };
            var panel = new LabelHelpPanel { Spacing = 8, Children = { label, help } };
            panel.Measure(new Size(400, 100));
            panel.Arrange(new Rect(0, 0, 400, 100));
            Assert.Equal(132, panel.DesiredSize.Width);
            Assert.Equal(24, panel.DesiredSize.Height);
            Assert.Equal(new Rect(0, 2, 100, 20), label.Bounds);
            Assert.Equal(new Rect(108, 0, 24, 24), help.Bounds);
        });
    }

    [Fact]
    public void AnInvisibleButtonLeavesTheLabelTheWholeRow()
    {
        OwnedUiDispatcher.Run(() =>
        {
            var label = new Border { Width = 100, Height = 20 };
            var help = new Border { Width = 24, Height = 24, IsVisible = false };
            var panel = new LabelHelpPanel { Spacing = 8, Children = { label, help } };
            panel.Measure(new Size(400, 100));
            Assert.Equal(100, panel.DesiredSize.Width);
        });
    }
}
