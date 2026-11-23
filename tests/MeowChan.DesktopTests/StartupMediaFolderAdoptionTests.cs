using System.Net;
using MeowChan.Boards.FourChan;
using MeowChan.Desktop;
using MeowChan.FolderSources;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

public sealed class StartupMediaFolderAdoptionTests
{
    [Theory]
    [InlineData("session")]
    [InlineData("journal")]
    [InlineData("queued-file")]
    public async Task AdoptingWithDefaultBRefusesAnOldWatchThatCanStillWriteInA(string assignment)
    {
        using TempDirectory root = new();
        string source = Directory.CreateDirectory(Path.Combine(root.Path, "A")).FullName;
        string downloads = Path.Combine(root.Path, "B");
        string profile = Path.Combine(root.Path, "selected");
        const string url = "https://boards.4chan.org/g/thread/123";
        ProfileSwitchService.PrepareNew(profile, source, "en");
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        {
            await new SessionStore(profile, lease).SaveAsync(new SessionSnapshot
            {
                ThreadUrls = [url],
                DownloadDirectories = { [url] = assignment == "session" ? source : downloads }
            });
            if (assignment != "session")
            {
                WorkStateStore work = new(profile, lease);
                WorkResource saved = work.CommitIntent(new WorkResource
                {
                    Url = url, OutputRoot = assignment == "journal" ? source : downloads,
                    Generation = 1, Intent = WorkIntent.Watching, IsThread = true
                });
                if (assignment == "queued-file")
                    Assert.True(work.CommitMedia(new MediaWork
                    {
                        ResourceUrl = url, Generation = saved.Generation, MediaIdentifier = "old",
                        DestinationPath = Path.Combine(source, "old.jpg"),
                        DownloadUrl = "https://i.4cdn.org/g/123.jpg", Status = MediaWorkStatus.Ready
                    }));
            }
            Config config = Config.LoadConfig(profile, lease);
            config.DownloadPath = downloads;
            config.SaveConfig(profile);
        }

        using StartupFolderSession start = new(source);
        Assert.Null(start.ProfileProblem(profile, []));
        using WatchTransport transport = new();
        using HttpClient client = new(transport);
        FourChanImageBoard provider = new(client);
        FolderSourceException error = await Assert.ThrowsAsync<FolderSourceException>(async () =>
        {
            await using DesktopSessionHost session = new(profile, [provider], readOnlyStartupSource: source);
            await session.StartAsync();
        });
        Assert.Equal(FolderSourceError.FolderIsDownloads, error.Error);
        Assert.Equal(0, transport.Requests);
        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
        Assert.False(ProfileLease.IsInUse(profile));
    }

    private sealed class WatchTransport : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"posts\":[{\"no\":123}]}")
            });
        }
    }
}
