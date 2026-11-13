using MeowChan;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

// Post filters, stage 1 (plan P2 part B): which posts a rule or a hidden post hides, shortens to a placeholder or
// highlights, and how "also hide replies" spreads over the quote graph.
[Collection("Localization")]
public sealed class PostFilterMatcherTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "100");
    // 16 bytes, once as hex and once as base64.
    private const string Md5Hex = "00112233445566778899aabbccddeeff";
    private static readonly string Md5Base64 = Convert.ToBase64String(Convert.FromHexString(Md5Hex));

    private static string Quote(string target) => "<a href=\"#p" + target + "\" class=\"quotelink\">&gt;&gt;" + target + "</a> ";

    private sealed record Post(string Html, string? Subject = null, string[]? Files = null, string? Md5 = null);

    private static ArchivedThreadView View(ResourceId id, params Post[] posts) => new(
        new ArchiveThreadSummary(id, "https://boards.4chan.org/" + id.Board + "/thread/" + id.Thread, "", "",
            ThreadSourceState.Archived, true, DateTimeOffset.UnixEpoch, null, posts.Length, 5),
        posts.Select((post, index) => new CapturedPost((100 + index).ToString(), index, PostFieldPresence.All, Subject: post.Subject,
            Html: post.Html, Attachments: post.Files == null && post.Md5 == null ? null
                : (post.Files ?? new[] { "file.png" }).Select((file, position) =>
                    new CapturedAttachment(position, "m" + index + "-" + position, OriginalFileName: file, ExpectedMd5: position == 0 ? post.Md5 : null)).ToArray()))
            .ToArray(),
        decimalPostOrder: true);

    private static ArchivedThreadView View(params Post[] posts) => View(Thread, posts);

    private static PostFilterRule Rule(string pattern, PostFilterField field = PostFilterField.Text, PostFilterAction action = PostFilterAction.Hide,
        PostFilterMatch match = PostFilterMatch.Contains, bool replies = false, string? source = null, string? board = null, bool enabled = true) =>
        PostFilterRules.Create(Guid.NewGuid(), PostFilterRules.Normalize(new PostFilterRuleDraft(field, match, pattern, action, replies, source, board, enabled)));

    private static PostFilterSnapshot Snapshot(IEnumerable<PostFilterRule>? rules = null, IEnumerable<HiddenPost>? hidden = null,
        HiddenPostsAction action = HiddenPostsAction.Stub, PostFilterStoreState state = PostFilterStoreState.Ready) =>
        new(3, action, (rules ?? Array.Empty<PostFilterRule>()).ToArray(), (hidden ?? Array.Empty<HiddenPost>()).ToArray(), state);

    private static PostFilterMatcher Matcher(PostFilterSnapshot snapshot)
    {
        PostFilterMatcher matcher = new();
        matcher.Update(snapshot);
        return matcher;
    }

    private static ReaderPostVerdict[] Classify(PostFilterMatcher matcher, ArchivedThreadView view)
    {
        ReaderPostVerdict[] verdicts = new ReaderPostVerdict[view.PostCount];
        ReaderQuoteGraph? graph = matcher.UsesQuoteGraph ? ReaderQuoteGraph.Build(view, CancellationToken.None) : null;
        matcher.Classify(view, graph, verdicts, CancellationToken.None);
        return verdicts;
    }

    private static ReaderPostVerdict[] Classify(PostFilterSnapshot snapshot, ArchivedThreadView view) => Classify(Matcher(snapshot), view);

    private static ReaderPostVisibility[] Visibility(ReaderPostVerdict[] verdicts) => verdicts.Select(verdict => verdict.Visibility).ToArray();

    private const ReaderPostVisibility S = ReaderPostVisibility.Shown;
    private const ReaderPostVisibility P = ReaderPostVisibility.Stub;
    private const ReaderPostVisibility H = ReaderPostVisibility.Hidden;

    [Fact]
    public void TextSubjectAndEveryFileNameAreComparedWithoutRegardToCase()
    {
        ArchivedThreadView view = View(
            new Post("opening post"),
            new Post("<p>Buy <b>CHEAP</b> pills now</p>"),
            new Post("plain", Subject: "Weekly GENERAL thread"),
            new Post("files", Files: new[] { "cat.png", "Promo-Banner.JPG" }),
            new Post("cheap is only in the subject of others"));
        ReaderPostVerdict[] verdicts = Classify(Snapshot(new[]
        {
            Rule("cheap pills", action: PostFilterAction.Hide),
            Rule("general", PostFilterField.Subject, PostFilterAction.Stub),
            Rule("promo-banner", PostFilterField.Filename, PostFilterAction.Highlight),
        }), view);
        Assert.Equal(new[] { S, H, P, S, S }, Visibility(verdicts));
        Assert.Equal(ReaderPostMarks.Highlight, verdicts[3].Marks);
        Assert.Equal(Texts.T("postFilter.reason.rule", "cheap pills"), verdicts[1].Reason);
        Assert.Equal(Texts.T("postFilter.reason.rule", "general"), verdicts[2].Reason);
        // A subject rule never looks at the text, a text rule never at the file names.
        Assert.Equal(new[] { S, S, S, S, S }, Visibility(Classify(Snapshot(new[] { Rule("thread", PostFilterField.Filename), Rule("cat.png") }), view)));
    }

    [Fact]
    public void ExactComparesTheWholeTrimmedValueWhileContainsFindsAPart()
    {
        ArchivedThreadView view = View(new Post("op"), new Post("  Bump  "), new Post("bump this thread"), new Post("bum"));
        Assert.Equal(new[] { S, H, S, S }, Visibility(Classify(Snapshot(new[] { Rule("bump", match: PostFilterMatch.Exact) }), view)));
        Assert.Equal(new[] { S, H, H, S }, Visibility(Classify(Snapshot(new[] { Rule("BUMP") }), view)));
        // Several contains rules of one field share one prefilter pass; each still reports its own hit.
        ReaderPostVerdict[] many = Classify(Snapshot(new[] { Rule("this", action: PostFilterAction.Highlight), Rule("bum", action: PostFilterAction.Stub), Rule("zzz") }), view);
        Assert.Equal(new[] { S, P, P, P }, Visibility(many));
        Assert.Equal(ReaderPostMarks.Highlight, many[2].Marks);
        Assert.Equal(ReaderPostMarks.None, many[1].Marks);
    }

    [Fact]
    public void Md5MatchesItsSixteenBytesWhetherHexOrBase64OnEitherSide()
    {
        ArchivedThreadView view = View(new Post("op"), new Post("a", Md5: Md5Base64), new Post("b", Md5: Md5Hex.ToUpperInvariant()),
            new Post("c", Md5: "not an md5"), new Post("d", Md5: Convert.ToBase64String(new byte[16])));
        Assert.Equal(new[] { S, H, H, S, S }, Visibility(Classify(Snapshot(new[] { Rule(Md5Hex, PostFilterField.Md5) }), view)));
        Assert.Equal(new[] { S, P, P, S, S }, Visibility(Classify(Snapshot(new[] { Rule(Md5Base64, PostFilterField.Md5, PostFilterAction.Stub) }), view)));
        Assert.True(CompiledPostFilters.TryMd5(Md5Base64, out UInt128 fromBase64));
        Assert.True(CompiledPostFilters.TryMd5(Md5Hex, out UInt128 fromHex));
        Assert.Equal(fromHex, fromBase64);
        Assert.False(CompiledPostFilters.TryMd5("abc", out _));
        Assert.False(CompiledPostFilters.TryMd5(null, out _));
    }

    [Fact]
    public void ARuleAppliesOnlyToItsSourceOrBoard()
    {
        Post[] posts = { new("op"), new("spam here") };
        ArchivedThreadView g = View(new ResourceId("4chan", "g", "100"), posts);
        ArchivedThreadView v = View(new ResourceId("4chan", "v", "200"), posts);
        ArchivedThreadView other = View(new ResourceId("8kun", "g", "300"), posts);
        PostFilterMatcher source = Matcher(Snapshot(new[] { Rule("spam", source: "4chan") }));
        PostFilterMatcher board = Matcher(Snapshot(new[] { Rule("spam", source: "4chan", board: "g") }));
        PostFilterMatcher everywhere = Matcher(Snapshot(new[] { Rule("spam") }));
        Assert.True(source.IsActive(g.Summary.Id)); Assert.True(source.IsActive(v.Summary.Id)); Assert.False(source.IsActive(other.Summary.Id));
        Assert.True(board.IsActive(g.Summary.Id)); Assert.False(board.IsActive(v.Summary.Id)); Assert.False(board.IsActive(other.Summary.Id));
        Assert.True(everywhere.IsActive(other.Summary.Id));
        Assert.Equal(new[] { S, H }, Visibility(Classify(source, v)));
        Assert.Equal(new[] { S, S }, Visibility(Classify(source, other)));
        Assert.Equal(new[] { S, H }, Visibility(Classify(board, g)));
        Assert.Equal(new[] { S, S }, Visibility(Classify(board, v)));
        Assert.Equal(new[] { S, H }, Visibility(Classify(everywhere, other)));
    }

    [Fact]
    public void AlsoHideRepliesSpreadsTransitivelyOverTheQuoteGraphButNeverForHighlights()
    {
        ArchivedThreadView view = View(
            new Post("op"),                                // 100
            new Post("spam origin"),                       // 101
            new Post(Quote("101") + "reply"),              // 102 -> 101
            new Post(Quote("102") + "reply to reply"),     // 103 -> 102 -> 101
            new Post(Quote("100") + "quotes only the op"), // 104
            new Post(Quote("104") + "and so on"));         // 105
        PostFilterMatcher hide = Matcher(Snapshot(new[] { Rule("spam", replies: true) }));
        Assert.True(hide.UsesQuoteGraph);
        ReaderPostVerdict[] verdicts = Classify(hide, view);
        Assert.Equal(new[] { S, H, H, H, S, S }, Visibility(verdicts));
        Assert.Equal(Texts.T("postFilter.reason.reply", "101"), verdicts[2].Reason);
        Assert.Equal(Texts.T("postFilter.reason.reply", "102"), verdicts[3].Reason);
        // A placeholder spreads as a placeholder; a hide reaching a placeholder reply wins.
        Assert.Equal(new[] { S, P, P, P, S, S }, Visibility(Classify(Snapshot(new[] { Rule("spam", action: PostFilterAction.Stub, replies: true) }), view)));
        ArchivedThreadView chain = View(new Post("op"), new Post("spam origin"), new Post(Quote("101") + "middle"), new Post(Quote("102") + "end"));
        ReaderPostVerdict[] mixed = Classify(Snapshot(new[]
        {
            Rule("spam", action: PostFilterAction.Stub, replies: true), Rule("middle", action: PostFilterAction.Hide, replies: true)
        }), chain);
        Assert.Equal(new[] { S, P, H, H }, Visibility(mixed));
        Assert.Equal(Texts.T("postFilter.reason.rule", "middle"), mixed[2].Reason);
        // Without "replies", and for a highlight, nothing spreads.
        Assert.Equal(new[] { S, H, S, S, S, S }, Visibility(Classify(Snapshot(new[] { Rule("spam") }), view)));
        PostFilterMatcher highlight = Matcher(Snapshot(new[] { Rule("spam", action: PostFilterAction.Highlight, replies: true) }));
        Assert.False(highlight.UsesQuoteGraph);
        Assert.Equal(new[] { S, S, S, S, S, S }, Visibility(Classify(highlight, view)));
        // The opening post hidden with its replies takes everything quoting it along, but not unrelated posts.
        Assert.Equal(new[] { H, S, S, S, H, H }, Visibility(Classify(Snapshot(hidden: new[] { new HiddenPost(Thread, "100", true, DateTimeOffset.UnixEpoch) },
            action: HiddenPostsAction.Hide), view)));
        // Without a graph (still being built) the direct verdicts stand.
        ReaderPostVerdict[] direct = new ReaderPostVerdict[view.PostCount];
        hide.Classify(view, null, direct, CancellationToken.None);
        Assert.Equal(new[] { S, H, S, S, S, S }, Visibility(direct));
    }

    [Fact]
    public void HideBeatsPlaceholderBeatsHighlightAndMarksCombine()
    {
        ArchivedThreadView view = View(new Post("op"), new Post("alpha beta"), new Post("alpha"), new Post("beta gamma"));
        ReaderPostVerdict[] verdicts = Classify(Snapshot(new[]
        {
            Rule("alpha", action: PostFilterAction.Highlight), Rule("beta", action: PostFilterAction.Stub), Rule("gamma", action: PostFilterAction.Hide)
        }), view);
        Assert.Equal(new[] { S, P, S, H }, Visibility(verdicts));
        Assert.Equal(ReaderPostMarks.Highlight, verdicts[1].Marks);
        Assert.Equal(ReaderPostMarks.Highlight, verdicts[2].Marks);
        Assert.Equal(Texts.T("postFilter.reason.rule", "beta"), verdicts[1].Reason);
        Assert.Equal(Texts.T("postFilter.reason.rule", "gamma"), verdicts[3].Reason);
    }

    [Fact]
    public void DisabledUnsupportedAndNotReadyRulesNeverApply()
    {
        ArchivedThreadView view = View(new Post("op"), new Post("spam"));
        PostFilterRule unsupported = new(Guid.NewGuid(), false, PostFilterField.Text, PostFilterMatch.Contains, "", PostFilterAction.Hide, false,
            null, null, "{\"match\":\"regex\",\"pattern\":\"sp.m\"}", "Unsupported post filter match.");
        PostFilterMatcher matcher = Matcher(Snapshot(new[] { unsupported, Rule("spam", enabled: false) }));
        Assert.False(matcher.IsActive(Thread));
        Assert.Equal(0, matcher.Filters.RuleCount);
        Assert.Equal(new[] { S, S }, Visibility(Classify(matcher, view)));
        foreach (PostFilterStoreState state in new[] { PostFilterStoreState.RecoveryAvailable, PostFilterStoreState.Unsupported, PostFilterStoreState.Failed })
            Assert.False(Matcher(Snapshot(new[] { Rule("spam") }, state: state)).IsActive(Thread));
        PostFilterMatcher none = new();
        none.Update(null);
        Assert.False(none.IsActive(Thread));
    }

    [Fact]
    public void HiddenPostsFollowTheHiddenPostsActionAndTheProjectionRemovesThem()
    {
        ArchivedThreadView view = View(new Post("op"), new Post("a"), new Post("b"));
        HiddenPost[] hidden = { new(Thread, "101", false, DateTimeOffset.UnixEpoch), new(new ResourceId("4chan", "g", "999"), "102", false, DateTimeOffset.UnixEpoch) };
        PostFilterMatcher stub = Matcher(Snapshot(hidden: hidden));
        Assert.True(stub.IsActive(Thread));
        Assert.False(stub.IsActive(new ResourceId("4chan", "g", "555")));
        ReaderPostVerdict[] verdicts = Classify(stub, view);
        Assert.Equal(new[] { S, P, S }, Visibility(verdicts)); // 102 is hidden in another thread only.
        Assert.Equal(Texts.T("postFilter.reason.single"), verdicts[1].Reason);
        PostFilterMatcher hide = Matcher(Snapshot(hidden: hidden, action: HiddenPostsAction.Hide));
        ReaderPostProjection projection = ReaderPostProjection.Build(view, null, new IReaderPostClassifier[] { hide }, null,
            new HashSet<string>(), CancellationToken.None);
        Assert.Equal(new[] { "100", "102" }, Enumerable.Range(0, projection.Count).Select(position => projection[position].Post.PostId));
        Assert.Equal(1, projection.HiddenCount);
        Assert.True(projection.IsHidden("101"));
    }

    [Fact]
    public void TheLastVerdictsServeTheNotesUntilTheRulesOrTheReadChange()
    {
        ArchivedThreadView view = View(new Post("op"), new Post("spam"), new Post("ok"));
        PostFilterMatcher matcher = Matcher(Snapshot(new[] { Rule("spam", action: PostFilterAction.Stub) }));
        long revision = matcher.Revision;
        int changes = 0;
        matcher.Changed += (_, _) => changes++;
        Assert.Null(matcher.VerdictOf(view, "101"));
        Classify(matcher, view);
        Assert.Equal(ReaderPostVisibility.Stub, matcher.VerdictOf(view, "101")?.Visibility);
        Assert.Equal(ReaderPostVisibility.Shown, matcher.VerdictOf(view, "102")?.Visibility);
        Assert.Null(matcher.VerdictOf(view, "999"));
        Assert.Null(matcher.VerdictOf(View(new ResourceId("4chan", "g", "777"), new Post("op"), new Post("spam")), "101"));
        matcher.Update(Snapshot());
        Assert.Equal(1, changes);
        Assert.True(matcher.Revision > revision);
        Assert.Null(matcher.VerdictOf(view, "101"));
        Assert.False(matcher.IsActive(Thread));
    }

    [Fact]
    public void ALongPatternIsShortenedInTheReason()
    {
        string pattern = new string('x', 100);
        string reason = CompiledPostFilters.RuleReason(Rule(pattern));
        Assert.Contains(new string('x', 59) + "…", reason);
        Assert.DoesNotContain(new string('x', 60), reason);
    }
}
