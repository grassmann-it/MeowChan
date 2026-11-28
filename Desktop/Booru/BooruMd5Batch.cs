using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Gallery;
using MeowChan.PluginHost.Sources.Booru;

namespace MeowChan.Desktop.Booru;

internal sealed record BooruMd5Match(string Name, BooruPostView Post)
{
    public override string ToString() => Name + " · #" + Post.PostId;
}

/// <summary>A sequential, resumable pass over a frozen selection. Only the caller's explicit Run starts work.</summary>
internal sealed class BooruMd5Batch(IReadOnlyList<MediaLibraryEntry> entries,
    Func<MediaLibraryEntry, CancellationToken, Task<string?>> readMd5, IBooruSearchPort search,
    Func<bool> isEnabled, Func<BooruContentFilter?> contentFilter, TimeProvider clock, int pageSize)
{
    private readonly Dictionary<string, ImmutableArray<BooruPostView>> _checked = new(StringComparer.Ordinal);
    private bool _running;
    internal int Completed { get; private set; }
    internal int MissingHash { get; private set; }
    internal int Total => entries.Count;
    internal DateTimeOffset? RetryAt { get; private set; }

    internal BooruContentFilter? ReadContentFilter() => contentFilter();

    // A failed or deferred request leaves the current file pending. Successful results are kept in memory for this pass.
    internal async Task<BooruCallStatus> RunAsync(Action<IEnumerable<BooruMd5Match>> onProgress, CancellationToken token)
    {
        if (_running) return BooruCallStatus.Unavailable;
        _running = true;
        try
        {
            if (RetryAt > clock.GetUtcNow()) return BooruCallStatus.Cooldown;
            RetryAt = null;
            while (Completed < Total)
            {
                token.ThrowIfCancellationRequested();
                if (!isEnabled() || ReadContentFilter() is null) return BooruCallStatus.Unavailable;
                MediaLibraryEntry entry = entries[Completed];
                string? query = BooruMd5Lookup.Query(await readMd5(entry, token));
                token.ThrowIfCancellationRequested();
                if (query is null)
                {
                    MissingHash++;
                    Completed++;
                    onProgress([]);
                    continue;
                }
                if (!isEnabled()) return BooruCallStatus.Unavailable;
                if (!_checked.TryGetValue(query, out ImmutableArray<BooruPostView> matches))
                {
                    BooruCallResult<BooruResultPage> result = await search.SearchAsync(query, 1, pageSize, token);
                    token.ThrowIfCancellationRequested();
                    if (!isEnabled()) return BooruCallStatus.Unavailable;
                    if (result.Status != BooruCallStatus.Ok || result.Value is not { } page)
                    {
                        RetryAt = result.RetryAt;
                        return result.Status == BooruCallStatus.Ok ? BooruCallStatus.Failed : result.Status;
                    }
                    matches = page.Posts.Where(post => !post.IsDeleted && !post.IsBanned
                        && post.Identity.SourceId == search.ProviderId && post.Identity.InstanceId == search.InstanceId
                        && BooruMd5Lookup.Query(post.Original?.ExpectedMd5) == query).ToImmutableArray();
                    _checked.Add(query, matches);
                }
                BooruContentFilter? filter = ReadContentFilter();
                if (filter is null) return BooruCallStatus.Unavailable;
                string name = entry.Name ?? entry.MediaId ?? string.Empty;
                BooruMd5Match[] allowed = matches.Where(post => IsAllowed(post, filter))
                    .Select(post => new BooruMd5Match(name, post)).ToArray();
                Completed++;
                onProgress(allowed);
            }
            return BooruCallStatus.Ok;
        }
        finally { _running = false; }
    }

    internal static bool IsAllowed(BooruPostView post, BooruContentFilter? filter)
    {
        if (filter is null) return false;
        try
        {
            BooruPostClaim claim = BooruClaimMapper.Map(post.Post, post.SourceId, post.Identity.InstanceId);
            return filter.Evaluate(claim).IsAllowed;
        }
        catch (InvalidDataException) { return false; }
    }
}
