using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using Microsoft.Data.Sqlite;

namespace MeowChan.Tests;

public sealed class ArchiveSearchDefinitionHostileInputTests
{
    [Fact]
    public void PersistedUnicodeKeepsCanonicalEquivalentsDistinctAndPreservesInvisibleCharacters()
    {
        string[] identities = [" é ", " e\u0301 ", "\u200Bsource", "源😀"];
        string text = "\té e\u0301 \u200B源😀\r\n";
        JsonObject wire = Wire();
        wire["text"] = text;
        wire["sources"] = new JsonArray(identities.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        wire["boards"] = new JsonArray(identities.Select(value => (JsonNode?)new JsonObject
        {
            ["source"] = value, ["board"] = value
        }).ToArray());

        ArchiveSearchSpec restored = ArchiveSearchSpecJson.Deserialize(wire.ToJsonString());
        Assert.Equal(text, restored.Text);
        Assert.Equal(identities.Order(StringComparer.Ordinal), restored.Sources);
        Assert.Equal(4, restored.Boards.Count);
        Assert.All(restored.Boards, board => Assert.Equal(board.Source, board.Board));
        string canonical = ArchiveSearchSpecJson.Serialize(restored);
        Assert.Equal(restored, ArchiveSearchSpecJson.Deserialize(canonical));
        Assert.Equal(canonical, ArchiveSearchSpecJson.Serialize(ArchiveSearchSpecJson.Deserialize(canonical)));
    }

    [Fact]
    public void TextLimitCountsUtf16UnitsNotRunesOrUtf8BytesAndDoesNotTrimBeforeValidation()
    {
        string atLimit = string.Concat(Enumerable.Repeat("😀", 256));
        JsonObject wire = Wire();
        wire["text"] = atLimit;
        Assert.Equal(atLimit, ArchiveSearchSpecJson.Deserialize(wire.ToJsonString()).Text);
        foreach (string tooLong in new[] { atLimit + "a", " " + atLimit, new string(' ', 513) })
        {
            wire["text"] = tooLong;
            Assert.Throws<ArgumentException>(() => ArchiveSearchSpecJson.Deserialize(wire.ToJsonString()));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void UnknownIntegralVersionsNeverProduceAnExecutableDefinition(int version)
    {
        JsonObject wire = Wire();
        wire["schemaVersion"] = version;
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(
            () => ArchiveSearchSpecJson.Deserialize(wire.ToJsonString()));
        Assert.Equal("schemaVersion", error.ParamName);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("boards")]
    public void DuplicateSelectorsStillConsumeTheBoundedInputBudget(string field)
    {
        JsonObject wire = Wire();
        JsonArray values = new();
        for (int i = 0; i < 128; i++)
            values.Add(field == "sources" ? JsonValue.Create("same") : new JsonObject { ["source"] = null, ["board"] = "same" });
        wire[field] = values;
        ArchiveSearchSpec accepted = ArchiveSearchSpecJson.Deserialize(wire.ToJsonString());
        if (field == "sources") Assert.Single(accepted.Sources);
        else Assert.Single(accepted.Boards);
        values.Add(field == "sources" ? JsonValue.Create("same") : new JsonObject { ["source"] = null, ["board"] = "same" });
        Assert.Throws<ArgumentException>(() => ArchiveSearchSpecJson.Deserialize(wire.ToJsonString()));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("sources")]
    [InlineData("boardSource")]
    [InlineData("boardName")]
    public void EscapedNulCannotCrossTheWireAsTextOrAnIdentity(string location)
    {
        JsonObject wire = Wire();
        switch (location)
        {
            case "text": wire["text"] = "left\0right"; break;
            case "sources": wire["sources"] = new JsonArray("left\0right"); break;
            default:
                wire["boards"] = new JsonArray(new JsonObject
                {
                    ["source"] = location == "boardSource" ? "left\0right" : null,
                    ["board"] = location == "boardName" ? "left\0right" : "x"
                });
                break;
        }
        Assert.Throws<ArgumentException>(() => ArchiveSearchSpecJson.Deserialize(wire.ToJsonString()));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,}")]
    public void BoundedMalformedDocumentsAreRejectedInsteadOfBecomingDefaultSearches(string json)
        => Assert.ThrowsAny<JsonException>(() => ArchiveSearchSpecJson.Deserialize(json));

    [Fact]
    public void EscapedDuplicateFieldNamesAreStillDuplicates()
    {
        string json = ArchiveSearchSpecJson.Serialize(new ArchiveSearchSpec());
        string duplicate = json.Insert(1, "\"te\\u0078t\":\"hidden predicate\",");
        Assert.Throws<JsonException>(() => ArchiveSearchSpecJson.Deserialize(duplicate));
    }

    [Theory]
    [InlineData(" \" OR title:needle NEAR(foo bar) -excluded * ")]
    [InlineData("%_\\'; DROP TABLE archive_threads; --")]
    [InlineData("源😀 e\u0301 \"quoted\"")]
    public void CompilerBindsPersistedQueryAndSqlLookingIdentitiesAsValues(string text)
    {
        const string identity = "x'); DROP TABLE archive_threads; --";
        ArchiveSearchSpec persisted = ArchiveSearchSpecJson.Deserialize(ArchiveSearchSpecJson.Serialize(
            new ArchiveSearchSpec(text, sources: [identity], boards: [new(identity, identity)])));
        ArchiveSearchRequest request = ArchiveSearchRequest.Create(persisted);
        ArchiveSearchCompiler indexed = new(request, "t.rowid IN (SELECT rowid FROM archive_thread_search WHERE archive_thread_search MATCH $match)", true);
        using SqliteCommand command = new(); // Parameter inspection only: no connection or database.
        indexed.Bind(command);
        Assert.Equal("\"" + text.Trim().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"", command.Parameters["$match"].Value);
        Assert.Equal(4, command.Parameters.Count);
        for (int i = 0; i < 3; i++) Assert.Equal(identity, command.Parameters["$filter" + i].Value);
        Assert.DoesNotContain(identity, indexed.Where);
        Assert.DoesNotContain(text, indexed.Where);
        Assert.Equal(text, persisted.Text);

        ArchiveSearchCompiler scan = new(request, "meowchan_contains(t.title)", false);
        using SqliteCommand scanCommand = new();
        scan.Bind(scanCommand);
        Assert.Equal(3, scanCommand.Parameters.Count);
        Assert.False(scanCommand.Parameters.Contains("$match"));
        Assert.DoesNotContain(identity, scan.Where);
    }

    private static JsonObject Wire() => JsonNode.Parse(ArchiveSearchSpecJson.Serialize(new ArchiveSearchSpec()))!.AsObject();
}
