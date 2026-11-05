using System.Globalization;
using System.Text.Json;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// UNRUN at authoring: pure production CollectionResult provenance, no ArchivePage/UI,
// exporters, profiles, filesystem fixtures, provider calls or network access.
[Collection("Localization")]
public sealed class ArchiveExportOmissionLocalizationTests : IDisposable
{
    private static readonly string[] Languages = ["en", "de", "es", "fr", "ru"];
    private const string Destination = "/synthetic/export.omission.Unavailable/{0}/ü 日本";
    private const string Backup = @"Z:\synthetic\export.backup\{0}\ü 日本";
    private static readonly string[] RawWarnings =
    [
        "export.omission.Unavailable",
        "RAW warning {0} <tag> ü 日本\nraw second line",
        "export.omission.Unavailable" // Duplicate raw warnings must not be collapsed.
    ];
    private readonly string _previousLanguage = Texts.Language;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _catalogs = new(StringComparer.Ordinal);

    public ArchiveExportOmissionLocalizationTests()
    {
        // No user directory: inspect precisely the shipped EN/DE + embedded ES/FR/RU.
        Texts.Initialize();
        Texts.SetLanguage("en");
        _catalogs.Add("en", BuiltInCatalog.English);
        _catalogs.Add("de", BuiltInCatalog.German);
        foreach (string language in new[] { "es", "fr", "ru" })
            _catalogs.Add(language, ReadEmbeddedCatalog(language));
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    public static IEnumerable<object[]> EveryReasonAndLanguage()
    {
        // Deliberately enumerate the real enum, not a six-value list that could silently
        // stop covering a future reason. New reasons must have actual shipped translations.
        foreach (ArchiveExportOmissionReason reason in Enum.GetValues<ArchiveExportOmissionReason>())
        foreach (string language in Languages)
            yield return [reason, language];
    }

    [Theory]
    [MemberData(nameof(EveryReasonAndLanguage))]
    public void EveryReasonUsesItsRealBundledTextInRetainedCollectionProvenance(
        ArchiveExportOmissionReason reason, string language)
    {
        SelectLanguage("en");
        ResourceId thread = new("omission-fixture", "board", "101");
        ArchiveCollectionMember member = Member(thread, "one", 11);
        ArchiveCollectionHtmlExportResult result = Result([member],
            [new ArchiveCollectionExportOmission(thread, "post", "media", reason)], rawDetails: false);
        var status = ArchivePage.ArchiveExportStatusText.CollectionResult(result);
        string english = status.Render();

        SelectLanguage(language);
        string reasonText = StrictText(language, "export.omission." + reason);
        string expected = string.Join("\n", new[]
        {
            StrictText(language, "export.collectionDone", 1, 11, 17, 1, 0),
            Destination,
            StrictText(language, "export.collectionMemberDone", Label(thread), 11, 1),
            Label(thread) + ": " + reasonText + " × 1"
        });
        Assert.Equal(expected, status.Render());
        Assert.Equal(expected, ArchivePage.CollectionExportResultText(result));
        // Check the localized line only: raw paths/warnings intentionally contain keys.
        string omissionLine = status.Render().Split('\n')[3];
        Assert.DoesNotContain("export.omission.", omissionLine, StringComparison.Ordinal);
        if (language != "en") Assert.NotEqual(english, status.Render());
        SelectLanguage("en");
        Assert.Equal(english, status.Render()); // Same provenance instance, no recreation.
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void AllReasonsGroupByFullThreadAndReasonWithoutChangingCountsOrderOrRawData(string creationLanguage)
    {
        SelectLanguage(creationLanguage);
        ResourceId a = new("source-a", "same-board", "101");
        ResourceId b = new("source-b", "same-board", "101"); // Source differs only.
        ResourceId c = new("source-a", "other-board", "101"); // Board differs only.
        ResourceId d = new("source-a", "same-board", "202"); // Thread differs only.
        ResourceId noOmissions = new("source-a", "same-board", "303");
        // Member order intentionally differs from the first occurrence of omission groups.
        ArchiveCollectionMember[] members =
        [Member(d, "d", 404), Member(noOmissions, "zero", 505), Member(b, "b", 202), Member(a, "a", 101), Member(c, "c", 303)];
        ArchiveExportOmissionReason[] reasons = Enum.GetValues<ArchiveExportOmissionReason>();
        List<ArchiveCollectionExportOmission> omissions = new();
        List<(ResourceId Thread, ArchiveExportOmissionReason Reason, int Count)> expectedGroups = new();
        foreach (ArchiveExportOmissionReason reason in reasons)
        {
            // Nonadjacent duplicates and fresh, value-equal ResourceIds: grouping by object
            // reference, contiguous run, post/media, reason alone or board/thread alone fails.
            foreach (ResourceId thread in new[] { b, a, c, d, b, a, b })
                omissions.Add(new ArchiveCollectionExportOmission(
                    new ResourceId(thread.Source, thread.Board, thread.Thread), "shared-post", "shared-media", reason));
            // Independent fixture plan; do not use GroupBy/Count over omissions as the oracle.
            expectedGroups.Add((b, reason, 3));
            expectedGroups.Add((a, reason, 2));
            expectedGroups.Add((c, reason, 1));
            expectedGroups.Add((d, reason, 1));
        }
        ArchiveCollectionHtmlExportResult result = Result(members, omissions.ToArray(), rawDetails: true);
        var status = ArchivePage.ArchiveExportStatusText.CollectionResult(result);
        string original = status.Render();
        ArchiveCollectionExportOmission[] originalOmissions = result.Omissions.ToArray();
        ArchiveCollectionMember[] originalMembers = result.Metadata!.Members.ToArray();
        string[] originalWarnings = result.Warnings.ToArray();

        // Every possible creation language must be able to refresh to the other four and back.
        foreach (string language in Languages.Where(code => code != creationLanguage)
                     .Prepend(creationLanguage).Append(creationLanguage))
        {
            SelectLanguage(language);
            int n = reasons.Length;
            string[] memberLines =
            [
                StrictText(language, "export.collectionMemberDone", Label(d), 404, n),
                StrictText(language, "export.collectionMemberDone", Label(noOmissions), 505, 0),
                StrictText(language, "export.collectionMemberDone", Label(b), 202, 3 * n),
                StrictText(language, "export.collectionMemberDone", Label(a), 101, 2 * n),
                StrictText(language, "export.collectionMemberDone", Label(c), 303, n)
            ];
            string[] omissionLines = expectedGroups.Select(group => Label(group.Thread) + ": "
                + StrictText(language, "export.omission." + group.Reason) + " × " + group.Count).ToArray();
            List<string> expected = new()
            {
                StrictText(language, "export.collectionDone", 5, 1515, 17, 7 * n, 3),
                Destination
            };
            expected.AddRange(memberLines);
            expected.AddRange(omissionLines);
            expected.AddRange(RawWarnings);
            expected.Add(StrictText(language, "export.backup", Backup));

            string actual = status.Render();
            Assert.Equal(string.Join("\n", expected), actual);
            Assert.Equal(actual, ArchivePage.CollectionExportResultText(result));
            string[] actualLines = actual.Split('\n');
            Assert.Equal(Destination, actualLines[1]);
            Assert.Equal(memberLines, actualLines.Skip(2).Take(5).ToArray());
            Assert.Equal(omissionLines, actualLines.Skip(7).Take(4 * n).ToArray());
            Assert.Equal(4 * n, omissionLines.Length);
            Assert.All(actualLines.Skip(7).Take(4 * n), line =>
                Assert.DoesNotContain("export.omission.", line, StringComparison.Ordinal));
            // Compare the complete tail, including warning duplicates, its embedded newline,
            // key-looking raw text and braces in both raw warning and backup argument.
            string rawTail = string.Join("\n", RawWarnings) + "\n" + StrictText(language, "export.backup", Backup);
            Assert.Equal(rawTail, string.Join("\n", actualLines.Skip(7 + 4 * n)));
            Assert.Contains(Backup, actual, StringComparison.Ordinal);
            Assert.Equal(originalOmissions, result.Omissions.ToArray());
            Assert.Equal(originalMembers, result.Metadata.Members.ToArray());
            Assert.Equal(originalWarnings, result.Warnings.ToArray());
            Assert.Equal(Destination, result.Directory);
            Assert.Equal(Backup, result.BackupDirectory);
            Assert.Equal(creationLanguage, result.Metadata.Options.Language);
            if (language != creationLanguage) Assert.NotEqual(original, actual);
        }
        Assert.Equal(original, status.Render());
    }

    private void SelectLanguage(string language)
    {
        Assert.Empty(Texts.Warnings);
        Assert.Contains(Texts.Languages, item => item.Code == language);
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
    }

    private string StrictText(string language, string key, params object?[] arguments)
    {
        Assert.Equal(language, Texts.Language);
        // Direct dictionary/resource presence, not Texts.T's English/key fallback.
        Assert.True(_catalogs[language].TryGetValue(key, out string? template), language + ": missing actual catalog key " + key);
        Assert.False(string.IsNullOrWhiteSpace(template), language + ": empty catalog key " + key);
        Assert.NotEqual(key, template);
        Assert.Equal(template, Texts.T(key));
        if (language != "en") Assert.NotEqual(_catalogs["en"][key], template);
        // Every count/identity/path argument must actually appear in the shipped template,
        // not merely be supplied to a format that silently dropped that placeholder.
        Assert.Equal(arguments.Length, System.Text.CompositeFormat.Parse(template!).MinimumArgumentCount);
        for (int index = 0; index < arguments.Length; index++)
            Assert.Contains("{" + index + "}", template!, StringComparison.Ordinal);
        // Strict string.Format throws on bad translated placeholders, unlike T's fallback.
        string expected = arguments.Length == 0 ? template!
            : string.Format(CultureInfo.GetCultureInfo(language), template!, arguments);
        Assert.Equal(expected, Texts.T(key, arguments));
        return expected;
    }

    private static IReadOnlyDictionary<string, string> ReadEmbeddedCatalog(string language)
    {
        string resource = "MeowChan.Desktop.Localization.Packs." + language + ".json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing shipped resource: " + resource);
        using JsonDocument document = JsonDocument.Parse(stream);
        Assert.Equal(language, document.RootElement.GetProperty("code").GetString());
        Dictionary<string, string> strings = new(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.GetProperty("strings").EnumerateObject())
        {
            Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
            Assert.True(strings.TryAdd(property.Name, property.Value.GetString()!), "Duplicate bundled key: " + property.Name);
        }
        return strings;
    }

    private static ArchiveCollectionHtmlExportResult Result(IReadOnlyList<ArchiveCollectionMember> members,
        IReadOnlyList<ArchiveCollectionExportOmission> omissions, bool rawDetails) =>
        new(Destination, Destination + "/index.html", members.Count, members.Sum(member => member.PostCount), 17, omissions)
        {
            Metadata = new ArchiveCollectionExportMetadata(1, Guid.Parse("3fb96816-95a4-47e6-93c9-1030225cc6db"), 2,
                members, DateTimeOffset.UnixEpoch, new ArchiveHtmlExportOptions(false) { Language = Texts.Language },
                Array.Empty<ArchiveExportFile>()),
            BackupDirectory = rawDetails ? Backup : null,
            Warnings = rawDetails ? RawWarnings.ToArray() : Array.Empty<string>()
        };

    private static ArchiveCollectionMember Member(ResourceId id, string page, int posts) =>
        new(id, "https://offline.invalid/" + page, "threads/" + page + ".html", DateTimeOffset.UnixEpoch, 1, posts);
    private static string Label(ResourceId id) => id.Source + " /" + id.Board + "/ " + id.Thread;
}
