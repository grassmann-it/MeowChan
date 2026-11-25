using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BooruListTagTooltipLimitTests
{
    [Theory]
    [InlineData(null, 60, 220)]
    [InlineData(1, 1, 220)]
    [InlineData(200, 200, 220)]
    [InlineData(200, 200, 12)]
    public void TooltipNamesAtMostTheConfiguredNumberOfHiddenTags(int? setting, int expected, int tagCount) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            (BooruUiHarness harness, string[] names) = CreateSearch(tagCount);
            BooruWorkbenchWindow window = new(harness.Services);
            Config config = new() { BooruResultTagsShown = 1 };
            if (setting is int limit) config.BooruHiddenTagTooltipLimit = limit;
            window.ApplyListProfile(config);
            try
            {
                window.Show();
                BooruReviewTestPlatform.Resize(window, 800);
                BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(window.Model.Search, "1boy"));
                BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => Toggle(window) is { IsTabStop: true },
                    "the hidden-tag toggle is laid out"));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                AssertTooltip(Assert.IsType<Button>(Toggle(window)), names, expected);
            }
            finally { window.Close(); }
        });

    [Fact]
    public void ReturningFromSettingsAppliesTheNewLimitToExistingResults() => BooruComponentDispatcher.Run(async () =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        using TempDirectory profile = new();
        (BooruUiHarness harness, string[] names) = CreateSearch(220);
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "media"), FirstRunSetupVersion = 1, WorkspaceMode = "Advanced",
            Language = "en", ShowBooruTools = true, BooruResultTagsShown = 1
        }.SaveConfig(profile.Path);
        await using DesktopSessionHost session = new(profile.Path, [],
            updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
        session.BooruServices = harness.Services;
        await using MainWindow window = new(session) { Width = 1440, Height = 800, Classes = { MotionPolicy.OffClass } };
        window.Show();
        window.PlatformImpl!.Activated?.Invoke();
        await window.StartupTimingRestoreTask;

        Button booru = ButtonById(window, "BooruNavigationButton");
        await BooruTestSupport.WaitAsync(() => booru.IsEffectivelyEnabled, "the Booru navigation button");
        Click(booru);
        BooruPage page = Assert.IsType<BooruPage>(window.BooruPageForTests);
        await BooruTestSupport.SearchAsync(page.Model.Search, "1boy");
        await BooruTestSupport.WaitAsync(() => Toggle(page) is { } toggle && ToolTip.GetTip(toggle) is string,
            "the results' hidden-tag tooltip");
        AssertTooltip(Assert.IsType<Button>(Toggle(page)), names, 60);

        Click(ButtonById(window, "SettingsNavigationButton"));
        Assert.True(window.SettingsPageVisible);
        SettingsEditor settings = window.SettingsEditor;
        NumericUpDown limit = Assert.Single(settings.GetLogicalDescendants().OfType<NumericUpDown>(),
            input => AutomationProperties.GetAutomationId(input) == "BooruHiddenTagTooltipLimitInput");
        limit.Value = 1;
        Click(ButtonById(settings, "SaveSettingsButton"));
        await BooruTestSupport.WaitAsync(() => !settings.IsBusy, "the new tooltip limit to be saved");
        Assert.False(settings.HasError, settings.ErrorMessage);
        Assert.Equal(1, session.Config.BooruHiddenTagTooltipLimit);

        Click(ButtonById(window, "BooruNavigationButton"));
        Assert.Equal(MainWindow.WorkspacePage.Booru, window.SelectedPage);
        Assert.Same(page, window.BooruPageForTests);
        window.UpdateLayout();
        AssertTooltip(Assert.IsType<Button>(Toggle(page)), names, 1);
        Assert.Equal(1, harness.Search.Requests);
    });

    private static (BooruUiHarness Harness, string[] Names) CreateSearch(int tagCount)
    {
        BooruUiHarness harness = new();
        string[] names = Enumerable.Range(0, tagCount).Select(index => $"tooltip_tag_{index:D3}").ToArray();
        BooruPostView sample = BooruUiFixtures.Post("101", preview: false);
        BooruPostView post = BooruPostView.From(sample.Post with
        {
            Tags = names.Select(name => new BooruTag(name, BooruTagCategory.General)).ToArray()
        });
        harness.Search.OnSearch = (query, page, _) =>
            Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, post)));
        return (harness, names);
    }

    private static Button? Toggle(Control root) => root.GetVisualDescendants().OfType<Button>().SingleOrDefault(
        button => AutomationProperties.GetAutomationId(button) == BooruWorkbenchView.MoreTagsPrefix + "101");

    private static Button ButtonById(Control root, string id) => Assert.Single(root.GetLogicalDescendants().OfType<Button>(),
        button => ControlAutomationPeer.CreatePeerForElement(button)?.GetAutomationId() == id);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void AssertTooltip(Button toggle, string[] names, int limit)
    {
        int hidden = Assert.Single(Enumerable.Range(1, names.Length),
            count => Equals(toggle.Content, Texts.T("booru.ui.tags.more", count)));
        string named = string.Join(", ", names.Skip(names.Length - hidden).Take(limit));
        if (hidden > limit) named += "…";
        string tooltip = Texts.T("booru.ui.tags.showAll", named);
        Assert.Equal(tooltip, ToolTip.GetTip(toggle));
        Assert.Equal(tooltip, AutomationProperties.GetHelpText(toggle));
    }
}
