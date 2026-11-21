using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// R2 and R3 (14.11.2026): the reader's "Thread" menu holds the open thread's downloader-side actions (the post-capture switch left
/// the toolbar), "Follow live" is "Show new posts automatically" (the settings popover's Thread section) with a state text in the status line. The page needs
/// an Avalonia application, so the contract is checked on its catalog entries, its XAML and the menu builders' source; the
/// behaviour (enabled states, check marks, the shell callbacks) is the reader-thread-menu smoke.
/// </summary>
[Collection("Localization")]
public sealed class ReaderThreadMenuTests : IDisposable
{
    public ReaderThreadMenuTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root(string relative, [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative));

    private static string Source(string relative) => File.ReadAllText(Root(relative));

    private static string[] AddedCommandIds(string source, string method)
    {
        int start = source.IndexOf(method, StringComparison.Ordinal);
        Assert.True(start >= 0, method);
        string body = source[start..];
        return Regex.Matches(body, "Add\\(\"([a-z.\\-]+)\"").Select(match => match.Groups[1].Value).ToArray();
    }

    [Fact]
    public void TheMenuListsExactlyTheThreadActionsOfThePlanInTheirOrder()
    {
        string[] expected =
        [
            "archive.capture-toggle", "watch.check", "watch.retry-media", "rules.edit-scope",
            "post-bookmarks.open-manager", "thread.details", "thread.show-in-media", "location.open-folder", "location.open-source", "selection.copy-links", "watch.remove"
        ];
        Assert.Equal(expected, ArchivePage.ReaderThreadMenuIds);
        // The builder adds exactly those entries, in that order (separators after the media rules and before the watch).
        string source = Source("Desktop/Views/Archive/ArchivePage.ThreadMenu.cs");
        Assert.Equal(expected, AddedCommandIds(source, "internal ContextMenu BuildReaderThreadMenu()"));
        Assert.Equal(2, Regex.Matches(source, "new Separator\\(\\)").Count);
        // Deleting a thread permanently stays in the lists.
        Assert.DoesNotContain("archive.delete-permanently", source);
    }

    [Fact]
    public void EveryEntryIsARealCommandThatNamesTheThreadMenuAsAPlace()
    {
        foreach (string id in ArchivePage.ReaderThreadMenuIds)
        {
            CommandDefinition command = CommandCatalog.Get(id);
            Assert.Contains("reader-thread-menu", command.Contexts);
            Assert.False(string.IsNullOrWhiteSpace(CommandCatalog.Label(id)), id);
        }
        // The old place of the capture switch (the toolbar) is gone from the catalog.
        Assert.DoesNotContain("reader-header", CommandCatalog.Get("archive.capture-toggle").Contexts);
    }

    [Fact]
    public void EveryDisabledEntryCanExplainItselfWithTheLanguagesOfTheArchiveMenu()
    {
        string source = Source("Desktop/Views/Archive/ArchivePage.ThreadMenu.cs");
        foreach (string key in new[] { "archive.command.busy", "archive.command.noCheck", "archive.command.noRetry",
            "archive.command.noWatch", "archive.command.noFolder", "archive.command.noSource" })
        {
            Assert.Contains("\"" + key + "\"", source);
            Assert.NotEqual(key, Texts.T(key));
        }
    }

    [Fact]
    public void TheToolbarHasAThreadMenuButtonRightBeforeTheSettingsGearAndNoCaptureButton()
    {
        XDocument page = XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml"));
        XElement[] items = page.Descendants().Where(element => element.Name.LocalName == "ActionToolBar").Single(bar => (string?)bar.Attribute(X + "Name") == "ReaderToolbar")
            .Elements().ToArray();
        string?[] names = items.Select(item => (string?)item.Attribute(X + "Name")).ToArray();
        Assert.DoesNotContain("CapturePostsButton", names);
        int gear = Array.IndexOf(names, "ReaderSettingsOpen"), thread = Array.IndexOf(names, "ReaderThreadOpen");
        Assert.True(gear >= 0 && thread == gear + 1, "Thread ▾ stands right after the settings gear (Tom, 21.11.: settings, then thread).");
        Assert.DoesNotContain("ReaderViewOpen", names);
        Assert.Contains("OnReaderThreadMenuClick", (string?)items[thread].Attribute("Click"));
        // Work-view id of the menu button (the policy row of the same id decides bar or More).
        Assert.Contains(items[thread].Attributes(), attribute => attribute.Name.LocalName == "WorkViewElement.Id" && attribute.Value == "reader.toolbar.thread");
        // The export button stands before full screen, which is the last item and the first to move under More (review 15.11.).
        Assert.True(Array.IndexOf(names, "ExportArchiveButton") < Array.IndexOf(names, "ArchiveReaderFullscreen"));
    }

    [Fact]
    public void FullScreenGetsItsLabelFromTheCommandCatalogInEveryLanguage()
    {
        Assert.Contains("CommandBindings.Apply(ArchiveReaderFullscreen, \"window.fullscreen\"", Source("Desktop/Views/Archive/ArchivePage.axaml.cs"));
    }

    [Fact]
    public void TheViewMenuIsGoneItsOptionsAreRowsOfTheSettingsPopoverAndTheMediaRulesStayInTheThreadMenu()
    {
        string source = Source("Desktop/Views/Archive/ArchivePage.ReaderView.cs");
        Assert.DoesNotContain("BuildReaderViewMenu", source);
        Assert.DoesNotContain("OnReaderViewMenuClick", source);
        Assert.DoesNotContain("ReaderViewOpen", Source("Desktop/Views/Archive/ArchivePage.axaml"));
        Assert.DoesNotContain(MeowChan.Desktop.ReaderQuickSettings.Layout, entry => entry is MeowChan.Desktop.QuickSettingsSessionEntry { CommandId: "rules.edit-scope" });
        Assert.Contains("rules.edit-scope", ArchivePage.ReaderThreadMenuIds);
    }

    [Fact]
    public void ShowNewPostsAutomaticallyKeepsItsPlaceInTheOptionOrderAndIsTheOldFollowLiveCommand()
    {
        FieldInfo order = typeof(ArchivePage).GetField("ReaderViewOptionOrder", BindingFlags.NonPublic | BindingFlags.Static)!;
        string[] ids = (string[])order.GetValue(null)!;
        // R6 (media switches), R8 (media sidebar) and R7 (reader settings) joined the menu after the conversation; "Show new posts
        // automatically" still comes after the conversation and right before the post filters, and the reader settings close it.
        Assert.Equal(["reader.view.hide-media", "reader.view.media-only", "reader.view.conversation", "reader.view.textless-large",
            "reader.view.focused-enlarge", "reader.view.hover-enlarge", "reader.sidebar.toggle", "watch.follow-live", "reader.post-filters",
            "reader.quick-settings"], ids);
        int live = Array.IndexOf(ids, "watch.follow-live");
        Assert.True(live > Array.IndexOf(ids, "reader.view.conversation") && ids[live + 1] == "reader.post-filters");
        // The command keeps its id, so saved user shortcuts stay valid; its label and place changed.
        CommandDefinition command = CommandCatalog.Get("watch.follow-live");
        Assert.Equal("Show new posts automatically", command.LabelEn);
        Assert.Contains("reader-settings", command.Contexts);
        Assert.Equal("Primary+Shift+L", command.Shortcut);
        Texts.SetLanguage("de");
        try { Assert.Equal("Neue Posts automatisch anzeigen", CommandCatalog.Label("watch.follow-live")); }
        finally { Texts.SetLanguage("en"); }
    }

    [Fact]
    public void TheStatusLineShowsTheLiveStateAsTextAndHasNoFollowLiveCheckBox()
    {
        XDocument page = XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml"));
        Assert.DoesNotContain(page.Descendants(A + "CheckBox"), box => (string?)box.Attribute(X + "Name") == "ReaderFollowLive");
        XElement state = page.Descendants(A + "TextBlock").Single(text => (string?)text.Attribute(X + "Name") == "ReaderLiveState");
        // Read aloud when it changes; not shown while following is off.
        Assert.Equal("Polite", (string?)state.Attributes().Single(attribute => attribute.Name.LocalName == "AutomationProperties.LiveSetting"));
        Assert.Equal("False", (string?)state.Attribute("IsVisible"));
        foreach (string key in new[] { "reader.live.stateOn", "reader.liveHint", "reader.thread.menu", "reader.thread.copyLink" })
            Assert.NotEqual(key, Texts.T(key));
    }

    [Fact]
    public void TheShortcutToggleAndTheMenuEntryDoNotBothHandleTheKey()
    {
        // The shell routes Ctrl+Shift+L itself (ToggleFollowLiveByShortcut); the view options' key handler must skip that entry,
        // otherwise one key press would switch it on and off again.
        string live = Source("Desktop/Views/Archive/ArchivePage.Live.cs");
        Assert.Contains("shortcutViaRouter: true", live);
        Assert.Contains("option.ShortcutViaRouter ||", Source("Desktop/Views/Archive/ArchivePage.ReaderView.cs"));
        Assert.Contains("live.ToggleFollowLiveByShortcut", Source("Desktop/Shell/MainWindow.Shortcuts.cs"));
    }
}
