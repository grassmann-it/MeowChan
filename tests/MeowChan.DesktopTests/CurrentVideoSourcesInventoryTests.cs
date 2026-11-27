using System.Security.Cryptography;
using System.Text.Json;

namespace MeowChan.DesktopTests;

public sealed class CurrentVideoSourcesInventoryTests
{
    private static readonly string[] Runtimes = ["win-x64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64"];

    [Theory]
    [InlineData("win-x64")]
    [InlineData("linux-x64")]
    [InlineData("linux-arm64")]
    [InlineData("osx-arm64")]
    [InlineData("osx-x64")]
    public void AnInstalledInventoryResolvesEveryComponentInTheExtractedArchive(string rid)
    {
        using var fixture = new PackageFixture();
        Assert.Empty(fixture.FindUnresolvedComponents(rid));
    }

    [Fact]
    public void MissingSourceForOneRuntimeIsDetectedEvenWhenAnotherRuntimeHasIt()
    {
        using var fixture = new PackageFixture(omitComponentFrom: "osx-arm64");
        Assert.Equal(new[] { "vlc" }, fixture.FindUnresolvedComponents("osx-arm64"));
        Assert.Empty(fixture.FindUnresolvedComponents("osx-x64"));
    }

    private sealed class PackageFixture : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("meowchan-video-sources-");
        private readonly Dictionary<string, Dictionary<string, string>> _inventories = new(StringComparer.Ordinal);

        public PackageFixture(string? omitComponentFrom = null)
        {
            string extracted = Directory.CreateDirectory(Path.Combine(_directory.FullName, "extracted")).FullName;
            var sourceFiles = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string name in new[] { "vlc-source", "binding-source", "build-recipe", "license", "patch" })
            {
                byte[] content = System.Text.Encoding.UTF8.GetBytes(name + " fixture");
                File.WriteAllBytes(Path.Combine(extracted, name), content);
                sourceFiles.Add(name, new { path = name, bytes = content.Length, sha256 = Hash(content) });
            }

            var offeredRuntimes = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (string rid in Runtimes)
            {
                var nativeFiles = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [rid + "/libvlc"] = Hash(System.Text.Encoding.UTF8.GetBytes(rid + " VLC"))
                };
                _inventories.Add(rid, nativeFiles);
                var components = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["libvlcsharp"] = new
                    {
                        version = "3.10.2", inputs = new[] { "binding-source" }, recipes = new[] { "build-recipe" },
                        patches = Array.Empty<string>(), licenses = new[] { "license" }
                    }
                };
                if (rid != omitComponentFrom)
                {
                    components.Add("vlc", new
                    {
                        version = "3.0.24", inputs = new[] { "vlc-source" }, recipes = new[] { "build-recipe" },
                        patches = new[] { "patch" }, licenses = new[] { "license" }
                    });
                }

                offeredRuntimes.Add(rid, new
                {
                    native_files = nativeFiles.ToDictionary(
                        file => file.Key, file => new { sha256 = file.Value, component = "vlc" }, StringComparer.Ordinal),
                    packages = new[] { new { file = rid + "-package", components = new[] { "vlc", "libvlcsharp" } } },
                    components
                });
            }

            File.WriteAllBytes(Path.Combine(extracted, "manifest.json"),
                JsonSerializer.SerializeToUtf8Bytes(new { runtimes = offeredRuntimes, files = sourceFiles }));
        }

        public IReadOnlyList<string> FindUnresolvedComponents(string rid)
        {
            string extracted = Path.Combine(_directory.FullName, "extracted");
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(extracted, "manifest.json")));
            JsonElement files = manifest.RootElement.GetProperty("files");
            JsonElement offered = manifest.RootElement.GetProperty("runtimes").GetProperty(rid);
            JsonElement nativeFiles = offered.GetProperty("native_files");
            var installedComponents = new HashSet<string>(StringComparer.Ordinal) { "libvlcsharp" };
            foreach ((string path, string checksum) in _inventories[rid])
            {
                JsonElement item = nativeFiles.GetProperty(path);
                Assert.Equal(checksum, item.GetProperty("sha256").GetString());
                installedComponents.Add(item.GetProperty("component").GetString()!);
            }

            JsonElement package = offered.GetProperty("packages").EnumerateArray().Single();
            Assert.Equal(rid + "-package", package.GetProperty("file").GetString());
            Assert.Equal(installedComponents.Order(StringComparer.Ordinal),
                package.GetProperty("components").EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));

            JsonElement components = offered.GetProperty("components");
            return installedComponents.Where(id => !components.TryGetProperty(id, out JsonElement source) ||
                !PayloadExists(source, files, extracted)).Order(StringComparer.Ordinal).ToArray();
        }

        private static bool PayloadExists(JsonElement source, JsonElement files, string extracted)
        {
            foreach (string kind in new[] { "inputs", "recipes", "patches", "licenses" })
            {
                foreach (JsonElement reference in source.GetProperty(kind).EnumerateArray())
                {
                    if (!files.TryGetProperty(reference.GetString()!, out JsonElement file))
                        return false;
                    string path = Path.Combine(extracted, file.GetProperty("path").GetString()!);
                    if (!File.Exists(path) || Hash(File.ReadAllBytes(path)) != file.GetProperty("sha256").GetString())
                        return false;
                }
            }

            return true;
        }

        private static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

        public void Dispose() => _directory.Delete(recursive: true);
    }
}
