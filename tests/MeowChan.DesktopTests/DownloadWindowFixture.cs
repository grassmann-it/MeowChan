using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

internal sealed class DownloadWindowFixture(MainWindow window, DesktopSessionHost host)
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    internal MainWindow Window { get; } = window;
    internal DataGrid Threads => Find<DataGrid>("ThreadsDataGrid");
    internal T Find<T>(string name) where T : Control => Window.FindControl<T>(name)!;

    internal static void Run(Func<DownloadWindowFixture, Task> exercise, Action? preparePlatform = null) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            preparePlatform?.Invoke();
            using TempDirectory profile = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1, WorkspaceMode = "Advanced",
                Language = "en", WindowWidth = 1240, WindowHeight = 900, SaveUrlsOnExit = false
            }.SaveConfig(profile.Path);
            BooruComponentDispatcher.Wait(RunAsync());

            async Task RunAsync()
            {
                await using DesktopSessionHost host = new(profile.Path, [],
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
                await using MainWindow window = new(host) { Classes = { MotionPolicy.OffClass } };
                window.Show();
                window.PlatformImpl!.Activated?.Invoke();
                await window.StartupTimingRestoreTask.WaitAsync(Deadline);
                // Keep the real host ready while this fixture publishes controlled offline rows.
                await ((Task)typeof(DesktopSessionHost).GetMethod("CloseProjectionAsync", PrivateInstance)!.Invoke(host, null)!).WaitAsync(Deadline);
                DesktopSessionProjection original = host.Projection;
                try { await exercise(new DownloadWindowFixture(window, host)); }
                finally
                {
                    Publish(host, original);
                    typeof(DesktopSessionHost).GetField("_captureAdmissionClosed", PrivateInstance)!.SetValue(host, false);
                }
            }
        });

    internal void ShowThreads(DesktopThreadEntry[] entries, params long[] checkable)
    {
        DesktopSessionProjection current = host.Projection;
        Publish(host, current with
        {
            Version = current.Version + 1,
            View = current.View with { Threads = entries },
            CheckableTargets = checkable.Select(id => new DiagnosticTarget(DiagnosticTargetKind.Thread, id)).ToArray()
        });
        CallWindow("RefreshSession");
        Layout();
        Assert.Equal(entries.Select(entry => entry.WatchId).Order(), Window.UrlList.Select(row => row.WatchId).Order());
    }

    internal void Select(params long[] ids)
    {
        Threads.SelectedItems.Clear();
        foreach (long id in ids) Threads.SelectedItems.Add(Window.UrlList.Single(row => row.WatchId == id));
        Layout();
        Assert.Equal(ids.Order(), Window.SelectedThreadsForTests().Select(row => row.WatchId).Order());
    }

    internal void Layout()
    {
        Dispatcher.UIThread.RunJobs();
        Window.UpdateLayout();
    }

    internal object? CallWindow(string name) => typeof(MainWindow).GetMethod(name, PrivateInstance)!.Invoke(Window, null);
    internal T WindowField<T>(string name) => (T)typeof(MainWindow).GetField(name, PrivateInstance)!.GetValue(Window)!;
    private static void Publish(DesktopSessionHost host, DesktopSessionProjection projection) =>
        typeof(DesktopSessionHost).GetField("_published", PrivateInstance)!.SetValue(host, projection);

    internal static DesktopThreadEntry Thread(long id, string? url = null, string directory = "",
        MediaWorkStatus? status = null)
    {
        url ??= "https://downloads.invalid/a/thread/" + id;
        ThreadWorkSnapshot? work = status is { } mediaStatus ? new(url, WorkIntent.Watching,
            ThreadSourceStateValue.Active, null, false, [new MediaWork
            {
                ResourceUrl = url, Generation = 1, MediaIdentifier = "media", DestinationPath = "media.png",
                DownloadUrl = "https://downloads.invalid/media.png", Status = mediaStatus
            }]) : null;
        return new DesktopThreadEntry(id, new ThreadSnapshot(new("offline", "a", id.ToString()), url, "Thread " + id,
            directory, ThreadActivity.Pending, ThreadSourceState.Active, null, 0, 0,
            new Dictionary<string, FailureInfo>(), null, new(0, 0)) { Work = work }, "Offline fixture", "/a/");
    }
}
