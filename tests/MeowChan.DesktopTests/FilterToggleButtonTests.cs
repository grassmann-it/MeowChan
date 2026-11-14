using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// S2: the filter toggle of Archive and Media is one ToggleButton with a count badge, not an Expander.
public sealed class FilterToggleButtonTests
{
    [Fact]
    public void ItIsAToggleButtonWithTheFilterToggleClassThatStartsClosedWithoutABadge()
    {
        FilterToggleButton toggle = new();
        Assert.IsAssignableFrom<Avalonia.Controls.Primitives.ToggleButton>(toggle);
        Assert.Contains("filter-toggle", toggle.Classes);
        Assert.NotEqual(true, toggle.IsChecked);
        Assert.False(toggle.IsBadgeVisible);
        Assert.Equal(string.Empty, toggle.BadgeText);
        Assert.Equal(0, toggle.Count);
    }

    [Theory]
    [InlineData(0, false, "")]
    [InlineData(1, true, "1")]
    [InlineData(3, true, "3")]
    [InlineData(12, true, "12")]
    [InlineData(-4, false, "")]
    public void TheBadgeShowsTheActiveCountOnlyAboveZero(int count, bool visible, string text)
    {
        FilterToggleButton toggle = new();
        toggle.Show("Filters", count, "Filters, " + count + " active");
        Assert.Equal(visible, toggle.IsBadgeVisible);
        Assert.Equal(text, toggle.BadgeText);
        Assert.Equal(Math.Max(0, count), toggle.Count);
        Assert.Equal("Filters", toggle.Label);
    }

    [Fact]
    public void TheAccessibleNameAndTheHintAreTheOwnersAndTheBadgeIsNotAnAccessibleElementOfItsOwn()
    {
        FilterToggleButton toggle = new();
        toggle.Show("Filters and sorting · Unapplied draft", 2, "Filters and sorting, 2 active", "Search to apply them");
        Assert.Equal("Filters and sorting, 2 active", AutomationProperties.GetName(toggle));
        Assert.Equal("Search to apply them", AutomationProperties.GetHelpText(toggle));
        Assert.Equal("Search to apply them", ToolTip.GetTip(toggle));
        Assert.Equal(AccessibilityView.Raw, AutomationProperties.GetAccessibilityView(toggle.Badge));
        toggle.Show("Filters", 0, "Filters");
        Assert.Equal(string.Empty, AutomationProperties.GetHelpText(toggle));
        Assert.Null(ToolTip.GetTip(toggle));
        Assert.False(toggle.IsBadgeVisible);
    }

    [Fact]
    public void TheBadgeUsesTheChipBadgeLanguage()
    {
        FilterToggleButton toggle = new();
        Assert.Contains("chip-badge", toggle.Badge.Classes);
        Assert.Contains("chip-compact", toggle.Badge.Classes);
    }
}
