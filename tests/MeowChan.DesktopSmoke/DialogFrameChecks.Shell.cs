using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Shell group: permanent archive deletion, gallery details, About, first-run setup and the
// startup error window. Private fixtures only: an unknown thread id (nothing can be deleted),
// a synthetic gallery entry, an About link without a target, and an injected setup save failure.
internal static partial class DialogFrameChecks
{
    private static partial async Task RunShellAsync(MainWindow owner)
    {
        await CheckArchiveDeletionFrameAsync(owner);
        await CheckGalleryDetailsFrameAsync(owner);
        await CheckAboutFrameAsync(owner);
        await CheckFirstRunFrameAsync(owner);
        await CheckStartupErrorFrameAsync(owner);
    }

    private static async Task CheckArchiveDeletionFrameAsync(MainWindow owner)
    {
        Window? Dialog() => owner.OwnedWindows.FirstOrDefault(item => item.Name == "ArchivePermanentDeleteDialog");
        IReadOnlyList<ResourceId> unknown = [new ResourceId("dialog-frame", "none", "0")];
        Task running = Invoke(owner, "DeleteArchivePermanentlyAsync", unknown, null);
        await Program.WaitUntilAsync(() => Dialog()?.IsVisible == true || running.IsCompleted);
        if (running.IsCompleted) await running;
        Window dialog = Dialog() ?? throw new InvalidOperationException("Archive deletion dialog did not open.");
        Require(!ShellFind<Button>(dialog, "ArchivePermanentDeleteConfirm").IsEnabled, "Unknown selection must not be deletable.");
        Require(ShellFind<Button>(dialog, "ArchivePermanentDeleteConfirm").Classes.Contains("danger"), "Destructive action is not marked danger.");
        Button cancel = ShellFind<Button>(dialog, "ArchivePermanentDeleteCancel");
        Button confirm = ShellFind<Button>(dialog, "ArchivePermanentDeleteConfirm");
        Require(cancel.TranslatePoint(new Point(0, 0), confirm) is { } offset && offset.X < 0, "Cancel is not separated before the destructive action.");
        await VerifyFrameAsync(dialog, "shell archive delete", "shell-archive-delete");
        string blocker = Texts.Language == "de" ? "kann nicht vollständig gelöscht werden" : "cannot be fully deleted";
        await VerifyErrorStripAsync(dialog, "shell archive delete blocker", blocker);
        await AtMinimumAsync(dialog, "shell archive delete minimum", "shell-archive-delete-min");
        PressEscape(dialog);
        await Program.WaitUntilAsync(() => Dialog() == null);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task CheckGalleryDetailsFrameAsync(MainWindow owner)
    {
        Window? Dialog() => owner.OwnedWindows.FirstOrDefault(item => item.Name == "GalleryDetailsDialog");
        MediaAssetReference asset = new("dialog-frame-asset", "dialog-frame", "original", "frame.png",
            Path.Combine(Path.GetTempPath(), "dialog-frame", "frame.png"), 4096,
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", "https://offline.invalid/frame.png", "frame.png", true);
        GalleryEntry entry = new(new GalleryAssetKey(new ResourceId("4chan", "g", "123"), Path.GetDirectoryName(asset.Path)!, asset.AssetId),
            null!, asset, null, null, false, false);
        Task running = Invoke(owner, "ShowGalleryDetailsAsync", entry);
        await Program.WaitUntilAsync(() => Dialog()?.IsVisible == true || running.IsCompleted);
        if (running.IsCompleted) await running;
        Window dialog = Dialog() ?? throw new InvalidOperationException("Gallery details dialog did not open.");
        await VerifyFrameAsync(dialog, "shell gallery details", "shell-gallery-details");
        await AtMinimumAsync(dialog, "shell gallery details minimum", "shell-gallery-details-min");
        PressEscape(dialog);
        await Program.WaitUntilAsync(() => Dialog() == null);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static async Task CheckAboutFrameAsync(MainWindow owner)
    {
        AboutWindow about = new(); // no UpdateSession: no browser or update requests
        bool closed = false;
        about.Closed += (_, _) => closed = true;
        Task showing = about.ShowDialog(owner);
        try
        {
            await Program.WaitUntilAsync(() => about.IsVisible);
            await VerifyFrameAsync(about, "shell about", "shell-about");
            // A link without a target fails inside the launcher argument guard: no process starts.
            Button project = about.FindControl<Button>("ProjectLink")!;
            project.Tag = " ";
            project.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SelectableTextBlock error = about.FindControl<SelectableTextBlock>("LinkErrorText")!;
            await Program.WaitUntilAsync(() => !string.IsNullOrEmpty(error.Text));
            await VerifyErrorStripAsync(about, "shell about link error", error.Text);
            Capture(about, "shell-about-error");
            about.FindControl<TabControl>("AboutTabs")!.SelectedIndex = 2;
            await VerifyFrameAsync(about, "shell about licenses", "shell-about-licenses");
            await AtMinimumAsync(about, "shell about minimum", "shell-about-min");
            PressEscape(about);
            await Program.WaitUntilAsync(() => closed);
            await showing;
        }
        finally { if (!closed) { about.Close(); await showing; } }
    }

    private static async Task CheckFirstRunFrameAsync(MainWindow owner)
    {
        string root = TemporaryWorkspacePaths.CreateDirectory("meowchan-dialog-frame-setup-");
        try
        {
            string folder = Path.Combine(root, "downloads");
            Directory.CreateDirectory(folder);
            Config config = new() { FirstRunSetupPending = true, MinimumFreeSpaceGiB = 0, DownloadPath = folder };
            int attempts = 0;
            var window = new FirstRunSetupWindow(config,
                _ => { attempts++; return Task.FromException<FirstRunSetupCommitResult>(new IOException("fixture save failure")); },
                new DownloadDestinationValidator(1, (path, _) => new(DownloadDestinationStatus.Ready, path)));
            Task<FirstRunSetupRunResult> showing = window.ShowAsync(owner);
            try
            {
                await Program.WaitUntilAsync(() => window.IsVisible);
                await VerifyFrameAsync(window, "shell first run", "shell-first-run");
                window.FindControl<Button>("SetupAcceptButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Program.WaitUntilAsync(() => !window.IsWorking && window.FindControl<TextBlock>("SetupError")!.IsVisible);
                Require(attempts == 1 && window.IsVisible, "Injected setup save failure did not stay in the dialog.");
                await VerifyErrorStripAsync(window, "shell first run save error", Texts.T("setup.saveUnconfirmed"));
                Capture(window, "shell-first-run-error");
                await AtMinimumAsync(window, "shell first run minimum", "shell-first-run-min");
                window.FindControl<Button>("SetupLaterButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                FirstRunSetupRunResult result = await showing.WaitAsync(TimeSpan.FromSeconds(10));
                Require(result.Outcome == FirstRunSetupOutcome.Deferred, "Later after a failed save claimed completion.");
            }
            finally { if (window.IsVisible) window.Close(); await showing.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckStartupErrorFrameAsync(MainWindow owner)
    {
        string message = string.Join(Environment.NewLine, Enumerable.Range(1, 40)
            .Select(line => "Fixture startup failure line " + line + ": profile could not be opened."));
        StartupErrorWindow window = new(message);
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show(owner);
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible);
            await VerifyFrameAsync(window, "shell startup error", "shell-startup-error");
            await AtMinimumAsync(window, "shell startup error minimum", "shell-startup-error-min");
            PressEscape(window);
            await Program.WaitUntilAsync(() => closed);
        }
        finally { if (!closed) window.Close(); }
    }

    private static async Task AtMinimumAsync(Window window, string label, string capture)
    {
        double width = window.Width, height = window.Height;
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - window.MinWidth) <= 1
            && Math.Abs(window.ClientSize.Height - window.MinHeight) <= 1);
        await VerifyFrameAsync(window, label, capture);
        window.Width = width;
        window.Height = height;
        await Settle();
    }

    private static Task Invoke(MainWindow owner, string method, params object?[] arguments) =>
        (Task)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, arguments)!;

    private static void PressEscape(Window window)
    {
        KeyEventArgs key = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        window.RaiseEvent(key);
        Require(key.Handled, window.Name + " did not handle Escape.");
    }

    private static T ShellFind<T>(Window dialog, string name) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
}
