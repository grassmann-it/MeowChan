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
        if (OperatingSystem.IsMacOS())
            Assert.Contains(catalog.Entries, e => e.Name == "VideoLAN.LibVLC.Mac" && e.License.Contains("GPL-2.0-or-later") && e.FullText.Contains("liblua_plugin.dylib"));
        else
            Assert.DoesNotContain(catalog.Entries, e => e.Name == "VideoLAN.LibVLC.Mac");
        Assert.Contains(catalog.Entries, e => e.Name == "MeowChan" && e.FullText.Contains("Artistic License"));
        if (OperatingSystem.IsMacOS())
            Assert.Contains(catalog.Search("dolby_surround_decoder"), e => e.Name == "VideoLAN.LibVLC.Mac");
        Assert.Contains(catalog.Search("Rasmus Andersson"), e => e.Name == "Avalonia.Fonts.Inter");
    }

    [Fact]
    public void LinuxPackageModeDistinguishesHostRuntimeAndBundledLocalTexts()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = FindRepository();
        string fixture = Path.Combine(Path.GetTempPath(), "meowchan-legal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            File.Copy(Path.Combine(root, "THIRD-PARTY-NOTICES.md"), Path.Combine(fixture, "THIRD-PARTY-NOTICES.md"));
            File.Copy(Path.Combine(root, "LICENSE"), Path.Combine(fixture, "LICENSE"));
            File.WriteAllText(Path.Combine(fixture, "linux-video-runtime.json"), "{\"Mode\":\"system\"}");
            OfflineLegalCatalog system = OfflineLegalCatalog.Load(fixture);
            Assert.Contains(system.Entries, e => e.Name == "Linux LibVLC (host required)" && e.FullText.Contains("does not ship a native VLC tree"));
            Assert.DoesNotContain(system.Entries, e => e.Name.EndsWith("(bundled Linux)", StringComparison.Ordinal));

            File.WriteAllText(Path.Combine(fixture, "linux-video-runtime.json"), "{\"Mode\":\"bundled\"}");
            File.WriteAllText(Path.Combine(fixture, "linux-video-runtime-inventory.json"),
                "{\"vlc_version\":\"3.0.20\",\"packages\":[{\"package\":\"libvlc5:amd64\",\"version\":\"3.0.20-1\",\"source\":\"vlc\",\"source_version\":\"3.0.20-1\"}]}");
            Directory.CreateDirectory(Path.Combine(fixture, "linux-video-licenses", "common-licenses"));
            File.WriteAllText(Path.Combine(fixture, "linux-video-licenses", "libvlc5_amd64.copyright"),
                "Copyright: VideoLAN\nLicense: LGPL-2.1+\nFull local component notice");
            File.WriteAllText(Path.Combine(fixture, "linux-video-licenses", "common-licenses", "LGPL-2.1"),
                "Full local common license text");
            OfflineLegalCatalog bundled = OfflineLegalCatalog.Load(fixture);
            Assert.Null(bundled.Error);
            Assert.Contains(bundled.Search("Full local component notice"), e => e.Name == "libvlc5:amd64 (bundled Linux)" && e.Version == "3.0.20-1");
            Assert.Contains(bundled.Search("Full local common license text"), e => e.Name == "Linux license text: common-licenses/LGPL-2.1");
        }
        finally { Directory.Delete(fixture, recursive: true); }
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
