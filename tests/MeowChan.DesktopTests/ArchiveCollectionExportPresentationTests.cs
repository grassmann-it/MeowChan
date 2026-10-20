using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

public sealed class ArchiveCollectionExportPresentationTests
{
    [Fact]
    public void FrozenSelectionRetainsOrderAndSourceIdentityWhenCallerChangesSelection()
    {
        ResourceId first = new("4chan", "g", "123");
        ResourceId otherSource = new("8kun", "g", "123");
        List<ResourceId> selection = new() { first, otherSource, first };
        ResourceId[] frozen = ArchivePage.FreezeExportMembers(selection);
        selection.Clear();
        Assert.Equal(new[] { first, otherSource }, frozen);
        Assert.Throws<ArgumentException>(() => ArchivePage.FreezeExportMembers(Array.Empty<ResourceId>()));
    }

    [Fact]
    public void CollectionFolderSuggestionAvoidsBothExistingDirectoriesAndFiles()
    {
        using TempDirectory directory = new();
        string first = ArchivePage.FreshCollectionExportDestination(directory.Path, 2);
        Directory.CreateDirectory(first);
        string second = ArchivePage.FreshCollectionExportDestination(directory.Path, 2);
        File.WriteAllText(second, "unrelated");
        string third = ArchivePage.FreshCollectionExportDestination(directory.Path, 2);
        Assert.NotEqual(first, third);
        Assert.NotEqual(second, third);
        Assert.Equal(directory.Path, Path.GetDirectoryName(third));
    }

    [Fact]
    public void ResultContainsEveryMemberOmissionsWarningsAndPreservedPath()
    {
        ResourceId first = new("4chan", "g", "123");
        ResourceId second = new("8kun", "g", "123");
        ArchiveCollectionHtmlExportResult result = new("/published", "/published/index.html", 2, 3, 1,
            new[] { new ArchiveCollectionExportOmission(second, "123", "media", ArchiveExportOmissionReason.VerificationFailed) })
        {
            Metadata = new ArchiveCollectionExportMetadata(1, Guid.NewGuid(), 2,
                new[] { new ArchiveCollectionMember(first, "https://first.test", "threads/a.html", null, 1, 2),
                    new ArchiveCollectionMember(second, "https://second.test", "threads/b.html", null, 2, 1) },
                DateTimeOffset.UtcNow, new ArchiveHtmlExportOptions(), Array.Empty<ArchiveExportFile>()),
            BackupDirectory = "/preserved",
            Warnings = new[] { "Fixture warning" }
        };
        string text = ArchivePage.CollectionExportResultText(result);
        Assert.Contains("4chan /g/ 123", text);
        Assert.Contains("8kun /g/ 123", text);
        Assert.Contains("/published", text);
        Assert.Contains("/preserved", text);
        Assert.Contains("Fixture warning", text);
        Assert.Contains(Texts.T("export.omission.VerificationFailed"), text);
        Assert.DoesNotContain("export.collection", text);
    }
}
