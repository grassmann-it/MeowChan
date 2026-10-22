using System.Diagnostics;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

[CollectionDefinition("External launcher", DisableParallelization = true)]
public sealed class ExternalLauncherCollection { }

[Collection("External launcher")]
public sealed class ExternalLauncherTests
{
    [Theory]
    [InlineData("firefox.desktop", "firefox")]
    [InlineData("chromium.desktop", "chromium")]
    [InlineData("google-chrome.desktop", "google-chrome")]
    [InlineData("brave-browser.desktop", "brave-browser")]
    [InlineData("microsoft-edge.desktop", "microsoft-edge")]
    [InlineData("vivaldi.desktop", "vivaldi")]
    public async Task SelectedKnownBrowserOpensExactUrlInNewWindow(string desktopId, string executable)
    {
        if (!OperatingSystem.IsLinux()) return;
        using LauncherFixture fixture = new(desktopId, executable);
        const string url = "https://www.paypal.com/donate/?hosted_button_id=5BR4MK748PDSQ&note='literal';value";

        await ExternalLauncher.OpenAsync(url).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "2", "--new-window", url, "unset", "/custom/gtk", "/usr/share:/var/lib/snapd/desktop" },
            File.ReadAllLines(fixture.BrowserCapture));
        Assert.False(File.Exists(fixture.FallbackCapture));
    }

    [Theory]
    [InlineData("custom-browser.desktop", "https://example.invalid/?a=1&b=2")]
    [InlineData("firefox.desktop", "/tmp/meowchan-folder")]
    public async Task UnknownBrowserOrNonHttpTargetUsesDesktopHandler(string desktopId, string target)
    {
        if (!OperatingSystem.IsLinux()) return;
        using LauncherFixture fixture = new(desktopId);

        await ExternalLauncher.OpenAsync(target).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { "1", target, "unset", "/custom/gtk", "/usr/share:/var/lib/snapd/desktop" },
            File.ReadAllLines(fixture.FallbackCapture));
        Assert.False(File.Exists(fixture.BrowserCapture));
    }

    [Fact]
    public async Task MissingKnownBrowserFallsBackToDesktopHandler()
    {
        if (!OperatingSystem.IsLinux()) return;
        using LauncherFixture fixture = new("firefox.desktop");

        await ExternalLauncher.OpenAsync("https://example.invalid/").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("https://example.invalid/", File.ReadAllLines(fixture.FallbackCapture)[1]);
    }

    [Fact]
    public async Task MissingBrowserSettingFallsBackToDesktopHandler()
    {
        if (!OperatingSystem.IsLinux()) return;
        using LauncherFixture fixture = new(null);

        await ExternalLauncher.OpenAsync("https://example.invalid/").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("https://example.invalid/", File.ReadAllLines(fixture.FallbackCapture)[1]);
    }

    [Fact]
    public async Task FailedSelectedBrowserReportsItsExitCode()
    {
        if (!OperatingSystem.IsLinux()) return;
        using LauncherFixture fixture = new("firefox.desktop", "firefox", browserExitCode: 4);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExternalLauncher.OpenAsync("https://example.invalid/").WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("code 4", error.Message);
        Assert.False(File.Exists(fixture.FallbackCapture));
    }

    [Fact]
    public async Task FailedFallbackReportsItsExitCode()
    {
        if (!OperatingSystem.IsLinux()) return;
        using LauncherFixture fixture = new("custom-browser.desktop", fallbackExitCode: 5);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExternalLauncher.OpenAsync("https://example.invalid/").WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("code 5", error.Message);
    }

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

    private sealed class LauncherFixture : IDisposable
    {
        private readonly Dictionary<string, string?> _original;
        private readonly string _directory;
        public string BrowserCapture => Path.Combine(_directory, "browser.txt");
        public string FallbackCapture => Path.Combine(_directory, "fallback.txt");

        public LauncherFixture(string? desktopId, string? browserExecutable = null, int browserExitCode = 0, int fallbackExitCode = 0)
        {
            _directory = Path.Combine(Path.GetTempPath(), "meowchan-launch-choice-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            string[] keys = ["PATH", "MEOWCHAN_BROWSER_CAPTURE", "MEOWCHAN_FALLBACK_CAPTURE", "GIO_MODULE_DIR", "GTK_PATH", "XDG_DATA_DIRS"];
            _original = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
            if (desktopId != null)
                WriteScript("xdg-settings", $"#!/bin/sh\nprintf '%s\\n' '{desktopId}'\n");
            WriteScript("xdg-open", "#!/bin/sh\nprintf '%s\\n' \"$#\" \"$1\" \"${GIO_MODULE_DIR-unset}\" \"${GTK_PATH-unset}\" \"${XDG_DATA_DIRS-unset}\" > \"$MEOWCHAN_FALLBACK_CAPTURE\"\nexit " + fallbackExitCode + "\n");
            if (browserExecutable != null)
                WriteScript(browserExecutable, "#!/bin/sh\nprintf '%s\\n' \"$#\" \"$1\" \"$2\" \"${GIO_MODULE_DIR-unset}\" \"${GTK_PATH-unset}\" \"${XDG_DATA_DIRS-unset}\" > \"$MEOWCHAN_BROWSER_CAPTURE\"\nexit " + browserExitCode + "\n");
            Environment.SetEnvironmentVariable("PATH", _directory);
            Environment.SetEnvironmentVariable("MEOWCHAN_BROWSER_CAPTURE", BrowserCapture);
            Environment.SetEnvironmentVariable("MEOWCHAN_FALLBACK_CAPTURE", FallbackCapture);
            Environment.SetEnvironmentVariable("GIO_MODULE_DIR", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "snap/code/common/.cache/gio-modules"));
            Environment.SetEnvironmentVariable("GTK_PATH", "/snap/code/271/usr/lib/gtk-3.0:/custom/gtk");
            Environment.SetEnvironmentVariable("XDG_DATA_DIRS", "/snap/code/271/usr/share:/usr/share:/var/lib/snapd/desktop");
        }

        private void WriteScript(string name, string contents)
        {
            if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
            string path = Path.Combine(_directory, name);
            File.WriteAllText(path, contents);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            foreach ((string key, string? value) in _original)
                Environment.SetEnvironmentVariable(key, value);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
