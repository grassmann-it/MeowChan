using MeowChan;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ArchiveLeadSelectionTests
{
    [Fact]
    public void ReplacingSelectionDropsAStaleVisibleLead()
    {
        ResourceId a = new("fixture", "b", "a");
        ResourceId b = new("fixture", "b", "b");
        Assert.Equal(b, ArchivePage.ResolveArchiveLeadId([a, b], new HashSet<ResourceId> { b }, a, b));
    }

    [Fact]
    public void MultiSelectionPreservesThePointerChosenLead()
    {
        ResourceId a = new("fixture", "b", "a");
        ResourceId b = new("fixture", "b", "b");
        Assert.Equal(b, ArchivePage.ResolveArchiveLeadId([a, b], new HashSet<ResourceId> { a, b }, b, a));
        Assert.Null(ArchivePage.ResolveArchiveLeadId([a, b], new HashSet<ResourceId>(), b));
    }
}
