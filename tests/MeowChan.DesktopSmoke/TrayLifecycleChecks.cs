using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

/// <summary>Exercises rapid tray creation/disposal in its own smoke subprocess.</summary>
internal static class TrayLifecycleChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(Config.GetConfigDirectory(), "rapid-tray");
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "downloads");
        config.SaveConfig();
        int ownedWindows = owner.OwnedWindows.Count;
        int trayIcons = TrayIcon.GetIcons(Application.Current!)?.Count ?? 0;
        try
        {
            for (int iteration = 0; iteration < 16; iteration++)
            {
                Stopwatch phase = Stopwatch.StartNew();
                Console.WriteLine($"SMOKE tray cycle {iteration + 1}/16 begin; shortcut observers={CommandBindings.ObserverCount}.");
                DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
                Console.WriteLine($"SMOKE tray cycle {iteration + 1}/16 host construction={phase.Elapsed.TotalMilliseconds:F1}ms.");
                phase.Restart();
                MainWindow window = new MainWindow(host);
                Console.WriteLine($"SMOKE tray cycle {iteration + 1}/16 window construction={phase.Elapsed.TotalMilliseconds:F1}ms; shortcut observers={CommandBindings.ObserverCount}.");
                bool closed = false;
                window.Closed += (_, _) => closed = true;
                Exception? failureBeforeCleanup = null;
                try
                {
                    phase.Restart();
                    window.Show(owner);
                    await Program.WaitUntilAsync(() => window.FindControl<Button>("AddUrlButton")!.IsEnabled);
                    Console.WriteLine($"SMOKE tray cycle {iteration + 1}/16 show-to-ready={phase.Elapsed.TotalMilliseconds:F1}ms.");
                    if (trayIcons > 0 && TrayIcon.GetIcons(Application.Current!)?.Count != trayIcons + 1)
                        throw new InvalidOperationException("A rapid window did not attach its native tray icon.");
                    // Close as soon as restoration permits, without waiting for DBus registration to settle.
                    phase.Restart();
                    window.Close();
                    // Hide is only the start of bounded shutdown; Closed follows drain, lease release and tray disposal.
                    await Program.WaitUntilAsync(() => closed);
                    Console.WriteLine($"SMOKE tray cycle {iteration + 1}/16 close-to-closed={phase.Elapsed.TotalMilliseconds:F1}ms; shortcut observers={CommandBindings.ObserverCount}.");
                    if (!host.IsProfileReleased || host.State != DesktopSessionState.Stopped)
                        throw new InvalidOperationException("Rapid close completed before releasing its session owner.");
                    if ((TrayIcon.GetIcons(Application.Current!)?.Count ?? 0) != trayIcons)
                        throw new InvalidOperationException("Rapid close leaked an application tray icon.");
                    using ProfileLease lease = ProfileLease.Acquire(profile);
                }
                catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
                finally
                {
                    await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
                    {
                        if (!closed)
                        {
                            window.Close();
                            await Program.WaitUntilAsync(() => closed);
                        }
                    }, exception => Console.Error.WriteLine("SMOKE cleanup failed in TrayLifecycleChecks: " + exception));
                }
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (owner.OwnedWindows.Count != ownedWindows)
                throw new InvalidOperationException("Rapid close left an owned window behind.");
            Program.CaptureWindow(owner, "smoke-tray-lifecycle.png");
            Console.WriteLine($"SMOKE tray lifecycle: 16 rapid native windows and profile lease reuses; native tray attachment exercised={trayIcons > 0}; no registration sleeps or exception suppression. Xvfb uses the stated no-tray fixture.");
        }
        finally
        {
            Directory.Delete(profile, true);
        }
    }
}
