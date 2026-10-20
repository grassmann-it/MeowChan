using System.Diagnostics;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ExternalLauncherTests
{
    [Fact]
    public async Task LinuxHandoffPreservesTheWholeUrlAndUsesSystemRuntimePaths()
    {
        if (!OperatingSystem.IsLinux()) return;
        string directory = Path.Combine(Path.GetTempPath(), "meowchan-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string url = "https://example.invalid/thread/123?one=with%20space&two='quoted';literal";
            string script = Path.Combine(directory, "launcher fixture.sh");
            string output = Path.Combine(directory, "received.txt");
            await File.WriteAllTextAsync(script,
                "#!/bin/sh\nprintf '%s\\n' \"$#\" \"$1\" \"${GIO_MODULE_DIR-unset}\" \"$GTK_PATH\" \"$XDG_DATA_DIRS\" > \"$MEOWCHAN_LAUNCH_TEST_OUTPUT\"\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ProcessStartInfo info = ExternalLauncher.CreateStartInfo(url);
            info.FileName = script; // No browser or desktop is involved in this fixture.
            info.Environment["MEOWCHAN_LAUNCH_TEST_OUTPUT"] = output;
            info.Environment["GIO_MODULE_DIR"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "snap/code/common/.cache/gio-modules");
            info.Environment["GTK_PATH"] = "/snap/code/271/usr/lib/gtk-3.0:/custom/gtk";
            info.Environment["XDG_DATA_DIRS"] = "/snap/code/271/usr/share:/usr/share:/var/lib/snapd/desktop";
            string? parentGio = Environment.GetEnvironmentVariable("GIO_MODULE_DIR");
            ExternalLauncher.RemoveSnapRuntimePaths(info);

            await ExternalLauncher.StartAsync(info).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(new[] { "1", url, "unset", "/custom/gtk", "/usr/share:/var/lib/snapd/desktop" },
                await File.ReadAllLinesAsync(output));
            Assert.Equal(parentGio, Environment.GetEnvironmentVariable("GIO_MODULE_DIR"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedLinuxHandoffIsReportedEvenWhenTheProcessStarted()
    {
        if (!OperatingSystem.IsLinux()) return;
        ProcessStartInfo info = ExternalLauncher.CreateStartInfo("https://example.invalid/");
        info.FileName = "/bin/sh";
        info.ArgumentList.Clear();
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("exit 4");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExternalLauncher.StartAsync(info).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("code 4", error.Message);
    }
}
