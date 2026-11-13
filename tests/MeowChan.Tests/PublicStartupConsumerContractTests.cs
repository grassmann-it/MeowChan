using MeowChan.Archive;
using MeowChan.Downloader;
using MeowChan.Persistence;

namespace MeowChan.Tests;

// Consumer order only: all ownership is established through public APIs.
public sealed class PublicStartupConsumerContractTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string RestoredUrl = "https://stub.test/g/thread/123";
    private const string AddedUrl = "https://stub.test/g/thread/124";

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)] // Package consumer: durable work without session restoration.
    public async Task FreshProfileRequiresStartWhichAlreadyRestoresBeforeCommands(
        bool useSessions, bool useWork, bool useContent, bool persist)
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        SessionStore? sessions = useSessions ? new(profile.Path, lease) : null;
        WorkStateStore? work = useWork ? new(profile.Path, lease) : null;
        await using ThreadContentRuntime? content = useContent ? new(profile.Path, lease) : null;
        if (sessions != null)
            await sessions.SaveAsync(new SessionSnapshot { ThreadUrls = [RestoredUrl] });
        work?.CommitIntent(new WorkResource
        {
            Url = RestoredUrl, OutputRoot = profile.Path, Generation = 1,
            IsThread = true, Intent = WorkIntent.Watching
        });
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1024 * 1024);
        queue.SetPaused(true); // Synthetic watches never poll or download.
        await using Registry registry = new([provider], queue, profile.Path, TimeSpan.FromHours(1), 3600,
            sessionStore: sessions, workStore: work, contentRuntime: content, persistSession: persist);

        InvalidOperationException restoreError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.RestoreAsync().WaitAsync(Deadline));
        Assert.Contains("Registry.StartAsync(lease)", restoreError.Message);
        InvalidOperationException addError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.AddAsync(AddedUrl).WaitAsync(Deadline));
        Assert.Contains("Registry.StartAsync(lease)", addError.Message);
        Assert.Empty(registry.Threads);
        Assert.False(File.Exists(Path.Combine(profile.Path, "archive.db")));
        if (content != null) Assert.False(content.IsInitialized);

        Task startup = registry.StartAsync(lease);
        await startup.WaitAsync(Deadline);
        Assert.Same(startup, registry.StartAsync(lease));
        if (content != null) Assert.True(content.IsInitialized);
        string[] expected = persist && (useSessions || useWork) ? [RestoredUrl] : [];
        Assert.Equal(expected, registry.Threads.Select(thread => thread.Url));
        var restored = registry.Threads.ToArray();
        await registry.RestoreAsync().WaitAsync(Deadline); // No second reconstruction or activation.
        Assert.Equal(restored, registry.Threads);
        Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync(AddedUrl).WaitAsync(Deadline)).Status);
        Assert.All(registry.Threads, thread => Assert.Equal(0, Assert.IsType<ProbeThread>(thread).Checks));
        await registry.StopAsync().WaitAsync(Deadline);
        Assert.Empty(registry.Threads);
        if (content != null) Assert.True(content.IsInitialized); // Still caller-owned.
        if (sessions != null) await sessions.SaveAsync(new SessionSnapshot()); // Lease/store still usable.
    }

    [Fact]
    public async Task AlreadyReadyLeaseRetainsDirectRestoreForASuccessorRegistry()
    {
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        SessionStore sessions = new(profile.Path, lease);
        WorkStateStore work = new(profile.Path, lease);
        using StubImageBoard provider = new();
        DownloadQueue firstQueue = new(1, 1024 * 1024);
        firstQueue.SetPaused(true);
        await using (Registry first = new([provider], firstQueue, profile.Path, TimeSpan.FromHours(1), 3600,
            sessionStore: sessions, workStore: work))
        {
            await first.StartAsync(lease).WaitAsync(Deadline);
            Assert.Equal(AdmissionStatus.Added, (await first.AddAsync(RestoredUrl).WaitAsync(Deadline)).Status);
            await first.StopAsync().WaitAsync(Deadline);
        }
        DownloadQueue nextQueue = new(1, 1024 * 1024);
        nextQueue.SetPaused(true);
        await using Registry next = new([provider], nextQueue, profile.Path, TimeSpan.FromHours(1), 3600,
            sessionStore: sessions, workStore: work);
        await next.RestoreAsync().WaitAsync(Deadline);
        Assert.Equal(RestoredUrl, Assert.Single(next.Threads).Url);
        Assert.Equal(AdmissionStatus.Added, (await next.AddAsync(AddedUrl).WaitAsync(Deadline)).Status);
        await next.StopAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task UnboundRegistryStillRestoresAndAdmitsWithoutProfileStartup()
    {
        using TempDirectory output = new();
        using StubImageBoard provider = new();
        DownloadQueue queue = new(1, 1024 * 1024);
        queue.SetPaused(true);
        await using Registry registry = new([provider], queue, output.Path, TimeSpan.FromHours(1), 3600,
            persistSession: false);
        await registry.RestoreAsync().WaitAsync(Deadline);
        Assert.Equal(AdmissionStatus.Added, (await registry.AddAsync(AddedUrl).WaitAsync(Deadline)).Status);
        var thread = Assert.Single(registry.Threads);
        await registry.RestoreAsync().WaitAsync(Deadline);
        Assert.Same(thread, Assert.Single(registry.Threads));
        Assert.False(File.Exists(Path.Combine(output.Path, "archive.db")));
        await registry.StopAsync().WaitAsync(Deadline);
    }
}
