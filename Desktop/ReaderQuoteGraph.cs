using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Interfaces;

namespace MeowChan.Desktop;

/// <summary>
/// Who quotes whom inside one thread read, by thread index. Built once per thread revision in the background and only
/// when a consumer needs it (P2's "also hide replies", P4's conversation tree). Only targets in the same thread that
/// were captured count; self references and duplicates are dropped. Storage is two compressed int arrays per direction.
/// </summary>
internal sealed class ReaderQuoteGraph
{
    private readonly int[] _targetStart, _targets, _replyStart, _replies;

    private ReaderQuoteGraph(ResourceId thread, long revision, int count, int[] targetStart, int[] targets, int[] replyStart, int[] replies)
    {
        Thread = thread; Revision = revision; PostCount = count;
        _targetStart = targetStart; _targets = targets; _replyStart = replyStart; _replies = replies;
    }

    internal ResourceId Thread { get; }
    internal long Revision { get; }
    internal int PostCount { get; }
    internal int EdgeCount => _targets.Length;

    internal bool Matches(ArchivedThreadView thread) => thread.Summary.Id == Thread
        && thread.Summary.Revision == Revision && thread.PostCount == PostCount;

    internal static ReaderQuoteGraph Build(ArchivedThreadView thread, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(thread);
        IReadOnlyList<CapturedPost> posts = thread.Posts;
        int count = posts.Count;
        ResourceId id = thread.Summary.Id;
        // The parser refuses a thread URL that is not HTTP(S); such a read has no resolvable references at all.
        bool parsable = Uri.TryCreate(thread.Summary.Url, UriKind.Absolute, out Uri? source) && source.Scheme is "http" or "https";
        int[] targetStart = new int[count + 1];
        List<int> targets = new();
        List<int> perPost = new();
        for (int index = 0; index < count; index++)
        {
            if ((index & 127) == 0) token.ThrowIfCancellationRequested();
            targetStart[index] = targets.Count;
            if (!parsable) continue;
            perPost.Clear();
            IReadOnlyList<ArchiveQuote> quotes;
            try { quotes = ArchiveQuoteParser.Parse(id, thread.Summary.Url, posts[index]); }
            catch (Exception error) when (error is RegexMatchTimeoutException or ArgumentException) { continue; }
            foreach (ArchiveQuote quote in quotes)
            {
                if (quote.Target is not { } target || target.Thread != id) continue;
                int targetIndex = thread.FindPostIndex(target.PostId);
                if (targetIndex >= 0 && targetIndex != index) perPost.Add(targetIndex);
            }
            if (perPost.Count == 0) continue;
            perPost.Sort();
            int previous = -1;
            foreach (int targetIndex in perPost)
            {
                if (targetIndex == previous) continue;
                targets.Add(targetIndex);
                previous = targetIndex;
            }
        }
        targetStart[count] = targets.Count;
        int[] targetArray = targets.ToArray();
        // Inverse direction: sources are visited in ascending order, so every reply list comes out sorted.
        int[] replyStart = new int[count + 1];
        foreach (int target in targetArray) replyStart[target + 1]++;
        for (int index = 0; index < count; index++) replyStart[index + 1] += replyStart[index];
        int[] replies = new int[targetArray.Length];
        int[] fill = (int[])replyStart.Clone();
        for (int sourceIndex = 0; sourceIndex < count; sourceIndex++)
            for (int edge = targetStart[sourceIndex]; edge < targetStart[sourceIndex + 1]; edge++)
                replies[fill[targetArray[edge]]++] = sourceIndex;
        return new ReaderQuoteGraph(id, thread.Summary.Revision, count, targetStart, targetArray, replyStart, replies);
    }

    /// <summary>Captured posts of the same thread that this post quotes, ascending, without itself and duplicates.</summary>
    internal ReadOnlySpan<int> TargetsOf(int threadIndex)
    {
        if ((uint)threadIndex >= (uint)PostCount) throw new ArgumentOutOfRangeException(nameof(threadIndex));
        return _targets.AsSpan(_targetStart[threadIndex], _targetStart[threadIndex + 1] - _targetStart[threadIndex]);
    }

    /// <summary>Posts of the same thread that quote this post, ascending.</summary>
    internal ReadOnlySpan<int> RepliesTo(int threadIndex)
    {
        if ((uint)threadIndex >= (uint)PostCount) throw new ArgumentOutOfRangeException(nameof(threadIndex));
        return _replies.AsSpan(_replyStart[threadIndex], _replyStart[threadIndex + 1] - _replyStart[threadIndex]);
    }
}
