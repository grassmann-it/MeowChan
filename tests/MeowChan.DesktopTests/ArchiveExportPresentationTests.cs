namespace MeowChan.DesktopTests;

public sealed class ArchiveExportPresentationTests
{
    [Fact]
    public void FreshFolderSuggestionNeverReusesAnExistingDirectoryOrFile()
    {
        using TempDirectory directory = new();
        ResourceId id = new("4chan", "g", "123");
        string first = ArchivePage.FreshExportDestination(directory.Path, id);
        Directory.CreateDirectory(first);
        string second = ArchivePage.FreshExportDestination(directory.Path, id);
        Assert.NotEqual(first, second);
        File.WriteAllText(second, "unrelated file");
        string third = ArchivePage.FreshExportDestination(directory.Path, id);
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
        Assert.Equal(directory.Path, Path.GetDirectoryName(third));
    }
}
