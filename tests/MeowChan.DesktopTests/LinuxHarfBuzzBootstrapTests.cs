using System.Runtime.InteropServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class LinuxHarfBuzzBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meowchan-text-bootstrap-" + Guid.NewGuid().ToString("N"));
    public LinuxHarfBuzzBootstrapTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void PublishedLibraryWinsOverDevelopmentAssetWithoutSystemFallback()
    {
        string development = Asset("linux-x64");
        Assert.Equal(development, LinuxHarfBuzzBootstrap.ResolveDeliveredPath(_root, Architecture.X64));
        string published = Path.Combine(_root, "libHarfBuzzSharp.so");
        File.WriteAllText(published, "published placeholder");
        Assert.Equal(published, LinuxHarfBuzzBootstrap.ResolveDeliveredPath(_root, Architecture.X64));
        File.Delete(published); File.Delete(development);
        Assert.Throws<DllNotFoundException>(() => LinuxHarfBuzzBootstrap.ResolveDeliveredPath(_root, Architecture.X64));
    }

    [Fact]
    public void DevelopmentAssetMustMatchProcessArchitecture()
    {
        string arm64 = Asset("linux-arm64");
        Assert.Equal(arm64, LinuxHarfBuzzBootstrap.ResolveDeliveredPath(_root, Architecture.Arm64));
        Assert.Throws<DllNotFoundException>(() => LinuxHarfBuzzBootstrap.ResolveDeliveredPath(_root, Architecture.X64));
        Assert.Throws<ArgumentException>(() => LinuxHarfBuzzBootstrap.ResolveDeliveredPath("relative", Architecture.X64));
    }

    private string Asset(string rid)
    {
        string directory = Path.Combine(_root, "runtimes", rid, "native");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "libHarfBuzzSharp.so");
        File.WriteAllText(path, "development placeholder");
        return path;
    }
}
