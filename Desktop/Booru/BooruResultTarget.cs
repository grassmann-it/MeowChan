namespace MeowChan.Desktop.Booru;

internal readonly record struct BooruResultRevision(int TabId, long Generation);

/// <summary>The post and provenance of a hit as shown in one search revision.</summary>
internal sealed record BooruResultTarget(
    BooruResultRevision Revision, BooruPostView Post, BooruLoadOrigin Origin, string? Query)
{
    internal static BooruResultTarget From(BooruSearchView search, BooruResultRowView row) =>
        new(search.ResultRevision, row.Post, search.IsPostResult ? BooruLoadOrigin.OneShot : BooruLoadOrigin.Search,
            search.IsPostResult ? null : search.ExecutedQuery);
}
