using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.FolderSources;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class InlineConfirmationFullscreenTests
{
    [Fact]
    public void EscapeCancelsTaggingClearAndKeepsFullscreenAndTheQueue() => WithFullscreenShell(async (window, host) =>
    {
        TagQueueItem item = new(new("4chan", "g", "123"), host.ProfileDirectory, "asset-1", new string('a', 64),
            1, "media-1", "original", "1", null, null, null, false, null, "1.png", SavedMediaKind.Image,
            TagQueueState.Open, DateTimeOffset.UnixEpoch);
        await host.EnqueueTagQueueAsync([item]);
        string path = Path.Combine(host.ProfileDirectory, TagQueueStore.FileName);
        byte[] before = await File.ReadAllBytesAsync(path);
        Assert.True(window.ShowTaggingPage());
        await BooruTestSupport.WaitAsync(() => window.TaggingPage is { IsBusy: false, Queue.Count: 1 }, "the tagging queue");
        TaggingPage page = window.TaggingPage!;
        Button clear = page.Actions.Items.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "tagging.clear");
        Assert.True(clear.Command!.CanExecute(null));
        clear.Command.Execute(null);
        window.UpdateLayout();
        Assert.True(page.ClearQuestionOpen);
        Assert.True(FindButton(page, "tagging.clear-cancel")!.IsFocused);

        Button stamp = FindButton(page, "tagging.stamp")!;
        Flyout flyout = Assert.IsType<Flyout>(stamp.Flyout);
        flyout.ShowAt(stamp);
        TextBox editor = Assert.IsAssignableFrom<Control>(flyout.Content).GetVisualDescendants().OfType<TextBox>()
            .Single(control => AutomationProperties.GetAutomationId(control) == "tagging.stamp-input");
        await BooruTestSupport.WaitAsync(() => flyout.IsOpen && editor.IsFocused, "focus in the open stamp editor");

        PressEscape(window);

        Assert.False(flyout.IsOpen);
        Assert.True(stamp.IsFocused);
        Assert.True(page.ClearQuestionOpen);
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));

        PressEscape(window);

        Assert.False(page.ClearQuestionOpen);
        Assert.True(page.ShownList.IsKeyboardFocusWithin);
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        Assert.Single(host.GetTagQueue()!.Items);
    });

    [Fact]
    public void EscapeCancelsFolderSourceRemovalAndKeepsFullscreenAndTheSource() => WithFullscreenShell(async (window, host) =>
    {
        using TempDirectory folder = new();
        FolderSourceRoot source = await host.AddFolderSourceAsync(folder.Path, "Photos", includeSubfolders: false);
        window.FindControl<Button>("SettingsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        SettingsEditor editor = window.SettingsEditor;
        await Dispatcher.UIThread.InvokeAsync(editor.UpdateLayout, DispatcherPriority.Background);
        editor.OpenSectionNavigation();
        const string storageSection = "settings.worker-budgets";
        Assert.True(editor.CanGoToSection(storageSection));
        editor.GoToSection(storageSection);
        FolderSourcesPanel panel = editor.FolderSourcesSection!;
        string removeName = "FolderSourceRemove_" + FolderSourceIdentity.Format(source.Id);
        await BooruTestSupport.WaitAsync(() => FindButton(panel, removeName) is { IsEffectivelyVisible: true, IsEffectivelyEnabled: true },
            "the linked folder in Settings");
        FindButton(panel, removeName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await BooruTestSupport.WaitAsync(() => FindButton(panel, "FolderSourceRemoveKeep") is { IsFocused: true }, "focus on Keep folder");
        Assert.True(FindButton(panel, "FolderSourceRemoveConfirm")!.IsEffectivelyVisible);

        PressEscape(window);

        Assert.Null(FindButton(panel, "FolderSourceRemoveConfirm"));
        Assert.True(FindButton(panel, removeName)!.IsFocused);
        Assert.Equal(WindowState.FullScreen, window.WindowState);
        Assert.Equal(source.Id, Assert.Single(host.ListFolderSources()).Root.Id);
    });

    private static Button? FindButton(Control root, string id) => root.GetVisualDescendants().OfType<Button>()
        .SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == id);

    private static void PressEscape(MainWindow window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void WithFullscreenShell(Func<MainWindow, DesktopSessionHost, Task> test) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruComponentDispatcher.Wait(ExerciseAsync());

        async Task ExerciseAsync()
        {
            using TempDirectory profile = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "Downloads"), DownloadsPaused = true, FolderSourceBackgroundHashing = false,
                FirstRunSetupVersion = 1, WorkspaceMode = "Advanced", Language = "en"
            }.SaveConfig(profile.Path);
            await using DesktopSessionHost host = new(profile.Path, [],
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(host) { Width = 1440, Height = 800, Classes = { MotionPolicy.OffClass } };
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            await window.StartupTimingRestoreTask;
            window.ToggleFullscreen();
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            await test(window, host);
        }
    });
}
