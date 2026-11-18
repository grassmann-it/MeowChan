using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// Export archive: command catalog entry, archive header placement and order, work-view row, all 12 language packs and the
// dialog's limit/count texts. Pure: no window, host or profile.
[Collection("Localization")]
public sealed class ArchiveTransferExportEntrypointTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public ArchiveTransferExportEntrypointTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previousLanguage); }

    [Theory]
    [InlineData("en", "Export archive")]
    [InlineData("de", "Archiv exportieren")]
    public void TheCommandIsAnArchiveHeaderActionWithTheDownloadIconAndNoShortcut(string language, string label)
    {
        Texts.SetLanguage(language);
        CommandDefinition command = CommandCatalog.Get("archive.transfer-export");
        Assert.Equal("command.archive.transfer-export", command.LabelKey);
        Assert.Equal("Export archive", command.LabelEn);
        Assert.Equal("Archiv exportieren", command.LabelDe);
        Assert.Equal("download", command.Icon);
        Assert.Equal(CommandScope.Archive, command.Scope);
        Assert.Equal(new[] { "archive-header" }, command.Contexts);
        Assert.Null(command.Shortcut);
        Assert.True(command.IsImplemented);
        Assert.Equal(label, CommandCatalog.Label("archive.transfer-export"));
        // Export labels carry no ellipsis; the tooltip says that a dialog opens.
        Assert.Equal(Texts.T("command.hint.opens-export-dialog"), CommandCatalog.DialogHint("archive.transfer-export"));
    }

    [Fact]
    public void TheWorkViewRowSitsBetweenImportAndCleanupUnderTheSameMore()
    {
        WorkViewEntry import = WorkViewPolicy.Get("archive.header.transfer-preview");
        WorkViewEntry export = WorkViewPolicy.Get("archive.header.transfer-export");
        WorkViewEntry cleanup = WorkViewPolicy.Get("archive.header.cleanup");
        Assert.Equal("archive.transfer-export", export.CommandId);
        Assert.Equal(WorkViewKind.Action, export.Kind);
        Assert.Equal(WorkViewPage.Archive, export.Page);
        Assert.Equal(Presence.Overflow, export.For(WorkView.Barebones));
        Assert.Equal(Presence.Overflow, export.For(WorkView.Simple));
        Assert.Equal(Presence.Direct, export.For(WorkView.Advanced));
        Assert.Equal((10, 15, 20), (import.OverflowOrder, export.OverflowOrder, cleanup.OverflowOrder));
    }

    [Fact]
    public void TheHeaderButtonFollowsImportAndPrecedesCleanupAndTargetsItsClickHandler()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement root = XDocument.Load(Source("Desktop/Views/Archive/ArchivePage.axaml")).Root!;
        XElement bar = Assert.Single(root.Descendants().Where(e => (string?)e.Attribute(x + "Name") == "ArchiveHeaderActions"));
        string[] order = bar.Elements().Select(e => (string?)e.Attribute(x + "Name") ?? "").ToArray();
        Assert.Equal(["ArchiveTransferPreview", "ArchiveTransferExport", "ArchiveCleanupOpen", "RefreshArchiveButton"], order);
        XElement button = bar.Elements().Single(e => (string?)e.Attribute(x + "Name") == "ArchiveTransferExport");
        Assert.Equal("archive.header.transfer-export", (string?)button.Attributes().Single(a => a.Name.LocalName == "WorkViewElement.Id"));
        string handler = Assert.IsType<string>((string?)button.Attribute("Click"));
        MethodInfo? method = typeof(ArchivePage).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(new[] { typeof(object), typeof(RoutedEventArgs) }, method!.GetParameters().Select(p => p.ParameterType));
        // The shortcut route asks the page, not the movable button.
        Assert.DoesNotContain("ArchiveTransferExport.IsEffectivelyVisible",
            File.ReadAllText(Source("Desktop/Views/Archive/ArchivePage.TransferExport.cs")), StringComparison.Ordinal);
    }

    public static TheoryData<string> PackCodes()
    {
        Texts.Initialize();
        TheoryData<string> data = new();
        foreach (string code in Texts.BundledPackCodes()) data.Add(code);
        return data;
    }

    [Theory]
    [MemberData(nameof(PackCodes))]
    public void EveryExportStringIsInEveryBundledPackWithTheSamePlaceholders(string code)
    {
        using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(Source("Desktop/Localization/Packs/" + code + ".json")));
        JsonElement strings = pack.RootElement.GetProperty("strings");
        foreach ((string key, string english, string german) in Expected())
        {
            Assert.True(strings.TryGetProperty(key, out JsonElement value), code + ": missing " + key);
            string text = value.GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(text), code + ": empty " + key);
            Assert.Equal(Placeholders(english), Placeholders(text));
            Assert.Equal(Placeholders(german), Placeholders(text));
            Assert.DoesNotContain("...", text, StringComparison.Ordinal);
        }
        Texts.SetLanguage(code);
        Assert.Empty(Texts.Warnings);
        Assert.Equal(strings.GetProperty("transfer.export.title").GetString(), Texts.T("transfer.export.title"));
        Assert.False(CommandCatalog.Label("archive.transfer-export").TrimEnd().EndsWith('…'));
    }

    [Fact]
    public void EnglishAndGermanCatalogsCarryEveryExportKey()
    {
        foreach ((string key, string english, string german) in Expected())
        {
            Assert.Equal(english, BuiltInCatalog.English[key]);
            Assert.Equal(german, BuiltInCatalog.German[key]);
        }
    }

    [Fact]
    public void TheDefaultFileNameUsesTheDateAndTheExtensionTheImportPickerAccepts()
    {
        Assert.Equal("meowchan-archive-2026-11-15.zip", ArchiveTransferExportDestination.DefaultFileName(new DateTime(2026, 11, 15, 23, 59, 0)));
        // The import picker's filter (ArchivePage.TransferPreview.cs) is *.zip.
        Assert.Contains("Patterns = new[] { \"*.zip\" }", File.ReadAllText(Source("Desktop/Views/Archive/ArchivePage.TransferPreview.cs")), StringComparison.Ordinal);
        Assert.Equal("zip", ArchiveTransferExportDestination.Extension);
        Assert.Null(ArchiveTransferExportDestination.Normalize("relative.zip"));
        Assert.Null(ArchiveTransferExportDestination.Normalize(""));
    }

    [Fact]
    public void LimitsAreShownUpFrontAndAnOverLimitSelectionIsExplained()
    {
        Assert.Equal(1000, ArchiveTransferExportCount.MaxThreads);
        Assert.Equal(100_000, ArchiveTransferExportCount.MaxPosts);
        ArchiveTransferExportCount within = new(3, 40, 3, false, 2048);
        Assert.True(within.WithinLimits);
        Assert.Equal(string.Empty, ArchiveTransferExportDialog.DescribeOverLimit(within));
        Assert.Contains("2 KiB", ArchiveTransferExportDialog.DescribeCount(within, ArchiveTransferExportMedia.Originals));
        Assert.DoesNotContain("KiB", ArchiveTransferExportDialog.DescribeCount(within, ArchiveTransferExportMedia.MetadataOnly));

        ArchiveTransferExportCount over = new(1200, 150_000, null, false, null);
        Assert.False(over.WithinLimits);
        string text = ArchiveTransferExportDialog.DescribeOverLimit(over);
        Assert.Contains("threads: 1,200 (at most 1,000)", text);
        Assert.Contains("posts: 150,000 (at most 100,000)", text);
        Assert.Contains("not counted", ArchiveTransferExportDialog.DescribeCount(over, ArchiveTransferExportMedia.Everything));
        Assert.Contains("media folders: more than 1,000",
            ArchiveTransferExportDialog.DescribeOverLimit(new(900, 900, 1001, true, null)));
        Texts.SetLanguage("de");
        Assert.Contains("Threads: 1.200 (höchstens 1.000)", ArchiveTransferExportDialog.DescribeOverLimit(over));
    }

    [Fact]
    public void FullExportTurnsTheBlockingLimitIntoASplitNoteAndTheResultListsEveryPackage()
    {
        ArchiveTransferExportCount over = new(1200, 150_000, null, false, null);
        ArchiveTransferExportCount large = new(200, 40_000, 200, false, 17L * 1024 * 1024 * 1024, EstimatedMediaFiles: 34_000);
        Assert.True(large.WithinLimits);
        Assert.True(large.LikelyTooLargeForOnePackage);
        Assert.Contains("Full export", ArchiveTransferExportDialog.DescribeLimitState(over, ArchiveTransferExportMedia.MetadataOnly, false));
        Assert.Contains("2 GiB", ArchiveTransferExportDialog.DescribeLimitState(large, ArchiveTransferExportMedia.Originals, false));
        Assert.Equal(string.Empty, ArchiveTransferExportDialog.DescribeLimitState(large, ArchiveTransferExportMedia.MetadataOnly, false));
        string split = Texts.T("transfer.export.splitNote");
        Assert.Equal(split, ArchiveTransferExportDialog.DescribeLimitState(over, ArchiveTransferExportMedia.MetadataOnly, true));
        Assert.Equal(split, ArchiveTransferExportDialog.DescribeLimitState(large, ArchiveTransferExportMedia.Everything, true));
        Assert.Contains("media files: 9,998", Texts.T("transfer.export.limits", 1000, 100_000, 100_000, 1000, 9998, "2 GiB"));

        string root = Path.GetTempPath();
        Assert.Equal(Path.Combine(root, "meowchan-archive-2026-11-15-part-03.zip"),
            ArchiveTransferExportDestination.PartPath(Path.Combine(root, "meowchan-archive-2026-11-15.zip"), 3));
        ArchiveTransferExportResult result = new(ArchiveTransferExportError.None)
        {
            Parts = [new(Path.Combine(root, "a-part-01.zip"), 2048, 16), new(Path.Combine(root, "a-part-02.zip"), 1024, 4)],
            WithoutMedia = [new ResourceId("4chan", "b", "1")], Skipped = [new(new ResourceId("4chan", "b", "2"), ArchiveTransferExportError.ValidationFailed)]
        };
        string text = ArchiveTransferExportDialog.DescribeResult(result);
        Assert.Contains("Packages written: 2 · threads: 20 · total size: 3 KiB", text);
        Assert.Contains("a-part-02.zip", text);
        Assert.Contains("4chan/b/1", text);
        Assert.Contains("Not exported: 4chan/b/2", text);
        Assert.Contains("Packages already written stay complete", ArchiveTransferExportDialog.DescribeResult(result with { Error = ArchiveTransferExportError.StaleSnapshot }));
    }

    [Fact]
    public void ANumberedPackageNameIsMatchedByThePlatformsFileNameCaseRules()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("meowchan-export-parts-");
        try
        {
            string full = Path.Combine(directory.FullName, "name.zip");
            File.WriteAllText(Path.Combine(directory.FullName, "NAME-PART-01.ZIP"), "x");
            File.WriteAllText(Path.Combine(directory.FullName, "name-part-xx.zip"), "x");
            // The pure rule: case-insensitive finds the upper-case name, case-sensitive does not.
            Assert.Equal(Path.Combine(directory.FullName, "NAME-PART-01.ZIP"),
                ArchiveTransferExportDestination.FirstExistingPart(full, StringComparison.OrdinalIgnoreCase));
            Assert.Null(ArchiveTransferExportDestination.FirstExistingPart(full, StringComparison.Ordinal));
            // The platform default: Windows and macOS insensitive, Linux sensitive.
            Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal,
                ArchiveTransferExportDestination.FileNameComparison);
            File.WriteAllText(ArchiveTransferExportDestination.PartPath(full, 2), "x");
            Assert.Equal(ArchiveTransferExportDestination.PartPath(full, 2), ArchiveTransferExportDestination.FirstExistingPart(full, StringComparison.Ordinal));
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData("DestinationExists", "transfer.export.error.exists")]
    [InlineData("StaleSnapshot", "transfer.export.error.stale")]
    [InlineData("SelectionLimit", "transfer.export.error.limit")]
    [InlineData("ValidationFailed", "transfer.export.error.validation")]
    [InlineData("InvalidDestination", "transfer.export.error.destination")]
    public void EachResultErrorHasItsOwnMessage(string error, string key)
    {
        Assert.Equal(key, ArchiveTransferExportDialog.ErrorKey(Enum.Parse<ArchiveTransferExportError>(error)));
        Assert.NotEqual(key, Texts.T(key));
    }

    private static IEnumerable<(string Key, string English, string German)> Expected() =>
        NativeTransferExportCatalog.Entries.Append(CommandLabelCatalog.Entries.Single(e => e.Key == "command.archive.transfer-export"));

    private static string[] Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+(?::[^}]*)?\}").Select(m => m.Value).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static string Source(string relative, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", relative));
}
