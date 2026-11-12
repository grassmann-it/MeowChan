using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// The only place that turns Danbooru UI state into localized text. Views call these methods again after
/// a language change, so no translated sentence is ever stored in a model.
/// </summary>
internal static class BooruText
{
    internal const string Prefix = "booru.ui.";

    /// <summary>
    /// Turns a stable machine code (host failure code, watcher error code) into the text shown inside the
    /// "({0})" of the failure sentences. The UI never shows a host or plugin diagnostic message, only a code.
    /// The default shows the code itself; the integration points it at the shared booru message mapping.
    /// </summary>
    internal static Func<string?, string> CodeText { get; set; } = code => string.IsNullOrWhiteSpace(code) ? "error" : code;

    private static string Code(string? code) => CodeText(code);

    internal static string Time(DateTimeOffset value) =>
        value.ToLocalTime().ToString("g", Texts.Culture);

    internal static string Time(DateTimeOffset? value, string fallbackKey) =>
        value is { } time ? Time(time) : Texts.T(fallbackKey);

    // ---- notices ----------------------------------------------------------------------------

    private static readonly HashSet<string> KnownRejections = new(StringComparer.Ordinal)
    {
        "watcher_removed", "watcher_paused", "check_already_running", "cooldown_active",
        "watcher_already_paused", "watcher_already_active"
    };

    internal static string Notice(BooruNotice notice)
    {
        string key = notice.Key;
        object?[] args = notice.Args.Select(arg => arg is DateTimeOffset time ? Time(time) : arg is null ? string.Empty : arg).ToArray();
        // Known domain rejections have their own sentence; unknown ones show their code.
        if (key == "booru.ui.watcher.reject" && args.Length == 1 && args[0] is string code && KnownRejections.Contains(code))
            return Texts.T(key + "." + code);
        if (key is "booru.ui.watcher.msg.failed" or "booru.ui.watcher.createFailed" or "booru.ui.watcher.reject"
            && args.Length == 1)
            args = new object?[] { Code(args[0] as string) };
        return Texts.T(key, args);
    }

    // ---- search status -----------------------------------------------------------------------

    /// <summary>The sentence for the current search state; <paramref name="paused"/> is the clock-evaluated pause.</summary>
    internal static string SearchStatus(BooruSearchView view, bool paused)
    {
        string query = view.ExecutedQuery ?? string.Empty;
        return view.Status switch
        {
            BooruSearchStatus.Idle => Texts.T(view.Mode == BooruInputMode.Post ? "booru.ui.status.idlePost" : "booru.ui.status.idle"),
            BooruSearchStatus.Searching => view.IsPostResult
                ? Texts.T("booru.ui.status.lookingUp", view.ExecutedPostId)
                : Texts.T("booru.ui.status.searching", query),
            BooruSearchStatus.Results => view.IsPostResult
                ? Texts.T("booru.ui.status.post", view.ExecutedPostId)
                : Texts.T("booru.ui.status.results", view.Rows.Length, view.Page, query),
            BooruSearchStatus.Empty => Texts.T("booru.ui.status.empty", query, view.Page),
            BooruSearchStatus.Offline => Texts.T("booru.ui.status.offline"),
            BooruSearchStatus.RateLimited => paused && view.RetryAt is { } until
                ? Texts.T("booru.ui.status.rateLimited", Time(until)) : Texts.T("booru.ui.status.pauseOver"),
            BooruSearchStatus.Cooldown => paused && view.RetryAt is { } stop
                ? Texts.T("booru.ui.status.cooldown", Time(stop)) : Texts.T("booru.ui.status.pauseOver"),
            BooruSearchStatus.Denied => Texts.T("booru.ui.status.denied"),
            BooruSearchStatus.NotFound => Texts.T("booru.ui.status.notFound"),
            BooruSearchStatus.Unavailable => Texts.T("booru.ui.status.unavailable"),
            BooruSearchStatus.InvalidInput => Issue(view.Parsed.Issue),
            _ => Texts.T("booru.ui.status.failed", Code(view.Code))
        };
    }

    internal static string Issue(BooruInputIssue issue) => Texts.T(Prefix + "issue." + issue);

    /// <summary>True for the states that are a plain error/limit message rather than neutral progress.</summary>
    internal static bool IsProblem(BooruSearchStatus status) => status is not (BooruSearchStatus.Idle
        or BooruSearchStatus.Searching or BooruSearchStatus.Results);

    // ---- rows ------------------------------------------------------------------------------------

    internal static string Rating(BooruRating rating) => Texts.T(Prefix + "rating." + rating);

    internal static string TagCategory(BooruTagCategory category) => Texts.T(Prefix + "tagCategory." + category);

    internal static string Original(BooruOriginalAccess access, bool withdrawn) =>
        Texts.T(Prefix + "original." + (withdrawn ? "Withdrawn" : access.ToString()));

    /// <summary>The line under a result: the load state, or null while nothing happened yet.</summary>
    internal static string? Load(BooruResultRowView row) => row.Load switch
    {
        BooruRowLoad.Idle => null,
        BooruRowLoad.Failed => Texts.T(Prefix + "load.Failed", Code(row.LoadCode)),
        _ => Texts.T(Prefix + "load." + row.Load)
    };

    // ---- watchers -----------------------------------------------------------------------------------

    internal static string WatcherStatus(BooruWatcherRowView row, bool coolingDown) => row.Watcher.Status == BooruWatcherStatus.Cooldown && !coolingDown
        ? Texts.T(Prefix + "watcher.status.CooldownOver")
        : Texts.T(Prefix + "watcher.status." + row.Watcher.Status);

    internal static string WatcherBlock(BooruWatcherBlock block, DateTimeOffset? cooldownUntil) => block switch
    {
        BooruWatcherBlock.None => string.Empty,
        BooruWatcherBlock.CoolingDown => Texts.T(Prefix + "watcher.why.CoolingDown", cooldownUntil is { } until ? Time(until) : string.Empty),
        _ => Texts.T(Prefix + "watcher.why." + block)
    };

    // ---- completeness (tests) -----------------------------------------------------------------------------

    /// <summary>Every key the Danbooru UI can request, including the enum-derived and model-notice ones.</summary>
    internal static IEnumerable<string> RequiredKeys()
    {
        foreach (string key in new[]
        {
            "booru.ui.title", "booru.ui.command.open", "booru.ui.intro", "booru.ui.provider", "booru.ui.noFilterNote",
            "booru.ui.tab.search", "booru.ui.tab.watchers", "booru.ui.mode.label", "booru.ui.mode.tags", "booru.ui.mode.post",
            "booru.ui.input.label", "booru.ui.input.placeholder.tags", "booru.ui.input.placeholder.post", "booru.ui.search",
            "booru.ui.lookup", "booru.ui.cancel", "booru.ui.query.sent", "booru.ui.query.hint", "booru.ui.post.sent",
            "booru.ui.page.info", "booru.ui.page.previous", "booru.ui.page.next", "booru.ui.page.limit",
            "booru.ui.status.idle", "booru.ui.status.idlePost", "booru.ui.status.searching", "booru.ui.status.lookingUp",
            "booru.ui.status.results", "booru.ui.status.post", "booru.ui.status.empty", "booru.ui.status.offline",
            "booru.ui.status.rateLimited", "booru.ui.status.cooldown", "booru.ui.status.pauseOver", "booru.ui.status.denied",
            "booru.ui.status.notFound", "booru.ui.status.unavailable", "booru.ui.status.failed", "booru.ui.status.retry",
            "booru.ui.post.title", "booru.ui.post.rating", "booru.ui.post.score", "booru.ui.post.favorites",
            "booru.ui.post.created", "booru.ui.post.urlLabel", "booru.ui.post.noPreview", "booru.ui.post.previewLoading",
            "booru.ui.post.previewUnavailable", "booru.ui.tags.label", "booru.ui.tags.none", "booru.ui.tags.more",
            "booru.ui.load", "booru.ui.loadFor",
            "booru.ui.watcher.create", "booru.ui.watcher.createHint", "booru.ui.watcher.created", "booru.ui.watcher.duplicate",
            "booru.ui.watcher.createNeedsTags", "booru.ui.watcher.createFailed",
            "booru.ui.watchers.intro", "booru.ui.watchers.removeNote", "booru.ui.watchers.empty", "booru.ui.watchers.loading",
            "booru.ui.watchers.loadFailed", "booru.ui.watchers.retry",
            "booru.ui.watcher.lastCheck", "booru.ui.watcher.nextCheck", "booru.ui.watcher.never", "booru.ui.watcher.notScheduled",
            "booru.ui.watcher.pausedNext", "booru.ui.watcher.hits", "booru.ui.watcher.baseline", "booru.ui.watcher.budget",
            "booru.ui.watcher.cooldown", "booru.ui.watcher.error", "booru.ui.watcher.checkNow", "booru.ui.watcher.pause",
            "booru.ui.watcher.resume", "booru.ui.watcher.remove", "booru.ui.watcher.checkNowFor", "booru.ui.watcher.pauseFor",
            "booru.ui.watcher.resumeFor", "booru.ui.watcher.removeFor", "booru.ui.watcher.removeAsk",
            "booru.ui.watcher.removeConfirm", "booru.ui.watcher.msg.checked", "booru.ui.watcher.msg.checkedNone", "booru.ui.watcher.msg.checkedPaused",
            "booru.ui.watcher.msg.paused", "booru.ui.watcher.msg.resumed", "booru.ui.watcher.msg.removed",
            "booru.ui.watcher.msg.rateLimited", "booru.ui.watcher.msg.cooldown", "booru.ui.watcher.msg.offline",
            "booru.ui.watcher.msg.failed", "booru.ui.watcher.reject",
            "booru.ui.watcher.restart", "booru.ui.watcher.restartFor", "booru.ui.watcher.msg.restarted",
            "booru.ui.post.open", "booru.ui.post.openFor", "booru.ui.post.openTip", "booru.ui.post.copy", "booru.ui.post.copyFor", "booru.ui.link.copied", "booru.ui.link.openFailed", "booru.ui.link.copyFailed", "booru.ui.watcher.latest", "booru.ui.watcher.latestOpenFor", "booru.ui.watcher.latestCopyFor", "booru.ui.notify.queued.title", "booru.ui.notify.queued"
        })
            yield return key;
        foreach (BooruInputIssue issue in Enum.GetValues<BooruInputIssue>().Where(issue => issue != BooruInputIssue.None))
            yield return Prefix + "issue." + issue;
        foreach (BooruRating rating in Enum.GetValues<BooruRating>())
            yield return Prefix + "rating." + rating;
        foreach (BooruTagCategory category in Enum.GetValues<BooruTagCategory>())
            yield return Prefix + "tagCategory." + category;
        foreach (string access in new[] { "Available", "NotOffered", "Restricted", "Withdrawn" })
            yield return Prefix + "original." + access;
        foreach (BooruRowLoad load in Enum.GetValues<BooruRowLoad>().Where(load => load != BooruRowLoad.Idle))
            yield return Prefix + "load." + load;
        foreach (BooruWatcherStatus status in Enum.GetValues<BooruWatcherStatus>())
            yield return Prefix + "watcher.status." + status;
        yield return Prefix + "watcher.status.CooldownOver";
        foreach (BooruWatcherBlock block in Enum.GetValues<BooruWatcherBlock>().Where(block => block != BooruWatcherBlock.None))
            yield return Prefix + "watcher.why." + block;
        foreach (string code in KnownRejections)
            yield return Prefix + "watcher.reject." + code;
        yield return Prefix + "watchers.catalogSize";
    }
}
