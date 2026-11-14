using Avalonia.Controls;

namespace MeowChan.DesktopTests;

// AP7: the Export dialog header shows a drop-down only for a real choice; a single option is text.
public sealed class ExportChoiceFieldTests
{
    private static ExportChoiceField.Option[] Options(params string[] labels) =>
        labels.Select(label => new ExportChoiceField.Option(label, label)).ToArray();

    [Fact]
    public void SingleOptionIsPlainTextAndSeveralOptionsAreADropDown()
    {
        ExportChoiceField field = new("Fixture");
        field.Set("Format", Options("HTML page"), "HTML page", true);
        Assert.False(field.Box.IsVisible);
        Assert.True(field.Fixed.IsVisible);
        Assert.Equal("HTML page", field.Fixed.Text);
        Assert.Equal("Format", field.Label.Text);
        field.Set("Format", Options("HTML page", "Link list"), "Link list", true);
        Assert.True(field.Box.IsVisible);
        Assert.False(field.Fixed.IsVisible);
        Assert.Equal("Link list", field.Value);
        field.Set("Format", Options(), null, true);
        Assert.False(field.IsVisible);
    }

    [Fact]
    public void RefreshKeepsTheItemsAndNeverReportsAUserChoice()
    {
        ExportChoiceField field = new("Fixture");
        List<object> chosen = new();
        field.Chosen += chosen.Add;
        field.Set("Scope", Options("This thread", "All matches"), "This thread", true);
        object? items = field.Box.ItemsSource;
        field.Set("Scope", Options("This thread", "All matches"), "All matches", false);
        Assert.Same(items, field.Box.ItemsSource);
        Assert.Equal("All matches", field.Value);
        Assert.False(field.Box.IsEnabled);
        Assert.Empty(chosen);
        field.Box.SelectedIndex = 0;
        Assert.Equal(new object[] { "This thread" }, chosen);
    }
}
