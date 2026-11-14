using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
using MeowChan.DesktopTests.DesignLint;

namespace MeowChan.DesktopTests;

public sealed class HeaderIoAlignmentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WordRateMatrixUsesSolidThenDashedRowsAndResetsWithoutStaleColumns(bool firstStacked)
    {
        Grid grid = new() { ColumnDefinitions = new("Auto,Auto,Auto,Auto"), RowDefinitions = new("Auto,Auto") };
        TextBlock firstWord = new(), firstValue = new(), secondWord = new(), secondValue = new();
        foreach (bool stacked in new[] { firstStacked, !firstStacked, firstStacked })
        {
            MainWindow.ArrangeHeaderRates(grid, firstWord, firstValue, secondWord, secondValue, stacked);
            Assert.Equal(stacked ? 2 : 1, grid.RowDefinitions.Count);
            Assert.Equal(stacked ? 2 : 4, grid.ColumnDefinitions.Count);
            Assert.Equal(0, Grid.GetRow(firstWord));
            Assert.Equal(0, Grid.GetColumn(firstWord));
            Assert.Equal(0, Grid.GetRow(firstValue));
            Assert.Equal(1, Grid.GetColumn(firstValue));
            Assert.Equal(stacked ? 1 : 0, Grid.GetRow(secondWord));
            Assert.Equal(stacked ? 0 : 2, Grid.GetColumn(secondWord));
            Assert.Equal(stacked ? 1 : 0, Grid.GetRow(secondValue));
            Assert.Equal(stacked ? 1 : 3, Grid.GetColumn(secondValue));
            Assert.Equal(4, grid.ColumnSpacing);
            Assert.Equal(2, grid.RowSpacing);
            Assert.Equal("HeaderIoLabel", grid.ColumnDefinitions[0].SharedSizeGroup);
            Assert.Equal("HeaderIoRate", grid.ColumnDefinitions[1].SharedSizeGroup);
            Assert.Equal(56, grid.ColumnDefinitions[1].MinWidth);
            Assert.Equal(stacked ? 0 : 8, secondWord.Margin.Left);
            if (!stacked)
            {
                Assert.Equal("HeaderIoLabel2", grid.ColumnDefinitions[2].SharedSizeGroup);
                Assert.Equal("HeaderIoRate", grid.ColumnDefinitions[3].SharedSizeGroup);
                Assert.Equal(56, grid.ColumnDefinitions[3].MinWidth);
                Assert.Equal(12, grid.ColumnSpacing + secondWord.Margin.Left);
            }
        }
    }

    [Fact]
    public void HeaderUsesTwoSharedLabelGroupsAndTwoSharedRateGroupsWithCaptionTnumValues()
    {
        XDocument document = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == name);
        Assert.Equal("True", (string?)Named("TransferLayoutGrid").Attribute("Grid.IsSharedSizeScope"));
        foreach (string name in new[] { "TransferDownMark", "TransferUpMark", "TransferWriteMark", "TransferReadMark" })
        {
            XElement word = Named(name);
            Assert.Equal("TextBlock", word.Name.LocalName);
            Assert.Equal("muted", (string?)word.Attribute("Classes"));
            Assert.Equal("{StaticResource Workspace.Text.Caption}", (string?)word.Attribute("FontSize"));
            Assert.Equal("14", (string?)word.Attribute("LineHeight"));
            Assert.Equal("Normal", (string?)word.Attribute("FontWeight"));
            Assert.Equal("Raw", (string?)word.Attribute("AutomationProperties.AccessibilityView"));
        }
        foreach (string name in new[] { "TransferSpeedLabel", "TransferUploadLabel", "TransferDiskLabel", "TransferDiskReadLabel" })
        {
            XElement value = Named(name);
            Assert.Equal("Right", (string?)value.Attribute("TextAlignment"));
            Assert.Equal("tnum", (string?)value.Attribute("FontFeatures"));
            Assert.Equal("0 B/s", (string?)value.Attribute("Text"));
            Assert.Equal("Normal", (string?)value.Attribute("FontWeight"));
            Assert.Equal("{StaticResource Workspace.Text.Caption}", (string?)value.Attribute("FontSize"));
            Assert.Equal("{DynamicResource Workspace.Text}", (string?)value.Attribute("Foreground"));
            Assert.Equal("14", (string?)value.Attribute("LineHeight"));
            Assert.Equal("Raw", (string?)value.Attribute("AutomationProperties.AccessibilityView"));
        }
        foreach (string name in new[] { "TransferSpeedText", "TransferDiskText" })
        {
            XElement block = Named(name);
            Assert.Equal("Grid", block.Name.LocalName);
            Assert.Equal("{StaticResource Workspace.Space.2}", (string?)block.Attribute("RowSpacing"));
            Assert.Equal("{StaticResource Workspace.Space.4}", (string?)block.Attribute("ColumnSpacing"));
            Assert.Contains(block.Descendants(), node => (string?)node.Attribute("SharedSizeGroup") == "HeaderIoLabel");
            Assert.Contains(block.Descendants(), node => (string?)node.Attribute("SharedSizeGroup") == "HeaderIoLabel2");
            Assert.Contains(block.Descendants(), node => (string?)node.Attribute("SharedSizeGroup") == "HeaderIoRate"
                && (string?)node.Attribute("MinWidth") == "56");
        }
        Assert.DoesNotContain(document.Descendants(), node => (string?)node.Attribute(x + "Name") is "TransferSpeedTitle" or "TransferDiskTitle");
        Assert.DoesNotContain(document.Descendants(), node => (string?)node.Attribute(x + "Name") is "TransferDownIcon" or "TransferUpIcon");
        Assert.DoesNotContain(DesignLintScanner.ScanFile("Desktop/Shell/MainWindow.axaml", File.ReadAllText(Root("Desktop/Shell/MainWindow.axaml")),
            new double[] { 11, 12, 13, 14, 16, 20 }), violation => violation.Rule == DesignRules.GlyphIcon);
    }

    [Fact]
    public void FirstLayoutUsesTheRowGraphSizeAndNoIconAttachmentRemains()
    {
        XDocument document = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == name);
        Assert.Equal("128", (string?)Named("TransferSpeedGraph").Attribute("Width"));
        Assert.Equal("14", (string?)Named("TransferSpeedGraph").Attribute("Height"));
        Assert.Equal("128", (string?)Named("TransferDiskGraph").Attribute("Width"));
        Assert.Equal("14", (string?)Named("TransferDiskGraph").Attribute("Height"));
        string chrome = File.ReadAllText(Root("Desktop/Shell/MainWindow.Chrome.cs"));
        Assert.DoesNotContain("Attach(TransferDown", chrome);
        Assert.DoesNotContain("Attach(TransferUp", chrome);
    }

    [Theory]
    [InlineData("TransferSpeedText", "TransferDownCell", "TransferSpeedLabel", "TransferUpCell", "TransferUploadLabel")]
    [InlineData("TransferDiskText", "TransferReadCell", "TransferDiskReadLabel", "TransferWriteCell", "TransferDiskLabel")]
    public void InitialMatrixOrdersTheSolidDirectionBeforeTheDashedDirection(string gridName,
        string firstWord, string firstRate, string secondWord, string secondRate)
    {
        XDocument document = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement grid = document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == gridName);
        XElement[] cells = grid.Elements().Where(node => node.Attribute(x + "Name") != null).ToArray();
        Assert.Equal(new[] { firstWord, firstRate, secondWord, secondRate }, cells.Select(node => (string?)node.Attribute(x + "Name")));
        for (int i = 0; i < cells.Length; i++)
            Assert.Equal(i, (int?)cells[i].Attribute("Grid.Column") ?? 0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IconModeIsOptInAndSwitchesBackWithoutHidingTheCell(bool firstIcons)
    {
        Grid cell = new();
        TextBlock word = new();
        Control marker = new();
        cell.Children.Add(word);
        cell.Children.Add(marker);
        foreach (bool icons in new[] { firstIcons, !firstIcons, firstIcons })
        {
            MainWindow.SetHeaderMarkerMode(word, marker, icons);
            Assert.Equal(!icons, word.IsVisible);
            Assert.Equal(icons, marker.IsVisible);
            Assert.True(cell.IsVisible);
        }
    }

    [Theory]
    [InlineData("Down", "NetworkDown", "Workspace.Accent")]
    [InlineData("Up", "NetworkUp", "Workspace.Accent")]
    [InlineData("Read", "DiskRead", "Workspace.Stats.Disk")]
    [InlineData("Write", "DiskWrite", "Workspace.Stats.Disk")]
    public void OptionalMarkersUseGraphColoursAndKeepOneAccessibleButton(string name, string direction, string brush)
    {
        XDocument document = XDocument.Load(Root("Desktop/Shell/MainWindow.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement cell = document.Descendants().Single(node => (string?)node.Attribute(x + "Name") == "Transfer" + name + "Cell");
        XElement icon = cell.Elements().Single(node => node.Name.LocalName == "HeaderIoMarker");
        Assert.Equal(direction, (string?)icon.Attribute("Direction"));
        Assert.Equal("False", (string?)icon.Attribute("IsVisible"));
        Assert.Equal("{DynamicResource " + brush + "}", (string?)icon.Attribute("Stroke"));
        Assert.Equal("Raw", (string?)icon.Attribute("AutomationProperties.AccessibilityView"));
        Assert.Equal("Raw", (string?)cell.Attribute("AutomationProperties.AccessibilityView"));
        Assert.Equal("Transparent", (string?)cell.Attribute("Background"));
        Assert.DoesNotContain(cell.DescendantsAndSelf().Attributes(), a => a.Name.LocalName is "Click" or "PointerPressed");
        Assert.Equal("Button", cell.Ancestors().First(node => node.Name.LocalName == "Button").Name.LocalName);
    }

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));
}
