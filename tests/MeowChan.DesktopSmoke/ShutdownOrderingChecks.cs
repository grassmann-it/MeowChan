using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Persistence;
using MeowChan.Storage;

namespace MeowChan.DesktopSmoke;

// The shell's real cleanup barrier is held; VideoLifecycleChecks separately proves that
// the player retains actual native ownership. This test proves their shutdown ordering.
internal static class ShutdownOrderingChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        DownloadSessionSmokeApp fixture = Application.Current as DownloadSessionSmokeApp
            ?? throw new InvalidOperationException("Shutdown ordering needs the offline download application.");
        TimeSpan deadline = TimeSpan.FromSeconds(15);
        TaskCompletionSource releaseCleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        string profile = window.SessionHost.ProfileDirectory;
        string saved = StorageLayout.GetMediaPath(window.SessionHost.Config.DownloadPath, "4chan", "g", "123", "777", ".png");
        window.FindControl<TextBox>("UrlInput")!.Text = "https://boards.4chan.org/g/thread/123";
        window.FindControl<Button>("AddUrlButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await fixture.Transport.MediaRequested.WaitAsync(deadline);
        await Program.WaitUntilAsync(() => window.UrlList.Count == 1 && window.UrlList[0].Active == 1);
        window.Width = 1120;
        await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - 1120) <= 1);
        FieldInfo cleanup = typeof(MainWindow).GetField("_galleryCleanupTask", BindingFlags.Instance | BindingFlags.NonPublic)!;
        cleanup.SetValue(window, releaseCleanup.Task);
        try
        {
            window.FindControl<MenuItem>("ExitMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await fixture.Transport.MediaCancelled.WaitAsync(deadline);
            Require(!releaseCleanup.Task.IsCompleted && !closed.Task.IsCompleted && window.IsVisible,
                "Native/gallery drain was not still pending when download cancellation was observed.");
            Require(!File.Exists(saved), "A canceled incomplete transfer was published during shutdown.");
            Task<DesktopAdmissionResult> add = window.SessionHost.AddAsync("https://boards.4chan.org/g/thread/123");
            bool rejected = false;
            try { await add.WaitAsync(deadline); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Shutdown accepted a new interactive mutation while gallery drain was held.");
            bool leaseHeld = false;
            try { using ProfileLease unexpected = ProfileLease.Acquire(profile); }
            catch (IOException) { leaseHeld = true; }
            Require(leaseHeld, "The profile lease was released before real gallery cleanup completed.");
            releaseCleanup.TrySetResult();
            await closed.Task.WaitAsync(deadline);
            Require(window.SessionHost.State == DesktopSessionState.Stopped,
                "Normal close did not finish engine drain.");
            using (ProfileLease released = ProfileLease.Acquire(profile)) { }
            Require(Math.Abs(Config.LoadConfig(profile).WindowWidth - 1120) <= 1,
                "Shutdown lost the last pending window preference.");
            Console.WriteLine("SMOKE shutdown ordering: active HTTP cancellation and mutation closure before held gallery drain; lease retained, final preference saved, then true close passed.");
        }
        finally { releaseCleanup.TrySetResult(); fixture.Transport.AllowMedia(); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
