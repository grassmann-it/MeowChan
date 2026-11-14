using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;

namespace MeowChan.DesktopTests;

public sealed class HeaderIoAlignmentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectionsHaveSeparateStableCellsAndSwitchBackWithoutStaleColumns(bool firstStacked)
    {
        Grid grid = new() { ColumnDefinitions = new("Auto,Auto,Auto,Auto,Auto") };
        TextBlock title = new(), firstMark = new(), first = new(), secondMark = new(), second = new();
        foreach (bool stacked in new[] { firstStacked, !firstStacked, firstStacked })
        {
            MainWindow.ArrangeHeaderRates(grid, title, firstMark, first, secondMark, second, stacked);
            Assert.Equal(stacked ? 2 : 1, Grid.GetRowSpan(title));
            Assert.Equal(0, Grid.GetRow(first));
            Assert.Equal(2, Grid.GetColumn(first));
            Assert.Equal(stacked ? 1 : 0, Grid.GetRow(second));
            Assert.Equal(stacked ? 2 : 4, Grid.GetColumn(second));
            Assert.Equal(stacked ? 1 : 3, Grid.GetColumn(secondMark));
            Assert.Equal(stacked ? null : "HeaderIoRate", grid.ColumnDefinitions[4].SharedSizeGroup);
            Assert.Equal(stacked ? 0 : 64, grid.ColumnDefinitions[4].MinWidth);
            Assert.Equal(stacked ? 0 : 8, secondMark.Margin.Left);
        }
    }

    [Fact]
    public void AllFourRatesUseSharedColumnsRightAlignmentAndTabularDigits()
    {
        XDocument document = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == name);
        Assert.Equal("True", (string?)Named("TransferLayoutGrid").Attribute("Grid.IsSharedSizeScope"));
        foreach (string name in new[] { "TransferSpeedLabel", "TransferUploadLabel", "TransferDiskReadLabel", "TransferDiskLabel" })
        {
            XElement value = Named(name);
            Assert.Equal("Right", (string?)value.Attribute("TextAlignment"));
            Assert.Equal("tnum", (string?)value.Attribute("FontFeatures"));
            Assert.Equal("0 B/s", (string?)value.Attribute("Text"));
        }
        foreach (string name in new[] { "TransferSpeedText", "TransferDiskText" })
        {
            XElement block = Named(name);
            Assert.Equal("Grid", block.Name.LocalName);
            Assert.Equal("4", (string?)block.Attribute("RowSpacing"));
            Assert.Equal("Auto,Auto", (string?)block.Attribute("RowDefinitions"));
            Assert.Contains(block.Descendants(), node => (string?)node.Attribute("SharedSizeGroup") == "HeaderIoTitle");
            Assert.Contains(block.Descendants(), node => (string?)node.Attribute("SharedSizeGroup") == "HeaderIoRate"
                && (string?)node.Attribute("MinWidth") == "64");
        }
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
