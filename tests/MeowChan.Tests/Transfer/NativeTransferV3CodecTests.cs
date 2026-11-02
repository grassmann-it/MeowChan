using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Storage;
using MeowChan.Testing;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

// Contract: passive-media-representation-v1.md. These fixtures contain only synthetic bytes;
// a validated package/stage is deliberately not treated as store admission or source authority.
public sealed class NativeTransferV3CodecTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid PackageId = Guid.Parse("993cd622-52bb-4f64-8277-3806412b26af");
    private static readonly ResourceId Id = new("4chan", "b", "7001");
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("synthetic v3 passive original and preview bytes");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    [Theory]
    [InlineData("original", "none")] [InlineData("original", "size")]
    [InlineData("original", "md5")] [InlineData("original", "sha1")]
    [InlineData("original", "sha256")] [InlineData("original", "all")]
    [InlineData("preview", "none")] [InlineData("preview", "size")]
    [InlineData("preview", "md5")] [InlineData("preview", "sha1")]
    [InlineData("preview", "sha256")] [InlineData("preview", "all")]
    [InlineData("retained", "none")] [InlineData("retained", "size")]
    [InlineData("retained", "md5")] [InlineData("retained", "sha1")]
    [InlineData("retained", "sha256")] [InlineData("retained", "all")]
    public async Task KeyedContainedWithoutHistoryChoosesV3AndStagesMeasuredBytes(string shape, string mismatch)
    {
        NativeTransferCatalog input = Catalog(shape, mismatch);
        byte[] package = await WritePackage(input);
        TransferPackageInspection inspected = await Inspect(package);
        Assert.True(inspected.IsPackageValid, inspected.ToString());
        Assert.Equal("chan-v3", inspected.Package!.Envelope.ContentSchema);
        Assert.Equal("chan-v3", NativeTransferCatalogSerializer.ContentSchema(input));
        Assert.All(CatalogJson(package)["threads"]!.AsArray(), t =>
            Assert.IsType<JsonArray>(t!["retainedBindings"]));
        Assert.Equal(new[] { "chan-v3" }, ManifestJson(package)["requiredFeatures"]!.AsArray()
            .Select(n => n!.GetValue<string>()));
        TransferAttachment incoming = Selected(inspected.Package.Catalog, shape);
        Assert.Equal(Selected(input, shape), incoming);
        Assert.Null(incoming.History);
        Assert.Equal(Hash(Bytes), incoming.PayloadSha256);
        Assert.Equal(TransferContentStatus.Contained, incoming.Status);
        Assert.NotNull(incoming.Metadata.AttachmentRevisionKey);
        AssertMeasured(Assert.Single(inspected.Package.Envelope.MeasuredPayloads).Value);
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(input),
            NativeTransferCatalogSerializer.Serialize(inspected.Package.Catalog));
        await AssertStage(package, async stage =>
        {
            Assert.Equal("chan-v3", stage.Package.Envelope.ContentSchema);
            Assert.Equal(incoming, Selected(stage.Package.Catalog, shape));
            using MemoryStream copy = new();
            AssertMeasured(await stage.CopyPayloadToAsync(Hash(Bytes), copy));
            Assert.Equal(Bytes, copy.ToArray());
            if (shape == "retained") Assert.Empty(stage.Package.Catalog.Threads[0].Posts);
        });
    }

    [Fact]
    public async Task SharedBytesKeepDistinctOriginalPreviewAndRetainedKeysWithoutInventingPostsOrHistory()
    {
        NativeTransferCatalog input = Catalog();
        byte[] package = await WritePackage(input);
        await AssertStage(package, async stage =>
        {
            TransferThread thread = stage.Package.Catalog.Threads[0];
            Assert.Equal("15", Assert.Single(thread.Posts).Metadata.PostId);
            Assert.Equal("999", Assert.Single(thread.RetainedBindings).PostId);
            TransferAttachment[] all = [.. thread.Posts[0].Attachments, thread.RetainedBindings[0].Attachment];
            Assert.Equal(3, all.Length);
            Assert.Equal(3, all.Select(a => a.Metadata.AttachmentRevisionKey).Distinct().Count());
            Assert.All(all, a => { Assert.Null(a.History); Assert.Equal(Hash(Bytes), a.PayloadSha256); });
            Assert.Single(stage.Package.Envelope.Payloads);
            Assert.Empty(stage.Package.Catalog.Threads[1].RetainedBindings);
            foreach (TransferAttachment a in all)
            {
                using MemoryStream copy = new();
                AssertMeasured(await stage.CopyPayloadToAsync(a.PayloadSha256!, copy));
                Assert.Equal(Bytes, copy.ToArray());
            }
        });
        string wire = CatalogJson(package).ToJsonString();
        foreach (string privateField in new[] { "operationId", "packageFingerprint", "importedAtUtc", "profileId", "policyRevision", "pluginOwnerId" })
            Assert.DoesNotContain(privateField, wire);
        JsonObject retained = Retained(CatalogJson(package));
        Assert.Equal(new[] { "position", "mediaId", "variant", "url", "originalFileName", "expectedSize", "expectedMd5",
            "expectedSha1", "expectedSha256", "sizeHint", "revisionKey", "role", "status", "payloadSha256", "history" },
            retained.Select(p => p.Key));
    }

    [Theory]
    [InlineData("original")] [InlineData("preview")]
    public async Task UnkeyedObservationsInMixedV3StillDoNotAcquireHistory(string shape)
    {
        NativeTransferCatalog input = Catalog("all", "sha256");
        TransferThread first = input.Threads[0];
        TransferMediaRole role = shape == "preview" ? TransferMediaRole.Preview : TransferMediaRole.Original;
        TransferAttachment[] media = first.Posts[0].Attachments.Select(a => a.Role == role
            ? a with { Metadata = a.Metadata with { AttachmentRevisionKey = null } } : a).ToArray();
        input = input with { Threads = [first with { Posts = [Post(media)] }, input.Threads[1]] };
        byte[] package = await WritePackage(input);
        await AssertStage(package, async stage =>
        {
            Assert.Equal("chan-v3", stage.Package.Envelope.ContentSchema);
            TransferAttachment parsed = Selected(stage.Package.Catalog, shape);
            Assert.Null(parsed.Metadata.AttachmentRevisionKey); Assert.Null(parsed.History);
            Assert.Equal(new string('0', 64), parsed.Metadata.ExpectedSha256);
            using MemoryStream copy = new();
            AssertMeasured(await stage.CopyPayloadToAsync(parsed.PayloadSha256!, copy));
            Assert.Equal(Bytes, copy.ToArray());
        });
    }

    [Theory]
    [InlineData("sha1")] [InlineData("sha256")] [InlineData("md5")]
    public async Task RealWriterCannotSilentlyStripAContradictoryNonNullHistory(string mismatch)
    {
        TransferAttachment contradictory = Selected(Catalog("original", mismatch), "original") with
        { History = new("strict", At, false, false) };
        NativeTransferCatalog input = ReplaceSelected(Catalog(), "original", contradictory);
        using MemoryStream output = new();
        TransferPayloadSource source = new(new(Bytes.Length, Hash(Bytes)),
            _ => Task.FromResult<Stream>(new MemoryStream(Bytes, false)));
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(output, input, [source], PackageId);
        Assert.False(result.IsComplete); Assert.Null(result.Package);
        Assert.NotEqual(TransferWriteError.None, result.Error);
        Assert.Equal("strict", Selected(input, "original").History!.Kind);
        Assert.True(output.CanWrite);
    }

    [Theory]
    [InlineData("unreferencedPayload")] [InlineData("missingPayloadEntry")]
    public async Task V3RequiresTheCompleteMeasuredPayloadInventoryToMatchReferences(string mutation)
    {
        byte[] package = await WritePackage(Catalog());
        JsonObject catalog = CatalogJson(package);
        if (mutation == "unreferencedPayload")
        {
            foreach (JsonNode? post in Thread(catalog)["posts"]!.AsArray())
                foreach (JsonNode? a in post!["attachments"]!.AsArray())
                { a!["status"] = "omitted"; a["payloadSha256"] = null; }
            Retained(catalog)["status"] = "omitted"; Retained(catalog)["payloadSha256"] = null;
            await AssertCatalogRejected(Repack(package, catalog), TransferCatalogError.InvalidReference);
        }
        else
        {
            Dictionary<string, byte[]> entries = Entries(package);
            entries.Remove("payload/" + Hash(Bytes));
            JsonObject manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
            manifest["payloads"] = new JsonArray();
            entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
            await AssertCatalogRejected(Zip(entries), TransferCatalogError.InvalidReference);
        }
    }

    [Theory]
    [InlineData("original", "chan-v1")] [InlineData("original", "chan-v2")]
    [InlineData("preview", "chan-v1")] [InlineData("preview", "chan-v2")]
    [InlineData("retained", "chan-v1")] [InlineData("retained", "chan-v2")]
    public async Task DownlevelCannotAdmitKeyedContainedNullHistory(string shape, string schema)
    {
        byte[] package = await WritePackage(Catalog(shape, "sha256"));
        JsonObject catalog = CatalogJson(package);
        catalog["schema"] = schema;
        if (schema == "chan-v1")
        {
            // v1 has no retained collection: represent the same keyed bytes as a current original.
            if (shape == "retained")
            {
                JsonObject media = Retained(catalog).DeepClone().AsObject();
                media["isDeleted"] = false;
                Thread(catalog)["posts"] = new JsonArray(new JsonObject
                {
                    ["id"] = "999", ["ordinal"] = 0, ["fields"] = (int)PostFieldPresence.All,
                    ["postedAtUtc"] = null, ["subject"] = null, ["author"] = null, ["html"] = null,
                    ["isDeleted"] = false, ["attachments"] = new JsonArray(media)
                });
            }
            foreach (JsonNode? thread in catalog["threads"]!.AsArray()) thread!.AsObject().Remove("retainedBindings");
        }
        byte[] downlevel = Repack(package, catalog, manifest =>
        {
            manifest["contentSchema"] = schema;
            manifest["requiredFeatures"] = new JsonArray(schema);
        });
        await AssertCatalogRejected(downlevel, TransferCatalogError.InvalidHistory);
    }

    [Theory]
    [InlineData("original", "strict")] [InlineData("original", "provider-mismatch")]
    [InlineData("retained", "strict")] [InlineData("retained", "provider-mismatch")]
    public async Task V3PreservesValidHistoricalAcceptanceAlongsidePassiveObservations(string shape, string kind)
    {
        TransferAttachment old = Media(shape == "retained" ? 7 : 0, shape == "retained" ? "113" : "111",
            shape == "retained" ? "999" : "15", TransferMediaRole.Original,
            kind == "strict" ? "none" : "md5") with
        { History = new(kind, At.AddDays(-10), false, kind != "strict") };
        NativeTransferCatalog input = ReplaceSelected(Catalog(), shape, old);
        byte[] package = await WritePackage(input);
        await AssertStage(package, stage =>
        {
            Assert.Equal("chan-v3", stage.Package.Envelope.ContentSchema);
            Assert.Equal(old, Selected(stage.Package.Catalog, shape));
            Assert.Null(Selected(stage.Package.Catalog, "preview").History);
            Assert.Equal(At.AddDays(-10), Selected(stage.Package.Catalog, shape).History!.AcceptedAtUtc);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("original", "sha1")] [InlineData("original", "sha256")]
    [InlineData("retained", "sha1")] [InlineData("retained", "sha256")]
    [InlineData("original", "size")] [InlineData("retained", "md5")]
    public async Task V3DoesNotForgiveContradictoryNonNullHistory(string shape, string mismatch)
    {
        byte[] package = await WritePackage(Catalog(shape, mismatch));
        JsonObject catalog = CatalogJson(package);
        Selected(catalog, shape)["history"] = History("strict");
        await AssertCatalogRejected(Repack(package, catalog), TransferCatalogError.InvalidHistory);
    }

    [Theory]
    [InlineData("strictSizeFlag")] [InlineData("strictMd5Flag")]
    [InlineData("mismatchWithoutMismatch")] [InlineData("passiveAsHistoryKind")]
    [InlineData("localRightsInHistory")] [InlineData("missingHistoryDate")]
    [InlineData("historyOnUnkeyed")]
    public async Task NonNullHistoryStillUsesTheOldExactBoundedSemantics(string mutation)
    {
        byte[] package = await WritePackage(Catalog());
        JsonObject catalog = CatalogJson(package), a = Current(catalog);
        JsonObject history = History("strict");
        a["history"] = history;
        TransferCatalogError expected = TransferCatalogError.InvalidHistory;
        switch (mutation)
        {
            case "strictSizeFlag": history["sizeMismatch"] = true; break;
            case "strictMd5Flag": history["md5Mismatch"] = true; break;
            case "mismatchWithoutMismatch": history["kind"] = "provider-mismatch"; break;
            case "passiveAsHistoryKind": history["kind"] = "passive-import"; break;
            case "localRightsInHistory": history["profileId"] = "SYNTHETIC-SECRET"; expected = TransferCatalogError.InvalidField; break;
            case "missingHistoryDate": history.Remove("acceptedAtUtc"); expected = TransferCatalogError.InvalidField; break;
            case "historyOnUnkeyed": a["revisionKey"] = null; break;
        }
        await AssertCatalogRejected(Repack(package, catalog), expected);
    }

    [Theory]
    [InlineData("missingRetainedField", (int)TransferCatalogError.InvalidField)]
    [InlineData("missingSiblingRetained", (int)TransferCatalogError.InvalidField)]
    [InlineData("retainedDeletionField", (int)TransferCatalogError.InvalidField)]
    [InlineData("retainedPreviewRole", (int)TransferCatalogError.InvalidField)]
    [InlineData("retainedMissingKey", (int)TransferCatalogError.InvalidField)]
    [InlineData("retainedUnknown", (int)TransferCatalogError.InvalidField)]
    [InlineData("missingHistoryField", (int)TransferCatalogError.InvalidField)]
    [InlineData("extraAuthority", (int)TransferCatalogError.InvalidField)]
    [InlineData("wrongRevision", (int)TransferCatalogError.InvalidRevision)]
    [InlineData("wrongRetainedPost", (int)TransferCatalogError.InvalidRevision)]
    [InlineData("missingPayload", (int)TransferCatalogError.InvalidReference)]
    [InlineData("unknownPayload", (int)TransferCatalogError.InvalidReference)]
    [InlineData("uppercasePayload", (int)TransferCatalogError.InvalidReference)]
    [InlineData("omittedWithPayload", (int)TransferCatalogError.InvalidReference)]
    [InlineData("unsafeUrl", (int)TransferCatalogError.UnsafeUrl)]
    [InlineData("unsafeHtml", (int)TransferCatalogError.UnsafeHtml)]
    [InlineData("missingPresence", (int)TransferCatalogError.InvalidPresence)]
    [InlineData("duplicateRetained", (int)TransferCatalogError.DuplicateIdentity)]
    [InlineData("duplicateCurrentRetained", (int)TransferCatalogError.DuplicateIdentity)]
    [InlineData("duplicatePosition", (int)TransferCatalogError.DuplicateIdentity)]
    [InlineData("duplicatePost", (int)TransferCatalogError.DuplicateIdentity)]
    [InlineData("duplicateThread", (int)TransferCatalogError.DuplicateIdentity)]
    [InlineData("badProviderSha256", (int)TransferCatalogError.InvalidField)]
    public async Task NullHistoryDoesNotRelaxOtherCatalogGuards(string mutation, int expectedError)
    {
        TransferCatalogError expected = (TransferCatalogError)expectedError;
        byte[] package = await WritePackage(Catalog());
        JsonObject catalog = CatalogJson(package), thread = Thread(catalog), a = Current(catalog), retained = Retained(catalog);
        JsonArray bindings = thread["retainedBindings"]!.AsArray();
        switch (mutation)
        {
            case "missingRetainedField": thread.Remove("retainedBindings"); break;
            case "missingSiblingRetained": catalog["threads"]![1]!.AsObject().Remove("retainedBindings"); break;
            case "retainedDeletionField": retained["isDeleted"] = false; break;
            case "retainedPreviewRole": retained["role"] = "preview"; break;
            case "retainedMissingKey": retained["revisionKey"] = null; break;
            case "retainedUnknown": retained["status"] = "unknown"; retained["payloadSha256"] = null; break;
            case "missingHistoryField": a.Remove("history"); break;
            case "extraAuthority": a["policyRevision"] = 5; break;
            case "wrongRevision": a["mediaId"] = "changed-without-rekey"; break;
            case "wrongRetainedPost": bindings[0]!["postId"] = "1000"; break;
            case "missingPayload": a["payloadSha256"] = null; break;
            case "unknownPayload": a["payloadSha256"] = new string('0', 64); break;
            case "uppercasePayload": a["payloadSha256"] = Hash(Bytes).ToUpperInvariant(); break;
            case "omittedWithPayload": a["status"] = "omitted"; break;
            case "unsafeUrl": a["url"] = "https://SYNTHETIC-SECRET@i.4cdn.org/b/111.jpg"; break;
            case "unsafeHtml": thread["posts"]![0]!["html"] = "<script>alert('SYNTHETIC-SECRET')</script>"; break;
            case "missingPresence": thread["posts"]![0]!["fields"] = 0; break;
            case "duplicateRetained": bindings.Add(bindings[0]!.DeepClone()); break;
            case "duplicateCurrentRetained":
                JsonObject current = a.DeepClone().AsObject(); current.Remove("isDeleted");
                bindings.Add(new JsonObject { ["postId"] = "15", ["attachment"] = current }); break;
            case "duplicatePosition":
                JsonObject second = a.DeepClone().AsObject(); second["revisionKey"] = null;
                thread["posts"]![0]!["attachments"]!.AsArray().Add(second); break;
            case "duplicatePost": thread["posts"]!.AsArray().Add(thread["posts"]![0]!.DeepClone()); break;
            case "duplicateThread": catalog["threads"]!.AsArray().Add(thread.DeepClone()); break;
            case "badProviderSha256": a["expectedSha256"] = "not-a-sha256"; break;
        }
        await AssertCatalogRejected(Repack(package, catalog), expected);
    }

    [Theory]
    [InlineData("featureV1", (int)TransferEnvelopeError.UnsupportedFeature, (int)TransferCatalogError.None)]
    [InlineData("featureV2", (int)TransferEnvelopeError.UnsupportedFeature, (int)TransferCatalogError.None)]
    [InlineData("featuresEmpty", (int)TransferEnvelopeError.UnsupportedFeature, (int)TransferCatalogError.None)]
    [InlineData("featuresDuplicate", (int)TransferEnvelopeError.UnsupportedFeature, (int)TransferCatalogError.None)]
    [InlineData("featureUnknown", (int)TransferEnvelopeError.UnsupportedFeature, (int)TransferCatalogError.None)]
    [InlineData("envelopeV2", (int)TransferEnvelopeError.None, (int)TransferCatalogError.UnsupportedSchema)]
    [InlineData("catalogV2", (int)TransferEnvelopeError.None, (int)TransferCatalogError.UnsupportedSchema)]
    [InlineData("futureVersion", (int)TransferEnvelopeError.UnsupportedVersion, (int)TransferCatalogError.None)]
    [InlineData("futureCatalog", (int)TransferEnvelopeError.None, (int)TransferCatalogError.UnsupportedSchema)]
    public async Task EnvelopeCatalogAndSingletonFeatureMustStillMatchExactly(string mutation,
        int expectedEnvelopeError, int expectedCatalogError)
    {
        TransferEnvelopeError envelopeError = (TransferEnvelopeError)expectedEnvelopeError;
        TransferCatalogError catalogError = (TransferCatalogError)expectedCatalogError;
        byte[] package = await WritePackage(Catalog());
        JsonObject catalog = CatalogJson(package);
        if (mutation == "catalogV2") catalog["schema"] = "chan-v2";
        if (mutation == "futureCatalog") catalog["schema"] = "chan-v999";
        byte[] malformed = Repack(package, catalog, manifest =>
        {
            switch (mutation)
            {
                case "featureV1": manifest["requiredFeatures"] = new JsonArray("chan-v1"); break;
                case "featureV2": manifest["requiredFeatures"] = new JsonArray("chan-v2"); break;
                case "featuresEmpty": manifest["requiredFeatures"] = new JsonArray(); break;
                case "featuresDuplicate": manifest["requiredFeatures"] = new JsonArray("chan-v3", "chan-v3"); break;
                case "featureUnknown": manifest["requiredFeatures"] = new JsonArray("chan-v3", "SYNTHETIC-SECRET"); break;
                case "envelopeV2": manifest["contentSchema"] = "chan-v2"; manifest["requiredFeatures"] = new JsonArray("chan-v2"); break;
                case "futureVersion": manifest["contentSchema"] = "chan-v999"; manifest["requiredFeatures"] = new JsonArray("chan-v999"); break;
            }
        });
        TransferPackageInspection result = await Inspect(malformed);
        Assert.False(result.IsPackageValid); Assert.Null(result.Package);
        Assert.Equal(envelopeError, result.EnvelopeError); Assert.Equal(catalogError, result.CatalogError);
        await AssertRejectedStage(malformed, envelopeError, catalogError);
    }

    [Theory]
    [InlineData("container", (int)TransferEnvelopeError.ContainerLimit)]
    [InlineData("entries", (int)TransferEnvelopeError.EntryLimit)]
    [InlineData("manifest", (int)TransferEnvelopeError.ManifestLimit)]
    [InlineData("catalog", (int)TransferEnvelopeError.PayloadLimit)]
    [InlineData("payload", (int)TransferEnvelopeError.PayloadLimit)]
    [InlineData("total", (int)TransferEnvelopeError.PayloadLimit)]
    public async Task V3ObeysTightenedEnvelopeBudgetsInValidatorAndStage(string mode, int expectedError)
    {
        TransferEnvelopeError expected = (TransferEnvelopeError)expectedError;
        byte[] package = await WritePackage(Catalog());
        TransferEnvelopeLimits limits = mode switch
        {
            "container" => new() { ContainerBytes = package.Length - 1 },
            "entries" => new() { Entries = 2 },
            "manifest" => new() { ManifestBytes = 1 },
            "catalog" => new() { CatalogBytes = 1 },
            "payload" => new() { PayloadBytes = Bytes.Length - 1 },
            _ => new() { TotalBytes = 1 }
        };
        TransferPackageInspection result = await Inspect(package, limits);
        Assert.False(result.IsPackageValid); Assert.Equal(expected, result.EnvelopeError);
        Assert.Equal(TransferCatalogError.None, result.CatalogError);
        await AssertRejectedStage(package, expected, TransferCatalogError.None, limits);
    }

    [Theory]
    [InlineData("threads")] [InlineData("text")]
    public async Task V3CatalogBudgetsAreNotBypassedByNullHistory(string mode)
    {
        byte[] package = await WritePackage(Catalog());
        JsonObject catalog = CatalogJson(package);
        if (mode == "threads")
            catalog["threads"] = new JsonArray(Enumerable.Range(0, 1001).Select(index =>
            {
                JsonObject clone = Thread(catalog).DeepClone().AsObject();
                clone["id"]!["thread"] = (7001 + index).ToString();
                return (JsonNode?)clone;
            }).ToArray());
        else Thread(catalog)["title"] = new string('x', 1024 * 1024 + 1);
        await AssertCatalogRejected(Repack(package, catalog), TransferCatalogError.BudgetExceeded);
    }

    [Theory]
    [InlineData("threads")] [InlineData("posts")] [InlineData("current")] [InlineData("retained")]
    public void SerializerRetainsObjectCountGuardsBeforeEmittingV3(string mode)
    {
        NativeTransferCatalog input = Catalog();
        TransferThread first = input.Threads[0];
        input = mode switch
        {
            "threads" => input with { Threads = Enumerable.Repeat(first, 1001).ToImmutableArray() },
            "posts" => input with { Threads = [first with { Posts = Enumerable.Repeat(first.Posts[0], 100_001).ToImmutableArray() }] },
            "current" => input with { Threads = [first with { Posts = [Post(Enumerable.Repeat(first.Posts[0].Attachments[0], 100_001).ToArray())] }] },
            _ => input with { Threads = [first with { RetainedBindings = Enumerable.Repeat(first.RetainedBindings[0], 100_001).ToImmutableArray() }] }
        };
        Assert.Throws<InvalidDataException>(() => NativeTransferCatalogSerializer.Serialize(input));
    }

    [Theory]
    [InlineData("legacy", "chan-v1")] [InlineData("active", "chan-v1")]
    [InlineData("retained", "chan-v2")]
    public async Task ExistingV1AndV2WriterOutputsStayDownlevelWhenV3IsNotNeeded(string mode, string expected)
    {
        TransferAttachment a = Media(0, "111", "15", TransferMediaRole.Original) with
        { History = new("strict", At, false, false) };
        TransferThread thread;
        if (mode == "legacy")
            thread = MakeThread(Post(a with { Metadata = a.Metadata with { AttachmentRevisionKey = null }, History = null }));
        else if (mode == "active") thread = MakeThread(Post(a));
        else thread = MakeThread() with
        { RetainedBindings = [new("999", Media(7, "113", "999", TransferMediaRole.Original) with { History = new("strict", At, false, false) })] };
        byte[] package = await WritePackage(new(At, [thread]));
        TransferPackageInspection result = await Inspect(package);
        Assert.True(result.IsPackageValid, result.ToString()); Assert.Equal(expected, result.Package!.Envelope.ContentSchema);
        Assert.Equal(expected, ManifestJson(package)["contentSchema"]!.GetValue<string>());
        Assert.Equal(new[] { expected }, ManifestJson(package)["requiredFeatures"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(mode == "retained", Thread(CatalogJson(package)).ContainsKey("retainedBindings"));
        await AssertStage(package, stage =>
        {
            Assert.Equal(expected, stage.Package.Envelope.ContentSchema);
            TransferAttachment parsed = Selected(stage.Package.Catalog, mode == "retained" ? "retained" : "original");
            Assert.Equal(mode == "legacy" ? null : a.History, parsed.History);
            Assert.Equal(mode == "legacy" ? null : mode == "active" ? a.Metadata.AttachmentRevisionKey
                : thread.RetainedBindings[0].Attachment.Metadata.AttachmentRevisionKey, parsed.Metadata.AttachmentRevisionKey);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("omitted")] [InlineData("unavailable")]
    public async Task KeyedNonContainedWithoutHistoryDoesNotNeedV3(string status)
    {
        TransferAttachment a = Media(7, "113", "999", TransferMediaRole.Original) with
        { Status = status == "omitted" ? TransferContentStatus.Omitted : TransferContentStatus.Unavailable, PayloadSha256 = null };
        NativeTransferCatalog input = new(At, [MakeThread() with { RetainedBindings = [new("999", a)] }]);
        byte[] package = await WritePackage(input, withPayload: false);
        TransferPackageInspection result = await Inspect(package);
        Assert.True(result.IsPackageValid, result.ToString());
        Assert.Equal("chan-v2", result.Package!.Envelope.ContentSchema);
        Assert.Equal(a, Selected(result.Package.Catalog, "retained"));
    }

    [Theory]
    [InlineData("chan-v1")] [InlineData("chan-v2")]
    public async Task IncomingV3WithOnlyHistoricalDataCanonicalizesToTheOldNecessaryVersion(string expected)
    {
        TransferAttachment a = Media(0, "111", "15", TransferMediaRole.Original) with { History = new("strict", At, false, false) };
        NativeTransferCatalog input = new(At, [expected == "chan-v1" ? MakeThread(Post(a)) : MakeThread() with
        { RetainedBindings = [new("999", Media(7, "113", "999", TransferMediaRole.Original) with { History = new("strict", At, false, false) })] }]);
        byte[] package = await WritePackage(input);
        JsonObject catalog = CatalogJson(package); catalog["schema"] = "chan-v3";
        foreach (JsonNode? node in catalog["threads"]!.AsArray())
            if (!node!.AsObject().ContainsKey("retainedBindings")) node["retainedBindings"] = new JsonArray();
        byte[] upgraded = Repack(package, catalog, m =>
        { m["contentSchema"] = "chan-v3"; m["requiredFeatures"] = new JsonArray("chan-v3"); });
        TransferPackageInspection result = await Inspect(upgraded);
        Assert.True(result.IsPackageValid, result.ToString());
        Assert.Equal(expected, NativeTransferCatalogSerializer.ContentSchema(result.Package!.Catalog));
        Assert.Equal(NativeTransferCatalogSerializer.Serialize(input), NativeTransferCatalogSerializer.Serialize(result.Package.Catalog));
    }

    [Theory]
    [InlineData("catalog")] [InlineData("manifest")]
    public async Task DuplicateJsonPropertiesRemainRejectedInV3(string location)
    {
        byte[] package = await WritePackage(Catalog());
        Dictionary<string, byte[]> entries = Entries(package);
        if (location == "catalog")
        {
            string text = Encoding.UTF8.GetString(entries["catalog.json"]);
            entries["catalog.json"] = Encoding.UTF8.GetBytes(text.Replace("\"schema\":\"chan-v3\"",
                "\"schema\":\"chan-v3\",\"schema\":\"chan-v3\"", StringComparison.Ordinal));
            JsonObject manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
            manifest["catalog"] = Blob(entries["catalog.json"]);
            entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        }
        else
        {
            string text = Encoding.UTF8.GetString(entries["manifest.json"]);
            entries["manifest.json"] = Encoding.UTF8.GetBytes(text.Replace("\"contentSchema\":\"chan-v3\"",
                "\"contentSchema\":\"chan-v3\",\"contentSchema\":\"chan-v3\"", StringComparison.Ordinal));
        }
        byte[] malformed = Zip(entries);
        if (location == "catalog") await AssertCatalogRejected(malformed, TransferCatalogError.InvalidField);
        else await AssertRejectedStage(malformed, TransferEnvelopeError.InvalidManifest, TransferCatalogError.None);
    }

    [Theory]
    [InlineData("bytes")] [InlineData("length")]
    public async Task NullHistoryNeverSkipsActualPayloadMeasurement(string mutation)
    {
        byte[] package = await WritePackage(Catalog("original", "sha256"));
        Dictionary<string, byte[]> entries = Entries(package);
        entries["payload/" + Hash(Bytes)] = mutation == "length" ? Bytes[..^1] : Bytes.Select(b => (byte)(b ^ 0x5a)).ToArray();
        byte[] malformed = Zip(entries); // fresh ZIP CRC, but the original declared payload SHA/length
        TransferEnvelopeError expected = mutation == "length" ? TransferEnvelopeError.LengthMismatch : TransferEnvelopeError.HashMismatch;
        TransferPackageInspection result = await Inspect(malformed);
        Assert.False(result.IsPackageValid); Assert.Equal(expected, result.EnvelopeError);
        await AssertRejectedStage(malformed, expected, TransferCatalogError.None);
    }

    private static TransferAttachment Media(int position, string mediaId, string postId, TransferMediaRole role, string mismatch = "none")
    {
        string variant = role == TransferMediaRole.Preview ? "preview" : "original";
        MediaProviderMetadata provider = new(mediaId, variant, $"https://i.4cdn.org/b/{mediaId}.jpg", mediaId + ".jpg",
            mismatch is "size" or "all" ? Bytes.Length + 7 : Bytes.Length,
            Convert.ToBase64String(mismatch is "md5" or "all" ? new byte[16] : MD5.HashData(Bytes)),
            mismatch is "sha256" or "all" ? new string('0', 64) : Hash(Bytes))
        { ExpectedSha1 = Convert.ToBase64String(mismatch is "sha1" or "all" ? new byte[20] : SHA1.HashData(Bytes)) };
        CapturedAttachment metadata = new(position, mediaId, variant, provider.Url, provider.OriginalFileName,
            ExpectedSize: provider.ExpectedSize, ExpectedMd5: provider.ExpectedMd5)
        {
            ExpectedSha1 = provider.ExpectedSha1, ExpectedSha256 = provider.ExpectedSha256,
            AttachmentRevisionKey = MediaRevisionKeys.CreateAttachmentKey(Id, postId, position, provider)
        };
        return new(metadata, role, TransferContentStatus.Contained, Hash(Bytes), null);
    }
    private static TransferPost Post(params TransferAttachment[] media) => new(
        new("15", long.MaxValue, PostFieldPresence.All, Subject: "", Html: "<b>synthetic</b>",
            Attachments: media.Select(a => a.Metadata).ToImmutableArray()), media.ToImmutableArray());
    private static TransferThread MakeThread(params TransferPost[] posts) => new(Id, null, "synthetic v3 thread",
        ThreadSourceState.NotFound, At, null, 0, false, posts.ToImmutableArray());
    private static NativeTransferCatalog Catalog(string shape = "all", string mismatch = "none")
    {
        List<TransferAttachment> current = [];
        if (shape is "all" or "original") current.Add(Media(0, "111", "15", TransferMediaRole.Original, mismatch));
        if (shape is "all" or "preview") current.Add(Media(1, "112", "15", TransferMediaRole.Preview, mismatch));
        TransferThread first = current.Count == 0 ? MakeThread() : MakeThread(Post(current.ToArray()));
        if (shape is "all" or "retained") first = first with
        { RetainedBindings = [new("999", Media(7, "113", "999", TransferMediaRole.Original, mismatch))] };
        return new(At, [first, MakeThread() with { Id = new("4chan", "b", "7002") }]);
    }
    private static TransferAttachment Selected(NativeTransferCatalog catalog, string shape) => shape == "retained"
        ? catalog.Threads[0].RetainedBindings[0].Attachment
        : catalog.Threads[0].Posts[0].Attachments.Single(a => a.Role == (shape == "preview" ? TransferMediaRole.Preview : TransferMediaRole.Original));
    private static NativeTransferCatalog ReplaceSelected(NativeTransferCatalog catalog, string shape, TransferAttachment replacement)
    {
        TransferThread first = catalog.Threads[0];
        if (shape == "retained") first = first with { RetainedBindings = [new("999", replacement)] };
        else first = first with { Posts = [Post(first.Posts[0].Attachments.Select(a => a.Role == TransferMediaRole.Original ? replacement : a).ToArray())] };
        return catalog with { Threads = [first, catalog.Threads[1]] };
    }
    private static async Task<byte[]> WritePackage(NativeTransferCatalog catalog, bool withPayload = true)
    {
        using MemoryStream stream = new();
        TransferPayloadSource[] sources = withPayload
            ? [new(new(Bytes.Length, Hash(Bytes)), _ => Task.FromResult<Stream>(new MemoryStream(Bytes, false)))] : [];
        TransferWriteResult result = await NativeTransferPackageWriter.WriteAsync(stream, catalog, sources, PackageId);
        Assert.True(result.IsComplete, result.Error.ToString());
        Assert.NotNull(result.Package); Assert.Equal(0, stream.Position);
        return stream.ToArray();
    }
    private static async Task<TransferPackageInspection> Inspect(byte[] package, TransferEnvelopeLimits? limits = null)
    {
        using MemoryStream stream = new(package, false);
        return await NativeTransferPackageValidator.InspectAsync(stream, limits: limits);
    }
    private static async Task AssertCatalogRejected(byte[] package, TransferCatalogError expected)
    {
        TransferPackageInspection result = await Inspect(package);
        Assert.False(result.IsPackageValid); Assert.Null(result.Package);
        Assert.Equal(TransferEnvelopeError.None, result.EnvelopeError); Assert.Equal(expected, result.CatalogError);
        Assert.DoesNotContain("SYNTHETIC-SECRET", result.ToString());
        await AssertRejectedStage(package, TransferEnvelopeError.None, expected);
    }
    private static async Task AssertStage(byte[] package, Func<NativeTransferImportStage, Task> inspect)
    {
        string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-v3-codec-");
        try
        {
            using MemoryStream source = new(package, false);
            NativeTransferStageResult result = await NativeTransferImportStage.CreateAsync(source, directory);
            Assert.Equal(NativeTransferStageError.None, result.Error);
            await using (NativeTransferImportStage stage = Assert.IsType<NativeTransferImportStage>(result.Stage))
                await inspect(stage);
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static async Task AssertRejectedStage(byte[] package, TransferEnvelopeError envelopeError,
        TransferCatalogError catalogError, TransferEnvelopeLimits? limits = null)
    {
        string directory = TemporaryWorkspacePaths.CreateDirectory("meowchan-v3-codec-rejected-");
        try
        {
            using MemoryStream source = new(package, false);
            NativeTransferStageResult result = await NativeTransferImportStage.CreateAsync(source, directory, limits: limits);
            Assert.Null(result.Stage);
            Assert.Equal(envelopeError == TransferEnvelopeError.ContainerLimit ? NativeTransferStageError.ContainerLimit : NativeTransferStageError.InvalidPackage, result.Error);
            Assert.Equal(envelopeError, result.EnvelopeError); Assert.Equal(catalogError, result.CatalogError);
            Assert.False(result.StageRetained); Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
            Assert.DoesNotContain("SYNTHETIC-SECRET", result.ToString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static void AssertMeasured(TransferMeasuredBlob blob)
    {
        Assert.Equal(Bytes.Length, blob.Length); Assert.Equal(Hash(Bytes), blob.Sha256);
        Assert.Equal(Convert.ToBase64String(MD5.HashData(Bytes)), blob.Md5);
        Assert.Equal(Convert.ToBase64String(SHA1.HashData(Bytes)), blob.Sha1);
    }
    private static JsonObject History(string kind) => new()
    { ["kind"] = kind, ["acceptedAtUtc"] = "2026-11-01T09:00:00.0000000Z", ["sizeMismatch"] = false, ["md5Mismatch"] = false };
    private static JsonObject Thread(JsonObject catalog) => catalog["threads"]![0]!.AsObject();
    private static JsonObject Current(JsonObject catalog) => Thread(catalog)["posts"]![0]!["attachments"]![0]!.AsObject();
    private static JsonObject Retained(JsonObject catalog) => Thread(catalog)["retainedBindings"]![0]!["attachment"]!.AsObject();
    private static JsonObject Selected(JsonObject catalog, string shape) => shape == "retained" ? Retained(catalog) : Current(catalog);
    private static JsonObject CatalogJson(byte[] package) => JsonNode.Parse(Entries(package)["catalog.json"])!.AsObject();
    private static JsonObject ManifestJson(byte[] package) => JsonNode.Parse(Entries(package)["manifest.json"])!.AsObject();
    private static JsonObject Blob(byte[] bytes) => new() { ["length"] = bytes.Length, ["sha256"] = Hash(bytes) };
    private static Dictionary<string, byte[]> Entries(byte[] package)
    {
        using MemoryStream source = new(package, false); using ZipArchive zip = new(source, ZipArchiveMode.Read);
        Dictionary<string, byte[]> entries = new(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream input = entry.Open(); using MemoryStream copy = new(); input.CopyTo(copy);
            entries.Add(entry.FullName, copy.ToArray());
        }
        return entries;
    }
    private static byte[] Repack(byte[] package, JsonObject catalog, Action<JsonObject>? mutateManifest = null)
    {
        Dictionary<string, byte[]> entries = Entries(package);
        entries["catalog.json"] = Encoding.UTF8.GetBytes(catalog.ToJsonString());
        JsonObject manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
        manifest["catalog"] = Blob(entries["catalog.json"]);
        mutateManifest?.Invoke(manifest);
        entries["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString());
        return Zip(entries);
    }
    private static byte[] Zip(Dictionary<string, byte[]> entries)
    {
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, true))
            foreach ((string name, byte[] bytes) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero); entry.ExternalAttributes = 0;
                using Stream target = entry.Open(); target.Write(bytes);
            }
        return output.ToArray();
    }
}
