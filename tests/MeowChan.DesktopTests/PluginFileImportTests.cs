using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Installation;
using MeowChan.Testing;

namespace MeowChan.DesktopTests;

/// <summary>
/// "Add plugin…" copies a file through the inventory's own inspection and nothing else: whatever the inventory rejects for a file
/// placed by hand is rejected here, nothing is overwritten, and a rejected or cancelled add leaves the plugin folder untouched.
/// </summary>
public sealed class PluginFileImportTests : IDisposable
{
    private readonly string _root = TemporaryWorkspacePaths.CreateDirectory("meowchan-plugin-import-");
    private const int FourMiB = 4 * 1024 * 1024; // the inventory's source limit
    private readonly string _plugins;
    private readonly string _outside;

    public PluginFileImportTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        _outside = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_plugins);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Outside(string name, byte[]? content = null)
    {
        string path = Path.Combine(_outside, name);
        File.WriteAllBytes(path, content ?? "public sealed class Plugin { }\n"u8.ToArray());
        return path;
    }

    private string[] FolderEntries() => Directory.EnumerateFileSystemEntries(_plugins).Where(path => Path.GetFileName(path) != PluginImportStaging.DirectoryName).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public async Task ACsFileIsCopiedByteForByteAndTheInventoryListsItWithoutProblems()
    {
        byte[] content = "// ü plugin\npublic sealed class Plugin { }\n"u8.ToArray();
        string source = Outside("My Plugin ü.cs", content);

        PluginFileImportResult result = await PluginFileImport.ImportAsync(source, _plugins);

        Assert.Equal(PluginFileImportStatus.Imported, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(_plugins, "My Plugin ü.cs"), result.Path);
        Assert.Equal(content, File.ReadAllBytes(result.Path!));
        Assert.Equal(new[] { "My Plugin ü.cs" }, FolderEntries());      // no staging file left in the folder
        Assert.Equal(content, File.ReadAllBytes(source));              // the chosen file is untouched
        PluginInventory scan = await new PluginInstallationInventory().ScanAsync(_plugins);
        PluginInstallationCandidate listed = Assert.Single(scan.Candidates);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_plugins, PluginImportStaging.DirectoryName)));
        Assert.Equal(result.CandidateId, listed.CandidateId);          // the page can select exactly this entry
        Assert.Equal(PluginCandidateKind.Source, listed.Kind);
        Assert.Empty(listed.Diagnostics);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root).Where(path => Path.GetFileName(path).StartsWith(".plugin-import-")));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("plugin.dll")]
    [InlineData("archive.cs.zip")]
    public async Task OtherFileTypesAreRejectedAndNothingIsCopied(string name)
    {
        PluginFileImportResult result = await PluginFileImport.ImportAsync(Outside(name), _plugins);
        Assert.Equal(PluginFileImportStatus.Unsupported, result.Status);
        Assert.False(result.Succeeded);
        Assert.Empty(FolderEntries());
    }

    [Fact]
    public async Task ABundleManifestAloneIsRejectedBecauseItsAssemblyAndResourcesCannotComeWithIt()
    {
        PluginFileImportResult result = await PluginFileImport.ImportAsync(Outside("tool.plugin.json", "{}"u8.ToArray()), _plugins);
        Assert.False(result.Succeeded);
        Assert.Empty(FolderEntries());
    }

    [Fact]
    public async Task AnEmptyFileAndATooLargeFileAreRejectedLikeTheInventoryDoes()
    {
        Assert.Equal(PluginFileImportStatus.Empty, (await PluginFileImport.ImportAsync(Outside("empty.cs", []), _plugins)).Status);
        byte[] large = new byte[FourMiB + 1];
        Assert.Equal(PluginFileImportStatus.TooLarge, (await PluginFileImport.ImportAsync(Outside("large.cs", large), _plugins)).Status);
        byte[] atLimit = new byte[FourMiB];
        Assert.Equal(PluginFileImportStatus.Imported, (await PluginFileImport.ImportAsync(Outside("limit.cs", atLimit), _plugins)).Status);
        Assert.Equal(new[] { "limit.cs" }, FolderEntries());
        Assert.Equal(4, PluginFileImport.SourceLimitMiB);
    }

    [Fact]
    public async Task ADirectoryNamedLikeASourceFileIsRejected()
    {
        string directory = Path.Combine(_outside, "folder.cs");
        Directory.CreateDirectory(directory);
        Assert.Equal(PluginFileImportStatus.NotRegularFile, (await PluginFileImport.ImportAsync(directory, _plugins)).Status);
        Assert.Empty(FolderEntries());
    }

    [Fact]
    public async Task ASymbolicLinkIsRejectedAsInTheInventory()
    {
        if (OperatingSystem.IsWindows()) return;
        string target = Outside("real.cs");
        string link = Path.Combine(_outside, "link.cs");
        File.CreateSymbolicLink(link, target);
        Assert.Equal(PluginFileImportStatus.NotRegularFile, (await PluginFileImport.ImportAsync(link, _plugins)).Status);
        Assert.Empty(FolderEntries());
    }

    [Fact]
    public async Task AFileOfTheSameNameIsNeverOverwritten()
    {
        string existing = Path.Combine(_plugins, "same.cs");
        File.WriteAllText(existing, "// the plugin that was there before");
        PluginFileImportResult result = await PluginFileImport.ImportAsync(Outside("same.cs"), _plugins);

        Assert.Equal(PluginFileImportStatus.NameTaken, result.Status);
        Assert.Equal("// the plugin that was there before", File.ReadAllText(existing));
        Assert.Equal(new[] { "same.cs" }, FolderEntries());
        if (!OperatingSystem.IsWindows())
        {
            // A dangling link of that name is a name clash too: the copy must not write through it.
            string danglingTarget = Path.Combine(_outside, "written-through.txt");
            File.CreateSymbolicLink(Path.Combine(_plugins, "dangling.cs"), danglingTarget);
            Assert.Equal(PluginFileImportStatus.NameTaken, (await PluginFileImport.ImportAsync(Outside("dangling.cs"), _plugins)).Status);
            Assert.False(File.Exists(danglingTarget));
        }
    }

    [Fact]
    public async Task AFileAlreadyInTheFolderIsNotCopiedOntoItself()
    {
        string inside = Path.Combine(_plugins, "inside.cs");
        File.WriteAllText(inside, "public sealed class Plugin { }");
        PluginFileImportResult result = await PluginFileImport.ImportAsync(inside, _plugins);
        Assert.Equal(PluginFileImportStatus.AlreadyInFolder, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(inside, result.Path);
        Assert.Equal("public sealed class Plugin { }", File.ReadAllText(inside));
        Assert.Equal(new[] { "inside.cs" }, FolderEntries());
    }

    [Fact]
    public async Task AMissingPluginFolderAndAMissingSourceReportFailureWithoutCreatingAnything()
    {
        string missingFolder = Path.Combine(_root, "no-plugins-here");
        PluginFileImportResult noFolder = await PluginFileImport.ImportAsync(Outside("a.cs"), missingFolder);
        Assert.Equal(PluginFileImportStatus.Failed, noFolder.Status);
        Assert.False(Directory.Exists(missingFolder));
        PluginFileImportResult noSource = await PluginFileImport.ImportAsync(Path.Combine(_outside, "gone.cs"), _plugins);
        Assert.Equal(PluginFileImportStatus.Failed, noSource.Status);
        Assert.NotNull(noSource.Detail);
        Assert.Empty(FolderEntries());
    }

    [Fact]
    public async Task ACancelledAddLeavesTheFolderUntouched()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginFileImport.ImportAsync(Outside("c.cs"), _plugins, cancelled.Token));
        Assert.Empty(FolderEntries());
    }

    /// <summary>The same judgement as a hand-placed file: for every kind of file, "added" exactly when the folder scan shows no problem.</summary>
    [Theory]
    [InlineData("ok.cs", "public sealed class Plugin { }", true)]
    [InlineData("OK-UPPER.CS", "public sealed class Plugin { }", true)]
    [InlineData("empty.cs", "", false)]
    [InlineData("readme.txt", "text", false)]
    [InlineData("plugin.dll", "MZ", false)]
    [InlineData("bundle.plugin.json", "{}", false)]
    public async Task AFileIsAddedExactlyWhenTheInventoryAcceptsItAsASource(string name, string content, bool accepted)
    {
        string byHand = Path.Combine(_root, "by-hand");
        Directory.CreateDirectory(byHand);
        File.WriteAllText(Path.Combine(byHand, name), content);
        PluginInstallationCandidate scanned = Assert.Single((await new PluginInstallationInventory().ScanAsync(byHand)).Candidates);
        bool inventoryAccepts = scanned.Kind == PluginCandidateKind.Source && scanned.Diagnostics.Count == 0;

        PluginFileImportResult result = await PluginFileImport.ImportAsync(Outside(name, System.Text.Encoding.UTF8.GetBytes(content)), _plugins);

        Assert.Equal(accepted, inventoryAccepts);
        Assert.Equal(inventoryAccepts, result.Succeeded);
        Assert.Equal(inventoryAccepts ? 1 : 0, FolderEntries().Length);
    }

    [Fact]
    public async Task ConcurrentImportsOfOneNamePublishExactlyOneCompleteFile()
    {
        string first = Outside("race.cs", "// first"u8.ToArray());
        string secondDirectory = Path.Combine(_root, "second-download");
        Directory.CreateDirectory(secondDirectory);
        string second = Path.Combine(secondDirectory, "race.cs");
        File.WriteAllText(second, "// second");

        PluginFileImportResult[] results = await Task.WhenAll(
            PluginFileImport.ImportAsync(first, _plugins), PluginFileImport.ImportAsync(second, _plugins));

        Assert.Single(results.Where(result => result.Status == PluginFileImportStatus.Imported));
        Assert.Single(results.Where(result => result.Status == PluginFileImportStatus.NameTaken));
        Assert.Contains(File.ReadAllText(Path.Combine(_plugins, "race.cs")), new[] { "// first", "// second" });
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_plugins, PluginImportStaging.DirectoryName)));
    }

    [Fact]
    public async Task AStagingSymlinkCannotRedirectTheImportOutsideThePluginFolder()
    {
        if (OperatingSystem.IsWindows()) return;
        string staging = Path.Combine(_plugins, PluginImportStaging.DirectoryName);
        Directory.CreateSymbolicLink(staging, _outside);
        string source = Outside("safe.cs");

        PluginFileImportResult result = await PluginFileImport.ImportAsync(source, _plugins);

        Assert.Equal(PluginFileImportStatus.Failed, result.Status);
        Assert.Equal(new[] { "safe.cs" }, Directory.EnumerateFileSystemEntries(_outside).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(_plugins, "safe.cs")));
        Directory.Delete(staging);
    }

    [Fact]
    public void ImportOnlyCopiesAndNeverCompilesDescribesEnablesOrStartsAnything()
    {
        string source = File.ReadAllText(SourceFile("Desktop/Plugins/PluginFileImport.cs"))
            + File.ReadAllText(SourceFile("Desktop/Views/Plugins/PluginsPage.Import.cs"));
        foreach (string forbidden in new[] { "ProfileStore", "SetEnabledAsync", "SetGrantAsync", "SelectCandidateAsync", "SaveConfigurationAsync",
            ".Compiler", "IPluginCompilationService", "BuildAsync", "StartTestWorker", "DescribeTestAsync", "ProbeAsync", "Process.Start", "ExternalLauncher" })
            Assert.DoesNotContain(forbidden, source);
    }

    private static string SourceFile(string relative, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "../..", relative));
}
