using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MeowChan.DesktopTests;

// Source wiring guards only: not runtime/UI tests. Read repository sources, never profiles or databases.
public sealed class CoreLimitUsageSourceContractTests
{
    private static string Source(string relative, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    private static string Compact(string source) => Regex.Replace(source, @"\s+", " ");

    [Fact]
    public void VisibleNumericLimitsStayBoundToConfigAndItsCoreRanges()
    {
        string config = Source("Desktop/Config.cs");
        string editor = Source("Desktop/Views/Settings/SettingsEditor.Limits.cs");
        string fields = Source("Desktop/Views/Settings/SettingsEditor.axaml");
        foreach (string property in new[] { "ArchiveTagFilterTagsPerList", "UrlTextExportLimitMiB", "SlashTagSuggestionCount" })
        {
            Assert.Contains($"public int {property} {{ get; set; }} = {property}Default;", config);
            Assert.Contains($"candidate.{property} = GetInteger({property}, {property}Label, Config.{property}Minimum, Config.{property}Maximum);", editor);
            Assert.Contains($"x:Name=\"{property}Input\" Value=\"{{Binding {property}, Mode=TwoWay}}\" Minimum=\"{{Binding {property}Minimum}}\" Maximum=\"{{Binding {property}Maximum}}\"", fields);
        }
        Assert.Contains("ArchiveTagFilterTagsPerListMaximum = Archive.ArchiveTagFilter.MaximumConfigurableTagsPerList;", config);
        Assert.Contains("UrlTextExportLimitMiBMaximum = Desktop.UrlTextExportFileWriter.MaximumLimitMiB;", config);
        Assert.Contains("DefaultTagsPerList = 32;", Source("libmeowchan/Archive/ArchiveTagFilter.cs"));
        Assert.Contains("MinimumTagsPerList = 8;", Source("libmeowchan/Archive/ArchiveTagFilter.cs"));
        Assert.Contains("MaximumConfigurableTagsPerList = 128;", Source("libmeowchan/Archive/ArchiveTagFilter.cs"));
        string writer = Source("Desktop/UrlTextExportFileWriter.cs");
        Assert.Contains("DefaultLimitMiB = 1;", writer);
        Assert.Contains("MinimumLimitMiB = 1;", writer);
        Assert.Contains("MaximumLimitMiB = 64;", writer);
    }

    [Fact]
    public void UrlOperationFreezesTheActualConfiguredWriterBudgetBeforeTheFirstReadAwait()
    {
        string caller = Source("Desktop/Views/Archive/ArchivePage.UrlTextExport.cs");
        int snapshot = caller.IndexOf("int limitMiB = UrlTextExportLimitMiBPreference;", StringComparison.Ordinal);
        int read = caller.IndexOf("summary = await", StringComparison.Ordinal);
        Assert.True(snapshot >= 0 && read > snapshot);
        Assert.Contains("new UrlTextExportFileWriter(limitMiB)", caller);
        Assert.Contains("writer.WriteAsync(planToWrite, target, token)), cancellation.Token, limitMiB)", caller);
        string host = Source("Desktop/Shell/MainWindow.Presentation.cs");
        int modeBranch = host.IndexOf("if (modeChanged)", StringComparison.Ordinal);
        foreach (string assignment in new[]
        {
            "_archivePage.UrlTextExportLimitMiBPreference = _config.UrlTextExportLimitMiB;",
            "_archivePage.ArchiveTagFilterTagsPerListPreference = _config.ArchiveTagFilterTagsPerList;",
            "_mediaPage.ArchiveTagFilterTagsPerListPreference = _config.ArchiveTagFilterTagsPerList;"
        })
        {
            int at = host.IndexOf(assignment, StringComparison.Ordinal);
            Assert.True(at >= 0 && modeBranch > at, assignment + " must apply even without a mode change.");
        }
    }

    [Fact]
    public void WriterBudgetDoesNotLiftTheSinglePageFormatOrDestinationPathCeilings()
    {
        string codec = Source("libmeowchan/Interop/Links/PlainUrlText.cs");
        Assert.Contains("MaxBytes = 1_048_576;", codec);
        Assert.Contains("MaxLines = 4096;", codec);
        Assert.Contains("MaxLineCharacters = 8192;", codec);
        string plan = Source("Desktop/ArchiveUrlTextExportPlan.cs");
        Assert.Contains("if (count >= PlainUrlText.MaxBytes) return null;", plan);
        Assert.Contains("parsed.Links.Count != 1", plan);
        Assert.Contains("selectedId.Source is not (\"4chan\" or \"8kun\")", plan);
        Assert.DoesNotContain("UrlTextExportLimitMiB", plan);
        string writer = Source("Desktop/UrlTextExportFileWriter.cs");
        Assert.Contains("MaximumPathCharacters = 4096;", writer);
        Assert.Contains("MaximumPathComponents = 128;", writer);
        Assert.Contains("destination.Length > MaximumPathCharacters", writer);
        Assert.Contains("parts.Length > MaximumPathComponents", writer);
        int bytes = writer.IndexOf("if (!IsWithinByteLimit(plan.ByteCount))", StringComparison.Ordinal);
        Assert.True(bytes >= 0 && writer.IndexOf("ValidatePath(destination", StringComparison.Ordinal) > bytes);
    }

    [Fact]
    public void PathLimitExceededIsNotPresentedAsConfiguredOutputMiBExceeded()
    {
        string dialog = Source("Desktop/Views/Archive/UrlTextExportDialog.cs");
        Assert.Contains("UrlTextExportFileWriteError.LimitExceeded => LimitFailureKey(_plan.ByteCount, LimitMiB)", dialog);
        Assert.Contains("(long)byteCount > (long)limitMiB * 1024 * 1024 ? \"error.outputLimit\" : \"error.pathLimit\"", dialog);
        Assert.Contains("Texts.T(_operation.StatusKey, _operation.LimitMiB)", dialog);
        string caller = Source("Desktop/Views/Archive/ArchivePage.UrlTextExport.cs");
        Assert.Contains("if (plan == null) { Message(Texts.T(\"urltext.export.error.invalidUrl\"), true); return; }", caller);
    }

    [Fact]
    public void SavedFiltersAndAllCallerReconstructionsUseTechnical128RatherThanLoweredAdmission()
    {
        // These specific call arguments contain no parenthesis characters inside string literals.
        // This is a targeted source guard, not a general C# parser.
        foreach (var (path, expected) in new[]
        {
            ("libmeowchan/Archive/ArchiveSearchSpecJson.cs", 1),
            ("Desktop/Views/Archive/ArchivePage.TagFilter.cs", 6),
            ("Desktop/Views/Media/MediaPage.Filters.cs", 3)
        })
        {
            string source = Source(path);
            MatchCollection calls = Regex.Matches(source, @"(?:new ArchiveTagFilter|ArchiveTagFilter\.OrNull)\(");
            Assert.Equal(expected, calls.Count);
            foreach (Match call in calls)
            {
                int start = call.Index + call.Length, at = start, depth = 1;
                for (; at < source.Length && depth > 0; at++)
                {
                    if (source[at] == '(') depth++;
                    else if (source[at] == ')') depth--;
                }
                Assert.Equal(0, depth);
                Assert.EndsWith("ArchiveTagFilter.MaximumConfigurableTagsPerList", source[start..(at - 1)].Trim());
            }
        }
        Assert.Contains("if (count > ArchiveTagFilter.MaximumConfigurableTagsPerList)", Source("libmeowchan/Archive/ArchiveSearchSpecJson.cs"));
        string archive = Compact(Source("Desktop/Views/Archive/ArchivePage.TagFilter.cs"));
        Assert.Contains("_draftIncludeTags.AddRange(tags.Include); _draftExcludeTags.AddRange(tags.Exclude);", archive);
    }

    [Fact]
    public void LoweredAdmissionChecksOnlyTheGrowingDestinationBeforeMutatingEitherList()
    {
        string archive = Compact(Source("Desktop/Views/Archive/ArchivePage.TagFilter.cs"));
        string media = Compact(Source("Desktop/Views/Media/MediaPage.Filters.cs"));
        Assert.Contains("DraftTagRefusal(include ? _draftIncludeTags : _draftExcludeTags, include ? _draftExcludeTags : _draftIncludeTags, id, ArchiveTagFilterTagsPerListPreference)", archive);
        Assert.Contains("DraftTagRefusal(wasIncluded ? _draftExcludeTags : _draftIncludeTags, Array.Empty<Guid>(), id, ArchiveTagFilterTagsPerListPreference)", archive);
        Assert.Contains("DraftTagRefusal(include ? _includeTags : _excludeTags, Array.Empty<Guid>(), id, ArchiveTagFilterTagsPerListPreference)", media);
        Assert.Contains("DraftTagRefusal(wasIncluded ? _excludeTags : _includeTags, Array.Empty<Guid>(), id, ArchiveTagFilterTagsPerListPreference)", media);
        Assert.Contains("list.Contains(id) ? null", archive);
        Assert.Contains("list.Count >= tagsPerList ? Texts.T(\"coreLimits.tags.tooMany\", tagsPerList)", archive);
        foreach (string source in new[] { archive, media })
        {
            int toggle = source.IndexOf("internal string? Toggle", StringComparison.Ordinal);
            int guard = source.IndexOf("DraftTagRefusal(wasIncluded", toggle, StringComparison.Ordinal);
            int mutation = source.IndexOf(".Remove(id);", toggle, StringComparison.Ordinal);
            Assert.True(guard > toggle && mutation > guard);
        }
    }
}
