using oChan.Desktop;
using oChan.Downloader;
using oChan.Interfaces;

namespace oChan.DesktopSmoke;

/// <summary>Passive, network-free view data. These fixtures never create provider watches.</summary>
internal static class SnapshotFixtures
{
    internal static DesktopThreadEntry Thread(long watchId, string id, string title, int completed = 0,
        int known = 100, int queued = 0, int active = 0, string board = "g")
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("OCHAN_PROFILE")!, "Downloads", "4chan", board, id);
        ThreadSnapshot snapshot = new ThreadSnapshot(new ResourceId("4chan", board, id),
            "https://boards.4chan.org/" + board + "/thread/" + id, title, directory,
            ThreadActivity.Pending, ThreadSourceState.Active, null,
            known, completed, new Dictionary<string, FailureInfo>(), null, new DownloadCounts(queued, active));
        return new DesktopThreadEntry(watchId, snapshot, "4chan", "/" + board + "/");
    }

    internal static DesktopBoardEntry Board(long watchId, string code = "g")
    {
        string directory = Path.Combine(Environment.GetEnvironmentVariable("OCHAN_PROFILE")!, "Downloads", "4chan", code);
        BoardSnapshot snapshot = new BoardSnapshot(new ResourceId("4chan", code),
            "https://boards.4chan.org/" + code + "/", "/" + code + "/", directory, null, null);
        return new DesktopBoardEntry(watchId, snapshot, "4chan", "/" + code + "/");
    }
}
