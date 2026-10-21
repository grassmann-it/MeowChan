using System.Security.Cryptography;
using System.Text.Json;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class LinuxVideoPreflightTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "meowchan-vlc-policy-" + Guid.NewGuid().ToString("N"));
    private string Bundle => Path.Combine(_directory, "libvlc/linux-x64");
    public LinuxVideoPreflightTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("3.0.19 Vetinari", false)]
    [InlineData("3.0.20 Vetinari", true)]
    [InlineData("3.0.20-3build6", true)]
    [InlineData("3.0.21 Vetinari", true)]
    [InlineData("3.0.24", true)]
    [InlineData("3.1.0", true)]
    [InlineData("4.0.0", false)]
    [InlineData("garbage", false)]
    public void VersionRequiresSupportedLibVlc3Abi(string version, bool supported) =>
        Assert.Equal(supported, LinuxVideoPreflight.IsSupportedVersion(version));

    [Fact]
    public void SourceAndSystemPackagesUseSystemRuntime()
    {
        Assert.False(LinuxVideoRuntimePolicy.Read(_directory, "linux-x64").Bundled);
        Marker("system");
        Assert.False(LinuxVideoRuntimePolicy.Read(_directory, "linux-x64").Bundled);
        Assert.True(Check().Success);
    }

    [Fact]
    public void PrivatePayloadCannotSilentlyBecomeASystemPackage()
    {
        Payload();
        Assert.Equal(LinuxVideoPreflightErrorKind.InvalidPackageMetadata, CheckUntouched().Kind);
        Marker("system");
        Assert.Equal(LinuxVideoPreflightErrorKind.InvalidPackageMetadata, CheckUntouched().Kind);
    }

    [Fact]
    public void MetadataFailureDoesNotTouchNativeCode()
    {
        File.WriteAllText(Path.Combine(_directory, "linux-video-runtime.json"), "{invalid");
        Assert.Equal(LinuxVideoPreflightErrorKind.InvalidPackageMetadata, CheckUntouched().Kind);
        Marker("bundled", rid: "linux-arm64");
        Assert.Equal(LinuxVideoPreflightErrorKind.InvalidPackageMetadata, CheckUntouched().Kind);
        Marker("unknown");
        Assert.Equal(LinuxVideoPreflightErrorKind.InvalidPackageMetadata, CheckUntouched().Kind);
    }

    [Fact]
    public void PrivatePayloadMustBeCompleteAndUntamperedBeforeLoading()
    {
        var files = Payload();
        Marker("bundled", files);
        Assert.True(Check().Success);
        File.WriteAllText(Path.Combine(Bundle, "lib/libvlc.so.5"), "tampered");
        Assert.Equal(LinuxVideoPreflightErrorKind.BundleDamaged, CheckUntouched().Kind);
        File.Delete(Path.Combine(Bundle, "lib/libvlc.so.5"));
        Assert.Equal(LinuxVideoPreflightErrorKind.BundleDamaged, CheckUntouched().Kind);
    }

    [Fact]
    public void UnlistedPluginCannotEnterTheEngine()
    {
        Marker("bundled", Payload());
        File.WriteAllText(Path.Combine(Bundle, "lib/vlc/plugins/injected.so"), "unlisted");
        Assert.Equal(LinuxVideoPreflightErrorKind.BundleDamaged, CheckUntouched().Kind);
    }

    [Fact]
    public void TraversalIsInvalidMetadata()
    {
        var files = Payload(); files.Add("../escape.so", new string('a', 64));
        Marker("bundled", files);
        Assert.Equal(LinuxVideoPreflightErrorKind.InvalidPackageMetadata, CheckUntouched().Kind);
    }

    [Fact]
    public void EscapingSymlinkIsRejectedButConfinedLibraryLinkIsAllowed()
    {
        if (!OperatingSystem.IsLinux()) return;
        var files = Payload();
        string original = Path.Combine(Bundle, "lib/libvlc.so.5");
        string target = Path.Combine(Bundle, "lib/libvlc.so.5.6.1");
        File.Move(original, target);
        File.CreateSymbolicLink(original, "libvlc.so.5.6.1");
        files.Add("lib/libvlc.so.5.6.1", files["lib/libvlc.so.5"]);
        Marker("bundled", files);
        Assert.True(Check().Success);
        File.Delete(original);
        string outside = Path.Combine(_directory, "outside.so");
        File.Copy(target, outside);
        File.CreateSymbolicLink(original, outside);
        Assert.Equal(LinuxVideoPreflightErrorKind.BundleDamaged, CheckUntouched().Kind);
    }

    [Fact]
    public void InstallationParentAliasesAreAllowedAndPayloadCyclesAreRejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        Marker("bundled", Payload());
        string alias = Path.Combine(_directory, "install-alias");
        Directory.CreateSymbolicLink(alias, _directory);
        var result = LinuxVideoPreflight.Check(alias, "linux-x64", () => "3.0.20", () => { });
        Assert.True(result.Success, result.Diagnostic);
        string library = Path.Combine(Bundle, "lib/libvlc.so.5");
        File.Delete(library);
        File.CreateSymbolicLink(library, "libvlc.so.5");
        Assert.Equal(LinuxVideoPreflightErrorKind.BundleDamaged, CheckUntouched().Kind);
    }

    [Fact]
    public void OldRuntimeAndEngineFailuresAreClassifiedWithoutFallback()
    {
        int engineCalls = 0;
        var result = LinuxVideoPreflight.Check(_directory, "linux-x64", () => "3.0.19", () => engineCalls++);
        Assert.Equal(LinuxVideoPreflightErrorKind.SystemBackendUnsupportedVersion, result.Kind);
        Assert.Equal(0, engineCalls);
        result = LinuxVideoPreflight.Check(_directory, "linux-x64", () => "3.0.20", () => throw new DllNotFoundException());
        Assert.Equal(LinuxVideoPreflightErrorKind.SystemBackendUnavailable, result.Kind);
        Marker("bundled", Payload());
        result = LinuxVideoPreflight.Check(_directory, "linux-x64", () => "3.0.20", () => throw new DllNotFoundException());
        Assert.Equal(LinuxVideoPreflightErrorKind.BundleDamaged, result.Kind);
        result = LinuxVideoPreflight.Check(_directory, "linux-x64", () => "4.0.0", () => engineCalls++);
        Assert.Equal(LinuxVideoPreflightErrorKind.BundleDamaged, result.Kind);
        Assert.Equal(0, engineCalls);
    }

    private LinuxVideoPreflightResult Check() => LinuxVideoPreflight.Check(_directory, "linux-x64", () => "3.0.20 Vetinari", () => { });
    private LinuxVideoPreflightResult CheckUntouched()
    {
        int calls = 0;
        var result = LinuxVideoPreflight.Check(_directory, "linux-x64", () => { calls++; return "3.0.21"; }, () => calls++);
        Assert.Equal(0, calls);
        return result;
    }

    private Dictionary<string, string> Payload()
    {
        Directory.CreateDirectory(Path.Combine(Bundle, "lib/vlc/plugins"));
        Directory.CreateDirectory(Path.Combine(Bundle, "lib/vlc/share"));
        var files = new Dictionary<string, string>();
        foreach (string path in new[] { "lib/libvlc.so.5", "lib/libvlccore.so.9", "lib/vlc/plugins/codec.so", "lib/vlc/share/data.dat" })
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(path);
            File.WriteAllBytes(Path.Combine(Bundle, path), data);
            files.Add(path, Convert.ToHexString(SHA256.HashData(data)));
        }
        return files;
    }

    private void Marker(string mode, Dictionary<string, string>? files = null, string rid = "linux-x64") =>
        File.WriteAllText(Path.Combine(_directory, "linux-video-runtime.json"), JsonSerializer.Serialize(new
        { SchemaVersion = 1, Mode = mode, RuntimeIdentifier = rid, Files = files ?? new() }));
}
