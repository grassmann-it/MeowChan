using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Archive;

/// <summary>One-based positions of the captured attachments, independent of manifests and download outcomes.</summary>
internal sealed class ThreadMediaPositions
{
    private readonly Dictionary<(string PostId, int Position), int> _numbers = new();

    internal ThreadMediaPositions(IEnumerable<CapturedPost> posts)
    {
        foreach (CapturedPost post in posts)
            foreach (CapturedAttachment attachment in (post.Attachments ?? Array.Empty<CapturedAttachment>())
                .Where(Includes).OrderBy(attachment => attachment.Position))
                _numbers.TryAdd((post.PostId, attachment.Position), _numbers.Count + 1);
    }

    internal int Count => _numbers.Count;

    internal int? NumberOf(string? postId, int? position) =>
        postId != null && position is int slot && _numbers.TryGetValue((postId, slot), out int number) ? number : null;

    internal static bool Includes(CapturedAttachment attachment) => !attachment.IsDeleted && attachment.MediaId != null;
}
