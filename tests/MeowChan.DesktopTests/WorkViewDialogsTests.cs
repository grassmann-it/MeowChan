using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>WV9 (plan AP5 §1.14): dialogs, tray, menu bars, the one-time notice, the view command and the help sentences.</summary>
[Collection("Localization")]
public sealed class WorkViewDialogsTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public WorkViewDialogsTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    // ---- Dialog matrix: only two extras fold, nothing that protects the user ----

    [Fact]
    public void OnlyTheBoardTextRulesAndTheExportExtrasFoldAndEverythingElseIsRequired()
    {
        WorkViewEntry[] rows = WorkViewPolicy.Entries.Where(entry => entry.Page == WorkViewPage.Dialogs).ToArray();
        WorkViewEntry[] reduced = rows.Where(entry => WorkViewSetting.All.Any(view => entry.For(view) != Presence.Required)).ToArray();
        Assert.Equal(["dialogs.board-admission.text-rules", "dialogs.export.options"], reduced.Select(entry => entry.Id).Order(StringComparer.Ordinal));
        Assert.All(reduced, entry => Assert.Equal(WorkViewKind.Section, entry.Kind));
        Assert.Equal([Presence.Collapsed, Presence.Direct, Presence.Direct],
            WorkViewSetting.All.Select(view => WorkViewPolicy.Get("dialogs.board-admission.text-rules").For(view)));
        Assert.Equal([Presence.Collapsed, Presence.Collapsed, Presence.Direct],
            WorkViewSetting.All.Select(view => WorkViewPolicy.Get("dialogs.export.options").For(view)));
    }

    [Theory]
    [InlineData("dialogs.board-admission.preview")]
    [InlineData("dialogs.export.html-safety")]
    [InlineData("dialogs.first-run-setup")]
    [InlineData("dialogs.delete-confirmation")]
    [InlineData("dialogs.archive-cleanup")]
    [InlineData("dialogs.archive-deletion")]
    [InlineData("dialogs.experimental-activation")]
    [InlineData("dialogs.plugin-approval")]
    [InlineData("dialogs.sdk-transition")]
    [InlineData("dialogs.media-conflict")]
    [InlineData("dialogs.tray-menu")]
    [InlineData("dialogs.mac-menu-bar")]
    public void SafetyDialogsTrayAndMenuBarAreRequiredInEveryViewEvenWithADefaultState(string id)
    {
        Assert.True(WorkViewPolicy.Get(id).Safety);
        foreach (WorkView view in WorkViewSetting.All)
        {
            Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view));
            Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view, nonDefaultState: true));
        }
    }

    [Fact]
    public void FoldedSectionsStartClosedInTheirViewsAndAStateOpensThem()
    {
        foreach ((string id, WorkView view, bool open) in new[]
        {
            ("dialogs.board-admission.text-rules", WorkView.Barebones, false), ("dialogs.board-admission.text-rules", WorkView.Simple, true),
            ("dialogs.board-admission.text-rules", WorkView.Advanced, true), ("dialogs.export.options", WorkView.Barebones, false),
            ("dialogs.export.options", WorkView.Simple, false), ("dialogs.export.options", WorkView.Advanced, true)
        })
        {
            Expander section = new() { IsExpanded = !open };
            WorkViewElement.SetId(section, id);
            WorkViewPresenter.ApplyExpander(section, view, viewChanged: true);
            Assert.Equal(open, section.IsExpanded);
            // R6: rules in use, an invalid rule or an update in progress open it in every view.
            Expander withState = new() { IsExpanded = false };
            WorkViewElement.SetId(withState, id);
            WorkViewPresenter.ApplyExpander(withState, view, viewChanged: true, nonDefaultState: true);
            Assert.True(withState.IsExpanded);
        }
    }

    // ---- Tray and macOS menu bar are never reduced ----

    [Fact]
    public void TheMacMenuBarAndTheTrayModelDoNotTakeTheView()
    {
        foreach (Type type in new[] { typeof(MacMenuModel), typeof(MacMenuNode), Type.GetType("MeowChan.Desktop.TrayMenuModel, MeowChan")! })
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                Assert.DoesNotContain(method.GetParameters(), parameter => parameter.ParameterType == typeof(WorkView) || parameter.ParameterType == typeof(WorkViewPolicy));
        // Every page stays one menu entry in the View menu in all views, whatever the footer shows (R3).
        string[] viewMenu = MacMenuModel.Build().Single(menu => menu.Id == "view").Items.Where(node => node.CommandId != null).Select(node => node.CommandId!).ToArray();
        Assert.All(MainPageOrder.CommandIds, id => Assert.Contains(id, viewMenu));
        Assert.Contains("session.pause", MacMenuModel.CommandIds());
    }

    [Fact]
    public void TheCodeOfTheTrayAndTheMacMenuNeverReadsTheView()
    {
        string[] files =
        [
            "Desktop/Commands/MacMenuModel.cs", "Desktop/Shell/MainWindow.MacMenu.cs", "Desktop/TrayMenuModel.cs", "Desktop/TrayStatsModel.cs",
            "Desktop/TrayStatsPresenter.cs", "Desktop/TrayStatsClaim.cs", "Desktop/LinuxTrayHost.cs", "Desktop/Shell/MainWindow.TrayStats.cs",
            "Desktop/Shell/MainWindow.StatisticsTray.cs"
        ];
        foreach (string file in files)
        {
            // The mac menu forwards the bindable cycle command to the window; that call is the only mention of a view.
            string text = File.ReadAllText(Root(file)).Replace("CycleWorkViewAsync", string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkView", text);
            Assert.DoesNotContain("WorkspaceMode", text);
        }
        // The tray menu itself is built in the window's code: from SetupTrayIcon to the end of RefreshTray.
        string window = File.ReadAllText(Root("Desktop/Shell/MainWindow.axaml.cs"));
        int start = window.IndexOf("private void SetupTrayIcon()", StringComparison.Ordinal);
        int end = window.IndexOf("private void RefreshTray()", StringComparison.Ordinal);
        Assert.InRange(start, 1, end);
        string tray = window[start..window.IndexOf("\n    }\n", end, StringComparison.Ordinal)];
        Assert.DoesNotContain("WorkView", tray);
        Assert.DoesNotContain("WorkspaceMode", tray);
        Assert.DoesNotContain("ConfiguredWorkView", tray);
    }

    // ---- The view command: bindable, no default shortcut ----

    [Fact]
    public void ViewCycleIsABindableCommandWithoutADefaultShortcutAndStepsThroughTheViews()
    {
        CommandDefinition command = CommandCatalog.Get("workspace.view-cycle");
        Assert.Null(command.Shortcut);
        Assert.Empty(command.DefaultGestures);
        Assert.Null(CommandCatalog.ShortcutDisplay("workspace.view-cycle"));
        Assert.Equal("Switch work view", command.Label);
        Assert.Contains("shell", command.ExecutionContexts);
        Assert.Equal([WorkView.Simple, WorkView.Advanced, WorkView.Barebones],
            WorkViewSetting.All.Select(WorkViewSetting.Next));
        Texts.SetLanguage("de");
        Assert.Equal("Arbeitsansicht wechseln", CommandCatalog.Label("workspace.view-cycle"));
    }

    // ---- Help (R9) and overflow name (R7) ----

    [Fact]
    public void TheShortcutHelpMarksOnlyCommandsWhoseButtonTheViewKeepsUnderMore()
    {
        WorkViewPage[] downloads = WorkViewHelp.HelpPages("Downloads", largeReaderOpen: false);
        Assert.True(WorkViewHelp.UnderMore("watch.copy-all-links", WorkView.Barebones, downloads));
        Assert.True(WorkViewHelp.UnderMore("watch.copy-all-links", WorkView.Simple, downloads)); // goal policy: Simple keeps it under More too
        Assert.False(WorkViewHelp.UnderMore("watch.copy-all-links", WorkView.Advanced, downloads));
        Assert.False(WorkViewHelp.UnderMore("watch.copy-all-links", WorkView.Barebones, WorkViewHelp.HelpPages("Archive", false)));
        // Footer destinations are in the shell's Tools menu where the view reduces them.
        Assert.True(WorkViewHelp.UnderMore("navigation.archive", WorkView.Barebones, downloads));
        Assert.False(WorkViewHelp.UnderMore("navigation.archive", WorkView.Simple, downloads));
        Assert.True(WorkViewHelp.UnderMore("navigation.rules", WorkView.Simple, downloads));
        Assert.False(WorkViewHelp.UnderMore("navigation.rules", WorkView.Advanced, downloads));
        Assert.False(WorkViewHelp.UnderMore("session.pause", WorkView.Barebones, downloads));
        Assert.False(WorkViewHelp.UnderMore("no.such.command", WorkView.Barebones, downloads));
        Assert.Equal([WorkViewPage.Shell, WorkViewPage.Reader], WorkViewHelp.HelpPages("Archive", largeReaderOpen: true));
        Assert.Equal([WorkViewPage.Shell, WorkViewPage.Statistics], WorkViewHelp.HelpPages("Statistics", false));
    }

    [Fact]
    public void TheOverflowButtonAnnouncesHowManyActionsItHolds()
    {
        Assert.Equal("More", ActionToolBar.OverflowAccessibleName(0));
        Assert.Equal("More, 1 action", ActionToolBar.OverflowAccessibleName(1));
        Assert.Equal("More, 5 actions", ActionToolBar.OverflowAccessibleName(5));
        Texts.SetLanguage("de");
        Assert.Equal("Mehr, 5 Aktionen", ActionToolBar.OverflowAccessibleName(5));
        Assert.Equal("Mehr, 1 Aktion", ActionToolBar.OverflowAccessibleName(1));
        Texts.SetLanguage("en");
    }

    [Fact]
    public void TheViewDescriptionsSayWhatIsMissingAndTheOldTextsAreGone()
    {
        Assert.Equal("Downloads first; the other tools are under More.", Texts.T("workview.description.barebones"));
        Texts.SetLanguage("de");
        Assert.Equal("Downloads im Vordergrund; Werkzeuge stehen unter Mehr.", Texts.T("workview.description.barebones"));
        Assert.DoesNotContain("mode.barebonesDescription", BuiltInCatalog.English.Keys);
        Assert.DoesNotContain("downloads.more", BuiltInCatalog.English.Keys);
        Assert.Contains("workview.moreOptions", BuiltInCatalog.English.Keys);
    }

    // ---- One-time notice for existing profiles ----

    [Fact]
    public void AStoredProfileWithoutTheFieldIsToldOnceAndNewOrSavedProfilesAreNot()
    {
        using TempDirectory directory = new();
        string file = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(file, "{\"WorkspaceMode\":\"Barebones\"}");
        Config existing = Config.LoadConfig(directory.Path);
        Assert.True(existing.WorkViewNoticePending);
        Assert.Equal("Barebones", existing.WorkspaceMode); // the saved view stays unchanged

        // Saving without the notice settled keeps it pending; settling it persists false and never turns it on again.
        existing.SaveConfig(directory.Path);
        Assert.True(Config.LoadConfig(directory.Path).WorkViewNoticePending);
        Config settled = existing.Clone();
        settled.WorkViewNoticePending = false;
        settled.SaveConfig(directory.Path);
        Config reloaded = Config.LoadConfig(directory.Path);
        Assert.False(reloaded.WorkViewNoticePending);
        Assert.Equal("Barebones", reloaded.WorkspaceMode);
        Config copy = new();
        copy.CopyFrom(reloaded);
        Assert.False(copy.WorkViewNoticePending);

        using TempDirectory fresh = new();
        Assert.False(Config.LoadConfig(fresh.Path).WorkViewNoticePending);
        Assert.False(new Config().WorkViewNoticePending);
        Assert.False(new Config { WorkspaceMode = "Simple" }.Clone().WorkViewNoticePending);
    }

    [Fact]
    public void TheNoticeTextsExistInEveryPackAndUseTheSharedKeys()
    {
        Assert.Equal("This view now shows fewer tools; everything is under More.", Texts.T("workview.notice.text"));
        foreach (string key in new[] { "workview.notice.switchAdvanced", "workview.notice.dismiss" })
            Assert.NotEqual(key, Texts.T(key));
        Texts.SetLanguage("de");
        Assert.Equal("Diese Ansicht zeigt jetzt weniger Werkzeuge; alles steht unter Mehr.", Texts.T("workview.notice.text"));
        Assert.Equal("Zu Erweitert wechseln", Texts.T("workview.notice.switchAdvanced"));
    }

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
