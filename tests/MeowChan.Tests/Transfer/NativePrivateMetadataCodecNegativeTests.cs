using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Favorites;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// P02 negative wire cases. Fixtures are synthetic only; this file deliberately does not
// exercise stores, profile leases, public catalog data, builds, or RUNSTATE.
public sealed class NativePrivateMetadataCodecNegativeTests
{
    private static ResourceId Thread(string value = "1") => new("synthetic", "b", value);
    private static NativePrivateMetadataProjection Empty(NativePrivateMetadataSelection selection = default,
        ImmutableArray<ResourceId>? threads = null)
        => new(threads ?? ImmutableArray.Create(Thread()), selection, [], [], []);

    private static NativePrivateMetadataProjection WithMedia(int count = 1, string sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        => new(ImmutableArray.Create(Thread()), new(MediaFavorites: true), [],
            Enumerable.Range(0, count).Select(i => new FavoriteAssetKey(Thread(), i.ToString(), "original", sha)).ToImmutableArray(), []);

    private static NativePrivateMetadataProjection WithNotes(int count = 1, string note = "synthetic note")
        => new(ImmutableArray.Create(Thread()), new(PostBookmarksAndNotes: true), [], [],
            Enumerable.Range(0, count).Select(i => new PostBookmark(new(Thread(), i.ToString()), note)).ToImmutableArray());

    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    private static void AssertRejected(ReadOnlyMemory<byte> wire)
    {
        NativePrivateMetadataInspection inspection = NativePrivateMetadataCodec.Inspect(wire);
        Assert.False(inspection.IsValid);
        Assert.NotEqual(NativePrivateMetadataCodecError.None, inspection.Error);
        Assert.Null(inspection.Projection);
    }

    private static void AssertSerializerRejects(NativePrivateMetadataProjection projection)
        => Assert.Throws<InvalidDataException>(() => NativePrivateMetadataCodec.Serialize(projection));

    [Fact]
    public void ValidProjectionIsValidAndDefaultSelectionIsOff()
    {
        NativePrivateMetadataProjection projection = Empty();
        byte[] wire = NativePrivateMetadataCodec.Serialize(projection);
        NativePrivateMetadataInspection inspection = NativePrivateMetadataCodec.Inspect(wire);
        Assert.True(inspection.IsValid, inspection.Error.ToString());
        Assert.NotNull(inspection.Projection);
        Assert.Equal(NativePrivateMetadataCodecError.None, inspection.Error);
        Assert.Equal(default, inspection.Projection.Selection);
        Assert.Contains("\"threadFavorites\":null", Encoding.UTF8.GetString(wire));
        Assert.Contains("\"mediaFavorites\":null", Encoding.UTF8.GetString(wire));
        Assert.Contains("\"postBookmarksAndNotes\":null", Encoding.UTF8.GetString(wire));
    }

    [Fact]
    public void SelectedEmptyRemainsDistinctAndPublicCatalogFieldsDoNotCrossTheWire()
    {
        NativePrivateMetadataProjection projection = Empty(new(true, true, true));
        string json = Encoding.UTF8.GetString(NativePrivateMetadataCodec.Serialize(projection));
        Assert.Contains("\"threadFavorites\":[]", json);
        Assert.Contains("\"mediaFavorites\":[]", json);
        Assert.Contains("\"postBookmarksAndNotes\":[]", json);
        Assert.DoesNotContain("owner", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lease", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("revision", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileName", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("length", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"meowchan-private-v1\",\"threads\":[],\"threadFavorites\":null,\"mediaFavorites\":null}")]
    [InlineData("{\"schema\":\"meowchan-private-v1\",\"threads\":[],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":null,\"future\":1}")]
    [InlineData("{\"schema\":\"meowchan-private-v1\",\"threads\":[],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":null,\"threads\":[]}")]
    [InlineData("{\"schema\":\"meowchan-private-v1\",\"threads\":[],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":null,\"th\\u0072eads\":[]}")]
    [InlineData("{\"schema\":\"meowchan-private-v1\",\"threads\":[],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":null,}")]
    [InlineData("{\"schema\":\"meowchan-private-v1\",\"threads\":[],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":null} trailing")]
    [InlineData("{\"schema\":\"meowchan-private-v2\",\"threads\":[],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":null}")]
    public void StrictObjectShapeRejectsMissingUnknownDuplicateEscapedAndNoncanonicalJson(string json)
        => AssertRejected(Utf8(json));

    [Fact]
    public void RejectsBomInvalidUtf8AndUnpairedSurrogate()
    {
        AssertRejected(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8(ValidDocument().ToJsonString())).ToArray());
        AssertRejected(new byte[] { 0xC3, 0x28 });
        AssertRejected(Utf8("{\"schema\":\"meowchan-private-v1\",\"threads\":[{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"\\uD800\"}],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":null}"));
    }

    [Theory]
    [InlineData("null", "null", "null", "null")]
    [InlineData("1", "null", "null", "null")]
    [InlineData("[]", "{}", "null", "null")]
    [InlineData("[]", "null", "{}", "null")]
    [InlineData("[]", "null", "null", "{}")]
    public void RejectsWrongTypes(string threads, string threadFavorites, string mediaFavorites, string notes)
        => AssertRejected(Utf8($"{{\"schema\":\"meowchan-private-v1\",\"threads\":{threads},\"threadFavorites\":{threadFavorites},\"mediaFavorites\":{mediaFavorites},\"postBookmarksAndNotes\":{notes}}}"));

    [Fact]
    public void RejectsCrCrLfControlsAndUnnormalizedNotes()
    {
        string prefix = "{\"schema\":\"meowchan-private-v1\",\"threads\":[{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"1\"}],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":[";
        AssertRejected(Utf8(prefix + "{\"thread\":{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"1\"},\"post\":\"1\",\"note\":\"a\\rb\"}]}"));
        AssertRejected(Utf8(prefix + "{\"thread\":{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"1\"},\"post\":\"1\",\"note\":\"a\\r\\nb\"}]}"));
        AssertRejected(Utf8(prefix + "{\"thread\":{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"1\"},\"post\":\"1\",\"note\":\"a\\u0000b\"}]}"));
    }

    [Fact]
    public void RejectsOutOfScopeAndDuplicateIdentitiesIncludingShaKeys()
    {
        string scope = "{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"1\"}";
        string foreign = "{\"source\":\"synthetic\",\"board\":\"b\",\"thread\":\"2\"}";
        AssertRejected(Utf8($"{{\"schema\":\"meowchan-private-v1\",\"threads\":[{scope}],\"threadFavorites\":[{foreign}],\"mediaFavorites\":null,\"postBookmarksAndNotes\":null}}"));
        AssertRejected(Utf8($"{{\"schema\":\"meowchan-private-v1\",\"threads\":[{scope},{scope}],\"threadFavorites\":null,\"mediaFavorites\":null,\"postBookmarksAndNotes\":null}}"));
        AssertSerializerRejects(new(ImmutableArray.Create(Thread()), new(ThreadFavorites: true), ImmutableArray.Create(Thread(), Thread()), [], []));
        AssertSerializerRejects(new(ImmutableArray.Create(Thread()), new(MediaFavorites: true), [],
            ImmutableArray.Create(new FavoriteAssetKey(Thread(), "1", "original", "a".PadRight(64, 'a')), new FavoriteAssetKey(Thread(), "1", "original", "a".PadRight(64, 'a'))), []));
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("not-sha256")]
    public void RejectsNonCanonicalSha256(string sha)
        => AssertSerializerRejects(WithMedia(sha: sha));

    [Fact]
    public void RejectsIdentifierNoteAndEntryBudgets()
    {
        AssertSerializerRejects(Empty(threads: ImmutableArray.Create(new ResourceId("synthetic", "b", new string('x', 257)))));
        AssertSerializerRejects(WithNotes(note: new string('x', 8_001)));
        AssertSerializerRejects(WithNotes(note: "x\ud800"));
        AssertSerializerRejects(WithNotes(note: "x\u0000"));
        AssertSerializerRejects(WithNotes(note: "x\r\ny"));
        AssertSerializerRejects(new(Enumerable.Range(0, 1_001).Select(i => Thread(i.ToString())).ToImmutableArray(), default, [], [], []));
        AssertSerializerRejects(WithMedia(50_001));
        AssertSerializerRejects(WithNotes(10_001));
    }

    [Fact]
    public void PreCancelledTokenIsObservedByBothApis()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => NativePrivateMetadataCodec.Serialize(Empty(), cts.Token));
        Assert.Throws<OperationCanceledException>(() => NativePrivateMetadataCodec.Inspect(Utf8("{}"), cts.Token));
    }

    private static JsonObject ValidDocument() => JsonNode.Parse("""
        {"schema":"meowchan-private-v1",
         "threads":[{"source":"synthetic","board":"b","thread":"1"}],
         "threadFavorites":[{"source":"synthetic","board":"b","thread":"1"}],
         "mediaFavorites":[{"thread":{"source":"synthetic","board":"b","thread":"1"},"mediaId":"m","variant":"original","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}],
         "postBookmarksAndNotes":[{"thread":{"source":"synthetic","board":"b","thread":"1"},"post":"p","note":"synthetic"}]}
        """)!.AsObject();

    private static JsonObject ObjectAt(JsonObject document, string location) => location switch
    {
        "root" => document,
        "scope" => document["threads"]![0]!.AsObject(),
        "favorite" => document["threadFavorites"]![0]!.AsObject(),
        "media" => document["mediaFavorites"]![0]!.AsObject(),
        "mediaThread" => document["mediaFavorites"]![0]!["thread"]!.AsObject(),
        "bookmark" => document["postBookmarksAndNotes"]![0]!.AsObject(),
        "bookmarkThread" => document["postBookmarksAndNotes"]![0]!["thread"]!.AsObject(),
        _ => throw new ArgumentOutOfRangeException(nameof(location))
    };

    public static IEnumerable<object[]> ObjectLocations()
    {
        foreach (string location in new[] { "root", "scope", "favorite", "media", "mediaThread", "bookmark", "bookmarkThread" })
            yield return new object[] { location };
    }

    [Theory]
    [MemberData(nameof(ObjectLocations))]
    public void EveryObjectRejectsMissingUnknownAndWrongTypedFields(string location)
    {
        string[] fields = ObjectAt(ValidDocument(), location).Select(pair => pair.Key).ToArray();
        foreach (string field in fields)
        {
            JsonObject missing = ValidDocument();
            ObjectAt(missing, location).Remove(field);
            AssertRejected(Utf8(missing.ToJsonString()));

            foreach (string wrongValue in new[] { "true", "42", "{}", "\"wrong-type\"" })
            {
                // Strings are valid types for scalar fields; test only incompatible types.
                if (wrongValue == "\"wrong-type\"" && ObjectAt(ValidDocument(), location)[field] is JsonValue)
                    continue;
                JsonObject wrong = ValidDocument();
                ObjectAt(wrong, location)[field] = JsonNode.Parse(wrongValue);
                AssertRejected(Utf8(wrong.ToJsonString()));
            }
        }
        JsonObject unknown = ValidDocument();
        ObjectAt(unknown, location)["syntheticUnknown"] = 1;
        AssertRejected(Utf8(unknown.ToJsonString()));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("lease")]
    [InlineData("revision")]
    [InlineData("fingerprint")]
    [InlineData("profilePath")]
    [InlineData("grants")]
    [InlineData("tags")]
    [InlineData("posts")]
    public void LocalAuthorityAndPublicCatalogFieldsAreNotPrivateExtensions(string field)
    {
        JsonObject document = ValidDocument();
        document[field] = "synthetic";
        AssertRejected(Utf8(document.ToJsonString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\tidentity")]
    [InlineData("bad\nidentity")]
    [InlineData("bad\u007fidentity")]
    public void InvalidScalarIdentitiesAreRejectedOnBothBoundaries(string value)
    {
        foreach (string field in new[] { "source", "board", "thread" })
        {
            JsonObject document = ValidDocument();
            document["threadFavorites"] = null;
            document["mediaFavorites"] = null;
            document["postBookmarksAndNotes"] = null;
            document["threads"]![0]![field] = value;
            AssertRejected(Utf8(document.ToJsonString()));
        }
        foreach (string field in new[] { "mediaId", "variant" })
        {
            JsonObject document = ValidDocument();
            document["mediaFavorites"]![0]![field] = value;
            AssertRejected(Utf8(document.ToJsonString()));
        }
        AssertSerializerRejects(new(ImmutableArray.Create(new ResourceId(value, "b", "1")), default, [], [], []));
        AssertSerializerRejects(new(ImmutableArray.Create(Thread()), new(PostBookmarksAndNotes: true), [], [],
            ImmutableArray.Create(new PostBookmark(new(Thread(), value), ""))));
    }

    [Fact]
    public void ScopeMembershipIsExactOrdinalForEveryCategory()
    {
        foreach (string category in new[] { "threadFavorites", "mediaFavorites", "postBookmarksAndNotes" })
        foreach (string component in new[] { "source", "board", "thread" })
        {
            JsonObject document = ValidDocument();
            JsonObject identity = (category == "threadFavorites" ? document[category]![0]! : document[category]![0]!["thread"]!).AsObject();
            identity[component] = component == "thread" ? "01" : identity[component]!.GetValue<string>().ToUpperInvariant();
            AssertRejected(Utf8(document.ToJsonString()));
        }
    }

    [Fact]
    public void DuplicateFavoriteAndBookmarkKeysRejectEvenWhenPayloadDiffers()
    {
        foreach (string category in new[] { "threadFavorites", "mediaFavorites", "postBookmarksAndNotes" })
        {
            JsonObject document = ValidDocument();
            JsonArray entries = document[category]!.AsArray();
            JsonObject duplicate = entries[0]!.DeepClone().AsObject();
            if (category == "postBookmarksAndNotes") duplicate["note"] = "different synthetic note";
            entries.Add(duplicate);
            AssertRejected(Utf8(document.ToJsonString()));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Sha256IsStrictOnReadAsWellAsWrite(string sha)
    {
        JsonObject document = ValidDocument();
        document["mediaFavorites"]![0]!["sha256"] = sha;
        AssertRejected(Utf8(document.ToJsonString()));
        AssertSerializerRejects(WithMedia(sha: sha));
    }

    [Fact]
    public void ByteBudgetAndJsonSyntaxAreStrict()
    {
        AssertRejected(new byte[16 * 1024 * 1024 + 1]);
        string valid = ValidDocument().ToJsonString();
        AssertRejected(Utf8("/* synthetic */" + valid));
        AssertRejected(Utf8(valid + "{}"));
        AssertRejected(Utf8(valid[..^1] + ",}"));
        AssertRejected(Utf8(new string('[', 13) + new string(']', 13)));
        AssertRejected(ReadOnlyMemory<byte>.Empty);
        AssertRejected(Utf8("null"));
        AssertRejected(Utf8("[]"));
    }

    [Fact]
    public void UnicodeScalarNoteLimitAllowsLfTabsAndPairsButNeverClips()
    {
        string maximum = string.Concat(Enumerable.Repeat("\U0001F63A", 8_000));
        NativePrivateMetadataInspection valid = NativePrivateMetadataCodec.Inspect(NativePrivateMetadataCodec.Serialize(WithNotes(note: maximum)));
        Assert.True(valid.IsValid);
        Assert.Equal(NativePrivateMetadataCodecError.None, valid.Error);
        Assert.Equal(maximum, Assert.Single(valid.Projection!.PostBookmarksAndNotes).Note);
        AssertSerializerRejects(WithNotes(note: maximum + "x"));
        AssertSerializerRejects(WithNotes(note: "\udc00"));
        AssertSerializerRejects(WithNotes(note: "\u0085"));
        byte[] canonical = NativePrivateMetadataCodec.Serialize(WithNotes(note: "a\t\nb"));
        Assert.True(NativePrivateMetadataCodec.Inspect(canonical).IsValid);
        JsonObject document = ValidDocument();
        document["postBookmarksAndNotes"]![0]!["note"] = new string('x', 8_001);
        AssertRejected(Utf8(document.ToJsonString()));
    }

    [Theory]
    [InlineData("threads", 1_001)]
    [InlineData("mediaFavorites", 50_001)]
    [InlineData("postBookmarksAndNotes", 10_001)]
    public void ParserRejectsEntryCapsWithOtherwiseValidUniqueIdentities(string category, int count)
    {
        JsonObject document = ValidDocument();
        JsonArray entries = new();
        for (int i = 0; i < count; i++)
        {
            JsonObject entry = document[category]![0]!.DeepClone().AsObject();
            entry[category is "threads" or "threadFavorites" ? "thread" : category == "mediaFavorites" ? "mediaId" : "post"] = i.ToString();
            entries.Add(entry);
        }
        document[category] = entries;
        AssertRejected(Utf8(document.ToJsonString()));
    }

    [Fact]
    public void WriterOutputCapRejectsLargeEncodedDocument()
    {
        string longId = new('x', 250);
        NativePrivateMetadataProjection projection = new(
            ImmutableArray.Create(Thread()), new(MediaFavorites: true), [],
            Enumerable.Range(0, 50_000).Select(i => new FavoriteAssetKey(Thread(), longId + i, "original", new string('a', 64))).ToImmutableArray(), []);
        // Every identity and count is individually valid; only encoded output exceeds 16 MiB.
        AssertSerializerRejects(projection);
    }
}

