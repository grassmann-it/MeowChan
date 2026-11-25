using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_PROFILE_SWITCH_ONLY. PR1 (profiles) in a real second shell over a private fixture profile:
// "⋯ › Profile ▸ New profile…" refuses the current profile folder, creates a new profile with its own download folder and
// hands it to the launcher; "Switch profile…" lists both, opens the new one, and refuses a profile another window holds.
// The launcher is a recording fake (no process is started) and the profile list lives in the fixture folder, so neither
// the real profile nor the real profile list is touched. Managed input only, no OS folder picker.
internal static class ProfileSwitchChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private sealed class RecordingLauncher : IProfileLauncher
    {
        internal readonly List<string> Started = new();
        internal readonly List<string?> MediaFolders = new();
        public void Launch(string profileDirectory, string? mediaFolder = null) { Started.Add(profileDirectory); MediaFolders.Add(mediaFolder); }
    }

    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        string root = Path.Combine(owner.SessionHost.ProfileDirectory, "profile-switch-fixture");
        string profile = Path.Combine(root, "current");
        try
        {
            Texts.SetLanguage("en");
            new Config { DownloadPath = Path.Combine(root, "downloads"), DownloadsPaused = true, SaveUrlsOnExit = false, CloseToTray = false,
                MinimizeToTray = false, WorkspaceMode = "Simple" }.SaveConfig(profile);
            await RunShellAsync(owner, root, profile);
            Console.WriteLine("SMOKE profile switch: Collection and App groups, Profile accessible name in English and German; "
                + "New profile refused the current folder and created a profile with its own downloads and id; Switch profile listed both, "
                + "launched the selected folder and refused a profile held by another window; fake launcher, private list; managed input only.");
        }
        finally { Texts.SetLanguage(language); }
    }

    private static async Task RunShellAsync(MainWindow owner, string root, string profile)
    {
        await using DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await using MainWindow shell = new(host) { Width = 1200, Height = 860 };
        RecordingLauncher launcher = new();
        shell.ProfileLauncher = launcher;
        shell.ProfileRegistryPath = Path.Combine(root, "config", "MeowChan", ProfileRegistry.FileName);
        shell.Show(owner);
        Exception? failure = null;
        try
        {
            await UntilAsync(() => host.State == DesktopSessionState.Ready && shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            MenuItem menu = shell.FindControl<MenuItem>("WorkspaceMenuItem")!;
            MenuItem profileMenu = MenuItemById(menu, "ProfileMenuItem");
            MenuItem settings = MenuItemById(menu, "SettingsMenuItem");
            // HeaderMenuGroups (43a1a9c1a) places Profile in Collection and Settings in App.
            RequireGroup(menu, profileMenu, "menu.group.collection");
            RequireGroup(menu, settings, "menu.group.app");
            Require(AccessibleName(profileMenu) == Texts.T("profile.menu"), "The Profile submenu has the wrong accessible name in English.");
            Texts.SetLanguage("de");
            await UntilAsync(() => AccessibleName(profileMenu) == Texts.T("profile.menu"));
            Texts.SetLanguage("en");
            await UntilAsync(() => AccessibleName(profileMenu) == Texts.T("profile.menu"));
            MenuItem createItem = MenuItemById(profileMenu, "ProfileNewMenuItem");
            MenuItem switchItem = MenuItemById(profileMenu, "ProfileSwitchMenuItem");
            MenuItem folderItem = MenuItemById(profileMenu, "ProfileFolderMenuItem");
            Require(profileMenu.Items.OfType<MenuItem>().SequenceEqual(new[] { createItem, switchItem, folderItem }),
                "The Profile submenu does not hold New, Switch and Open folder.");
            Require(AccessibleName(createItem) == CommandCatalog.Label("profile.new")
                && AccessibleName(switchItem) == CommandCatalog.Label("profile.switch")
                && AccessibleName(folderItem) == CommandCatalog.Label("profile.open-folder"),
                "A profile action does not expose its catalog label.");

            // New profile: the current folder is refused, a fresh folder is created and handed to the launcher.
            await InvokeProfileMenuAsync(menu, profileMenu, createItem);
            await UntilAsync(() => shell.ProfileDialogWindow is NewProfileDialog { IsVisible: true });
            NewProfileDialog create = (NewProfileDialog)shell.ProfileDialogWindow!;
            Require(create.Title == "New profile" && create.FolderBox.Text!.Contains(".MeowChan-profiles", StringComparison.Ordinal), "The dialog does not suggest a profile folder.");
            Require(!create.ShowInTaskbar, "The New profile dialog has its own taskbar entry.");
            create.NameBox.Text = "Cats";
            create.FolderBox.Text = profile;
            create.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => create.ErrorText.Text == Texts.T("profile.error.folderCurrent"));
            string target = Path.Combine(root, "profiles", "cats"), downloads = Path.Combine(root, "cats-downloads");
            create.FolderBox.Text = target;
            create.DownloadsBox.Text = downloads;
            Program.CaptureWindow(create, "smoke-profile-new.png");
            create.CreateButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => shell.ProfileDialogWindow == null && launcher.Started.Count == 1);
            Require(launcher.Started[0] == target, "The launcher did not get the new profile folder.");
            Require(Config.LoadConfig(target).DownloadPath == downloads && ProfileInstanceIdentity.TryRead(target) != null,
                "The new profile has no own download folder or no id.");
            ProfileRegistry registry = new(shell.ProfileRegistryPath);
            Require(registry.Read().Entries.Select(entry => entry.Path).SequenceEqual(new[] { host.ProfileDirectory, target }), "The list does not hold both profiles.");

            // Switch profile: both rows, the new one opens; a profile held by another window cannot be opened.
            await InvokeProfileMenuAsync(menu, profileMenu, switchItem);
            await UntilAsync(() => shell.ProfileDialogWindow is SwitchProfileDialog { IsVisible: true });
            SwitchProfileDialog change = (SwitchProfileDialog)shell.ProfileDialogWindow!;
            await UntilAsync(() => change.LoadTask.IsCompleted); // the rows appear at once; the folders answer afterwards
            Require(!change.ShowInTaskbar, "The Switch profile dialog has its own taskbar entry.");
            Require(change.Rows.Children.Count == 2 && !change.EmptyText.IsVisible, "The switch dialog does not list two profiles.");
            Program.CaptureWindow(change, "smoke-profile-switch.png");
            Button open = OpenButton(change, target);
            Require(open.IsEnabled, "The new profile cannot be opened.");
            open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => shell.ProfileDialogWindow == null && launcher.Started.Count == 2);
            Require(launcher.Started[1] == target, "Switch profile did not launch the selected profile folder.");
            using (ProfileLease held = ProfileLease.Acquire(target))
            {
                await InvokeProfileMenuAsync(menu, profileMenu, switchItem);
                await UntilAsync(() => shell.ProfileDialogWindow is SwitchProfileDialog { IsVisible: true });
                change = (SwitchProfileDialog)shell.ProfileDialogWindow!;
                await UntilAsync(() => change.LoadTask.IsCompleted);
                Require(!OpenButton(change, target).IsEnabled, "A profile held by another window can still be opened.");
                Require(change.Rows.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == Texts.T("profile.state.inUse")), "The held profile is not marked as open.");
                Program.CaptureWindow(change, "smoke-profile-switch-in-use.png");
                change.Close();
                await UntilAsync(() => shell.ProfileDialogWindow == null);
            }
            Require(launcher.Started.Count == 2 && shell.IsVisible, "A refused profile was started, or this window closed.");
        }
        catch (Exception thrown) { failure = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failure, () => CloseAsync(host, shell),
                exception => Console.Error.WriteLine("SMOKE cleanup failed in ProfileSwitchChecks: " + exception));
        }
    }

    private static string? AccessibleName(MenuItem item) => ControlAutomationPeer.CreatePeerForElement(item)?.GetName();

    private static MenuItem MenuItemById(MenuItem parent, string id) =>
        parent.Items.OfType<MenuItem>().Single(item => ControlAutomationPeer.CreatePeerForElement(item)?.GetAutomationId() == id);

    private static void RequireGroup(MenuItem menu, MenuItem item, string labelKey)
    {
        MenuItem? label = menu.Items.TakeWhile(entry => !ReferenceEquals(entry, item)).Reverse()
            .TakeWhile(entry => entry is not Separator).OfType<MenuItem>()
            .FirstOrDefault(entry => entry.Classes.Contains("menu-group-label"));
        Require(label?.Tag is string key && key == labelKey && AccessibleName(label) == Texts.T(labelKey),
            $"The {item.Name} menu item is not in the '{Texts.T(labelKey)}' group.");
    }

    private static async Task InvokeProfileMenuAsync(MenuItem menu, MenuItem profileMenu, MenuItem action)
    {
        menu.IsSubMenuOpen = true;
        try
        {
            await UntilAsync(() => profileMenu.IsEffectivelyVisible && profileMenu.Bounds.Width > 0);
            profileMenu.IsSubMenuOpen = true;
            await UntilAsync(() => action.IsEffectivelyVisible && action.IsEffectivelyEnabled && action.Bounds.Width > 0);
            SmokeActionClick.Execute(action);
        }
        finally
        {
            profileMenu.IsSubMenuOpen = false;
            menu.IsSubMenuOpen = false;
        }
    }

    private static Button OpenButton(SwitchProfileDialog dialog, string folder) =>
        dialog.Rows.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Tag, folder));

    private static async Task CloseAsync(DesktopSessionHost host, MainWindow shell)
    {
        foreach (Window child in shell.OwnedWindows.Where(child => child.IsVisible).ToArray()) child.Close();
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(Deadline);
        await UntilAsync(() => !shell.IsVisible);
        await host.DisposeAsync().AsTask().WaitAsync(Deadline);
        Require(host.IsProfileReleased, "The fixture host did not release its private profile.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Profile switch UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
