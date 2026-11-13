using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// One post address from the Add line, finished: <see cref="Status"/> is Added or AlreadyPresent when the original is in (or
/// already in) the download queue, otherwise null; <see cref="Text"/> is the localized line for the feedback list.
/// </summary>
internal sealed record BooruAddPostOutcome(MeowChan.AdmissionStatus? Status, string Text);

/// <summary>
/// The one-shot a pasted Danbooru post address starts: one post read, then, if the post offers an original, the same
/// hand-over to the existing download queue as the window's "Load original" (origin one-shot, no query kept). It
/// creates no watcher and asks nothing else. Every failure becomes a sentence; nothing is thrown.
/// </summary>
internal static class BooruAddUrl
{
    internal static async Task<BooruAddPostOutcome> LoadPostAsync(BooruWorkbenchServices services, string postId, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            BooruCallResult<BooruPostView> read = await services.Search.ReadPostAsync(postId, token).ConfigureAwait(false);
            if (read.Status != BooruCallStatus.Ok)
                return Failure(postId, ReadFailure(read));
            if (read.Value is not { } post || !string.Equals(post.PostId, postId, StringComparison.Ordinal))
                return Failure(postId, Texts.T("booru.ui.status.failed", BooruCodeLabel("wrong_post")));
            if (post.OriginalAccess != BooruOriginalAccess.Available)
                return Failure(postId, Texts.T(BooruText.Prefix + "load." + (post.OriginalAccess == BooruOriginalAccess.Restricted
                    ? BooruRowLoad.Restricted : BooruRowLoad.NoOriginal)));
            BooruLoadResult load = await services.Queue.LoadOriginalAsync(post, BooruLoadOrigin.OneShot, null, token).ConfigureAwait(false);
            return load.Status switch
            {
                BooruLoadStatus.Queued => new(MeowChan.AdmissionStatus.Added, Line(postId, Texts.T(BooruText.Prefix + "load.Queued"))),
                BooruLoadStatus.AlreadyQueued => new(MeowChan.AdmissionStatus.AlreadyPresent, Line(postId, Texts.T(BooruText.Prefix + "load.AlreadyQueued"))),
                BooruLoadStatus.AlreadyLoaded => new(MeowChan.AdmissionStatus.AlreadyPresent, Line(postId, Texts.T(BooruText.Prefix + "load.AlreadyLoaded"))),
                BooruLoadStatus.NoOriginal => Failure(postId, Texts.T(BooruText.Prefix + "load.NoOriginal")),
                BooruLoadStatus.Restricted => Failure(postId, Texts.T(BooruText.Prefix + "load.Restricted")),
                BooruLoadStatus.Offline => Failure(postId, Texts.T(BooruText.Prefix + "load.Offline")),
                _ => Failure(postId, Texts.T(BooruText.Prefix + "load.Failed", BooruCodeLabel(load.Code)))
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(postId, Texts.T("booru.ui.status.failed", BooruCodeLabel("exception")));
        }
    }

    private static string ReadFailure(BooruCallResult<BooruPostView> result) => result.Status switch
    {
        BooruCallStatus.Offline => Texts.T("booru.ui.status.offline"),
        BooruCallStatus.RateLimited => result.RetryAt is { } until
            ? Texts.T("booru.ui.status.rateLimited", BooruText.Time(until)) : Texts.T("booru.ui.status.pauseOver"),
        BooruCallStatus.Cooldown => result.RetryAt is { } stop
            ? Texts.T("booru.ui.status.cooldown", BooruText.Time(stop)) : Texts.T("booru.ui.status.pauseOver"),
        BooruCallStatus.Denied => Texts.T("booru.ui.status.denied"),
        BooruCallStatus.NotFound => Texts.T("booru.ui.status.notFound"),
        BooruCallStatus.Unavailable => Texts.T("booru.ui.status.unavailable"),
        _ => Texts.T("booru.ui.status.failed", BooruCodeLabel(result.Code))
    };

    private static string BooruCodeLabel(string? code) => BooruText.CodeText(code);

    private static string Line(string postId, string text) => Texts.T("booru.ui.add.post", postId, text);

    private static BooruAddPostOutcome Failure(string postId, string text) => new(null, Line(postId, text));
}
