using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// New cases only: the selected late result already has Full29/Full44 evidence.
internal static class SettingsPickerLifetimeChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-picker-lifetime-");
        try
        {
            await CheckLateCompletion(owner, Path.Combine(root, "cancel"), failed: false);
            await CheckLateCompletion(owner, Path.Combine(root, "failure"), failed: true);
            Console.WriteLine("SMOKE picker lifetime: late injected cancellation/failure after EndLifetime produced no notifications, draft/config/file changes or leaked busy state; native dialog not exercised.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckLateCompletion(MainWindow owner, string profile, bool failed)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        var pending = new TaskCompletionSource<IReadOnlyList<IStorageFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SettingsChecks.FixtureStorageProvider { Result = pending.Task };
        AvaloniaLocator.CurrentMutable.Bind<IStorageProviderFactory>().ToConstant(new SettingsChecks.FixtureStorageProviderFactory(provider));
        Config config = Config.LoadConfig(profile);
        config.DownloadPath = Path.Combine(profile, "original");
        config.SaveConfig();
        string file = Path.Combine(profile, "settings.json");
        byte[] original = File.ReadAllBytes(file);
        string configPath = config.DownloadPath;
        var window = new SettingsWindow(config);
        Task dialog = window.ShowDialog(owner);
        List<string?> changes = new();
        PropertyChangedEventHandler observer = (_, args) => changes.Add(args.PropertyName);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            TextBox path = window.Editor.FindControl<TextBox>("DownloadPathInput")!;
            const string raw = "relative pending ü literal%20";
            path.Text = raw;
            await Program.WaitUntilAsync(() => window.Editor.DownloadPath == raw);
            window.Editor.FindControl<Button>("BrowseButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.Editor.IsBusy && provider.PickerCalls == 1, "Picker did not enter exactly one pending operation.");
            window.Editor.EndLifetime();
            window.Editor.PropertyChanged += observer;
            if (failed) pending.SetException(new IOException("Synthetic late picker failure"));
            else pending.SetResult(Array.Empty<IStorageFolder>());
            await Program.WaitUntilAsync(() => !window.Editor.IsBusy);
            Require(changes.Count == 0 && window.Editor.DownloadPath == raw && path.Text == raw
                && !window.Editor.HasError && window.Editor.ErrorDetails == null
                && config.DownloadPath == configPath && File.ReadAllBytes(file).SequenceEqual(original),
                "Retired picker cancellation/failure notified, showed an error, or changed path/config/file state.");
            Console.WriteLine($"SMOKE picker lifetime {(failed ? "late-failure" : "late-cancel")}: changes=0; raw/config/file unchanged; busy=false.");
        }
        finally
        {
            window.Editor.PropertyChanged -= observer;
            pending.TrySetResult(Array.Empty<IStorageFolder>());
            await Program.WaitUntilAsync(() => !window.Editor.IsBusy);
            if (window.IsVisible) window.Close();
            await dialog.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
