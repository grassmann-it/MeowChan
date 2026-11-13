using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Persistence;
using MeowChan.Transfer;

namespace MeowChan.Tests.Transfer;

public sealed class PostFilterTransferBoundaryRegressionTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "100");
    private static readonly DateTimeOffset Added = new(2026, 11, 13, 10, 20, 30, TimeSpan.Zero);
    private static readonly Guid RuleId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonemptyImportReplacesRatherThanMergesRulesHiddenPostsAndPolicy(bool admitted)
    {
        PostFilterRule incomingRule = Rule(RuleId, "incoming") with { Enabled = false, Replies = true };
        HiddenPost incomingPost = new(Thread, "101", true, Added);
        byte[] image = ExportImage([incomingRule], [incomingPost]);
        // Source revisions are provenance, never a target revision or a merge instruction.
        JsonNode document = JsonNode.Parse(image)!;
        document["sourceRevision"] = long.MaxValue;
        image = Encoding.UTF8.GetBytes(document.ToJsonString());

        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore target = new(directory.Path, lease);
        target.AddRule(RuleId, Draft("old value for incoming id"), 0);
        target.AddRule(Guid.NewGuid(), Draft("target only"), 1);
        target.HidePost(Thread, "101", false, DateTimeOffset.UnixEpoch, 2);
        target.HidePost(Thread, "target-only", false, Added, 3);
        PostFilterSnapshot before = target.SetHiddenPostsAction(HiddenPostsAction.Hide, 4);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        byte[] previous = File.ReadAllBytes(path);
        using ProfileAdmissionToken? admission = admitted ? lease.Admission.EnterStoreWriter() : null;

        PostFilterTransferImport result = admitted
            ? PostFilterProfileTransfer.ImportAdmitted(target, image, before.Revision, admission!)
            : PostFilterProfileTransfer.Import(target, image, before.Revision);

        Assert.True(result.IsImported);
        Assert.Equal(before.Revision + 1, result.Snapshot!.Revision);
        Assert.Equal(previous, File.ReadAllBytes(path + ".bak"));
        PostFilterSnapshot reopened = new PostFilterStore(directory.Path, lease).Load();
        Assert.Equal(before.Revision + 1, reopened.Revision);
        Assert.Equal(HiddenPostsAction.Stub, reopened.HiddenPostsAction);
        Assert.Equal(incomingRule, Assert.Single(reopened.Rules));
        Assert.Equal(incomingPost, Assert.Single(reopened.HiddenPosts));
    }

    [Fact]
    public void RoundTripKeepsSamePatternRulesDistinctByFieldMatchAndQualifiedScope()
    {
        PostFilterRule[] expected =
        [
            Rule(Guid.NewGuid(), "same"),
            Rule(Guid.NewGuid(), "same") with { Match = PostFilterMatch.Exact },
            Rule(Guid.NewGuid(), "same") with { Field = PostFilterField.Subject },
            Rule(Guid.NewGuid(), "same") with { Field = PostFilterField.Filename },
            Rule(Guid.NewGuid(), "same") with { Source = "4chan" },
            Rule(Guid.NewGuid(), "same") with { Source = "4chan", Board = "g", Replies = true },
            Rule(Guid.NewGuid(), "same") with { Source = "4chan", Board = "a", Enabled = false },
            Rule(Guid.NewGuid(), "same") with { Source = "8kun", Board = "g", Action = PostFilterAction.Stub },
            Rule(Guid.NewGuid(), "same") with { Source = "4chan", Board = "g", Match = PostFilterMatch.Exact }
        ];
        AssertRoundTrip(expected, []);
    }

    [Fact]
    public void RoundTripKeepsEveryHiddenIdentityComponentAndLeadingZeros()
    {
        HiddenPost first = new(Thread, "101", false, Added);
        HiddenPost[] expected =
        [
            first,
            first with { Thread = new ResourceId("8kun", "g", "100"), Replies = true },
            first with { Thread = new ResourceId("4chan", "a", "100") },
            first with { Thread = new ResourceId("4chan", "g", "200") },
            first with { Post = "00101", Replies = true },
            first with { Post = "102" }
        ];
        AssertRoundTrip([], expected);
    }

    [Fact]
    public void RoundTripAcceptsExactPatternCodepointAndIdentityLengthLimits()
    {
        string identity = new('i', PostFilterRules.MaximumIdentityLength);
        // Supplementary scalars count once, not as two UTF-16 code units.
        string pattern = string.Concat(Enumerable.Repeat("😀", PostFilterRules.MaximumPatternCodepoints));
        PostFilterRule rule = Rule(RuleId, pattern) with { Source = identity, Board = identity };
        HiddenPost hidden = new(new ResourceId(identity, identity, identity), identity, true, Added);
        AssertRoundTrip([rule], [hidden]);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("-1", true)]
    [InlineData("2", true)]
    [InlineData("2147483647", true)]
    [InlineData("2147483648", false)]
    [InlineData("1.5", false)]
    [InlineData("\"1\"", false)]
    [InlineData("null", false)]
    public void UnsupportedOrMalformedVersionsFailClosed(string versionJson, bool unsupported)
    {
        JsonNode document = ValidIncomingDocument();
        document["version"] = JsonNode.Parse(versionJson);
        AssertRejectedWithoutMutation(Encoding.UTF8.GetBytes(document.ToJsonString()), unsupported
            ? PostFilterTransferError.UnsupportedVersion : PostFilterTransferError.InvalidDocument);
    }

    [Theory]
    [InlineData("negative-revision")]
    [InlineData("overflow-revision")]
    [InlineData("long-pattern")]
    [InlineData("long-rule-source")]
    [InlineData("long-rule-board")]
    [InlineData("long-hidden-source")]
    [InlineData("long-hidden-board")]
    [InlineData("long-hidden-thread")]
    [InlineData("long-hidden-post")]
    [InlineData("empty-hidden-post")]
    [InlineData("control-hidden-post")]
    [InlineData("missing-hidden-added")]
    [InlineData("malformed-hidden-added")]
    [InlineData("duplicate-hidden-after-valid-prefix")]
    [InlineData("duplicate-rule-after-valid-prefix")]
    public void LateInvalidValuesCannotPartiallyReplaceEitherCollection(string kind)
    {
        JsonNode document = ValidIncomingDocument();
        JsonNode lastRule = document["rules"]![1]!;
        JsonNode lastPost = document["hiddenPosts"]![1]!;
        string tooLong = new('x', PostFilterRules.MaximumIdentityLength + 1);
        switch (kind)
        {
            case "negative-revision": document["sourceRevision"] = -1; break;
            case "overflow-revision": document["sourceRevision"] = JsonNode.Parse("9223372036854775808"); break;
            case "long-pattern":
                lastRule["pattern"] = string.Concat(Enumerable.Repeat("😀", PostFilterRules.MaximumPatternCodepoints)) + "x";
                break;
            case "long-rule-source": lastRule["source"] = tooLong; break;
            case "long-rule-board": lastRule["source"] = "4chan"; lastRule["board"] = tooLong; break;
            case "long-hidden-source": lastPost["source"] = tooLong; break;
            case "long-hidden-board": lastPost["board"] = tooLong; break;
            case "long-hidden-thread": lastPost["thread"] = tooLong; break;
            case "long-hidden-post": lastPost["post"] = tooLong; break;
            case "empty-hidden-post": lastPost["post"] = ""; break;
            case "control-hidden-post": lastPost["post"] = "101\n"; break;
            case "missing-hidden-added": lastPost.AsObject().Remove("added"); break;
            case "malformed-hidden-added": lastPost["added"] = "not-a-timestamp"; break;
            case "duplicate-hidden-after-valid-prefix":
                lastPost["post"] = document["hiddenPosts"]![0]!["post"]!.GetValue<string>();
                break;
            case "duplicate-rule-after-valid-prefix":
                lastRule["pattern"] = "FIRST INCOMING";
                lastRule["action"] = "stub";
                lastRule["enabled"] = false;
                lastRule["replies"] = true;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        AssertRejectedWithoutMutation(Encoding.UTF8.GetBytes(document.ToJsonString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CodecAcceptsExactCollectionCeilingsButRejectsOneMore(bool hidden)
    {
        int ceiling = hidden ? ProfileStoreLimits.HiddenPostCeiling : ProfileStoreLimits.PostFilterRuleCeiling;
        byte[] atLimit = CollectionImage(hidden, ceiling);
        Assert.True(atLimit.Length < PostFilterStore.MaximumFileBytes);
        PostFilterTransferInspection inspection = PostFilterProfileTransfer.Inspect(atLimit);
        Assert.True(inspection.IsValid);
        Assert.Equal(ceiling, hidden ? inspection.Snapshot!.HiddenPosts.Length : inspection.Snapshot!.Rules.Length);
        byte[] overLimit = CollectionImage(hidden, ceiling + 1);
        // The count guard, not the byte guard or a duplicate identity, must refuse this image.
        Assert.True(overLimit.Length < PostFilterStore.MaximumFileBytes);
        AssertRejectedWithoutMutation(overLimit);
    }

    [Fact]
    public void DocumentByteLimitIsInclusiveAndOversizeImportCannotWrite()
    {
        byte[] image = ExportImage([], []);
        // JSON whitespace is valid. Padding isolates the byte limit from every semantic limit.
        byte[] padded = new byte[PostFilterStore.MaximumFileBytes + 1];
        Array.Fill(padded, (byte)' ');
        image.CopyTo(padded, 0);
        Assert.True(PostFilterProfileTransfer.Inspect(padded.AsMemory(0, PostFilterStore.MaximumFileBytes)).IsValid);
        AssertRejectedWithoutMutation(padded);
    }

    [Theory]
    [InlineData("bom")]
    [InlineData("invalid-utf8")]
    [InlineData("truncated-after-hidden-posts")]
    public void BrokenEncodingOrTruncatedPayloadCannotReplacePolicy(string kind)
    {
        byte[] valid = Encoding.UTF8.GetBytes(ValidIncomingDocument().ToJsonString());
        byte[] broken = kind switch
        {
            "bom" => new byte[] { 0xef, 0xbb, 0xbf }.Concat(valid).ToArray(),
            "invalid-utf8" => valid.Concat(new byte[] { 0xc3, 0x28 }).ToArray(),
            "truncated-after-hidden-posts" => valid[..^1],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        AssertRejectedWithoutMutation(broken);
    }

    private static PostFilterRule Rule(Guid id, string pattern) =>
        new(id, true, PostFilterField.Text, PostFilterMatch.Contains, pattern, PostFilterAction.Highlight, false, null, null);

    private static PostFilterRuleDraft Draft(string pattern) =>
        new(PostFilterField.Text, PostFilterMatch.Contains, pattern, PostFilterAction.Hide, false);

    private static byte[] ExportImage(PostFilterRule[] rules, HiddenPost[] hidden)
    {
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore source = new(directory.Path, lease);
        long revision = source.Load().Revision;
        foreach (PostFilterRule rule in rules)
            revision = source.AddRule(rule.Id, new(rule.Field, rule.Match, rule.Pattern, rule.Action,
                rule.Replies, rule.Source, rule.Board, rule.Enabled), revision).Revision;
        foreach (HiddenPost post in hidden)
            revision = source.HidePost(post.Thread, post.Post, post.Replies, post.Added, revision).Revision;
        PostFilterTransferExport exported = PostFilterProfileTransfer.Export(source);
        Assert.True(exported.IsExported);
        Assert.Equal(revision, exported.Snapshot!.SourceRevision);
        Assert.Equal(rules, exported.Snapshot.Rules.ToArray());
        Assert.Equal(hidden, exported.Snapshot.HiddenPosts.ToArray());
        return exported.Bytes!;
    }

    private static void AssertRoundTrip(PostFilterRule[] rules, HiddenPost[] hidden)
    {
        byte[] image = ExportImage(rules, hidden);
        PostFilterTransferInspection inspected = PostFilterProfileTransfer.Inspect(image);
        Assert.True(inspected.IsValid);
        Assert.Equal(rules, inspected.Snapshot!.Rules.ToArray());
        Assert.Equal(hidden, inspected.Snapshot.HiddenPosts.ToArray());
        using TempDirectory directory = new();
        using (ProfileLease lease = ProfileLease.Acquire(directory.Path))
        {
            PostFilterStore target = new(directory.Path, lease);
            Assert.True(PostFilterProfileTransfer.Import(target, image, 0).IsImported);
        }
        using ProfileLease reopenedLease = ProfileLease.Acquire(directory.Path);
        PostFilterStore reopened = new(directory.Path, reopenedLease);
        PostFilterSnapshot loaded = reopened.Load();
        Assert.Equal(PostFilterStoreState.Ready, loaded.State);
        Assert.Equal(1, loaded.Revision);
        Assert.Equal(rules, loaded.Rules.ToArray());
        Assert.Equal(hidden, loaded.HiddenPosts.ToArray());
        PostFilterTransferExport reexported = PostFilterProfileTransfer.Export(reopened);
        Assert.True(reexported.IsExported);
        Assert.Equal(rules, reexported.Snapshot!.Rules.ToArray());
        Assert.Equal(hidden, reexported.Snapshot.HiddenPosts.ToArray());
    }

    private static JsonNode ValidIncomingDocument() => JsonNode.Parse(ExportImage(
        [Rule(RuleId, "first incoming"), Rule(Guid.Parse("22222222-2222-2222-2222-222222222222"), "second incoming")],
        [new(Thread, "101", true, Added), new(Thread, "102", false, Added)]))!;

    private static void AssertRejectedWithoutMutation(byte[] bytes,
        PostFilterTransferError error = PostFilterTransferError.InvalidDocument)
    {
        PostFilterTransferInspection inspected = PostFilterProfileTransfer.Inspect(bytes);
        Assert.Equal(error, inspected.Error);
        Assert.False(inspected.IsValid);
        Assert.Null(inspected.Snapshot);
        using TempDirectory directory = new();
        using ProfileLease lease = ProfileLease.Acquire(directory.Path);
        PostFilterStore target = new(directory.Path, lease);
        target.AddRule(Guid.NewGuid(), Draft("keep local policy"), 0);
        target.HidePost(Thread, "local-hidden", true, Added, 1);
        PostFilterSnapshot before = target.SetHiddenPostsAction(HiddenPostsAction.Hide, 2);
        string path = Path.Combine(directory.Path, PostFilterStore.FileName);
        byte[] primary = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        bool reachedWrite = false;
        target.BeforeAdmittedMutationForTests = _ => reachedWrite = true;
        using ProfileAdmissionToken admission = lease.Admission.EnterStoreWriter();
        // Exercise both production entry points, including the admitted path used by DesktopSessionHost.
        foreach (bool admitted in new[] { false, true })
        {
            PostFilterTransferImport result = admitted
                ? PostFilterProfileTransfer.ImportAdmitted(target, bytes, before.Revision, admission)
                : PostFilterProfileTransfer.Import(target, bytes, before.Revision);
            Assert.Equal(error, result.Error);
            Assert.False(result.IsImported);
            Assert.Null(result.Snapshot);
            Assert.False(reachedWrite);
            Assert.Equal(primary, File.ReadAllBytes(path));
            Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
            Assert.Empty(Directory.GetFiles(directory.Path, ".post-filters.json.*.tmp"));
            foreach (PostFilterSnapshot after in new[] { target.Load(), new PostFilterStore(directory.Path, lease).Load() })
            {
                Assert.Equal(PostFilterStoreState.Ready, after.State);
                Assert.Equal(before.Revision, after.Revision);
                Assert.Equal(before.HiddenPostsAction, after.HiddenPostsAction);
                Assert.Equal(before.Rules.ToArray(), after.Rules.ToArray());
                Assert.Equal(before.HiddenPosts.ToArray(), after.HiddenPosts.ToArray());
            }
        }
    }

    private static byte[] CollectionImage(bool hidden, int count)
    {
        // Stream the large boundary fixture instead of allocating 100,000 JsonNode objects.
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", "meowchan-post-filters-transfer");
            writer.WriteNumber("version", 1);
            writer.WriteNumber("sourceRevision", 0);
            writer.WriteString("hiddenPostsAction", "stub");
            writer.WriteStartArray("rules");
            if (!hidden)
                for (int i = 0; i < count; i++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", new Guid(i + 1, 0, 0, new byte[8]));
                    writer.WriteBoolean("enabled", true);
                    writer.WriteString("field", "text");
                    writer.WriteString("match", "contains");
                    writer.WriteString("pattern", "rule " + i.ToString(CultureInfo.InvariantCulture));
                    writer.WriteString("action", "hide");
                    writer.WriteBoolean("replies", false);
                    writer.WriteEndObject();
                }
            writer.WriteEndArray();
            writer.WriteStartArray("hiddenPosts");
            if (hidden)
                for (int i = 0; i < count; i++)
                {
                    writer.WriteStartObject();
                    writer.WriteString("source", Thread.Source);
                    writer.WriteString("board", Thread.Board);
                    writer.WriteString("thread", Thread.Thread);
                    writer.WriteString("post", i.ToString(CultureInfo.InvariantCulture));
                    writer.WriteBoolean("replies", false);
                    writer.WriteString("added", PostFilterRules.FormatAdded(Added));
                    writer.WriteEndObject();
                }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }
}
