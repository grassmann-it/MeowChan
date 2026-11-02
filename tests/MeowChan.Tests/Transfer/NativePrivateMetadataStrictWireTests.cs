using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Favorites;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Synthetic wire fixtures only. These tests do not establish historical public-byte
// compatibility, package admission, consent to apply, or publication authority.
public sealed class NativePrivateMetadataStrictWireTests
{
    private static readonly ResourceId Thread = new("synthetic", "b", "1");
    private static readonly ResourceId Other = new("synthetic", "b", "2");
    private static readonly string[] ObjectPointers =
    [
        "", "/threads/0", "/threadFavorites/0", "/mediaFavorites/0",
        "/mediaFavorites/0/thread", "/postBookmarksAndNotes/0", "/postBookmarksAndNotes/0/thread"
    ];
    private const string Sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Note = "synthetic 雪 🐈\n\t<inert & \"quoted\">";
    private const string ObjectSlot = "__STRICT_WIRE_EXACT_OBJECT_SLOT__";
    private const string TextSlot = "__STRICT_WIRE_EXACT_TEXT_SLOT__";

    private static JsonObject Document() => JsonNode.Parse("""
        {"schema":"meowchan-private-v1",
         "threads":[{"source":"synthetic","board":"b","thread":"1"}],
         "threadFavorites":[{"source":"synthetic","board":"b","thread":"1"}],
         "mediaFavorites":[{"thread":{"source":"synthetic","board":"b","thread":"1"},"mediaId":"m","variant":"original","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],
         "postBookmarksAndNotes":[{"thread":{"source":"synthetic","board":"b","thread":"1"},"post":"p","note":"synthetic"}]}
        """)!.AsObject();

    public static IEnumerable<object[]> ObjectFields()
    {
        foreach (string pointer in ObjectPointers)
        foreach (var field in NodeAt(Document(), pointer).AsObject())
            yield return [pointer, field.Key];
    }

    [Theory]
    [MemberData(nameof(ObjectFields))]
    public void DuplicateLiteralAndEscapedFieldsRejectAtTheExactObjectPointer(string pointer, string field)
    {
        JsonObject baseline = Document();
        AssertValid(Utf8(baseline.ToJsonString()));
        JsonObject target = NodeAt(baseline, pointer).AsObject();
        string value = target[field]!.ToJsonString();
        foreach (string name in new[] { JsonSerializer.Serialize(field), EscapedName(field) })
        {
            string duplicate = target.ToJsonString()[..^1] + "," + name + ":" + value + "}";
            string wire = WithRawNodeAt(baseline, pointer, duplicate);

            // JsonDocument retains duplicate properties. Verify the fixture itself:
            // exactly this object's property count changes, even though all four
            // thread identities deliberately have byte-identical representations.
            using JsonDocument parsedFixture = JsonDocument.Parse(wire);
            foreach (string candidate in ObjectPointers)
            {
                string[] expected = NodeAt(baseline, candidate).AsObject().Select(pair => pair.Key).ToArray();
                if (candidate == pointer) expected = [.. expected, field];
                Assert.Equal(expected, ElementAt(parsedFixture.RootElement, candidate)
                    .EnumerateObject().Select(property => property.Name).ToArray());
            }
            JsonElement duplicated = ElementAt(parsedFixture.RootElement, pointer);
            Assert.Equal(2, duplicated.EnumerateObject().Count(property => property.Name == field));
            Assert.All(duplicated.EnumerateObject().Where(property => property.Name == field),
                property => Assert.Equal(value, property.Value.GetRawText()));
            AssertRejected(Utf8(wire), NativePrivateMetadataCodecError.InvalidField);
        }
    }

    [Theory]
    [MemberData(nameof(ObjectFields))]
    public void SingleKnownEscapedNameIsAcceptedAtEveryExactObjectPointer(string pointer, string field)
    {
        JsonObject baseline = Document();
        NativePrivateMetadataProjection expected = AssertValid(Utf8(baseline.ToJsonString()));
        JsonObject target = NodeAt(baseline, pointer).AsObject();
        string replacement = "{" + string.Join(",", target.Select(pair =>
            (pair.Key == field ? EscapedName(pair.Key) : JsonSerializer.Serialize(pair.Key))
            + ":" + pair.Value!.ToJsonString())) + "}";
        string wire = WithRawNodeAt(baseline, pointer, replacement);
        Assert.Contains(EscapedName(field) + ":", wire);
        using JsonDocument parsedFixture = JsonDocument.Parse(wire);
        Assert.Equal(1, ElementAt(parsedFixture.RootElement, pointer)
            .EnumerateObject().Count(property => property.Name == field));
        AssertValues(expected, AssertValid(Utf8(wire)));
    }

    [Fact]
    public void BomIsRejectedWhenTheSameDocumentWithoutBomIsValid()
    {
        byte[] valid = Utf8(Document().ToJsonString());
        AssertValid(valid);
        byte[] withBom = [(byte)0xef, (byte)0xbb, (byte)0xbf, .. valid];
        Assert.Equal(valid, withBom.AsSpan(3).ToArray());
        AssertRejected(withBom, NativePrivateMetadataCodecError.InvalidUtf8);
    }

    [Theory]
    [InlineData("FF")]
    [InlineData("C328")]
    [InlineData("EDA080")]
    [InlineData("F09F")]
    public void InvalidUtf8InsideANoteRejectsButItsLiteralReplacementCharactersAreAccepted(string hex)
    {
        JsonObject document = Document();
        document["postBookmarksAndNotes"]![0]!["note"] = "before" + TextSlot + "after";
        string[] pieces = SplitOne(document.ToJsonString(), TextSlot);
        byte[] invalidText = Convert.FromHexString(hex);
        Assert.Throws<DecoderFallbackException>(() => new UTF8Encoding(false, true).GetString(invalidText));
        string replacement = Encoding.UTF8.GetString(invalidText);
        Assert.Contains("\ufffd", replacement);

        byte[] valid = [.. Utf8(pieces[0]), .. Utf8(replacement), .. Utf8(pieces[1])];
        NativePrivateMetadataProjection accepted = AssertValid(valid);
        Assert.Equal("before" + replacement + "after", Assert.Single(accepted.PostBookmarksAndNotes).Note);
        byte[] invalid = [.. Utf8(pieces[0]), .. invalidText, .. Utf8(pieces[1])];
        // Lenient decoding changes only the invalid string bytes into the accepted
        // control document, so JSON shape/syntax alone cannot explain rejection.
        Assert.Equal(Encoding.UTF8.GetString(valid), Encoding.UTF8.GetString(invalid));
        AssertRejected(invalid, NativePrivateMetadataCodecError.InvalidUtf8);
    }

    public static IEnumerable<object[]> InvalidScopeIdentifiers()
    {
        foreach (string field in new[] { "source", "board", "thread" })
        foreach (string rawValue in new[]
        {
            "\"\"", "\" \"", "\"bad\\tidentity\"", "\"bad\\nidentity\"",
            "\"bad\\u0000identity\"", "\"bad\\u007fidentity\"",
            "\"bad\\u0085identity\"", "\"\\uD800\"", "\"\\uDC00\""
        })
            yield return [field, rawValue];
    }

    [Theory]
    [MemberData(nameof(InvalidScopeIdentifiers))]
    public void InvalidScopeIdentifierRejectsWithoutAnyCategoryMembershipConfound(string field, string rawValue)
    {
        JsonObject baseline = PublicOnlyDocument();
        NativePrivateMetadataProjection valid = AssertValid(Utf8(baseline.ToJsonString()));
        Assert.Equal(default(NativePrivateMetadataSelection), valid.Selection);
        string wire = WithRawNodeAt(baseline, "/threads/0/" + field, rawValue);
        using JsonDocument fixture = JsonDocument.Parse(wire);
        Assert.Equal(rawValue, fixture.RootElement.GetProperty("threads")[0].GetProperty(field).GetRawText());
        foreach (string category in CategoryNames)
            Assert.Equal(JsonValueKind.Null, fixture.RootElement.GetProperty(category).ValueKind);
        AssertRejected(Utf8(wire));
    }

    public static IEnumerable<object[]> SelectionAndOccupancy()
    {
        for (int selected = 0; selected < 8; selected++)
        for (int occupied = 0; occupied < 8; occupied++)
            if ((occupied & ~selected) == 0)
                yield return [selected, occupied];
    }

    [Theory]
    [MemberData(nameof(SelectionAndOccupancy))]
    public void MixedOccupancySelectionPreservesScopeSourceAndIndependentPublicContext(int incomingBits, int occupiedBits)
    {
        NativeTransferCatalog publicContext = PublicContext();
        byte[] publicBefore = NativeTransferCatalogSerializer.Serialize(publicContext);
        NativePrivateMetadataProjection original = Projection(incomingBits, occupiedBits);
        byte[] originalWire = NativePrivateMetadataCodec.Serialize(original);
        NativePrivateMetadataProjection incoming = AssertValid(originalWire);
        AssertValues(original, incoming);
        AssertCategoryPresence(originalWire, incomingBits, occupiedBits);
        AssertValues(Projection(0, 0), incoming.Select());
        Assert.Equal(publicBefore, NativeTransferCatalogSerializer.Serialize(publicContext));

        for (int requestedBits = 0; requestedBits < 8; requestedBits++)
        {
            if ((requestedBits & ~incomingBits) != 0)
            {
                Assert.Throws<InvalidDataException>(() => incoming.Select(Selection(requestedBits)));
            }
            else
            {
                NativePrivateMetadataProjection filtered = incoming.Select(Selection(requestedBits));
                Assert.NotSame(incoming, filtered);
                AssertValues(Projection(requestedBits, occupiedBits & requestedBits), filtered);
                byte[] filteredWire = NativePrivateMetadataCodec.Serialize(filtered);
                AssertCategoryPresence(filteredWire, requestedBits, occupiedBits & requestedBits);
                AssertValues(filtered, AssertValid(filteredWire));
                AssertValues(Projection(0, 0), filtered.Select());
            }
            AssertValues(original, incoming);
            Assert.Equal(originalWire, NativePrivateMetadataCodec.Serialize(incoming));
            // Same-runtime noninterference only, not historical byte compatibility.
            Assert.Equal(publicBefore, NativeTransferCatalogSerializer.Serialize(publicContext));
        }
    }

    [Theory]
    [MemberData(nameof(SelectionAndOccupancy))]
    public void PrivateSerializationRepeatsAndRoundtripsByteForByteForTheSameOrderedValues(int selected, int occupied)
    {
        NativePrivateMetadataProjection projection = Projection(selected, occupied);
        byte[] expected = NativePrivateMetadataCodec.Serialize(projection);
        for (int repeat = 0; repeat < 3; repeat++)
        {
            Assert.Equal(expected, NativePrivateMetadataCodec.Serialize(projection));
            NativePrivateMetadataProjection parsed = AssertValid(expected);
            AssertValues(projection, parsed);
            Assert.Equal(expected, NativePrivateMetadataCodec.Serialize(parsed));
            Assert.Equal(expected, NativePrivateMetadataCodec.Serialize(Projection(selected, occupied)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParserAcceptsExactly8000AstralOrMixedScalarsAndRejects8001WithoutClipping(bool mixed)
    {
        string maximum = mixed
            ? string.Concat(Enumerable.Repeat("a\U0001F408", 4_000))
            : string.Concat(Enumerable.Repeat("\U0001F408", 8_000));
        Assert.Equal(mixed ? 12_000 : 16_000, maximum.Length);
        JsonObject baseline = PublicOnlyDocument();
        baseline["postBookmarksAndNotes"] = Document()["postBookmarksAndNotes"]!.DeepClone();
        baseline["postBookmarksAndNotes"]![0]!["note"] = maximum;
        // Independently authored JSON, not output from the codec under test.
        NativePrivateMetadataProjection accepted = AssertValid(Utf8(baseline.ToJsonString()));
        Assert.Equal(maximum, Assert.Single(accepted.PostBookmarksAndNotes).Note);
        foreach (string oneScalar in new[] { "x", "\U0001F408" })
        {
            baseline["postBookmarksAndNotes"]![0]!["note"] = maximum + oneScalar;
            AssertRejected(Utf8(baseline.ToJsonString()));
        }
    }

    public static IEnumerable<object[]> IdentifierBoundaries()
    {
        foreach (string field in new[] { "source", "board", "thread", "mediaId", "variant", "post" })
        foreach (string alphabet in new[] { "bmp", "astral", "mixed" })
            yield return [field, alphabet];
    }

    [Theory]
    [MemberData(nameof(IdentifierBoundaries))]
    public void ParserUses256Utf16UnitsForIdentifiersNot256UnicodeScalars(string field, string alphabet)
    {
        string maximum = alphabet switch
        {
            "bmp" => new string('x', 256),
            "astral" => string.Concat(Enumerable.Repeat("\U0001F408", 128)),
            "mixed" => string.Concat(Enumerable.Repeat("a\U0001F408", 85)) + "b",
            _ => throw new ArgumentOutOfRangeException(nameof(alphabet))
        };
        Assert.Equal(256, maximum.Length);
        NativePrivateMetadataProjection accepted = AssertValid(Utf8(IdentifierDocument(field, maximum).ToJsonString()));
        string? retained = field switch
        {
            "source" => Assert.Single(accepted.Threads).Source,
            "board" => Assert.Single(accepted.Threads).Board,
            "thread" => Assert.Single(accepted.Threads).Thread,
            "mediaId" => Assert.Single(accepted.MediaFavorites).MediaIdentifier,
            "variant" => Assert.Single(accepted.MediaFavorites).Variant,
            "post" => Assert.Single(accepted.PostBookmarksAndNotes).Key.Post,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Equal(maximum, retained);
        string over = maximum + "x";
        Assert.Equal(257, over.Length);
        AssertRejected(Utf8(IdentifierDocument(field, over).ToJsonString()));
    }

    private static readonly string[] CategoryNames = ["threadFavorites", "mediaFavorites", "postBookmarksAndNotes"];

    private static JsonObject PublicOnlyDocument()
    {
        JsonObject document = Document();
        foreach (string category in CategoryNames) document[category] = null;
        return document;
    }

    private static JsonObject IdentifierDocument(string field, string value)
    {
        JsonObject document = PublicOnlyDocument();
        if (field is "source" or "board" or "thread")
            document["threads"]![0]![field] = value;
        else
        {
            string category = field == "post" ? "postBookmarksAndNotes" : "mediaFavorites";
            document[category] = Document()[category]!.DeepClone();
            document[category]![0]![field] = value;
        }
        return document;
    }

    private static NativePrivateMetadataSelection Selection(int bits) =>
        new((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);

    private static NativePrivateMetadataProjection Projection(int selected, int occupied) => new(
        [Thread, Other], Selection(selected), (occupied & 1) != 0 ? [Thread] : [],
        (occupied & 2) != 0 ? [new FavoriteAssetKey(Thread, "media-雪-🐈", "original", Sha256)] : [],
        (occupied & 4) != 0 ? [new PostBookmark(new(Thread, "post-🐈"), Note)] : []);

    private static NativeTransferCatalog PublicContext()
    {
        DateTimeOffset at = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        return new(at, [new TransferThread(Thread, null, "Synthetic public context",
            ThreadSourceState.NotFound, at, at, 0, true, [])]);
    }

    private static void AssertCategoryPresence(byte[] bytes, int selected, int occupied)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        for (int category = 0; category < CategoryNames.Length; category++)
        {
            JsonElement value = document.RootElement.GetProperty(CategoryNames[category]);
            Assert.Equal((selected & (1 << category)) == 0 ? JsonValueKind.Null : JsonValueKind.Array, value.ValueKind);
            if (value.ValueKind == JsonValueKind.Array)
                Assert.Equal((occupied & (1 << category)) == 0 ? 0 : 1, value.GetArrayLength());
        }
    }

    private static NativePrivateMetadataProjection AssertValid(byte[] bytes)
    {
        NativePrivateMetadataInspection result = NativePrivateMetadataCodec.Inspect(bytes);
        Assert.True(result.IsValid, result.Error.ToString());
        Assert.Equal(NativePrivateMetadataCodecError.None, result.Error);
        return Assert.IsType<NativePrivateMetadataProjection>(result.Projection);
    }

    private static void AssertRejected(byte[] bytes, NativePrivateMetadataCodecError? expected = null)
    {
        NativePrivateMetadataInspection result = NativePrivateMetadataCodec.Inspect(bytes);
        Assert.False(result.IsValid);
        Assert.Null(result.Projection);
        Assert.NotEqual(NativePrivateMetadataCodecError.None, result.Error);
        if (expected.HasValue) Assert.Equal(expected.Value, result.Error);
    }

    private static void AssertValues(NativePrivateMetadataProjection expected, NativePrivateMetadataProjection actual)
    {
        Assert.Equal(expected.Selection, actual.Selection);
        Assert.Equal(expected.Threads.ToArray(), actual.Threads.ToArray());
        Assert.Equal(expected.ThreadFavorites.ToArray(), actual.ThreadFavorites.ToArray());
        Assert.Equal(expected.MediaFavorites.ToArray(), actual.MediaFavorites.ToArray());
        Assert.Equal(expected.PostBookmarksAndNotes.ToArray(), actual.PostBookmarksAndNotes.ToArray());
    }

    // Only fixed fixture pointers are supported; no general JSON Pointer escaping is needed.
    private static JsonNode NodeAt(JsonNode root, string pointer)
    {
        JsonNode current = root;
        foreach (string segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            current = current is JsonArray array
                ? array[int.Parse(segment, CultureInfo.InvariantCulture)]!
                : ((JsonObject)current)[segment]!;
        return current;
    }

    private static JsonElement ElementAt(JsonElement root, string pointer)
    {
        JsonElement current = root;
        foreach (string segment in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            current = current.ValueKind == JsonValueKind.Array
                ? current[int.Parse(segment, CultureInfo.InvariantCulture)] : current.GetProperty(segment);
        return current;
    }

    private static string WithRawNodeAt(JsonObject baseline, string pointer, string raw)
    {
        string template;
        if (pointer.Length == 0) template = JsonSerializer.Serialize(ObjectSlot);
        else
        {
            JsonObject copy = baseline.DeepClone().AsObject();
            int separator = pointer.LastIndexOf('/');
            JsonNode parent = NodeAt(copy, pointer[..separator]);
            string leaf = pointer[(separator + 1)..];
            if (parent is JsonArray array) array[int.Parse(leaf, CultureInfo.InvariantCulture)] = ObjectSlot;
            else ((JsonObject)parent)[leaf] = ObjectSlot;
            template = copy.ToJsonString();
        }
        string[] pieces = SplitOne(template, JsonSerializer.Serialize(ObjectSlot));
        return pieces[0] + raw + pieces[1];
    }

    private static string[] SplitOne(string template, string marker)
    {
        string[] pieces = template.Split(marker, StringSplitOptions.None);
        Assert.Equal(2, pieces.Length);
        return pieces;
    }

    private static string EscapedName(string name) => "\"\\u"
        + ((int)name[0]).ToString("x4", CultureInfo.InvariantCulture) + name[1..] + "\"";

    private static byte[] Utf8(string value) => new UTF8Encoding(false, true).GetBytes(value);
}
