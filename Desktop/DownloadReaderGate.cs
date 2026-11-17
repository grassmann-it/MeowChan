using System;

namespace MeowChan.Desktop;

/// <summary>
/// "Read posts" on a Downloads row (DL-01): whether the archive has anything to open for it. A thread that was just added, or that failed before its
/// first capture, is in the list but not in the archive, and the reader would open empty with an instruction ("Select an archived thread") that has
/// nothing to select there.
/// </summary>
internal static class DownloadReaderGate
{
    /// <summary>
    /// True when the row's thread has no capture yet (the catalog has no capture time for it) and no saved medium either. A thread whose posts were
    /// never captured but whose media was saved still opens (the reader shows it as "Media only"); a legacy provider without capture support
    /// (<see cref="ThreadSnapshot.Content"/> is null) says nothing and is never refused.
    /// </summary>
    internal static bool NothingCaptured(ThreadSnapshot snapshot) =>
        snapshot.Content is { LastCapturedAt: null } && (snapshot.Work?.SavedMedia ?? snapshot.CompletedMedia) == 0;

    /// <summary>
    /// A thread whose posts were never captured (it has saved media, or the gate would have refused it) opens the large reader on its media tab: the
    /// posts tab would only say that no post text exists.
    /// </summary>
    internal static bool OpensOnMedia(int postCount, DateTimeOffset? lastCapturedAt) => postCount == 0 && lastCapturedAt == null;
}
