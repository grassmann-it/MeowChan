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
                AssertTwoPages(restored);
                Assert.Equal(parent.Replies.ToArray(), restored.Replies.ToArray());
            }
            finally { window.Close(); }
        });
    }

    private static Task<ArchivedThreadView> CaptureRepliesAsync(ReaderArchiveTestFixture fixture) =>
        fixture.CaptureAsync(Thread, "Reply pages",
        [
            new("100", 0, PostFieldPresence.All, Html: "Parent post"),
            new("101", 1, PostFieldPresence.All, Html: Reply("Visible reply")),
            new("102", 2, PostFieldPresence.All, Html: Reply(FilteredText)),
            new("103", 3, PostFieldPresence.All, Html: Reply("A third reply"))
        ]);

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

    private static ArchivePostRow LoadTwoPages(ArchivePage page, Window window, ListBox list)
    {
        page.InlineReplyPageSize = ReaderDisplayLimits.ReplyStepMinimum;
        ArchivePostRow parent = Parent(list);
        BooruComponentDispatcher.Wait(page.SetRepliesExpandedAsync(parent, true));
        Assert.Equal("101", Assert.Single(parent.Replies).Quote.Origin.PostId);
        BooruComponentDispatcher.WaitUntil(() => FindMore() != null, "the first page's More replies button");
        FindMore()!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        BooruComponentDispatcher.WaitUntil(() => parent.Replies.Count == 2, "the second reply page");
        AssertTwoPages(parent);
        return parent;

        Button? FindMore()
        {
            window.UpdateLayout();
            return window.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(button => button.IsEffectivelyVisible && AutomationProperties.GetAutomationId(button) == "ReaderMoreReplies");
        }
    }

    private static void AssertTwoPages(ArchivePostRow parent)
    {
        Assert.True(parent.RepliesExpanded);
        Assert.Equal(3, parent.BacklinkCount);
        Assert.Equal(new[] { "101", "102" }, parent.Replies.Select(reply => reply.Quote.Origin.PostId));
        Assert.Equal(1, parent.RepliesOffset);
        Assert.True(parent.HasMoreReplies);
    }
}
