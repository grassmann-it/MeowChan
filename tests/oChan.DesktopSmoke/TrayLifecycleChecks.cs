using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using oChan.Desktop;
using oChan.Interfaces;
using oChan.Persistence;

namespace oChan.DesktopSmoke;

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
                MainWindow window = new MainWindow(new DesktopSessionHost(profile, Array.Empty<IImageBoard>()));
                try
                {
                    window.Show(owner);
                    await Program.WaitUntilAsync(() => window.FindControl<Button>("AddUrlButton")!.IsEnabled);
                    if (trayIcons > 0 && TrayIcon.GetIcons(Application.Current!)?.Count != trayIcons + 1)
                        throw new InvalidOperationException("A rapid window did not attach its native tray icon.");
                    // Close as soon as restoration permits, without waiting for DBus registration to settle.
                    window.Close();
                    await Program.WaitUntilAsync(() => !window.IsVisible);
                    if ((TrayIcon.GetIcons(Application.Current!)?.Count ?? 0) != trayIcons)
                        throw new InvalidOperationException("Rapid close leaked an application tray icon.");
                    using ProfileLease lease = ProfileLease.Acquire(profile);
                }
                finally
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                        await Program.WaitUntilAsync(() => !window.IsVisible);
                    }
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
