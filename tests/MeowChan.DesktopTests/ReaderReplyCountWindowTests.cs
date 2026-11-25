using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderReplyCountWindowTests
{
    [Fact]
    public async Task ACountPublishedDuringAFailedCaptureWriteStillOpensTheFirstReplyPage()
    {
        ResourceId thread = new("4chan", "g", "100");
        using ReaderArchiveTestFixture fixture = new();
        ArchivedThreadView view = await fixture.CaptureAsync(thread, "Reply count during a capture write",
        [
            new("100", 0, PostFieldPresence.All, Html: "Parent post"),
            new("101", 1, PostFieldPresence.All, Html: "<a href=\"#p100\" class=\"quotelink\">&gt;&gt;100</a> Captured reply.")
        ]);

        TaskCompletionSource write = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool writing = false;
        fixture.Run(page =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Window window = new() { Content = page, Width = 1320, Height = 760 };
            TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource publish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            page.ReaderExpandRepliesPreference = true;
            page.BeforeInlineReplyCountsPublicationAsync = (_, ids, token) =>
            {
                if (!ids.Contains("100")) return Task.CompletedTask;
                requested.TrySetResult();
                return publish.Task.WaitAsync(token);
            };
            Task capture = Task.CompletedTask;
            try
            {
                page.IsAvailable = true;
                window.Show();
                ReaderArchiveTestFixture.Load(page, view);
                BooruComponentDispatcher.WaitUntil(() =>
                {
                    window.UpdateLayout();
                    return requested.Task.IsCompleted;
                }, "the held reply count");
                ListBox list = window.GetVisualDescendants().OfType<ListBox>()
                    .Single(control => AutomationProperties.GetAutomationId(control) == "ArchivePosts");
                ArchivePostRow parent = list.Items.OfType<ArchivePostRow>().Single(row => row.Id == "100");
                Assert.NotNull(list.ContainerFromItem(parent));
                // The preference applies to the selected post (F11b).
                list.SelectedItem = parent;
                Assert.Null(parent.BacklinkCount);
                capture = page.ToggleThreadCaptureAsync();
                Assert.True(writing);
                Assert.True(page.IsBusy);

                publish.SetResult();
                BooruComponentDispatcher.Wait(page.InlineReplyCountsTask);
                Assert.True(page.IsBusy);
                Assert.Equal(1, parent.BacklinkCount);
                Assert.True(parent.RepliesExpanded);
                write.SetException(new IOException("Fixture capture write failed."));
                BooruComponentDispatcher.Wait(capture);

                Assert.False(page.IsBusy);
                Assert.Same(parent, list.Items[0]);
                BooruComponentDispatcher.WaitUntil(() => parent.RepliesLoaded && !parent.RepliesLoading,
                    "the first automatic reply page after the capture write failure");
                Assert.Equal("101", Assert.Single(parent.Replies).Quote.Origin.PostId);
            }
            finally
            {
                publish.TrySetResult();
                write.TrySetCanceled();
                BooruComponentDispatcher.Wait(capture);
                BooruComponentDispatcher.Wait(page.InlineReplyCountsTask);
                window.Close();
            }
        }, setCapture: (_, _) =>
        {
            writing = true;
            return write.Task;
        });
    }

    [Fact]
    public async Task ReplyCountsAreRequestedAndPublishedOnlyForTheViewportAndPrefetch()
    {
        const int postCount = 65;
        const int prefetch = 3;
        ResourceId thread = new("4chan", "g", "100");
        string[] ids = Enumerable.Range(100, postCount).Select(number => number.ToString()).ToArray();
        CapturedPost[] posts = ids.Select((id, index) =>
        {
            string target = ids[(index + postCount - 1) % postCount];
            return new CapturedPost(id, index, PostFieldPresence.All,
                Html: $"<p><a href=\"#p{target}\" class=\"quotelink\">&gt;&gt;{target}</a> Post {id}.</p>");
        }).ToArray();
        using ReaderArchiveTestFixture fixture = new();
        ArchivedThreadView view = await fixture.CaptureAsync(thread, "Reply count window", posts);

        fixture.Run(page =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            Window window = new() { Content = page, Width = 1320, Height = 560 };
            TaskCompletionSource publish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            string[]? requested = null;
            page.ReaderPreviewPrefetchPosts = prefetch;
            page.BeforeInlineReplyCountsPublicationAsync = (_, batch, token) =>
            {
                requested ??= batch.ToArray();
                return publish.Task.WaitAsync(token);
            };
            try
            {
                window.Show();
                ReaderArchiveTestFixture.Load(page, view);
                BooruComponentDispatcher.WaitUntil(() =>
                {
                    window.UpdateLayout();
                    return requested != null;
                }, "the initial reply count request");

                int[] visible = list.GetRealizedContainers()
                    .Where(card => card.IsVisible && card.TranslatePoint(default, list) is { } top
                        && top.Y < list.Bounds.Height && top.Y + card.Bounds.Height > 0)
                    .Select(list.IndexFromContainer).Where(index => index >= 0).Order().ToArray();
                Assert.NotEmpty(visible);
                Assert.Equal(0, visible[0]);
                int end = visible[^1] + 1 + prefetch;
                Assert.InRange(end, 1, postCount - 1);
                Assert.Equal(ids[..end], requested);

                publish.SetResult();
                BooruComponentDispatcher.Wait(page.InlineReplyCountsTask);
                ReaderPostRows rows = Assert.IsType<ReaderPostRows>(list.ItemsSource);
                for (int index = 0; index < end; index++)
                    Assert.Equal(1, rows[index].BacklinkCount);
                Assert.Null(rows[64].BacklinkCount);
            }
            finally
            {
                page.BeforeInlineReplyCountsPublicationAsync = null;
                publish.TrySetResult();
                window.Close();
                BooruComponentDispatcher.Wait(page.InlineReplyCountsTask);
            }
        });
    }
}
