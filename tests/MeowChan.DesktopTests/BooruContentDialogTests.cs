using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Persistence;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BooruContentDialogTests
{
    private static BooruContentProfileSnapshot Snapshot(BooruContentProfileState state)
    {
        BooruContentSettings settings = new(BooruPostClaimRating.Sensitive, string.Empty, []);
        return new BooruContentProfileSnapshot(state, settings, new BooruContentFilter(1, settings));
    }

    private static T ById<T>(Control root, string id) where T : Control => root.GetVisualDescendants().OfType<T>()
        .First(control => AutomationProperties.GetAutomationId(control) == id);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button BooruNavigation(MainWindow window) => Assert.Single(window.GetLogicalDescendants().OfType<Button>(),
        button => ControlAutomationPeer.CreatePeerForElement(button).GetAutomationId() == "BooruNavigationButton");

    private static DesktopSessionHost CreateSession(string profile, string rules)
    {
        new Config
        {
            DownloadPath = Path.Combine(profile, "media"), FirstRunSetupVersion = 1, WorkspaceMode = "Advanced",
            ShowBooruTools = true, AutomaticUpdateChecks = false
        }.SaveConfig(profile);
        File.WriteAllText(Path.Combine(profile, BooruContentProfileStore.FileName), rules);
        return new(profile, [],
            updateSessionFactory: (directory, config) => new UpdateSession(directory, config, disableEnvironment: () => "1"));
    }

    [Fact]
    public void DialogUsesTheSharedDialogLanguageAndHasNoImplicitDefaultAction() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        BooruContentDialog dialog = new(Snapshot(BooruContentProfileState.Missing), (_, _) => Task.CompletedTask);
        dialog.Show();
        try
        {
            Button confirm = ById<Button>(dialog, "BooruContentConfirm");
            Assert.False(confirm.IsDefault);
            Assert.Contains("primary", confirm.Classes);
            Assert.False(dialog.ShowInTaskbar);
            Border errorHost = Assert.Single(dialog.GetVisualDescendants().OfType<Border>(),
                border => border.Name == DialogFrame.ErrorStripName);
            Assert.False(errorHost.IsVisible);
            Border strip = Assert.IsType<Border>(errorHost.Child);
            ScrollViewer scroll = Assert.IsType<ScrollViewer>(strip.Child);
            TextBlock status = Assert.IsType<TextBlock>(scroll.Content);
            Assert.Contains("workspace-error", status.Classes);
            Assert.False(status.IsVisible);
            ComboBox maximum = ById<ComboBox>(dialog, "BooruContentMaximum");
            Assert.True(maximum.IsEnabled);
            TextBlock prompt = Assert.Single(dialog.GetVisualDescendants().OfType<TextBlock>(),
                block => block.Text == Texts.T("booru.content.prompt"));
            Assert.Same(prompt, AutomationProperties.GetLabeledBy(maximum));
            Assert.Null(AutomationProperties.GetName(maximum));
            Assert.Equal(prompt.Text, ControlAutomationPeer.CreatePeerForElement(maximum).GetName());
        }
        finally { dialog.Close(false); }
    });

    [Fact]
    public void DialogCannotBeClosedAndInputsAreLockedWhileSaving() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        TaskCompletionSource gate = new();
        BooruContentDialog dialog = new(Snapshot(BooruContentProfileState.Missing), (_, _) => gate.Task);
        dialog.Show();
        try
        {
            ComboBox maximum = ById<ComboBox>(dialog, "BooruContentMaximum");
            TextBox blacklist = ById<TextBox>(dialog, "BooruContentBlacklist");
            Button confirm = ById<Button>(dialog, "BooruContentConfirm");
            Click(confirm);
            Assert.False(maximum.IsEnabled);
            Assert.False(blacklist.IsEnabled);
            dialog.Close(false);
            Assert.True(dialog.IsVisible);
            gate.SetResult();
            BooruComponentDispatcher.WaitUntil(() => !dialog.IsVisible, "the dialog to close after saving");
        }
        finally { if (dialog.IsVisible) { dialog.Close(false); } }
    });

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RetryingARevisionConflictPreservesLocalEditsAndNewRules(bool editRating, bool editBlacklist) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken writer = lease.Admission.EnterOrdinaryWriter();
        BooruContentProfileStore store = new(lease);
        BooruContentProfileSnapshot initial = store.SaveAdmitted(new(BooruPostClaimRating.Explicit, "cat", []), 0, writer);
        List<long> revisions = [];
        BooruContentDialog dialog = new(initial, (settings, revision) =>
        {
            revisions.Add(revision);
            store.SaveAdmitted(settings, revision, writer);
            return Task.CompletedTask;
        });
        dialog.Show();
        try
        {
            BooruContentSourceBlacklist sourceRule = new("e621", "default", "bird");
            BooruContentProfileSnapshot changed = store.SaveAdmitted(
                new(BooruPostClaimRating.General, "fox", [sourceRule]), 1, writer);
            Assert.Equal(2L, changed.Revision);
            ComboBox maximum = ById<ComboBox>(dialog, "BooruContentMaximum");
            TextBox blacklist = ById<TextBox>(dialog, "BooruContentBlacklist");
            if (editRating) maximum.SelectedIndex = 1;
            if (editBlacklist) blacklist.Text = "dog";
            Button confirm = ById<Button>(dialog, "BooruContentConfirm");
            Click(confirm);
            BooruComponentDispatcher.WaitUntil(() => confirm.IsEnabled, "the failed save to settle");
            Assert.True(dialog.IsVisible);
            BooruContentProfileSnapshot afterConflict = store.Load();
            Assert.Equal(changed.Revision, afterConflict.Revision);
            Assert.Equal(sourceRule, Assert.Single(afterConflict.Settings.SourceBlacklists));
            Assert.Equal(Texts.T(editRating ? "booru.content.sensitive" : "booru.content.general"), maximum.SelectedItem);
            Assert.Equal(editBlacklist ? "dog" : "fox", blacklist.Text);
            Click(confirm);
            BooruComponentDispatcher.WaitUntil(() => !dialog.IsVisible, "the retry to close the dialog");
            Assert.Equal([1L, 2L], revisions);
            BooruContentProfileSnapshot saved = store.Load();
            Assert.Equal(3L, saved.Revision);
            Assert.Equal(editRating ? BooruPostClaimRating.Sensitive : BooruPostClaimRating.General, saved.Settings.MaximumRating);
            Assert.Equal(editBlacklist ? "dog" : "fox", saved.Settings.GlobalBlacklist);
            Assert.Equal(sourceRule, Assert.Single(saved.Settings.SourceBlacklists));
        }
        finally { if (dialog.IsVisible) dialog.Close(false); }
    });

    [Theory]
    [InlineData("{", nameof(BooruContentProfileState.Unreadable))]
    [InlineData("""{"schemaVersion":99}""", nameof(BooruContentProfileState.Unsupported))]
    public void ReopeningBooruAppliesAnExternallyRepairedDocument(string brokenJson, string expectedState) => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        string path = Path.Combine(profile.Path, BooruContentProfileStore.FileName);
        await using DesktopSessionHost session = CreateSession(profile.Path, brokenJson);
        BooruWorkspaceContent content = BooruWorkspaceContent.ForProfile(session.ProfileLeaseForTests);
        Assert.Equal(expectedState, content.Snapshot.State.ToString());
        BooruWorkbenchServices ports = new BooruUiHarness().Services;
        session.BooruServices = new(ports.Search, ports.Queue, ports.Watchers, ports.Previews) { Content = content };
        await using MainWindow window = new(session) { Width = 1440, Height = 800, Classes = { MotionPolicy.OffClass } };
        window.Show();
        window.PlatformImpl!.Activated?.Invoke();
        await window.StartupTimingRestoreTask;

        Button navigation = BooruNavigation(window);
        await BooruTestSupport.WaitAsync(() => navigation.IsEffectivelyEnabled, "the Booru navigation button");
        Click(navigation);
        await BooruTestSupport.WaitAsync(() => window.OwnedWindows.OfType<BooruContentDialog>().Any(dialog => dialog.IsVisible),
            "the content rules dialog");
        BooruContentDialog dialog = Assert.Single(window.OwnedWindows.OfType<BooruContentDialog>());
        Assert.False(ById<Button>(dialog, "BooruContentConfirm").IsVisible);
        Button cancel = ById<Button>(dialog, "BooruContentCancel");
        Assert.True(cancel.IsEffectivelyEnabled);
        TextBlock[] visibleText = dialog.GetVisualDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).ToArray();
        Assert.Contains(visibleText, block => block.Text == Texts.T("booru.content.readError", BooruContentProfileStore.FileName));
        Assert.DoesNotContain(visibleText, block => block.Text == Texts.T("booru.content.saveError"));
        Click(cancel);
        await BooruTestSupport.WaitAsync(() => !dialog.IsVisible && navigation.IsEffectivelyEnabled, "the closed dialog");
        Assert.Equal(brokenJson, await File.ReadAllTextAsync(path));

        const string repaired = """{"schemaVersion":1,"revision":2,"settings":{"maximumRating":"general","globalBlacklist":"cat","sourceBlacklists":[]}}""";
        await File.WriteAllTextAsync(path, repaired);
        Assert.False(content.HasChoice);
        Click(navigation);
        await BooruTestSupport.WaitAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Booru, "Booru navigation after repair");
        Assert.Empty(window.OwnedWindows.OfType<BooruContentDialog>());
        Assert.True(content.HasChoice);
        Assert.Equal(2L, content.Snapshot.Revision);
        Assert.Equal("cat", content.Snapshot.Settings.GlobalBlacklist);
        Assert.Equal(repaired, await File.ReadAllTextAsync(path));
    });

    [Fact]
    public void EditingAfterExternalRepairUpdatesActiveResultsEvenWhenCanceled() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        string path = Path.Combine(profile.Path, BooruContentProfileStore.FileName);
        const string initial = """{"schemaVersion":1,"revision":1,"settings":{"maximumRating":"explicit","globalBlacklist":"","sourceBlacklists":[]}}""";
        await using DesktopSessionHost session = CreateSession(profile.Path, initial);
        BooruWorkspaceContent content = BooruWorkspaceContent.ForProfile(session.ProfileLeaseForTests);
        BooruUiHarness harness = new();
        BooruPostView explicitPost = BooruUiFixtures.Post("1", BooruOriginalAccess.NotOffered,
            rating: BooruRating.Explicit, tags: ["dog"], preview: false);
        BooruPostView blacklistedPost = BooruUiFixtures.Post("2", BooruOriginalAccess.NotOffered, tags: ["cat"], preview: false);
        BooruPostView allowedPost = BooruUiFixtures.Post("3", BooruOriginalAccess.NotOffered, tags: ["dog"], preview: false);
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, explicitPost, blacklistedPost, allowedPost)));
        BooruWorkbenchServices ports = harness.Services;
        session.BooruServices = new(ports.Search, ports.Queue, ports.Watchers, ports.Previews) { Content = content };
        await using MainWindow window = new(session) { Width = 1440, Height = 800, Classes = { MotionPolicy.OffClass } };
        window.Show();
        window.PlatformImpl!.Activated?.Invoke();
        await window.StartupTimingRestoreTask;

        Button navigation = BooruNavigation(window);
        await BooruTestSupport.WaitAsync(() => navigation.IsEffectivelyEnabled, "the Booru navigation button");
        Click(navigation);
        BooruPage page = Assert.IsType<BooruPage>(window.BooruPageForTests);
        BooruSearchModel search = page.Model.Search;
        await BooruTestSupport.SearchAsync(search, "1boy");
        Assert.Equal(3, search.View.Rows.Length);

        const string repaired = """{"schemaVersion":1,"revision":2,"settings":{"maximumRating":"general","globalBlacklist":"cat","sourceBlacklists":[]}}""";
        await File.WriteAllTextAsync(path, repaired);
        window.UpdateLayout();
        Button edit = ById<Button>(page.View.Header, "BooruContentSettings");
        Assert.Same(window, TopLevel.GetTopLevel(edit));
        Assert.True(edit.IsEffectivelyVisible);
        Assert.True(edit.IsEffectivelyEnabled);
        Click(edit);
        await BooruTestSupport.WaitAsync(() => window.OwnedWindows.OfType<BooruContentDialog>().Any(dialog => dialog.IsVisible),
            "the content rules dialog");
        BooruContentDialog dialog = Assert.Single(window.OwnedWindows.OfType<BooruContentDialog>());
        Assert.Equal(Texts.T("booru.content.general"), ById<ComboBox>(dialog, "BooruContentMaximum").SelectedItem);
        TextBox blacklist = ById<TextBox>(dialog, "BooruContentBlacklist");
        Assert.Equal("cat", blacklist.Text);
        Assert.Equal(2L, content.Snapshot.Revision);
        Assert.Equal(allowedPost.Identity, Assert.Single(search.View.Rows).Post.Identity);

        blacklist.Text = "dog";
        Click(ById<Button>(dialog, "BooruContentCancel"));
        await BooruTestSupport.WaitAsync(() => !dialog.IsVisible && navigation.IsEffectivelyEnabled, "the closed dialog");
        Click(window.FindControl<Button>("DownloadsNavigationButton")!);
        Assert.Equal(MainWindow.WorkspacePage.Downloads, window.SelectedPage);
        Click(BooruNavigation(window));
        Assert.Equal(MainWindow.WorkspacePage.Booru, window.SelectedPage);
        Assert.Same(page, window.BooruPageForTests);
        Assert.Empty(window.OwnedWindows.OfType<BooruContentDialog>());
        Assert.Equal(allowedPost.Identity, Assert.Single(search.View.Rows).Post.Identity);
        Assert.False(search.IsMediaAllowed(explicitPost));
        Assert.False(search.IsMediaAllowed(blacklistedPost));
        Assert.True(search.IsMediaAllowed(allowedPost));
        Assert.Equal(repaired, await File.ReadAllTextAsync(path));
    });
}
