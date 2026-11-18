using Avalonia.Controls;
using Avalonia.LogicalTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

// These are real Avalonia logical-tree attachments, not a source-string check or a
// native window. A label must never remain in a panel's children without its parent.
[Collection("Localization")]
public sealed class WorkspaceChipLogicalParentTests : IDisposable
{
    public WorkspaceChipLogicalParentTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AChipAttachesDetachesAndReattachesWithOneParentForEveryChild(bool negatable, bool exclude)
    {
        ClosableTag chip = WorkspaceChips.CreateTermChip("Source: Danbooru", negatable, exclude, () => { }, () => { });
        Panel row = new WrapPanel();
        LogicalRoot root = new() { Child = row };
        for (int attachment = 0; attachment < 3; attachment++)
        {
            row.Children.Add(chip);
            Assert.Same(row, chip.Parent);
            AssertParentTree(chip, attached: true);
            Assert.Equal("Source: Danbooru", WorkspaceChips.TermChipLabel(chip));
            row.Children.Remove(chip);
            Assert.Null(chip.Parent);
            AssertParentTree(chip, attached: false);
        }
        root.Child = null;
    }

    [Fact]
    public void RebuildingALiveFilterRowKeepsIncludedExcludedAndPlainLabelsParented()
    {
        WrapPanel row = new();
        LogicalRoot root = new() { Child = row };
        for (int refresh = 0; refresh < 4; refresh++)
        {
            Control[] oldChips = row.Children.ToArray();
            row.Children.Clear();
            foreach (Control previous in oldChips) AssertParentTree(previous, attached: false);
            row.Children.Add(WorkspaceChips.CreateTermChip("Source: Danbooru", true, false, () => { }, () => { }));
            row.Children.Add(WorkspaceChips.CreateTermChip("Without type: Video", true, true, () => { }, () => { }));
            row.Children.Add(WorkspaceChips.CreateTermChip("Favourites", false, false, () => { }));
            Assert.Equal(3, row.Children.Count);
            AssertParentTree(row, attached: true);
        }
        root.Child = null;
        AssertParentTree(row, attached: false);
    }

    private static void AssertParentTree(ILogical parent, bool attached)
    {
        Assert.Equal(attached, parent.IsAttachedToLogicalTree);
        foreach (ILogical child in parent.LogicalChildren)
        {
            Assert.Same(parent, child.LogicalParent);
            AssertParentTree(child, attached);
        }
    }

    private sealed class LogicalRoot : Decorator, ILogicalRoot;
}
