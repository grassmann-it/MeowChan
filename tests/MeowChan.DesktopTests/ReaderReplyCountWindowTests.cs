using Avalonia;
using Avalonia.Controls;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class ReaderReplyCountWindowTests
{
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
