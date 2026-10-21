using MeowChan.Desktop.Legal;

namespace MeowChan.DesktopTests;

public sealed class OfflineLegalCatalogTests
{
    [Fact]
    public void PackageInventoryAndFullTextsUseShippedNotices()
    {
        string root = FindRepository();
        OfflineLegalCatalog catalog = OfflineLegalCatalog.Load(root);

        Assert.Null(catalog.Error);
        Assert.Contains(catalog.Entries, e => e.Name == "Avalonia" && e.Version == "12.1.4" && e.FullText.Contains("MIT License"));
        Assert.Contains(catalog.Entries, e => e.Name == "Feather Icons" && e.Copyright.Contains("Cole Bemis") && e.FullText.Contains("THE SOFTWARE IS PROVIDED"));
        Assert.Contains(catalog.Entries, e => e.Name == "VideoLAN.LibVLC.Mac" && e.License.Contains("GPL-2.0-or-later") && e.FullText.Contains("liblua_plugin.dylib"));
        Assert.Contains(catalog.Entries, e => e.Name == "MeowChan" && e.FullText.Contains("Artistic License"));
        Assert.Contains(catalog.Search("dolby_surround_decoder"), e => e.Name == "VideoLAN.LibVLC.Mac");
        Assert.Contains(catalog.Search("Rasmus Andersson"), e => e.Name == "Avalonia.Fonts.Inter");
    }

    private static string FindRepository([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        // Artifact-path test outputs can be outside the checkout. Locate this
        // source file's repository, then check the actual packaged inputs.
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
        if (!File.Exists(Path.Combine(root, "THIRD-PARTY-NOTICES.md")) || !File.Exists(Path.Combine(root, "LICENSE")))
            throw new DirectoryNotFoundException("Repository notices not found at " + root);
        return root;
    }
}
