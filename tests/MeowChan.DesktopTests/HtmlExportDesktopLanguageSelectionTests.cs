namespace MeowChan.DesktopTests;

// This is source-wiring evidence only. Rendering and persisted metadata are exercised
// by HtmlExportLocalizationRenderingTests; this does not claim an OS input journey.
public sealed class HtmlExportDesktopLanguageSelectionTests
{
    [Theory]
    [InlineData("ArchivePage.Export.cs", "private async void OnExportStart(")]
    [InlineData("ArchivePage.Export.Collection.cs", "private async Task StartCollectionExportAsync(")]
    public void BothDesktopExportsFreezeCurrentLanguageBeforeAsyncWork(string file, string entryPoint)
    {
        string source = File.ReadAllText(RepositoryPath("Desktop/Views/Archive/" + file));
        string method = source[source.IndexOf(entryPoint, StringComparison.Ordinal)..];
        int options = method.IndexOf("ArchiveHtmlExportOptions options = new(", StringComparison.Ordinal);
        int language = method.IndexOf("Language = Texts.Language", options, StringComparison.Ordinal);
        int firstAwait = method.IndexOf("await ", options, StringComparison.Ordinal);
        Assert.True(options >= 0);
        Assert.True(language > options);
        Assert.True(firstAwait > language);
        Assert.Contains("options,", method[(language + "Language = Texts.Language".Length)..]);
    }

    private static string RepositoryPath(string path)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(path);
    }
}
