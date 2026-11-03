using System.Runtime.InteropServices;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using Microsoft.Win32.SafeHandles;

namespace MeowChan.DesktopTests;

public sealed class StartupPreviewSpecialFileRegressionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);
    private static StartupPreviewSnapshot Snapshot() => new(
        new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), 1, 0,
        new[] { new StartupPreviewRow(false, "source", "b", "valid", 1, 0) });

    [Fact]
    public async Task UnixFifoIsRejectedWithoutPeerAndRegularAndMissingControlsStillWork()
    {
        if (!SupportedUnix) return;
        using Fixture fixture = new();
        using ProfileLease lease = ProfileLease.Acquire(fixture.Path);
        StartupPreviewStore store = new(fixture.Path, lease);
        Assert.Null(store.Load());
        store.Save(Snapshot());
        Assert.Equal("valid", store.Load()!.Rows[0].Title);
        File.Delete(fixture.Cache);
        CreateFifo(fixture.Cache);
        Task<Exception?> load = Task.Run<Exception?>(() => Record.Exception(() => { store.Load(); }));
        await Guard(load, fixture.Cache);
        Assert.IsType<InvalidDataException>(await load);
        // Replacing the rejected leaf with an ordinary cache must remain usable.
        File.Delete(fixture.Cache);
        store.Save(Snapshot());
        Assert.Equal("valid", store.Load()!.Rows[0].Title);
    }

    [Fact]
    public async Task UnixFifoCacheFallsBackToRealRestoreThenStopsAndReleasesProfile()
    {
        if (!SupportedUnix) return;
        using Fixture fixture = new();
        new Config { SaveUrlsOnExit = true, DownloadsPaused = true,
            AutomaticUpdateChecks = false }.SaveConfig(fixture.Path);
        CreateFifo(fixture.Cache);
        ObservedStore store = new();
        DesktopSessionHost host = new(fixture.Path, Array.Empty<IImageBoard>(), (_, _) => store);
        try
        {
            await Guard(host.StartAsync(), fixture.Cache);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            Assert.Null(host.StartupPreview);
            Assert.Equal(1, store.Loads);
            Assert.Empty(host.Projection.View.Threads);
            Assert.Empty(host.Projection.View.Boards);
        }
        finally
        {
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.True(host.IsProfileReleased);
        using ProfileLease reacquired = ProfileLease.Acquire(fixture.Path);
    }

    private static bool SupportedUnix => (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;

    private static void CreateFifo(string path) => Assert.True(MkFifo(path, 384 /* 0600 */) == 0,
        $"Synthetic FIFO creation failed: errno {Marshal.GetLastPInvokeError()}.");

    private static async Task Guard(Task operation, string fifo)
    {
        if (await Task.WhenAny(operation, Task.Delay(Watchdog)) == operation)
        {
            await operation;
            return;
        }
        // Rescue ONLY after the deadline. A pending read-only FIFO open counts as
        // a reader: nonblocking WRONLY connects without waiting, then close gives
        // that reader EOF. Do not hold RDWR open (it would keep Read waiting).
        int nonblocking = OperatingSystem.IsMacOS() ? 0x4 : 0x800;
        using (SafeFileHandle writer = new((IntPtr)Open(fifo, 1 | nonblocking), ownsHandle: true))
            Assert.False(writer.IsInvalid, "FIFO rescue could not connect to the blocked reader.");
        // Drain/observe the old broken implementation before failing, so its lease
        // is not disposed underneath an orphaned blocking reader.
        try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception error) when (error is not TimeoutException) { }
        Assert.Fail("Startup preview exceeded FIFO watchdog; rescue writer was required.");
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed class ObservedStore : ISessionStore
    {
        internal int Loads;
        public Task<SessionLoadResult> LoadAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Loads);
            return Task.FromResult(new SessionLoadResult(new SessionSnapshot(), null, false));
        }
        public Task SaveAsync(SessionSnapshot snapshot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class Fixture : IDisposable
    {
        // Resolve macOS /var and /tmp aliases for this synthetic destination only;
        // production ancestor RejectLinks must not be bypassed by the fixture.
        internal string Path { get; } = System.IO.Path.Combine(Physical(new DirectoryInfo(
            System.IO.Path.GetTempPath())), "meowchan-preview-special-" + Guid.NewGuid().ToString("N"));
        internal string Cache => System.IO.Path.Combine(Path, "startup-preview.json");
        internal Fixture() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
        private static string Physical(DirectoryInfo directory)
        {
            if (directory.LinkTarget is not null)
                return Physical(new DirectoryInfo(directory.ResolveLinkTarget(true)!.FullName));
            return directory.Parent is null ? directory.FullName
                : System.IO.Path.Combine(Physical(directory.Parent), directory.Name);
        }
    }
}
