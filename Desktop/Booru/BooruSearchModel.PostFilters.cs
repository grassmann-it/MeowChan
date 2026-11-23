using System;
using System.Collections.Immutable;
using System.Linq;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Booru;

internal enum BooruPostMediaType { Image, Animation, Video, Audio, Unknown }

/// <summary>Session-only display criteria. These never become provider query terms.</summary>
internal sealed record BooruPostFilter
{
    internal BooruRating? Rating { get; init; }
    internal int? MinimumScore { get; init; }
    internal BooruPostMediaType? MediaType { get; init; }
    internal ImmutableDictionary<string, bool> Tags { get; init; } = ImmutableDictionary<string, bool>.Empty.WithComparers(StringComparer.Ordinal);
    internal bool IsActive => Rating is not null || MinimumScore is not null || MediaType is not null || !Tags.IsEmpty;

    internal bool Matches(BooruPostView post) =>
        (Rating is null || post.Rating == Rating)
        && (MinimumScore is null || post.Score is { } score && score >= MinimumScore)
        && (MediaType is null || TypeOf(post) == MediaType)
        && Tags.All(filter => post.Tags.Any(tag => string.Equals(tag.Name, filter.Key, StringComparison.Ordinal)) == filter.Value);

    // Only original metadata identifies the media; a JPEG thumbnail is not evidence of an image original.
    internal static BooruPostMediaType TypeOf(BooruPostView post) => post.Original?.Extension?.TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "png" or "webp" or "avif" or "bmp" or "tif" or "tiff" => BooruPostMediaType.Image,
        "gif" or "apng" or "zip" => BooruPostMediaType.Animation,
        "webm" or "mp4" or "mkv" or "mov" or "avi" or "m4v" => BooruPostMediaType.Video,
        "mp3" or "ogg" or "opus" or "flac" or "wav" or "m4a" => BooruPostMediaType.Audio,
        _ => BooruPostMediaType.Unknown
    };
}

internal sealed partial record BooruSearchView
{
    internal BooruPostFilter PostFilter { get; init; } = new();
    internal ImmutableArray<BooruResultRowView> VisibleRows => PostFilter.IsActive
        ? Rows.Where(row => PostFilter.Matches(row.Post)).ToImmutableArray() : Rows;
}

internal sealed partial class BooruSearchModel
{
    internal void SetPostFilter(BooruPostFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.MinimumScore is < MeowChan.Config.BooruPostFilterScoreMinimum or > MeowChan.Config.BooruPostFilterScoreMaximum)
            throw new ArgumentOutOfRangeException(nameof(filter));
        lock (_gate)
        {
            if (_disposed || _view.PostFilter == filter) return;
            _view = _view with { PostFilter = filter };
        }
        RaiseChanged();
    }

    internal void SetPostFilterTag(string tag, bool? include)
    {
        lock (_gate)
        {
            if (_disposed) return;
            BooruPostFilter filter = _view.PostFilter;
            if (!filter.Tags.ContainsKey(tag) && !_view.Rows.Any(row => row.Post.Tags.Any(value => value.Name == tag))) return;
            _view = _view with { PostFilter = filter with { Tags = include is { } enabled
                ? filter.Tags.SetItem(tag, enabled) : filter.Tags.Remove(tag) } };
        }
        RaiseChanged();
    }

    internal void ResetPostFilter() => SetPostFilter(new BooruPostFilter());
}
