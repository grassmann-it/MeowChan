namespace MeowChan.Boards.Base;

using System;
using System.Collections.Generic;
using MeowChan.Archive;

internal sealed record AdaptiveRecheckSettings
{
    internal const int MinimumSeconds = 10;
    internal const int DefaultStartSeconds = 60;
    internal const int MaximumSeconds = 900;

    internal AdaptiveRecheckSettings(int minimumSeconds, int startSeconds, int maximumSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSeconds, MinimumSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(startSeconds, minimumSeconds);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumSeconds, startSeconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumSeconds, MaximumSeconds);
        Minimum = TimeSpan.FromSeconds(minimumSeconds);
        Start = TimeSpan.FromSeconds(startSeconds);
        Maximum = TimeSpan.FromSeconds(maximumSeconds);
    }

    internal TimeSpan Minimum { get; }
    internal TimeSpan Start { get; }
    internal TimeSpan Maximum { get; }
}

// One policy per monitor lifetime. Keep IDs across partial snapshots so an edit,
// deletion or previously seen post appearing again does not speed up polling.
internal sealed class AdaptiveRecheckPolicy(AdaptiveRecheckSettings settings)
{
    private readonly HashSet<string> _seenPosts = new(StringComparer.Ordinal);
    private TimeSpan _interval = settings.Start;
    private bool _hasObservation;
    private bool _hasScheduled;
    private bool _newPosts;

    internal AdaptiveRecheckSettings Settings { get; } = settings;

    internal void ObservePosts(IReadOnlyList<CapturedPost> posts)
    {
        foreach (CapturedPost post in posts)
            if (_seenPosts.Add(post.PostId) && _hasObservation && !post.IsDeleted) _newPosts = true;
        _hasObservation = true;
    }

    internal TimeSpan AfterCheck(bool succeeded)
    {
        if (!succeeded) return _interval;
        if (_newPosts) _interval = Settings.Minimum;
        else if (_hasScheduled) _interval = TimeSpan.FromTicks(Math.Min(_interval.Ticks * 2, Settings.Maximum.Ticks));
        _hasScheduled = true;
        _newPosts = false;
        return _interval;
    }
}
