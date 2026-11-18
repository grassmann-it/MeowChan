using MeowChan.Booru;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruWatcherTransitionCodeCatalogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 7, 0, 0, TimeSpan.Zero);
    private readonly string _previousLanguage = Texts.Language;

    public BooruWatcherTransitionCodeCatalogTests() => Texts.Initialize();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage(_previousLanguage); }

    [Theory]
    [InlineData("en", BooruCodes.WatcherNotResumable,
        "The provider cooldown cannot be applied to this watcher in its current state. The watcher is unchanged.")]
    [InlineData("de", BooruCodes.WatcherNotResumable,
        "Die Anbieterpause kann im aktuellen Zustand nicht für diese Suchbeobachtung übernommen werden. Die Suchbeobachtung ist unverändert.")]
    [InlineData("en", BooruCodes.CooldownNotActive,
        "This watcher has no active provider cooldown to reset. The watcher is unchanged.")]
    [InlineData("de", BooruCodes.CooldownNotActive,
        "Diese Suchbeobachtung hat keine aktive Anbieterpause zum Zurücksetzen. Die Suchbeobachtung ist unverändert.")]
    public void TransitionRejectionsHaveDedicatedEnglishAndGermanText(string language, string code, string expected)
    {
        string key = BooruCodes.Key(code);
        Assert.Equal(1, BooruCodes.All.Count(known => known == code));
        var entry = Assert.Single(BooruCodeCatalog.Entries, item => item.Key == key);
        Assert.DoesNotContain(BooruStatusCatalog.Entries, item => item.Key == key);
        Assert.Equal(expected, language == "en" ? entry.English : entry.German);
        Assert.Equal(expected, language == "en" ? BuiltInCatalog.English[key] : BuiltInCatalog.German[key]);

        Texts.SetLanguage(language);
        Assert.Equal(expected, BooruMessages.WatcherProblem(code));
        Assert.Equal(expected, BooruCodeTexts.Localized(code));
        Assert.NotEqual(BooruMessages.WatcherProblem("unlisted_transition"), BooruMessages.WatcherProblem(code));
        Assert.DoesNotContain("{", expected);
    }

    [Fact]
    public void LateCooldownRejectionsKeepTheExistingErrorOrCooldownInsteadOfResumingTheWatcher()
    {
        DanbooruWatcher checking = DanbooruWatcher.BeginCheck(CreateWatcher(), Now).Watcher;
        DanbooruWatcher error = DanbooruWatcher.ApplyError(checking, "provider_error", Now).Watcher;
        DanbooruWatcher cooldown = DanbooruWatcher.ApplyDeferred(checking, Now.AddMinutes(2), "provider-throttled", Now).Watcher;

        // PreserveProviderCooldown (not the explicit Resume command) produces watcher_not_resumable.
        // Its text must not claim that an error watcher can never be resumed by the user.
        foreach (DanbooruWatcher watcher in new[] { error, cooldown })
        {
            DanbooruWatcherTransition rejected = DanbooruWatcher.PreserveProviderCooldown(
                watcher, Now.AddMinutes(3), "provider-throttled", Now);
            AssertRejection(rejected, watcher, BooruCodes.WatcherNotResumable);
        }
    }

    [Fact]
    public void ResetWithoutAStoredCooldownIsARejectionNotARecovery()
    {
        DanbooruWatcher initial = CreateWatcher();
        foreach (DanbooruWatcherState state in Enum.GetValues<DanbooruWatcherState>())
        {
            DanbooruWatcher watcher = initial with { State = state, CooldownUntilUtc = null };
            AssertRejection(DanbooruWatcher.ResetProviderCooldown(watcher), watcher, BooruCodes.CooldownNotActive);
        }

        // A stale timestamp alone does not turn an Error watcher into a resettable Cooldown watcher.
        DanbooruWatcher errorWithFence = initial with
        {
            State = DanbooruWatcherState.Error,
            ErrorCode = "provider_error",
            CooldownUntilUtc = Now.AddMinutes(2)
        };
        AssertRejection(DanbooruWatcher.ResetProviderCooldown(errorWithFence), errorWithFence, BooruCodes.CooldownNotActive);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void UnknownTransitionCodesKeepTheirFailureIdentity(string language)
    {
        Texts.SetLanguage(language);
        const string unknown = "unlisted_transition";
        Assert.False(BooruCodes.IsKnown(unknown));
        Assert.Equal(Texts.T("booru.watcher.problem.unknown", unknown), BooruMessages.WatcherProblem(unknown));
        Assert.Equal(unknown, BooruCodeTexts.Localized(unknown));
        Assert.Equal(Texts.T("booru.failure.unknown", unknown), BooruMessages.Failure(unknown));
    }

    [Fact]
    public void ExistingResetBoundaryCodesHaveOnlyOneCatalogEntry()
    {
        foreach (string code in new[]
        {
            BooruCodes.ProviderCooldownUnavailable, BooruCodes.TransportResetFailed, BooruCodes.TransportResetUnavailable
        })
        {
            string key = BooruCodes.Key(code);
            Assert.Equal(1, BooruCodes.All.Count(known => known == code));
            Assert.Single(BooruCodeCatalog.Entries.Concat(BooruStatusCatalog.Entries), entry => entry.Key == key);
        }
    }

    private static void AssertRejection(DanbooruWatcherTransition result, DanbooruWatcher original, string code)
    {
        Assert.False(result.Accepted);
        Assert.Equal(code, result.RejectionCode);
        Assert.Same(original, result.Watcher);
        Assert.Null(result.RequestedPage);
        Assert.Empty(result.AddedPostIds);
        Assert.True(BooruCodes.IsKnown(result.RejectionCode!));
    }

    private static DanbooruWatcher CreateWatcher()
    {
        Assert.True(DanbooruWatcher.TryCreate("catalog-contract-watcher", "cat", Now, out DanbooruWatcher? watcher, out _));
        return watcher!;
    }
}
