using System.Collections.Concurrent;
using System.Net;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Boards.FourChan;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.Tests;

public sealed class AdaptiveRecheckPolicyTests
{
    [Fact]
    public void OnlyPreviouslyUnseenLivePostsShortenTheInterval()
    {
        AdaptiveRecheckPolicy policy = new(new(15, 45, 100));
        CapturedPost opening = new("123", 0, PostFieldPresence.All);
        CapturedPost reply = new("124", 1, PostFieldPresence.All);
        policy.ObservePosts([opening, reply]);
        Assert.Equal(TimeSpan.FromSeconds(45), policy.AfterCheck(succeeded: true));

        policy.ObservePosts([opening with { Html = "Edited" }]);
        Assert.Equal(TimeSpan.FromSeconds(90), policy.AfterCheck(succeeded: true));
        Assert.Equal(TimeSpan.FromSeconds(100), policy.AfterCheck(succeeded: false));
        policy.ObservePosts([reply, new("125", 2, PostFieldPresence.IsDeleted, IsDeleted: true)]);
        Assert.Equal(TimeSpan.FromSeconds(100), policy.AfterCheck(succeeded: true));

        policy.ObservePosts([new("126", 3, PostFieldPresence.All)]);
        Assert.Equal(TimeSpan.FromSeconds(15), policy.AfterCheck(succeeded: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FourChanKeepsConditionalRequestsAndChangesOnlyThePollingDelay(bool adaptive)
    {
        using TempDirectory directory = new();
        DateTimeOffset modified = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        ConcurrentQueue<DateTimeOffset?> observedValidators = new();
        using DelegateHandler handler = new((request, _) =>
        {
            Assert.Equal("/g/thread/123.json", request.RequestUri!.AbsolutePath);
            observedValidators.Enqueue(request.Headers.IfModifiedSince);
            int check = observedValidators.Count;
            if (check is not (1 or 7)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(check == 1 ? """{"posts":[{"no":123}]}"""
                    : """{"posts":[{"no":123},{"no":124}]}""")
            };
            response.Content.Headers.LastModified = check == 1 ? modified : modified.AddSeconds(1);
            return Task.FromResult(response);
        });
        using StubImageBoard provider = new(handler);
        DownloadQueue queue = new(1, 1024 * 1024);
        if (adaptive) queue.AdaptiveRecheck = new(10, 60, 900);
        ClockedThread thread = new(provider.Board);
        try
        {
            thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(75));
            int[] expected = adaptive ? [60, 120, 240, 480, 900, 900, 10, 20] : [75, 75, 75, 75, 75, 75, 75, 75];
            for (int index = 0; index < expected.Length; index++)
            {
                await TestWait.UntilAsync(() => thread.Clock.Waits > index);
                TimeSpan interval = thread.Intervals.Last();
                Assert.InRange(interval, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15));
                Assert.Equal(TimeSpan.FromSeconds(expected[index]), interval);
                Assert.Null(thread.LastCheckFailure);
                Assert.Equal(index + 1, observedValidators.Count);
                if (index < expected.Length - 1) thread.Clock.Advance();
            }
            Assert.Equal(new DateTimeOffset?[] { null, modified, modified, modified, modified, modified, modified, modified.AddSeconds(1) },
                observedValidators.ToArray());

            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(75));
            await TestWait.UntilAsync(() => thread.Clock.Waits == expected.Length + 1);
            Assert.Equal(TimeSpan.FromSeconds(expected[0]), thread.Intervals.Last());
        }
        finally
        {
            await thread.StopMonitoringAsync().WaitAsync(TestWait.Timeout);
            await queue.StopAllAsync().WaitAsync(TestWait.Timeout);
        }
    }

    private sealed class ClockedThread(IBoard board) : FourChanThread(board, new Uri("https://boards.4chan.org/g/thread/123"))
    {
        internal AsyncStepGate Clock { get; } = new();
        internal ConcurrentQueue<TimeSpan> Intervals { get; } = new();

        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            Intervals.Enqueue(interval);
            return Clock.WaitAsync(cancellationToken);
        }
    }
}
