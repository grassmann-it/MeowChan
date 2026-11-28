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
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class BooruContentDialogTests
{
    private static BooruContentProfileSnapshot Snapshot(BooruContentProfileState state)
    {
        BooruContentSettings settings = new(BooruPostClaimRating.Sensitive, string.Empty, []);
        return new BooruContentProfileSnapshot(state, settings, new BooruContentFilter(1, settings));
    }

    private static T ById<T>(Window window, string id) where T : Control => window.GetVisualDescendants().OfType<T>()
        .First(control => AutomationProperties.GetAutomationId(control) == id);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
            TextBlock status = dialog.GetVisualDescendants().OfType<TextBlock>().First(block => block.Classes.Contains("workspace-error"));
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

    [Fact]
    public void RetryingARevisionConflictPreservesNewSourceBlacklists() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        using ProfileLease lease = ProfileLease.Acquire(profile.Path);
        lease.Admission.CompleteStartup();
        using ProfileAdmissionToken writer = lease.Admission.EnterOrdinaryWriter();
        BooruContentProfileStore store = new(lease);
        BooruContentProfileSnapshot initial = store.SaveAdmitted(new(BooruPostClaimRating.General, "cat", []), 0, writer);
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
            BooruContentProfileSnapshot changed = store.SaveAdmitted(initial.Settings with { SourceBlacklists = [sourceRule] }, 1, writer);
            Assert.Equal(2L, changed.Revision);
            ById<ComboBox>(dialog, "BooruContentMaximum").SelectedIndex = 1;
            ById<TextBox>(dialog, "BooruContentBlacklist").Text = "dog";
            Button confirm = ById<Button>(dialog, "BooruContentConfirm");
            Click(confirm);
            BooruComponentDispatcher.WaitUntil(() => confirm.IsEnabled, "the failed save to settle");
            Assert.True(dialog.IsVisible);
            BooruContentProfileSnapshot afterConflict = store.Load();
            Assert.Equal(changed.Revision, afterConflict.Revision);
            Assert.Equal(sourceRule, Assert.Single(afterConflict.Settings.SourceBlacklists));
            Click(confirm);
            BooruComponentDispatcher.WaitUntil(() => !dialog.IsVisible, "the retry to close the dialog");
            Assert.Equal([1L, 2L], revisions);
            BooruContentProfileSnapshot saved = store.Load();
            Assert.Equal(3L, saved.Revision);
            Assert.Equal(BooruPostClaimRating.Sensitive, saved.Settings.MaximumRating);
            Assert.Equal("dog", saved.Settings.GlobalBlacklist);
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
        await File.WriteAllTextAsync(path, brokenJson);
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "media"), FirstRunSetupVersion = 1, WorkspaceMode = "Advanced",
            ShowBooruTools = true, AutomaticUpdateChecks = false
        }.SaveConfig(profile.Path);
        await using DesktopSessionHost session = new(profile.Path, [],
            updateSessionFactory: (directory, config) => new UpdateSession(directory, config, disableEnvironment: () => "1"));
        BooruWorkspaceContent content = BooruWorkspaceContent.ForProfile(session.ProfileLeaseForTests);
        Assert.Equal(expectedState, content.Snapshot.State.ToString());
        BooruWorkbenchServices ports = new BooruUiHarness().Services;
        session.BooruServices = new(ports.Search, ports.Queue, ports.Watchers, ports.Previews) { Content = content };
        await using MainWindow window = new(session) { Width = 1440, Height = 800, Classes = { MotionPolicy.OffClass } };
        window.Show();
        window.PlatformImpl!.Activated?.Invoke();
        await window.StartupTimingRestoreTask;

        Button navigation = Assert.Single(window.GetLogicalDescendants().OfType<Button>(),
            button => ControlAutomationPeer.CreatePeerForElement(button).GetAutomationId() == "BooruNavigationButton");
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
}
