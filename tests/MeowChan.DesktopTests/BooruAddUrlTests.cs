using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

/// <summary>The one-shot a pasted Danbooru post address starts. Fake ports only; no network.</summary>
[Collection("Localization")]
public sealed class BooruAddUrlTests : IDisposable
{
    private readonly BooruUiHarness _h = new();

    public BooruAddUrlTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose() => Texts.SetLanguage("en");

    [BooruSharedFact]
    public async Task AvailableOriginalIsQueuedOnceAsAOneShotWithoutQueryOrWatcher()
    {
        BooruAddPostOutcome outcome = await BooruAddUrl.LoadPostAsync(_h.Services, "123", CancellationToken.None);

        Assert.Equal(MeowChan.AdmissionStatus.Added, outcome.Status);
        Assert.Contains("#123", outcome.Text);
        Assert.Equal(new[] { "123" }, _h.Search.PostReads);
        Assert.Empty(_h.Search.Searches);
        (string postId, BooruLoadOrigin origin) = Assert.Single(_h.Queue.Loads);
        Assert.Equal("123", postId);
        Assert.Equal(BooruLoadOrigin.OneShot, origin);
        Assert.Null(Assert.Single(_h.Queue.LoadQueries));
        Assert.Empty(_h.Watchers.Calls);
    }

    [BooruSharedTheory]
    [InlineData("AlreadyQueued")]
    [InlineData("AlreadyLoaded")]
    public async Task PostAlreadyInTheQueueOrLibraryIsReportedAsPresent(string name)
    {
        BooruLoadStatus status = Enum.Parse<BooruLoadStatus>(name);
        _h.Queue.OnLoad = (_, _) => Task.FromResult(new BooruLoadResult(status));
        BooruAddPostOutcome outcome = await BooruAddUrl.LoadPostAsync(_h.Services, "5", CancellationToken.None);
        Assert.Equal(MeowChan.AdmissionStatus.AlreadyPresent, outcome.Status);
    }

    [BooruSharedTheory]
    [InlineData("NotOffered")]
    [InlineData("Restricted")]
    public async Task PostWithoutAUsableOriginalNeverReachesTheQueue(string name)
    {
        BooruOriginalAccess access = Enum.Parse<BooruOriginalAccess>(name);
        _h.Search.OnPost = (id, _) => Task.FromResult(BooruCallResult<BooruPostView>.Ok(BooruUiFixtures.Post(id, access)));
        BooruAddPostOutcome outcome = await BooruAddUrl.LoadPostAsync(_h.Services, "8", CancellationToken.None);
        Assert.Null(outcome.Status);
        Assert.Empty(_h.Queue.Loads);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Text));
    }

    [BooruSharedTheory]
    [InlineData("NotFound")]
    [InlineData("Offline")]
    [InlineData("Denied")]
    [InlineData("Unavailable")]
    [InlineData("Failed")]
    public async Task ReadFailureIsAFeedbackLineAndQueuesNothing(string name)
    {
        BooruCallStatus status = Enum.Parse<BooruCallStatus>(name);
        _h.Search.OnPost = (_, _) => Task.FromResult(BooruCallResult<BooruPostView>.Of(status, "x"));
        BooruAddPostOutcome outcome = await BooruAddUrl.LoadPostAsync(_h.Services, "9", CancellationToken.None);
        Assert.Null(outcome.Status);
        Assert.Contains("#9", outcome.Text);
        Assert.Empty(_h.Queue.Loads);
    }

    [BooruSharedFact]
    public async Task AnswerForAnotherPostIsRefused()
    {
        _h.Search.OnPost = (_, _) => Task.FromResult(BooruCallResult<BooruPostView>.Ok(BooruUiFixtures.Post("999")));
        BooruAddPostOutcome outcome = await BooruAddUrl.LoadPostAsync(_h.Services, "10", CancellationToken.None);
        Assert.Null(outcome.Status);
        Assert.Empty(_h.Queue.Loads);
    }

    [BooruSharedFact]
    public async Task QueueFailureAndExceptionsBecomeSentencesNotThrows()
    {
        _h.Queue.OnLoad = (_, _) => Task.FromResult(new BooruLoadResult(BooruLoadStatus.Offline));
        Assert.Null((await BooruAddUrl.LoadPostAsync(_h.Services, "11", CancellationToken.None)).Status);

        _h.Queue.OnLoad = (_, _) => throw new InvalidOperationException("boom");
        Assert.Null((await BooruAddUrl.LoadPostAsync(_h.Services, "12", CancellationToken.None)).Status);
    }

    [BooruSharedFact]
    public async Task CancellationPropagates()
    {
        using CancellationTokenSource cancel = new();
        _h.Search.OnPost = (_, token) => Task.FromCanceled<BooruCallResult<BooruPostView>>(new CancellationToken(true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BooruAddUrl.LoadPostAsync(_h.Services, "13", cancel.Token));
    }

    [BooruSharedFact]
    public void SafebooruPostLinkIsAcceptedByTheWindowInputAsTheSamePost()
    {
        BooruParsedInput parsed = BooruInput.Parse(BooruInputMode.Post, "https://safebooru.donmai.us/posts/321");
        Assert.Equal(BooruInputKind.Post, parsed.Kind);
        Assert.Equal("321", parsed.PostId);
        // A query on a post link stays refused there, as before.
        Assert.Equal(BooruInputKind.Invalid, BooruInput.Parse(BooruInputMode.Post, "https://safebooru.donmai.us/posts/321?q=cat").Kind);
    }
}
