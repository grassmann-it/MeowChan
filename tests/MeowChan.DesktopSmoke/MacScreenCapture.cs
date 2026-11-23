using System.Diagnostics;
using SkiaSharp;

namespace MeowChan.DesktopSmoke;

internal static class MacScreenCapture
{
    internal static async Task TryCaptureAsync(string stage)
    {
        string? directory = Environment.GetEnvironmentVariable("MEOWCHAN_MAC_SCREENSHOT_DIR");
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(directory))
            return;

        string image = Path.Combine(directory, $"macos-screen-{stage}.png");
        // Publish only completed captures. The existing artifact upload excludes hidden files.
        string temporary = Path.Combine(directory, $".macos-screen-{stage}.png");
        try
        {
            // Let Cocoa paint the visible window or finish the Space transition before sampling.
            await Task.Delay(350);
            ProcessStartInfo info = new("/usr/sbin/screencapture") { UseShellExecute = false };
            info.ArgumentList.Add("-x");
            info.ArgumentList.Add("-t");
            info.ArgumentList.Add("png");
            info.ArgumentList.Add(temporary);
            using Process capture = Process.Start(info)
                ?? throw new InvalidOperationException("screencapture did not start");
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(4));
            try
            {
                await capture.WaitForExitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                if (!capture.HasExited) capture.Kill();
                throw new TimeoutException("screencapture timed out");
            }
            if (capture.ExitCode != 0)
                throw new InvalidOperationException($"screencapture exited with {capture.ExitCode}; check screen recording permission and the GUI session");

            using SKBitmap? bitmap = SKBitmap.Decode(temporary);
            if (bitmap == null)
                throw new InvalidDataException("screencapture produced no readable PNG");
            if (Array.TrueForAll(bitmap.Pixels, pixel => pixel.Alpha == 0 || (pixel.Red < 8 && pixel.Green < 8 && pixel.Blue < 8)))
                throw new InvalidDataException("the captured screen is black or transparent");
            File.Move(temporary, image);
            Console.WriteLine($"SMOKE macOS screenshot {stage}: {image}");
        }
        catch (Exception error)
        {
            Warn(stage, error);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) { Warn(stage, error); }
        }
    }

    private static void Warn(string stage, Exception error) =>
        Console.Error.WriteLine($"WARNING macOS screenshot {stage}: {error.Message}");
}
