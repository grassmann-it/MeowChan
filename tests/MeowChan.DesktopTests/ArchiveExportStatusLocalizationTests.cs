using System.Runtime.CompilerServices;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Pure retained-presentation provenance plus narrow source wiring. This file does
// not construct an ArchivePage or claim a native export/language-switch journey.
[Collection("Localization")]
public sealed class ArchiveExportStatusLocalizationTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public ArchiveExportStatusLocalizationTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    [Fact]
    public void LocalizedStatusKeepsItsKeyArgumentsAndRendersEveryBundledLanguage()
    {
        object?[] arguments = { 12, 34 };
        ArchivePage.ArchiveExportStatusText status =
            ArchivePage.ArchiveExportStatusText.Localized("archive.exportProgress", arguments);
        arguments[0] = 999;

        string english = status.Render();
        Assert.Equal(Texts.T("archive.exportProgress", 12, 34), english);
        foreach (string language in new[] { "de", "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(language, Texts.Language);
            Assert.Equal(Texts.T("archive.exportProgress", 12, 34), status.Render());
            Assert.Contains("12", status.Render(), StringComparison.Ordinal);
            Assert.Contains("34", status.Render(), StringComparison.Ordinal);
            if (language != "en") Assert.NotEqual(english, status.Render());
        }
    }

    [Fact]
    public void RawStatusesDoNotInterpretKeyLookingTextOrChangeWithLanguage()
    {
        const string raw = "archive.exportDone {0}\nraw worker detail ü /tmp/export";
        ArchivePage.ArchiveExportStatusText status = ArchivePage.ArchiveExportStatusText.Raw(raw);
        foreach (string language in new[] { "de", "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(raw, status.Render());
        }
    }

    [Fact]
    public void FailureSnapshotRerendersTheHostSentenceAndPreservesTheRawDetailLine()
    {
        const string detail = "IOException {0}\n/raw/detail";
        ArchivePage.ArchiveExportStatusText status = ArchivePage.ArchiveExportStatusText.Failed(detail);
        string english = status.Render();
        Assert.Equal(Texts.T("export.failed") + "\n" + detail, english);

        foreach (string language in new[] { "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T("export.failed") + "\n" + detail, status.Render());
            Assert.NotEqual(english, status.Render());
            Assert.Contains(detail, status.Render(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("export.historyError")]
    [InlineData("export.openFailed")]
    [InlineData("export.resultsFailed")]
    public void WrapperSnapshotsKeepTheirRawArgumentAcrossEveryBundledLanguage(string key)
    {
        const string detail = "RAW DIAGNOSTIC {0}\n/synthetic/path";
        ArchivePage.ArchiveExportStatusText status = ArchivePage.ArchiveExportStatusText.Localized(key, detail);
        string english = status.Render();
        Assert.Equal(Texts.T(key, detail), english);

        foreach (string language in new[] { "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T(key, detail), status.Render());
            Assert.Contains(detail, status.Render(), StringComparison.Ordinal);
            Assert.NotEqual(english, status.Render());
        }
    }

    [Fact]
    public void NestedLocalizedMessageRerendersOuterAndInnerHostSentences()
    {
        ArchivePage.ArchiveExportStatusText status = ArchivePage.ArchiveExportStatusText.Localized(
            "export.openFailed", ArchivePage.ArchiveExportStatusText.Localized("export.changed"));
        string english = status.Render();
        Assert.Equal(Texts.T("export.openFailed", Texts.T("export.changed")), english);

        foreach (string language in new[] { "de", "es", "fr", "ru" })
        {
            Texts.SetLanguage(language);
            string expected = Texts.T("export.openFailed", Texts.T("export.changed"));
            Assert.Equal(expected, status.Render());
            Assert.Contains(Texts.T("export.changed"), status.Render(), StringComparison.Ordinal);
            Assert.NotEqual(english, status.Render());
        }
    }

    [Fact]
    public void RetainedSingleResultRerendersCountsAndBackupWhilePreservingPaths()
    {
        ArchiveHtmlExportResult result = new("/published/single", "/published/single/index.html", 12, 34,
            new[] { new ArchiveExportOmission("42", "media", ArchiveExportOmissionReason.Unavailable) })
        {
            BackupDirectory = "/preserved/single",
            Warnings = new[] { "RAW PROVIDER WARNING {0}" }
        };
        ArchivePage.ArchiveExportStatusText status = ArchivePage.ArchiveExportStatusText.SingleResult(result);

        foreach (string language in new[] { "de", "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            string expected = Texts.T("archive.exportDone", 12, 34, 1, 1) + "\n/published/single\n"
                + Texts.T("export.backup", "/preserved/single");
            Assert.Equal(expected, status.Render());
            Assert.Contains("/published/single", status.Render(), StringComparison.Ordinal);
            Assert.Contains("/preserved/single", status.Render(), StringComparison.Ordinal);
            // Preserve the previous single-thread presentation: warnings are counted,
            // but their raw provider text is not duplicated into this status line.
            Assert.DoesNotContain("RAW PROVIDER WARNING", status.Render());
        }
    }

    [Fact]
    public void RetainedCollectionResultRerendersAllHostPartsAndKeepsRawDetails()
    {
        ResourceId first = new("4chan", "g", "123");
        ResourceId second = new("8kun", "g", "456");
        ArchiveCollectionHtmlExportResult result = new("/published/collection", "/published/collection/index.html",
            2, 3, 1, new[] { new ArchiveCollectionExportOmission(second, "9", "media", ArchiveExportOmissionReason.VerificationFailed) })
        {
            Metadata = new ArchiveCollectionExportMetadata(1, Guid.NewGuid(), 2,
                new[]
                {
                    new ArchiveCollectionMember(first, "https://first.test", "threads/a.html", null, 1, 1),
                    new ArchiveCollectionMember(second, "https://second.test", "threads/b.html", null, 2, 2)
                },
                DateTimeOffset.UtcNow, new ArchiveHtmlExportOptions(), Array.Empty<ArchiveExportFile>()),
            BackupDirectory = "/preserved/collection",
            Warnings = new[] { "RAW COLLECTION WARNING {0}" }
        };
        ArchivePage.ArchiveExportStatusText status = ArchivePage.ArchiveExportStatusText.CollectionResult(result);
        string english = status.Render();

        foreach (string language in new[] { "de", "es", "fr", "ru", "en" })
        {
            Texts.SetLanguage(language);
            string firstLabel = "4chan /g/ 123";
            string secondLabel = "8kun /g/ 456";
            string expected = string.Join("\n", new[]
            {
                Texts.T("export.collectionDone", 2, 3, 1, 1, 1),
                "/published/collection",
                Texts.T("export.collectionMemberDone", firstLabel, 1, 0),
                Texts.T("export.collectionMemberDone", secondLabel, 2, 1),
                secondLabel + ": " + Texts.T("export.omission.VerificationFailed") + " × 1",
                "RAW COLLECTION WARNING {0}",
                Texts.T("export.backup", "/preserved/collection")
            });
            Assert.Equal(expected, status.Render());
            Assert.Equal(expected, ArchivePage.CollectionExportResultText(result));
            Assert.Contains("/published/collection", status.Render(), StringComparison.Ordinal);
            Assert.Contains("/preserved/collection", status.Render(), StringComparison.Ordinal);
        }

        Texts.SetLanguage("fr");
        Assert.NotEqual(english, status.Render());
    }

    [Fact]
    public void LanguageRefreshRerendersBothRetainedControlsWithoutWorkOrHistoryOperations()
    {
        string dialog = ReadExportSource("ArchivePage.Export.Dialog.cs");
        int localizeStart = dialog.IndexOf("private void LocalizeExport()", StringComparison.Ordinal);
        Assert.True(localizeStart >= 0);
        string localize = dialog[localizeStart..];
        Assert.Contains("RenderExportStatusText();", localize);
        Assert.Contains("RenderExportErrorText();", localize);
        Assert.DoesNotContain("Reload", localize, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveHtmlExporter", localize, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordPublished", localize, StringComparison.Ordinal);

        string status = ReadExportSource("ArchivePage.Export.Status.cs");
        Assert.DoesNotContain("Reload", status, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveHtmlExporter.Export", status, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordPublished", status, StringComparison.Ordinal);
        Assert.Contains("private ArchiveExportStatusText _exportStatusText", status);
        Assert.Contains("private ArchiveExportStatusText _exportErrorText", status);
    }

    [Fact]
    public void AllRelevantExportPathsStoreProvenanceInsteadOfPreRenderedCatalogText()
    {
        foreach (string name in new[] { "ArchivePage.Export.cs", "ArchivePage.Export.Collection.cs", "ArchivePage.Export.Results.cs" })
        {
            string source = ReadExportSource(name);
            Assert.DoesNotContain("ExportStatus(Texts.T(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ExportError(Texts.T(", source, StringComparison.Ordinal);
        }

        string single = ReadExportSource("ArchivePage.Export.cs");
        Assert.Contains("ArchiveExportStatusText.SingleResult(result)", single);
        Assert.Contains("ArchiveExportStatusText.Localized(\"archive.exportCancelling\")", single);
        Assert.Contains("ArchiveExportStatusText.Failed(exception.Message)", single);
        Assert.Contains("Language = Texts.Language", single);

        Assert.DoesNotContain("IOException(Texts.T(\"export.changed\")", single, StringComparison.Ordinal);
        Assert.Contains("ArchiveExportStatusText.Localized(\"export.changed\")", single);

        string collection = ReadExportSource("ArchivePage.Export.Collection.cs");
        Assert.Contains("ArchiveExportStatusText.CollectionResult(result)", collection);
        Assert.DoesNotContain("IOException(Texts.T(\"export.changed\")", collection, StringComparison.Ordinal);
        Assert.Contains("ArchiveExportStatusText.Localized(\"export.changed\")", collection);
        Assert.Contains("ArchiveExportStatusText.Localized(\"export.collectionProgress\"", collection);
        Assert.Contains("ArchiveExportStatusText.Failed(exception.Message)", collection);
        Assert.Contains("Language = Texts.Language", collection);

        string results = ReadExportSource("ArchivePage.Export.Results.cs");
        Assert.Contains("ArchiveExportStatusText.Localized(\"export.resultsEmpty\")", results);
        Assert.Contains("ArchiveExportStatusText.Localized(\"export.resultsFailed\", exception.Message)", results);
    }

    private static string ReadExportSource(string name, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!,
            "../../Desktop/Views/Archive", name)));
}
