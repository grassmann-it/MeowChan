using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderReplyRestoreTests
{
    private static readonly ResourceId Thread = new("4chan", "g", "100");
    private static readonly ResourceId OtherThread = new("4chan", "g", "200");
    private const string FilteredText = "Text hidden after the first visit";

    [Fact]
    public async Task QuoteBackRestoresTheScrollAnchorAndSelectedRepliesIndependently()
    {
        using ReaderArchiveTestFixture fixture = new();
        ArchivedThreadView view = await fixture.CaptureAsync(Thread, "Reply anchors",
            Enumerable.Range(100, 80).Select(id => new CapturedPost(id.ToString(), id - 100, PostFieldPresence.All,
                Html: id switch
                {
                    141 => "<a href='#p140' class='quotelink'>&gt;&gt;140</a> Reply to the reading anchor",
                    143 => "<a href='#p142' class='quotelink'>&gt;&gt;142</a> Reply to the selected post",
                    _ => "Post " + id
                })).ToArray());
        await fixture.CaptureAsync(OtherThread, "Quote target", [new("200", 0, PostFieldPresence.All, Html: "Other thread")]);

        fixture.Run(page =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Window window = new() { Content = page, Width = 1320, Height = 760 };
            try
            {
                page.IsAvailable = true;
                window.Show();
                ReaderArchiveTestFixture.Load(page, view);
                ListBox list = Posts(window);
                BooruComponentDispatcher.Wait(page.ResumeReadingPositionAsync(new("4chan", "g", "100", "140", -16, "142")));
                page.ReaderExpandRepliesPreference = true;
                ArchivePostRow Row(string id) => (ArchivePostRow)list.Items[page.Projection!.PositionOf(id)]!;
                bool AtPlace() => page.CurrentThread?.Summary.Id == Thread
                    && list.SelectedItem is ArchivePostRow { Id: "142", RepliesExpanded: true, RepliesLoaded: true }
                    && page.CurrentReaderAnchor is ("140", >= -18 and <= -14);
                BooruComponentDispatcher.WaitUntil(AtPlace, "the distinct reading anchor and its selected post");

                ArchiveQuoteRow jump = new(new ArchiveQuote(new(Thread, "140"), new(OtherThread, "200"), ">>200", null));
                foreach (bool expanded in new[] { true, false })
                {
                    BooruComponentDispatcher.Wait(page.SetRepliesExpandedAsync(Row("140"), expanded));
                    BooruComponentDispatcher.Wait(page.NavigateQuoteAsync(jump));
                    Assert.Equal(OtherThread, page.CurrentThread?.Summary.Id);
                    page.GoHistoryBackByShortcut();
                    BooruComponentDispatcher.WaitUntil(AtPlace, "the reading anchor after quote back");
                    Assert.Equal(expanded, Row("140").RepliesExpanded);
                    Assert.Equal("141", Assert.Single(Row("140").Replies).Quote.Origin.PostId);
                }
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturningToLoadedReplyPagesAppliesTheNewPostFilter(bool fromGallery)
    {
        using ReaderArchiveTestFixture fixture = new();
        ArchivedThreadView origin = await CaptureRepliesAsync(fixture);
        await fixture.CaptureAsync(OtherThread, "Quote target", [new("200", 0, PostFieldPresence.All, Html: "Other thread")]);

        fixture.Run(page =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Window window = new() { Content = page, Width = 1320, Height = 760 };
            try
            {
                window.Show();
                ReaderArchiveTestFixture.Load(page, origin);
                ListBox list = Posts(window);
                ArchivePostRow parent = LoadTwoPages(page, window, list);
                string originalExcerpt = parent.Replies[0].ReplyExcerpt;
                string filteredExcerpt = parent.Replies[1].ReplyExcerpt;
                Assert.Contains(FilteredText, filteredExcerpt);

                Action? returnFromGallery = fromGallery ? page.CaptureGalleryOrigin() : null;
                ArchiveQuoteRow jump = new(new ArchiveQuote(new(Thread, "100"), new(OtherThread, "200"), ">>200", null));
                BooruComponentDispatcher.Wait(page.NavigateQuoteAsync(jump));
                Assert.Equal(OtherThread, page.CurrentThread?.Summary.Id);
                PostFilterRule rule = new(Guid.NewGuid(), true, PostFilterField.Text, PostFilterMatch.Contains,
                    FilteredText, PostFilterAction.Hide, false, null, null);
                page.PostFilter.Update(new(1, HiddenPostsAction.Stub, [rule], [], PostFilterStoreState.Ready));

                if (returnFromGallery != null) returnFromGallery();
                else page.GoHistoryBackByShortcut();
                BooruComponentDispatcher.Wait(page.WaitForReaderViewAsync());
                Assert.Equal(Thread, page.CurrentThread?.Summary.Id);
                Assert.Equal(-1, page.Projection!.PositionOf("102"));
                ArchivePostRow restored = Parent(list);
                Assert.NotSame(parent, restored);
                AssertTwoPages(restored);
                Assert.Equal(originalExcerpt, restored.Replies[0].ReplyExcerpt);
                Assert.Equal(Texts.T("postFilter.hiddenReply"), restored.Replies[1].ReplyExcerpt);

                page.PostFilter.Update(PostFilterSnapshot.Empty(PostFilterStoreState.Ready));
                page.RequestProjectionRebuild();
                BooruComponentDispatcher.Wait(page.WaitForReaderViewAsync());
                ArchivePostRow unfiltered = Parent(list);
                AssertTwoPages(unfiltered);
                Assert.Equal(filteredExcerpt, unfiltered.Replies[1].ReplyExcerpt);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public async Task ContinuingInTheSameRevisionKeepsBothLoadedReplyPages()
    {
        using ReaderArchiveTestFixture fixture = new();
        ArchivedThreadView origin = await CaptureRepliesAsync(fixture);
        fixture.Run(page =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Window window = new() { Content = page, Width = 1320, Height = 760 };
            try
            {
                window.Show();
                page.IsAvailable = true;
                ReaderArchiveTestFixture.Load(page, origin);
                ListBox list = Posts(window);
                ArchivePostRow parent = LoadTwoPages(page, window, list);
                BooruComponentDispatcher.Wait(page.ResumeReadingPositionAsync(new("4chan", "g", "100", "100", 0, "100")));
                BooruComponentDispatcher.Wait(page.WaitForReaderViewAsync());

                Assert.Equal(origin.Summary.Revision, page.CurrentThread?.Summary.Revision);
                ArchivePostRow restored = Parent(list);
                Assert.NotSame(parent, restored);
                AssertTwoPages(restored);
                Assert.Equal(parent.Replies.ToArray(), restored.Replies.ToArray());
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ContinuingRefreshesReplyPagesAfterACaptureInAnotherThread(int originalReplyCount)
    {
        using ReaderArchiveTestFixture fixture = new();
        ArchivedThreadView origin = await CaptureRepliesAsync(fixture, originalReplyCount);
        ResourceId earlierThread = new("4chan", "g", "10");
        await fixture.CaptureAsync(earlierThread, "Earlier thread", [new("10", 0, PostFieldPresence.All, Html: "Another opening post")]);

        fixture.Run(page =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Window window = new() { Content = page, Width = 1320, Height = 760 };
            try
            {
                window.Show();
                page.IsAvailable = true;
                ReaderArchiveTestFixture.Load(page, origin);
                ListBox list = Posts(window);
                ArchivePostRow parent = LoadTwoPages(page, window, list, originalReplyCount);

                BooruComponentDispatcher.Wait(fixture.CaptureAsync(earlierThread, "Earlier thread",
                [
                    new("110", 1, PostFieldPresence.All,
                        Html: "<a href='/g/thread/100#p100' class='quotelink'>&gt;&gt;100</a> New cross-thread reply")
                ]));
                BooruComponentDispatcher.Wait(page.ResumeReadingPositionAsync(new("4chan", "g", "100", "100", 0, "100")));
                BooruComponentDispatcher.Wait(page.WaitForReaderViewAsync());

                Assert.Equal(Thread, page.CurrentThread?.Summary.Id);
                Assert.Equal(origin.Summary.Revision, page.CurrentThread?.Summary.Revision);
                Assert.Equal(origin.PostCount, page.CurrentThread?.PostCount);
                ArchivePostRow restored = Parent(list);
                Assert.NotSame(parent, restored);
                Assert.True(restored.RepliesExpanded);
                Assert.Equal(originalReplyCount + 1, restored.BacklinkCount);
                Assert.Equal(1, restored.RepliesOffset);
                ArchivePostId[] expected = [new(earlierThread, "110"), new(Thread, "101"), new(Thread, "102")];
                Assert.Equal(expected[..2], restored.Replies.Select(reply => reply.Quote.Origin));
                Assert.True(restored.HasMoreReplies);

                LoadNextReplyPage(window, restored);
                Assert.Equal(expected, restored.Replies.Select(reply => reply.Quote.Origin));
                Assert.Equal(2, restored.RepliesOffset);
                Assert.Equal(originalReplyCount > 2, restored.HasMoreReplies);
            }
            finally { window.Close(); }
        });
    }

    private static Task<ArchivedThreadView> CaptureRepliesAsync(ReaderArchiveTestFixture fixture, int replyCount = 3)
    {
        CapturedPost[] posts =
        [
            new("100", 0, PostFieldPresence.All, Html: "Parent post"),
            new("101", 1, PostFieldPresence.All, Html: Reply("Visible reply")),
            new("102", 2, PostFieldPresence.All, Html: Reply(FilteredText)),
            new("103", 3, PostFieldPresence.All, Html: Reply("A third reply"))
        ];
        return fixture.CaptureAsync(Thread, "Reply pages", posts[..(replyCount + 1)]);
    }

    private static string Reply(string text) => $"<p><a href=\"#p100\" class=\"quotelink\">&gt;&gt;100</a> {text}</p>";

    private static ListBox Posts(Window window)
    {
        ListBox? list = null;
        BooruComponentDispatcher.WaitUntil(() =>
        {
            window.UpdateLayout();
            list = window.GetVisualDescendants().OfType<ListBox>()
                .SingleOrDefault(control => AutomationProperties.GetAutomationId(control) == "ArchivePosts");
            return list?.ItemsSource is ReaderPostRows;
        }, "the reader list after layout");
        return list!;
    }

    private static ArchivePostRow Parent(ListBox list) =>
        list.Items.OfType<ArchivePostRow>().Single(row => row.Id == "100");

    private static ArchivePostRow LoadTwoPages(ArchivePage page, Window window, ListBox list, int totalReplies = 3)
    {
        page.InlineReplyPageSize = ReaderDisplayLimits.ReplyStepMinimum;
        ArchivePostRow parent = Parent(list);
        BooruComponentDispatcher.Wait(page.SetRepliesExpandedAsync(parent, true));
        Assert.Equal("101", Assert.Single(parent.Replies).Quote.Origin.PostId);
        LoadNextReplyPage(window, parent);
        AssertTwoPages(parent, totalReplies);
        return parent;
    }

    private static void LoadNextReplyPage(Window window, ArchivePostRow parent)
    {
        int expected = parent.Replies.Count + 1;
        BooruComponentDispatcher.WaitUntil(() => FindMore() != null, "the More replies button for the parent post");
        FindMore()!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        BooruComponentDispatcher.WaitUntil(() => parent.Replies.Count == expected, "the next reply page");

        Button? FindMore()
        {
            window.UpdateLayout();
            return window.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(button => button.IsEffectivelyVisible && ReferenceEquals(button.DataContext, parent)
                    && AutomationProperties.GetAutomationId(button) == "ReaderMoreReplies");
        }
    }

    private static void AssertTwoPages(ArchivePostRow parent, int totalReplies = 3)
    {
        Assert.True(parent.RepliesExpanded);
        Assert.Equal(totalReplies, parent.BacklinkCount);
        Assert.Equal(new[] { "101", "102" }, parent.Replies.Select(reply => reply.Quote.Origin.PostId));
        Assert.Equal(1, parent.RepliesOffset);
        Assert.Equal(totalReplies > 2, parent.HasMoreReplies);
    }
}
