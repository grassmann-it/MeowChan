using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Automation;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

/// <summary>When a found update is announced: fake release source only, no network.</summary>
[Collection("Localization")]
public sealed class UpdateNotificationTests
{
    private static UpdateInfo Info(string version) => new(SemanticVersion.Parse(version),
        new Uri("https://github.com/grassmann-it/MeowChan/releases/tag/v" + version),
        new Uri("https://github.com/grassmann-it/MeowChan/releases/tag/v" + version));

    [Fact]
    public void NewVersionIsAnnouncedOncePerAppStartAndLaterHoldsUntilTheNextStart()
    {
        UpdateNotifier notifier = new();
        UpdateView view = new(Update: Info("2.0.0"));
        Assert.Equal("2.0.0", notifier.Evaluate(view, automaticChecks: true, informationAllowed: true)?.Version.ToString());
        // Every later refresh (language change, another check, "Later") stays quiet in this app start.
        Assert.Null(notifier.Evaluate(view, true, true));
        Assert.Null(notifier.Evaluate(view with { LastCheckedUtc = DateTimeOffset.UtcNow }, true, true));
        // A newer release found later is a different version and is announced.
        Assert.Equal("2.1.0", notifier.Evaluate(new UpdateView(Update: Info("2.1.0")), true, true)?.Version.ToString());
        // The next app start has a fresh notifier.
        Assert.NotNull(new UpdateNotifier().Evaluate(view, true, true));
    }

    [Fact]
    public void NothingIsAnnouncedWithoutUpdateOrWhenChecksAreDisabledOrNotificationsAreOff()
    {
        UpdateView found = new(Update: Info("2.0.0"));
        Assert.Null(new UpdateNotifier().Evaluate(new UpdateView(), true, true));
        Assert.Null(new UpdateNotifier().Evaluate(found with { EnvironmentDisabled = true }, true, true));
        Assert.Null(new UpdateNotifier().Evaluate(found, automaticChecks: false, informationAllowed: true));
        UpdateNotifier off = new();
        Assert.Null(off.Evaluate(found, true, informationAllowed: false));
        // Turning the notifications on later still announces it: the refusal did not use up the one toast.
        Assert.NotNull(off.Evaluate(found, true, true));
    }

    [Fact]
    public void ManualCheckAnnouncesEvenWithAutomaticChecksOff()
    {
        UpdateInfo update = Info("2.0.0");
        UpdateView manual = new(Update: update, ManualResult: new UpdateCheckResult(UpdateCheckStatus.Available, update));
        Assert.NotNull(new UpdateNotifier().Evaluate(manual, automaticChecks: false, informationAllowed: true));
    }

    [Fact]
    public async Task SkippedVersionIsSilentAndANewerVersionShowsAgain()
    {
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-update-notify-" + Guid.NewGuid().ToString("N"));
        Handler handler = new() { Body = Release("2.0.0") };
        try
        {
            UpdateNotifier notifier = new();
            await using (UpdateSession first = new(profile, new Config(), new HttpClient(handler), new UpdateServiceTests.FakeTime(), () => null, SemanticVersion.Parse("1.0.0")))
            {
                await first.CheckAsync(UpdateCheckKind.Automatic);
                Assert.Equal("2.0.0", notifier.Evaluate(first.View, true, true)?.Version.ToString());
                // A skip names the version: a different one is left alone.
                await first.SkipAsync(SemanticVersion.Parse("1.5.0"));
                Assert.NotNull(first.View.Update);
                await first.SkipAsync(SemanticVersion.Parse("2.0.0"));
                Assert.Null(first.View.Update);
                Assert.Null(notifier.Evaluate(first.View, true, true));
            }
            // After a restart the skipped version stays silent: no toast, no indicator.
            handler.Body = Release("2.0.0");
            await using (UpdateSession restarted = new(profile, new Config(), new HttpClient(handler), new UpdateServiceTests.FakeTime(), () => null, SemanticVersion.Parse("1.0.0")))
            {
                await restarted.CheckAsync(UpdateCheckKind.Automatic);
                Assert.Null(restarted.View.Update);
                Assert.Null(new UpdateNotifier().Evaluate(restarted.View, true, true));
                // A newer release than the skipped one is shown again.
                handler.Body = Release("2.2.0");
                await restarted.CheckAsync();
                UpdateInfo shown = Assert.IsType<UpdateInfo>(new UpdateNotifier().Evaluate(restarted.View, true, true));
                Assert.Equal("2.2.0", shown.Version.ToString());
            }
        }
        finally { if (Directory.Exists(profile)) Directory.Delete(profile, true); }
    }

    [Fact]
    public async Task DistributionOverrideAndAutomaticChecksOffProduceNoToastAndNoIndicator()
    {
        string profile = Path.Combine(Path.GetTempPath(), "meowchan-update-notify-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using UpdateSession disabled = new(profile, new Config(), new HttpClient(new Handler { Body = Release("2.0.0") }),
                new UpdateServiceTests.FakeTime(), () => "1", SemanticVersion.Parse("1.0.0"));
            await disabled.CheckAsync(UpdateCheckKind.Automatic);
            Assert.True(disabled.View.EnvironmentDisabled);
            Assert.Null(disabled.View.Update);
            Assert.Null(new UpdateNotifier().Evaluate(disabled.View, true, true));
            await using UpdateSession off = new(profile + "-off", new Config { AutomaticUpdateChecks = false },
                new HttpClient(new Handler { Body = Release("2.0.0") }), new UpdateServiceTests.FakeTime(), () => null, SemanticVersion.Parse("1.0.0"));
            await off.CheckAsync(UpdateCheckKind.Automatic);
            Assert.Null(off.View.Update);
            Assert.Null(new UpdateNotifier().Evaluate(off.View, false, true));
        }
        finally
        {
            foreach (string path in new[] { profile, profile + "-off" }) if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Theory]
    [InlineData("en", "MeowChan 2.0.0 is available", "Release notes", "Later", "Skip this version")]
    [InlineData("de", "MeowChan 2.0.0 ist verfügbar", "Versionshinweise", "Später", "Diese Version überspringen")]
    public async Task ToastHasTitleThreeNamedActionsAndRunsExactlyOne(string language, string title, string notes, string later, string skip)
    {
        string previous = Texts.Language;
        try
        {
            Texts.SetLanguage(language);
            Assert.Equal(title, Texts.T("updates.toastTitle", SemanticVersion.Parse("2.0.0")));
            List<string> ran = new();
            int closes = 0;
            FeedbackToastContent toast = new(Texts.T("updates.toastTitle", SemanticVersion.Parse("2.0.0")), Texts.T("updates.toastMessage"), null,
                _ => closes++, new[]
                {
                    new FeedbackToastAction(Texts.T("updates.toastNotes"), () => { ran.Add("notes"); return Task.CompletedTask; }),
                    new FeedbackToastAction(Texts.T("updates.toastLater"), () => { ran.Add("later"); return Task.CompletedTask; }),
                    new FeedbackToastAction(Texts.T("updates.skip"), () => { ran.Add("skip"); return Task.CompletedTask; })
                });
            Assert.Equal(new[] { notes, later, skip }, toast.ActionButtons.Select(button => (string)button.Content!).ToArray());
            Assert.All(toast.ActionButtons, button => Assert.Equal((string)button.Content!, AutomationProperties.GetName(button)));
            Assert.Equal(FeedbackToastContent.AccessibleName(title, Texts.T("updates.toastMessage")), AutomationProperties.GetName(toast));
            Assert.Equal(3, toast.ActionButtons.Select(button => button.Name).Distinct().Count());
            await toast.RunActionAsync(1);
            await toast.RunActionAsync(2); // The toast is done after one choice.
            Assert.Equal(new[] { "later" }, ran);
            Assert.Equal(1, closes);
            Assert.All(toast.ActionButtons, button => Assert.False(button.IsEnabled));
        }
        finally { Texts.SetLanguage(previous); }
    }

    private static string Release(string version) => JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, html_url = "https://github.com/grassmann-it/MeowChan/releases/tag/v" + version,
        draft = false, prerelease = false, assets = Array.Empty<object>()
    });

    private sealed class Handler : HttpMessageHandler
    {
        internal string Body = Release("2.0.0");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
    }
}
