using System.Text;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Import;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>
/// A damaged, newer or non-file local-imports.json blocks imports with a stable code that the dialog turns into a localized
/// sentence; the host never lets the store's exception escape, and the catalog is never read as "no imports".
/// </summary>
[Collection("Localization")]
public sealed class ManualImportCatalogDamageTests : IDisposable
{
    private readonly string _previous = Texts.Language;
    public ManualImportCatalogDamageTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previous); }

    // "newer" marks the one damage that is a newer catalog rather than a broken one; the enum itself is internal.
    [Theory]
    [InlineData("empty", "damaged")]
    [InlineData("truncated", "damaged")]
    [InlineData("invalid-utf8", "damaged")]
    [InlineData("wrong-root", "damaged")]
    [InlineData("duplicate-field", "damaged")]
    [InlineData("future-version", "newer")]
    [InlineData("directory", "damaged")]
    [InlineData("symlink-to-valid-file", "damaged")]
    [InlineData("dangling-symlink", "damaged")]
    public async Task ABlockedCatalogIsReportedAsACodeWithALocalizedSentence(string kind, string expectedKind)
    {
        ManualImportError expected = expectedKind == "newer" ? ManualImportError.CatalogNewer : ManualImportError.CatalogDamaged;
        using TempDirectory temp = new();
        string profile = Path.Combine(temp.Path, "profile");
        new Config { DownloadPath = Path.Combine(temp.Path, "downloads"), DownloadsPaused = true }.SaveConfig(profile);
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(host.ManualImportBlocked());

        string catalog = Path.Combine(profile, ManualImportCatalogStore.FileName);
        string good = "{\"Version\":1,\"Revision\":0,\"Batches\":[]}";
        switch (kind)
        {
            case "empty": File.WriteAllBytes(catalog, []); break;
            case "truncated": File.WriteAllText(catalog, good[..^3]); break;
            case "invalid-utf8": File.WriteAllBytes(catalog, [.. Encoding.UTF8.GetBytes("{\"Version\":1,\"Revision\":0,\"Batches\":[],\"X\":\""), 0xC3, 0x28, (byte)'"', (byte)'}']); break;
            case "wrong-root": File.WriteAllText(catalog, "[]"); break;
            case "duplicate-field": File.WriteAllText(catalog, "{\"Version\":1,\"Revision\":0,\"Revision\":1,\"Batches\":[]}"); break;
            case "future-version": File.WriteAllText(catalog, "{\"Version\":2,\"Revision\":0,\"Batches\":[]}"); break;
            case "directory": Directory.CreateDirectory(catalog); break;
            case "symlink-to-valid-file":
                File.WriteAllText(Path.Combine(temp.Path, "valid.json"), good);
                File.CreateSymbolicLink(catalog, Path.Combine(temp.Path, "valid.json"));
                break;
            default: File.CreateSymbolicLink(catalog, Path.Combine(temp.Path, "missing.json")); break;
        }

        ManualImportError? blocked = null;
        Exception? error = Record.Exception(() => blocked = host.ManualImportBlocked());
        Assert.True(error == null, $"ManualImportBlocked threw {error?.GetType().FullName}: {error?.Message}");
        Assert.Equal(expected, blocked);
        Assert.Null(host.ReadManualImportCatalog());

        string key = ManualImportSession.MessageOf(blocked!.Value);
        Assert.Equal(expected == ManualImportError.CatalogNewer ? "import.error.newer" : "import.error.damaged", key);
        string english = new ManualImportException(blocked.Value).Message;
        foreach (string language in new[] { "en", "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            string text = ManualImportSession.MessageText(key);
            Assert.NotEqual(key, text);
            Assert.False(string.IsNullOrWhiteSpace(text));
            if (language != "en") Assert.NotEqual(english, text);
            Assert.DoesNotContain(profile, text, StringComparison.Ordinal);
        }
    }
}
