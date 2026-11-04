using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.Tests;

public sealed class ArchiveSearchDefinitionCompatibilityRegressionTests
{
    private static readonly Guid FirstId = Guid.Parse("29000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = Guid.Parse("29000000-0000-0000-0000-000000000002");
    private static readonly Guid ThirdId = Guid.Parse("29000000-0000-0000-0000-000000000003");
    private static readonly Guid FourthId = Guid.Parse("29000000-0000-0000-0000-000000000004");

    [Theory]
    [InlineData(ArchiveThreadSort.LatestCapture, "latest-capture")]
    [InlineData(ArchiveThreadSort.OldestFirst, "oldest-first")]
    [InlineData(ArchiveThreadSort.Title, "title")]
    public async Task RetainedV1FileSurvivesRenameAndRestartWithExactCountPageAndSelectionIdentities(
        ArchiveThreadSort sort, string wireSort)
    {
        using TempDirectory directory = new();
        string path = StorePath(directory.Path);
        JsonObject pairs = Definition(wireSort, ["a", "A", " A ", "A"],
            [("a", "y"), (" A ", " x "), ("A", "x"), ("A", "x")]);
        JsonObject wildcard = Definition(wireSort, ["a", " A "], [(null, " x ")]);
        JsonObject contradiction = Definition(wireSort, ["A"], [(" A ", "x")]);
        JsonObject caseSensitive = Definition(wireSort, ["a"], [("a", "x")]);
        byte[] original = Bytes(Envelope(7,
            Entry(FirstId, "pairs", pairs), Entry(SecondId, "wildcard", wildcard),
            Entry(ThirdId, "contradiction", contradiction), Entry(FourthId, "case", caseSensitive)));
        File.WriteAllBytes(path, original);

        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            SavedArchiveSearchStore store = new(directory.Path, lease);
            SavedArchiveSearchSnapshot loaded = store.Load();
            Assert.Equal(SavedSearchStoreState.Ready, loaded.State);
            Assert.Equal(7, loaded.Revision);
            ArchiveSearchSpec exact = Assert.IsType<ArchiveSearchSpec>(loaded.Searches[0].Definition);
            Assert.Equal(new[] { " A ", "A", "a" }, exact.Sources);
            Assert.Equal(new[]
            {
                new ArchiveBoardConstraint(" A ", " x "), new ArchiveBoardConstraint("A", "x"),
                new ArchiveBoardConstraint("a", "y")
            }, exact.Boards);
            Assert.Equal(sort, exact.Sort);
            Assert.Equal(ArchiveWorkFilter.Followup, exact.WorkFilter);
            Assert.All(loaded.Searches, entry => Assert.True(entry.CanApply));
            Assert.Equal(original, File.ReadAllBytes(path));
            SavedArchiveSearchSnapshot renamed = store.Rename(FirstId, "renamed pairs", loaded.Revision);
            Assert.Equal(8, renamed.Revision);
            Assert.Equal(exact, renamed.Searches[0].Definition);
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));

            await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(directory.Path, lease);
            DateTimeOffset captured = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            int index = 0;
            // Identical thread IDs, titles and times force all three sorts to use exact identity tie-breaks.
            foreach (string source in new[] { "A", " A ", "a", "B" })
            foreach (string board in new[] { "x", " x ", "y" })
                await catalog.CaptureAsync(new CapturedThreadSnapshot(new(source, board, "1"),
                    "https://compatibility.test/thread/" + index++, "compatibility needle",
                    ThreadSourceState.Archived, captured, directory.Path,
                    [new CapturedPost("1", 0, PostFieldPresence.Html, Html: "synthetic body")]));
        }

        using (ProfileLease restartedLease = ProfileLease.Acquire(directory.Path))
        {
            SavedArchiveSearchSnapshot restarted = new SavedArchiveSearchStore(directory.Path, restartedLease).Load();
            Assert.Equal(SavedSearchStoreState.Ready, restarted.State);
            Assert.Equal(8, restarted.Revision);
            Assert.Equal(new[] { FirstId, SecondId, ThirdId, FourthId }, restarted.Searches.Select(entry => entry.Id));
            Assert.Equal("renamed pairs", restarted.Searches[0].Name);
            await using ArchiveReader reader = await ArchiveReader.OpenAsync(directory.Path);
            await AssertIdentityViews(reader, restarted.Searches[0],
                [new(" A ", " x ", "1"), new("A", "x", "1"), new("a", "y", "1")]);
            await AssertIdentityViews(reader, restarted.Searches[1],
                [new(" A ", " x ", "1"), new("a", " x ", "1")]);
            await AssertIdentityViews(reader, restarted.Searches[2], []);
            await AssertIdentityViews(reader, restarted.Searches[3], [new("a", "x", "1")]);
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void RetainedV1FileRoundTripsNullablePredicatesAllSourceStatesAndDateRangeWithoutDerivedValues(
        bool? captureEnabled, bool? hasPosts)
    {
        using TempDirectory directory = new();
        string path = StorePath(directory.Path);
        JsonObject definition = Definition("oldest-first", [" B ", "A"],
            [(null, " x "), (" B ", " y ")], ["active", "archived", "not-found"], captureEnabled, hasPosts,
            new JsonObject
            {
                ["basis"] = "first-seen", ["startDate"] = "2026-03-29",
                ["endDate"] = "2026-10-25", ["timeZoneId"] = "Europe/Berlin"
            });
        byte[] original = Bytes(Envelope(12, Entry(FirstId, "complete v1", definition)));
        File.WriteAllBytes(path, original);
        ArchiveSearchSpec? retained = null;

        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            SavedArchiveSearchStore store = new(directory.Path, lease);
            SavedArchiveSearchSnapshot loaded = store.Load();
            Assert.Equal(SavedSearchStoreState.Ready, loaded.State);
            Assert.Equal(12, loaded.Revision);
            SavedArchiveSearch saved = Assert.Single(loaded.Searches);
            ArchiveSearchSpec exact = Assert.IsType<ArchiveSearchSpec>(saved.Definition);
            Assert.Equal(new[] { " B ", "A" }, exact.Sources);
            Assert.Equal(new[]
            {
                new ArchiveBoardConstraint(null, " x "), new ArchiveBoardConstraint(" B ", " y ")
            }, exact.Boards);
            Assert.Equal(new[] { ThreadSourceState.Active, ThreadSourceState.Archived, ThreadSourceState.NotFound }, exact.SourceStates);
            Assert.Equal(captureEnabled, exact.CaptureEnabled);
            Assert.Equal(hasPosts, exact.HasPosts);
            Assert.Equal(ArchiveThreadSort.OldestFirst, exact.Sort);
            Assert.Equal(ArchiveWorkFilter.Followup, exact.WorkFilter);
            Assert.Equal(new ArchiveDateRange(new(2026, 3, 29), new(2026, 10, 25), "Europe/Berlin"), exact.DateRange);
            Assert.Equal(definition.ToJsonString(), saved.RawDefinition);
            Assert.Equal(original, File.ReadAllBytes(path));
            string canonical = ArchiveSearchSpecJson.Serialize(exact);
            ArchiveSearchSpec restored = ArchiveSearchSpecJson.Deserialize(canonical);
            Assert.Equal(exact, restored);
            Assert.Equal(canonical, ArchiveSearchSpecJson.Serialize(restored));
            using JsonDocument json = JsonDocument.Parse(canonical);
            Assert.Equal(new[] { "basis", "startDate", "endDate", "timeZoneId" },
                json.RootElement.GetProperty("dateRange").EnumerateObject().Select(property => property.Name));
            retained = exact;

            SavedArchiveSearchSnapshot renamed = store.Rename(FirstId, "complete v1 renamed", loaded.Revision);
            Assert.Equal(exact, Assert.Single(renamed.Searches).Definition);
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        }

        using (ProfileLease restartedLease = ProfileLease.Acquire(directory.Path))
        {
            SavedArchiveSearchSnapshot restarted = new SavedArchiveSearchStore(directory.Path, restartedLease).Load();
            Assert.Equal(SavedSearchStoreState.Ready, restarted.State);
            Assert.Equal(13, restarted.Revision);
            SavedArchiveSearch saved = Assert.Single(restarted.Searches);
            Assert.Equal(FirstId, saved.Id);
            Assert.Equal("complete v1 renamed", saved.Name);
            ArchiveSearchSpec exact = Assert.IsType<ArchiveSearchSpec>(saved.Definition);
            Assert.Equal(retained, exact);
            Assert.Equal(new ArchiveDateRange(new(2026, 3, 29), new(2026, 10, 25), "Europe/Berlin"), exact.DateRange);
            Assert.Equal(new[] { ThreadSourceState.Active, ThreadSourceState.Archived, ThreadSourceState.NotFound }, exact.SourceStates);
            Assert.Equal(captureEnabled, exact.CaptureEnabled);
            Assert.Equal(hasPosts, exact.HasPosts);
            Assert.Equal(definition.ToJsonString(), saved.RawDefinition);
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        }
    }

    [Theory]
    [InlineData("definition")]
    [InlineData("board")]
    [InlineData("dateRange")]
    [InlineData("entry")]
    [InlineData("envelope")]
    public void AdditiveV1FieldRemainsOpaqueAndBlocksWritesAcrossRestart(string location)
    {
        using TempDirectory directory = new();
        string path = StorePath(directory.Path);
        JsonObject root = Envelope(6, Entry(FirstId, "known", Definition()), Entry(SecondId, "additive", Definition()));
        JsonObject second = root["searches"]!.AsArray()[1]!.AsObject();
        switch (location)
        {
            case "definition":
                second["definition"]!["futureField"] = new JsonObject { ["mode"] = "synthetic" };
                break;
            case "board":
                second["definition"]!["boards"]![0]!["futureBoardField"] = "synthetic";
                break;
            case "dateRange":
                second["definition"]!["dateRange"] = new JsonObject
                {
                    ["basis"] = "first-seen", ["startDate"] = "2026-03-29",
                    ["endDate"] = null, ["timeZoneId"] = "UTC", ["futureDateField"] = true
                };
                break;
            case "entry":
                second["futureEntryField"] = new JsonArray("synthetic");
                break;
            case "envelope":
                root["futureEnvelopeField"] = new JsonObject { ["version"] = 2 };
                break;
            default: throw new ArgumentOutOfRangeException(nameof(location));
        }
        string raw = second["definition"]!.ToJsonString();
        if (location is "definition" or "board" or "dateRange")
            Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(raw));
        byte[] primary = Bytes(root);
        byte[] backup = Bytes(Envelope(2, Entry(ThirdId, "older backup", Definition())));
        File.WriteAllBytes(path, primary);
        File.WriteAllBytes(path + ".bak", backup);

        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(directory.Path);
            SavedArchiveSearchStore store = new(directory.Path, lease);
            SavedArchiveSearchSnapshot snapshot = store.Load();
            Assert.Equal(SavedSearchStoreState.Unsupported, snapshot.State);
            Assert.False(snapshot.CanWrite);
            Assert.Equal(6, snapshot.Revision);
            Assert.Equal(new[] { FirstId, SecondId }, snapshot.Searches.Select(entry => entry.Id));
            Assert.Equal("additive", snapshot.Searches[1].Name);
            Assert.Null(snapshot.Searches[1].Definition);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Searches[1].UnsupportedReason));
            Assert.Equal(raw, snapshot.Searches[1].RawDefinition);
            if (location == "envelope")
                Assert.All(snapshot.Searches, entry => Assert.False(entry.CanApply));
            else
            {
                Assert.True(snapshot.Searches[0].CanApply);
                Assert.False(snapshot.Searches[1].CanApply);
            }
            AssertWritesRejected(store, snapshot, FirstId);
            Assert.Throws<InvalidOperationException>(() => store.Recover(snapshot.Revision));
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
            Assert.Empty(Directory.GetFiles(directory.Path, "archive-searches.json.corrupt-*"));
            Assert.Empty(Directory.GetFiles(directory.Path, ".archive-searches.json.*.tmp"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FutureDefinitionInMixedFileRemainsOpaqueAcrossRestartWithoutDiscardingValidSibling(bool withFuturePredicate)
    {
        using TempDirectory directory = new();
        string path = StorePath(directory.Path);
        JsonObject known = Definition("title", [" A "], [(" A ", " x ")]);
        JsonObject future = Definition("title", ["B"], [("B", "y")]);
        future["schemaVersion"] = 2;
        if (withFuturePredicate)
            future["futurePredicate"] = new JsonObject { ["tags"] = new JsonArray("synthetic-tag"), ["mode"] = "all" };
        byte[] primary = Bytes(Envelope(9, Entry(FirstId, "known", known), Entry(SecondId, "future", future)));
        byte[] backup = Bytes(Envelope(4, Entry(ThirdId, "older backup", Definition())));
        File.WriteAllBytes(path, primary);
        File.WriteAllBytes(path + ".bak", backup);
        string? retainedRaw = null;
        ArchiveSearchSpec? retainedKnown = null;
        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(directory.Path);
            SavedArchiveSearchStore store = new(directory.Path, lease);
            SavedArchiveSearchSnapshot snapshot = store.Load();
            Assert.Equal(SavedSearchStoreState.Unsupported, snapshot.State);
            Assert.False(snapshot.CanWrite);
            Assert.Equal(9, snapshot.Revision);
            Assert.Equal(2, snapshot.Searches.Count);
            SavedArchiveSearch valid = snapshot.Searches[0];
            SavedArchiveSearch unsupported = snapshot.Searches[1];
            Assert.Equal(FirstId, valid.Id);
            Assert.True(valid.CanApply); // A read-only store is not the same as an unexecutable known sibling.
            ArchiveSearchSpec definition = Assert.IsType<ArchiveSearchSpec>(valid.Definition);
            Assert.Equal(new[] { " A " }, definition.Sources);
            Assert.Equal(new ArchiveBoardConstraint(" A ", " x "), Assert.Single(definition.Boards));
            Assert.Equal(SecondId, unsupported.Id);
            Assert.False(unsupported.CanApply);
            Assert.Null(unsupported.Definition);
            Assert.False(string.IsNullOrWhiteSpace(unsupported.UnsupportedReason));
            JsonNode opaque = JsonNode.Parse(unsupported.RawDefinition)!;
            Assert.Equal(2, opaque["schemaVersion"]!.GetValue<int>());
            if (withFuturePredicate)
            {
                Assert.Equal("all", opaque["futurePredicate"]!["mode"]!.GetValue<string>());
                Assert.Equal("synthetic-tag", opaque["futurePredicate"]!["tags"]![0]!.GetValue<string>());
            }
            else Assert.Null(opaque["futurePredicate"]);
            if (restart == 0) { retainedRaw = unsupported.RawDefinition; retainedKnown = definition; }
            else { Assert.Equal(retainedRaw, unsupported.RawDefinition); Assert.Equal(retainedKnown, definition); }
            AssertWritesRejected(store, snapshot, FirstId);
            Assert.Throws<InvalidOperationException>(() => store.Recover(snapshot.Revision));
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
            Assert.Empty(Directory.GetFiles(directory.Path, "archive-searches.json.corrupt-*"));
        }
    }

    [Theory]
    [InlineData("duplicate-board-source", false)]
    [InlineData("duplicate-board-source", true)]
    [InlineData("missing-board-source", false)]
    [InlineData("missing-board-source", true)]
    [InlineData("null-sources", false)]
    [InlineData("null-sources", true)]
    [InlineData("blank-source", false)]
    [InlineData("blank-source", true)]
    [InlineData("source-limit", false)]
    [InlineData("source-limit", true)]
    [InlineData("invalid-date", false)]
    [InlineData("invalid-date", true)]
    [InlineData("invalid-capture-type", false)]
    [InlineData("invalid-capture-type", true)]
    [InlineData("missing-sort", false)]
    [InlineData("missing-sort", true)]
    public void MalformedKnownEntryBetweenValidSiblingsCannotBecomePartialOrBroaderDefinition(
        string damage, bool withBackup)
    {
        using TempDirectory directory = new();
        string path = StorePath(directory.Path);
        string malformed = MalformedDefinition(damage);
        // Keep the bad entry in the middle: neither the already-read prefix nor the valid suffix may leak.
        string primaryJson = "{\"schemaVersion\":1,\"revision\":8,\"searches\":[" +
            Entry(FirstId, "valid prefix", Definition("title", [" A "], [(" A ", " x ")])).ToJsonString() +
            ",{\"id\":\"" + SecondId + "\",\"name\":\"invalid\",\"definition\":" + malformed + "}," +
            Entry(ThirdId, "valid suffix", Definition("title", ["B"], [("B", "y")])).ToJsonString() + "]}";
        byte[] primary = Encoding.UTF8.GetBytes(primaryJson);
        byte[] backup = Bytes(Envelope(3, Entry(FourthId, "reviewed backup", Definition("oldest-first", ["a"], [("a", "x")]))));
        File.WriteAllBytes(path, primary);
        if (withBackup) File.WriteAllBytes(path + ".bak", backup);
        for (int restart = 0; restart < 2; restart++)
        {
            using ProfileLease lease = ProfileLease.Acquire(directory.Path);
            SavedArchiveSearchStore store = new(directory.Path, lease);
            SavedArchiveSearchSnapshot snapshot = store.Load();
            Assert.False(snapshot.CanWrite);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.Error));
            if (withBackup)
            {
                Assert.Equal(SavedSearchStoreState.RecoveryAvailable, snapshot.State);
                Assert.Equal(3, snapshot.Revision);
                SavedArchiveSearch recoveredCandidate = Assert.Single(snapshot.Searches);
                Assert.Equal(FourthId, recoveredCandidate.Id);
                Assert.Equal("reviewed backup", recoveredCandidate.Name);
                ArchiveSearchSpec exact = Assert.IsType<ArchiveSearchSpec>(recoveredCandidate.Definition);
                Assert.Equal(new[] { "a" }, exact.Sources);
                Assert.Equal(new ArchiveBoardConstraint("a", "x"), Assert.Single(exact.Boards));
                Assert.Equal(ArchiveThreadSort.OldestFirst, exact.Sort);
                Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
            }
            else
            {
                Assert.Equal(SavedSearchStoreState.Failed, snapshot.State);
                Assert.Empty(snapshot.Searches);
                Assert.Throws<InvalidOperationException>(() => store.Recover(snapshot.Revision));
                Assert.False(File.Exists(path + ".bak"));
            }
            AssertWritesRejected(store, snapshot, FirstId);
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory.Path, "archive-searches.json.corrupt-*"));
        }
    }

    private static async Task AssertIdentityViews(ArchiveReader reader, SavedArchiveSearch saved, ResourceId[] expected)
    {
        Assert.True(saved.CanApply);
        ArchiveSearchRequest request = ArchiveSearchRequest.Create(Assert.IsType<ArchiveSearchSpec>(saved.Definition));
        Assert.Equal(expected, await reader.SelectThreadIdsBySpecAsync(request));
        Assert.Equal(expected, (await reader.SelectThreadSummariesBySpecAsync(request)).Select(item => item.Id));
        List<ResourceId> pages = [];
        for (int offset = 0; offset <= expected.Length; offset++)
        {
            ArchiveThreadPage page = await reader.SearchThreadsBySpecAsync(request, offset, 1);
            Assert.Equal(expected.Length, page.TotalCount);
            Assert.Equal(offset, page.Offset);
            if (offset < expected.Length) pages.Add(Assert.Single(page.Items).Id);
            else Assert.Empty(page.Items);
        }
        Assert.Equal(expected, pages);
    }

    private static void AssertWritesRejected(SavedArchiveSearchStore store, SavedArchiveSearchSnapshot snapshot, Guid id)
    {
        Assert.Throws<SavedSearchRevisionException>(() => store.Rename(id, "must not write", snapshot.Revision));
        Assert.Throws<SavedSearchRevisionException>(() => store.Replace(id, new ArchiveSearchSpec(), snapshot.Revision));
        Assert.Throws<SavedSearchRevisionException>(() => store.Delete(id, snapshot.Revision));
        Assert.Throws<SavedSearchRevisionException>(() => store.Create(Guid.NewGuid(), "must not create", new ArchiveSearchSpec(), snapshot.Revision));
    }

    private static string MalformedDefinition(string damage)
    {
        JsonObject definition = Definition("title", ["A"], [("A", "x")]);
        switch (damage)
        {
            case "duplicate-board-source":
                string valid = definition.ToJsonString();
                const string boardFields = "\"boards\":[{\"source\":\"A\",\"board\":\"x\"}]";
                Assert.Contains(boardFields, valid);
                return valid.Replace(boardFields,
                    "\"boards\":[{\"source\":\"A\",\"source\":null,\"board\":\"x\"}]", StringComparison.Ordinal);
            case "missing-board-source":
                Assert.True(definition["boards"]![0]!.AsObject().Remove("source"));
                break;
            case "null-sources": definition["sources"] = null; break;
            case "blank-source": definition["sources"] = new JsonArray(" "); break;
            case "source-limit":
                JsonArray sources = new();
                for (int index = 0; index < 129; index++) sources.Add("source-" + index);
                definition["sources"] = sources;
                break;
            case "invalid-date":
                definition["dateRange"] = new JsonObject
                {
                    ["basis"] = "first-seen", ["startDate"] = "2026-02-30",
                    ["endDate"] = null, ["timeZoneId"] = "UTC"
                };
                break;
            case "invalid-capture-type": definition["captureEnabled"] = 1; break;
            case "missing-sort": definition.Remove("sort"); break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
        return definition.ToJsonString();
    }

    // Deliberately construct a retained v1 wire fixture without using the serializer under test.
    private static JsonObject Definition(string sort = "title", string[]? sources = null,
        (string? Source, string Board)[]? boards = null, string[]? sourceStates = null,
        bool? captureEnabled = true, bool? hasPosts = true, JsonObject? dateRange = null)
    {
        JsonArray sourceArray = new();
        foreach (string source in sources ?? ["A"]) sourceArray.Add(source);
        JsonArray boardArray = new();
        foreach ((string? source, string board) in boards ?? [("A", "x")])
            boardArray.Add(new JsonObject { ["source"] = source, ["board"] = board });
        JsonArray stateArray = new();
        foreach (string state in sourceStates ?? ["archived"]) stateArray.Add(state);
        return new JsonObject
        {
            ["schemaVersion"] = 1, ["text"] = "compatibility needle", ["sources"] = sourceArray,
            ["boards"] = boardArray, ["sourceStates"] = stateArray,
            ["captureEnabled"] = captureEnabled.HasValue ? JsonValue.Create(captureEnabled.Value) : null,
            ["hasPosts"] = hasPosts.HasValue ? JsonValue.Create(hasPosts.Value) : null, ["sort"] = sort,
            ["workFilter"] = "followup", ["dateRange"] = dateRange?.DeepClone()
        };
    }

    private static JsonObject Entry(Guid id, string name, JsonObject definition) => new()
    {
        ["id"] = id.ToString(), ["name"] = name, ["definition"] = definition
    };

    private static JsonObject Envelope(long revision, params JsonObject[] entries)
    {
        JsonArray searches = new();
        foreach (JsonObject entry in entries) searches.Add(entry);
        return new JsonObject { ["schemaVersion"] = 1, ["revision"] = revision, ["searches"] = searches };
    }

    private static byte[] Bytes(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString());
    private static string StorePath(string directory) => Path.Combine(directory, "archive-searches.json");
}
