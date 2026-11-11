using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MeowChan.Interfaces;

namespace MeowChan.Archive;

/// <summary>
/// The strict portable wire contract, shared by saved searches and query consumers. Schema 1 is
/// unchanged; schema 2 is schema 1 plus a mandatory own-tag filter and is written only when one exists.
/// </summary>
public static class ArchiveSearchSpecJson
{
    public static string Serialize(ArchiveSearchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Tags is { } tags)
        {
            // Same members and order as schema 1, followed by the tag filter.
            System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(Serialize(spec.WithTags(null)))!;
            node["schemaVersion"] = 2;
            node["tags"] = new System.Text.Json.Nodes.JsonObject
            {
                ["match"] = tags.Match == ArchiveTagMatch.Any ? "any" : "all",
                ["include"] = new System.Text.Json.Nodes.JsonArray(tags.Include.Select(id => (System.Text.Json.Nodes.JsonNode)id.ToString("D")).ToArray()),
                ["exclude"] = new System.Text.Json.Nodes.JsonArray(tags.Exclude.Select(id => (System.Text.Json.Nodes.JsonNode)id.ToString("D")).ToArray())
            };
            return node.ToJsonString();
        }
        return JsonSerializer.Serialize(new
        {
            schemaVersion = spec.SchemaVersion,
            text = spec.Text,
            sources = spec.Sources,
            boards = spec.Boards.Select(board => new { source = board.Source, board = board.Board }),
            sourceStates = spec.SourceStates.Select(StateName),
            captureEnabled = spec.CaptureEnabled,
            hasPosts = spec.HasPosts,
            sort = SortName(spec.Sort),
            workFilter = WorkName(spec.WorkFilter),
            dateRange = spec.DateRange == null ? null : new
            {
                basis = spec.DateRange.Basis,
                startDate = spec.DateRange.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                endDate = spec.DateRange.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                timeZoneId = spec.DateRange.TimeZoneId
            }
        });
    }

    public static ArchiveSearchSpec Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("A search definition object is required.");
        int version = root.TryGetProperty("schemaVersion", out JsonElement schema) && schema.ValueKind == JsonValueKind.Number
            && schema.TryGetInt32(out int parsed) ? parsed : 0;
        ArchiveTagFilter? tagFilter = null;
        if (version == 2)
        {
            Fields(root, "schemaVersion", "text", "sources", "boards", "sourceStates", "captureEnabled", "hasPosts", "sort", "workFilter", "dateRange", "tags");
            JsonElement tags = root.GetProperty("tags");
            Fields(tags, "match", "include", "exclude");
            try
            {
                tagFilter = new ArchiveTagFilter(TagIds(tags.GetProperty("include")), TagIds(tags.GetProperty("exclude")),
                    Text(tags.GetProperty("match")) switch
                    {
                        "all" => ArchiveTagMatch.All, "any" => ArchiveTagMatch.Any,
                        _ => throw new JsonException("Unsupported tag match.")
                    });
            }
            catch (ArgumentException error) { throw new JsonException("Invalid tag filter: " + error.Message); }
        }
        else Fields(root, "schemaVersion", "text", "sources", "boards", "sourceStates", "captureEnabled", "hasPosts", "sort", "workFilter", "dateRange");
        ArchiveDateRange? dateRange = null;
        JsonElement date = root.GetProperty("dateRange");
        if (date.ValueKind != JsonValueKind.Null)
        {
            Fields(date, "basis", "startDate", "endDate", "timeZoneId");
            dateRange = new ArchiveDateRange(Day(date.GetProperty("startDate")), Day(date.GetProperty("endDate")),
                Text(date.GetProperty("timeZoneId")), Text(date.GetProperty("basis")));
        }
        return ArchiveSearchSpec.FromExactIdentities(Text(root.GetProperty("text")),
            root.GetProperty("sources").EnumerateArray().Select(Text),
            root.GetProperty("boards").EnumerateArray().Select(board =>
            {
                Fields(board, "source", "board");
                return new ArchiveBoardConstraint(NullableText(board.GetProperty("source")), Text(board.GetProperty("board")));
            }),
            root.GetProperty("sourceStates").EnumerateArray().Select(value => Text(value) switch
            {
                "active" => ThreadSourceState.Active, "archived" => ThreadSourceState.Archived,
                "not-found" => ThreadSourceState.NotFound, _ => throw new JsonException("Unsupported source state.")
            }), NullableBool(root.GetProperty("captureEnabled")), NullableBool(root.GetProperty("hasPosts")),
            Text(root.GetProperty("sort")) switch
            {
                "latest-capture" => ArchiveThreadSort.LatestCapture, "oldest-first" => ArchiveThreadSort.OldestFirst,
                "title" => ArchiveThreadSort.Title, _ => throw new JsonException("Unsupported sort.")
            }, Text(root.GetProperty("workFilter")) switch
            {
                "all" => ArchiveWorkFilter.All, "watching" => ArchiveWorkFilter.Watching, "followup" => ArchiveWorkFilter.Followup,
                "intervention" => ArchiveWorkFilter.Intervention, "completed" => ArchiveWorkFilter.Completed,
                "stopped" => ArchiveWorkFilter.Stopped, _ => throw new JsonException("Unsupported work filter.")
            }, dateRange, version == 2 ? 1 : root.GetProperty("schemaVersion").GetInt32()).WithTags(tagFilter);
    }

    private static Guid[] TagIds(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array) throw new JsonException("A tag list is required.");
        return array.EnumerateArray().Select(item =>
        {
            string text = Text(item);
            // Exactly the lowercase hyphenated form this contract writes.
            if (!Guid.TryParseExact(text, "D", out Guid id) || !string.Equals(id.ToString("D"), text, StringComparison.Ordinal))
                throw new JsonException("A tag reference must be a canonical identifier.");
            return id;
        }).ToArray();
    }

    private static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("A search definition object is required.");
        HashSet<string> remaining = new(names, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new JsonException("Unknown or duplicate search field: " + property.Name);
        if (remaining.Count != 0) throw new JsonException("Missing search field: " + remaining.First());
    }
    private static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new JsonException("A string is required.");
    private static string? NullableText(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Text(value);
    private static bool? NullableBool(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : value.GetBoolean();
    private static DateOnly? Day(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (!DateOnly.TryParseExact(Text(value), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
            throw new JsonException("Calendar dates must use YYYY-MM-DD.");
        return day;
    }
    private static string StateName(ThreadSourceState state) => state switch
    {
        ThreadSourceState.Active => "active", ThreadSourceState.Archived => "archived", ThreadSourceState.NotFound => "not-found",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
    private static string SortName(ArchiveThreadSort sort) => sort switch
    {
        ArchiveThreadSort.LatestCapture => "latest-capture", ArchiveThreadSort.OldestFirst => "oldest-first", ArchiveThreadSort.Title => "title",
        _ => throw new ArgumentOutOfRangeException(nameof(sort))
    };
    private static string WorkName(ArchiveWorkFilter work) => work switch
    {
        ArchiveWorkFilter.All => "all", ArchiveWorkFilter.Watching => "watching", ArchiveWorkFilter.Followup => "followup",
        ArchiveWorkFilter.Intervention => "intervention", ArchiveWorkFilter.Completed => "completed", ArchiveWorkFilter.Stopped => "stopped",
        _ => throw new ArgumentOutOfRangeException(nameof(work))
    };
}
