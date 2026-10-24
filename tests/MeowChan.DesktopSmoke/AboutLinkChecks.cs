using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan;

namespace MeowChan.DesktopSmoke;

internal static class AboutLinkChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1")
            throw new InvalidOperationException("About link checks require the private Linux Xvfb desktop.");

        string profile = Environment.GetEnvironmentVariable("MEOWCHAN_PROFILE")
            ?? throw new InvalidOperationException("The smoke profile is missing.");
        string fixture = Path.Combine(profile, "about-link-fixture");
        Directory.CreateDirectory(fixture);
        string capture = Path.Combine(fixture, "captured.txt");
        string launcher = Path.Combine(fixture, "xdg-open");
        await File.WriteAllTextAsync(launcher, "#!/bin/sh\nexit 91\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string settings = Path.Combine(fixture, "xdg-settings");
        await File.WriteAllTextAsync(settings, "#!/bin/sh\nprintf '%s\\n' firefox.desktop\n");
        File.SetUnixFileMode(settings, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string browser = Path.Combine(fixture, "firefox");
        await File.WriteAllTextAsync(browser, """
            #!/bin/sh
            printf '%s\n' "$#" "$1" "$2" "${GIO_MODULE_DIR-unset}" "${GTK_PATH-unset}" "${XDG_DATA_DIRS-unset}" "${LD_LIBRARY_PATH-unset}" >> "$MEOWCHAN_ABOUT_CAPTURE"
            """ + "\n");
        File.SetUnixFileMode(browser, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string[] keys = ["PATH", "MEOWCHAN_ABOUT_CAPTURE", "GIO_MODULE_DIR", "GTK_PATH", "XDG_DATA_DIRS", "LD_LIBRARY_PATH"];
        Dictionary<string, string?> original = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        AboutWindow? about = null;
        try
        {
            Environment.SetEnvironmentVariable("PATH", fixture + Path.PathSeparator + original["PATH"]);
            Environment.SetEnvironmentVariable("MEOWCHAN_ABOUT_CAPTURE", capture);
            Environment.SetEnvironmentVariable("GIO_MODULE_DIR", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "snap/code/common/.cache/gio-modules"));
            Environment.SetEnvironmentVariable("GTK_PATH", "/snap/code/271/usr/lib/gtk-3.0:/custom/gtk");
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", "/snap/code/271/usr/share:/usr/share:/var/lib/snapd/desktop");
            Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", "/snap/code/271/usr/lib:/usr/lib");

            window.FindControl<MenuItem>("AboutMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => window.OwnedWindows.OfType<AboutWindow>().Any(candidate => candidate.IsVisible));
            about = window.OwnedWindows.OfType<AboutWindow>().Single();

            await ClickAndVerifyAsync(about.FindControl<Button>("ProjectLink")!, capture, 0,
                "https://github.com/grassmann-it/MeowChan");
            await ClickAndVerifyAsync(about.FindControl<Button>("DonationLink")!, capture, 1,
                "https://www.paypal.com/donate/?hosted_button_id=5BR4MK748PDSQ");
            if (!string.IsNullOrEmpty(about.FindControl<SelectableTextBlock>("LinkErrorText")!.Text))
                throw new InvalidOperationException("About reported a launcher failure despite the successful fixture handoff.");
            about.Close();
            about = null;
            await UpdateGuiChecks.RunAsync(window, capture);
            Console.WriteLine("SMOKE About project and PayPal buttons handed exact new-window URLs to the selected fake Firefox with sanitized Snap paths.");
        }
        finally
        {
            if (about?.IsVisible == true)
                about.FindControl<Button>("CloseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            foreach (string key in keys)
                Environment.SetEnvironmentVariable(key, original[key]);
        }
    }

    private static async Task ClickAndVerifyAsync(Button button, string capture, int clickIndex, string expectedUrl)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => File.Exists(capture) && File.ReadAllLines(capture).Length >= (clickIndex + 1) * 7);
        string[] values = File.ReadAllLines(capture);
        string[] expected = ["2", "--new-window", expectedUrl, "unset", "/custom/gtk", "/usr/share:/var/lib/snapd/desktop", "/usr/lib"];
        if (values.Length != (clickIndex + 1) * 7 || !values.Skip(clickIndex * 7).SequenceEqual(expected))
            throw new InvalidOperationException($"About link {clickIndex + 1} reached the launcher with incorrect URL or runtime environment: {string.Join(" | ", values)}");
    }
}
