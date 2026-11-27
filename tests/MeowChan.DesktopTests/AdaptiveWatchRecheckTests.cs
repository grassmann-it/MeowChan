using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MeowChan.Boards.Base;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class AdaptiveWatchRecheckTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private const string ThreadUrl = "https://boards.4chan.org/g/thread/123";

    [Fact]
    public async Task ADesktopWatchUsesAdaptiveDefaultsAndCanSwitchToItsFixedProfileInterval()
    {
        using var profile = new TempDirectory();
        string media = Path.Combine(profile.Path, "media");
        var config = new Config { AutomaticUpdateChecks = false, DownloadPath = media, RecheckTimer = 75 };
        config.SaveConfig(profile.Path);
        string settingsPath = Path.Combine(profile.Path, "settings.json");
        JsonObject legacySettings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        legacySettings.Remove(nameof(Config.AdaptiveRecheckEnabled));
        legacySettings.Remove(nameof(Config.AdaptiveRecheckStartSeconds));
        legacySettings.Remove(nameof(Config.AdaptiveRecheckMinimumSeconds));
        legacySettings.Remove(nameof(Config.AdaptiveRecheckMaximumSeconds));
        File.WriteAllText(settingsPath, legacySettings.ToJsonString());
        var clock = new CheckClock();
        using var transport = new ThreadTransport(check => Posts(hasReply: check > 1));
        using var client = new HttpClient(transport);
        await using var host = new DesktopSessionHost(profile.Path, [new ClockedBoard(client, clock)]);
        await host.StartAsync().WaitAsync(Deadline);
        Assert.NotNull((await host.AddAsync(ThreadUrl).WaitAsync(Deadline)).WatchId);

        await clock.ExpectAsync(60);
        clock.Advance();
        await clock.ExpectAsync(10);

        Config fixedSettings = host.Config.Clone();
        fixedSettings.AdaptiveRecheckEnabled = false;
        await host.ApplySettingsAsync(fixedSettings).WaitAsync(Deadline);
        clock.Advance();
        await clock.ExpectAsync(75);
        clock.Advance();
        await clock.ExpectAsync(75);
        Assert.Equal(4, transport.ThreadChecks);
    }

    [Fact]
    public async Task ThreadErrorsBackOffAfterAReplyAndRecoveryResetsTheFailureDelay()
    {
        using var directory = new TempDirectory();
        var clock = new CheckClock();
        using var transport = new ThreadTransport(check => check switch
        {
            1 => Posts(hasReply: false),
            2 => Posts(hasReply: true),
            >= 3 and <= 10 or 13 => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            11 => throw new SourceCheckDeferredException(clock.GetUtcNow().AddMinutes(30), "retry_after",
                initialNoSend: false, new HttpRequestException("The source requested a longer cooldown.")),
            _ => new HttpResponseMessage(HttpStatusCode.NotModified)
        });
        using var client = new HttpClient(transport);
        var board = new ClockedBoard(client, clock);
        var thread = new ClockedThread(board.GetBoard(new Uri("https://boards.4chan.org/g/")), new Uri(ThreadUrl), clock);
        var queue = new DownloadQueue(1, 1024 * 1024, timeProvider: clock) { AdaptiveRecheck = new(10, 60, 900) };
        int[] expectedSeconds = [60, 10, 20, 40, 80, 160, 320, 640, 900, 900, 1800, 20, 40];
        try
        {
            thread.StartMonitoring(queue, directory.Path, TimeSpan.FromSeconds(75));
            for (int index = 0; index < expectedSeconds.Length; index++)
            {
                await clock.ExpectAsync(expectedSeconds[index]);
                Assert.Equal(index is >= 2 and <= 10 or 12, thread.LastCheckFailure != null);
                if (index < expectedSeconds.Length - 1) clock.Advance();
            }
            Assert.Equal(expectedSeconds.Length, transport.ThreadChecks);
        }
        finally
        {
            await thread.StopMonitoringAsync().WaitAsync(Deadline);
            await queue.StopAllAsync().WaitAsync(Deadline);
        }
    }

    [Theory]
    [InlineData("""{"AdaptiveRecheckMinimumSeconds":9}""")]
    [InlineData("""{"AdaptiveRecheckMinimumSeconds":61}""")]
    [InlineData("""{"AdaptiveRecheckMaximumSeconds":59}""")]
    [InlineData("""{"AdaptiveRecheckMaximumSeconds":901}""")]
    public void ProfileValidationRejectsUnorderedOrOutOfRangeIntervals(string json) =>
        Assert.Throws<ArgumentOutOfRangeException>(ReadConfig(json).Validate);

    [Fact]
    public void SettingsEditorValidatesPersistsAndResetsTheAdaptiveSchedule() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using var profile = new TempDirectory();
        Config config = new() { DownloadPath = Path.Combine(profile.Path, "media") };
        Config? saved = null;
        var editor = new SettingsEditor(config, candidate =>
        {
            candidate.SaveConfig(profile.Path);
            saved = candidate;
            return Task.CompletedTask;
        }) { IsAvailable = true };
        try
        {
            ToggleSwitch enabled = Find<ToggleSwitch>(editor, "AdaptiveRecheckEnabledInput");
            NumericUpDown start = Find<NumericUpDown>(editor, "AdaptiveRecheckStartSecondsInput");
            NumericUpDown min = Find<NumericUpDown>(editor, "AdaptiveRecheckMinimumSecondsInput");
            NumericUpDown max = Find<NumericUpDown>(editor, "AdaptiveRecheckMaximumSecondsInput");
            Button save = Find<Button>(editor, "SaveSettingsButton");
            Assert.True(enabled.IsChecked);
            Assert.Equal((60m, 10m, 900m), (start.Value, min.Value, max.Value));
            min.Value = 70;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(editor.HasError);
            Assert.Null(saved);

            start.Value = 80;
            max.Value = 400;
            enabled.IsChecked = false;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => !editor.IsBusy, "adaptive settings save"));
            Assert.False(editor.HasError, editor.ErrorMessage);
            Assert.NotNull(saved);
            Config stored = Config.LoadConfig(profile.Path);
            Assert.False(stored.AdaptiveRecheckEnabled);
            Assert.Equal((80, 70, 400), (stored.AdaptiveRecheckStartSeconds,
                stored.AdaptiveRecheckMinimumSeconds, stored.AdaptiveRecheckMaximumSeconds));
            Config portable = ReadConfig(SettingsTransfer.Export(stored, "1.2.0").ToJsonString());
            Assert.False(portable.AdaptiveRecheckEnabled);
            Assert.Equal((80, 70, 400), (portable.AdaptiveRecheckStartSeconds,
                portable.AdaptiveRecheckMinimumSeconds, portable.AdaptiveRecheckMaximumSeconds));

            editor.ResetDraft(SettingsEditor.SettingsResetScope.Downloads);
            Assert.True(enabled.IsChecked);
            Assert.Equal((60m, 10m, 900m), (start.Value, min.Value, max.Value));
        }
        finally { editor.EndLifetime(); }
    });

    private static Config ReadConfig(string json) => JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Config)!;

    private static T Find<T>(SettingsEditor editor, string id) where T : Control =>
        Assert.Single(editor.GetLogicalDescendants().OfType<T>(),
            control => ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId() == id);

    private static HttpResponseMessage Posts(bool hasReply) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(hasReply ? """{"posts":[{"no":123},{"no":124}]}""" : """{"posts":[{"no":123}]}""")
    };

    private sealed class ThreadTransport(Func<int, HttpResponseMessage> next) : HttpMessageHandler
    {
        internal int ThreadChecks { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/boards.json")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"boards":[{"board":"g","title":"Technology"}]}""")
                });
            Assert.Equal("/g/thread/123.json", request.RequestUri.AbsolutePath);
            return Task.FromResult(next(++ThreadChecks));
        }
    }

    private sealed class ClockedBoard(HttpClient client, CheckClock clock) : FourChanImageBoard(client)
    {
        public override IThread GetThread(Uri threadUri) => new ClockedThread(
            GetBoard(new Uri("https://boards.4chan.org/g/")), threadUri, clock);
    }

    private sealed class ClockedThread(IBoard board, Uri threadUri, CheckClock clock) : FourChanThread(board, threadUri)
    {
        protected override Task WaitForNextCheckAsync(TimeSpan interval, CancellationToken cancellationToken) =>
            clock.WaitAsync(interval, cancellationToken);
    }

    private sealed class CheckClock : TimeProvider
    {
        private readonly Channel<(TimeSpan Delay, TaskCompletionSource Release)> _waits =
            Channel.CreateUnbounded<(TimeSpan, TaskCompletionSource)>();
        private (TimeSpan Delay, TaskCompletionSource Release) _current;
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        internal Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(_waits.Writer.TryWrite((delay, release)));
            return release.Task.WaitAsync(cancellationToken);
        }

        internal async Task ExpectAsync(int seconds)
        {
            _current = await _waits.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
            Assert.Equal(TimeSpan.FromSeconds(seconds), _current.Delay);
        }

        internal void Advance()
        {
            _now += _current.Delay;
            _current.Release.SetResult();
        }
    }
}
