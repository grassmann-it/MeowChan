using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
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
            BooruUiHarness harness = new();
            string[] names = Enumerable.Range(0, tagCount).Select(index => $"tooltip_tag_{index:D3}").ToArray();
            BooruPostView sample = BooruUiFixtures.Post("101", preview: false);
            BooruPostView post = BooruPostView.From(sample.Post with
            {
                Tags = names.Select(name => new BooruTag(name, BooruTagCategory.General)).ToArray()
            });
            harness.Search.OnSearch = (query, page, _) =>
                Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, page, true, post)));
            BooruWorkbenchWindow window = new(harness.Services);
            Config config = new() { BooruResultTagsShown = 1 };
            if (setting is int limit) config.BooruHiddenTagTooltipLimit = limit;
            window.ApplyListProfile(config);
            try
            {
                window.Show();
                BooruReviewTestPlatform.Resize(window, 800);
                BooruComponentDispatcher.Wait(BooruTestSupport.SearchAsync(window.Model.Search, "1boy"));
                BooruComponentDispatcher.Wait(BooruTestSupport.WaitAsync(() => Toggle() is { IsTabStop: true },
                    "the hidden-tag toggle is laid out"));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                Button toggle = Assert.IsType<Button>(Toggle());
                int hidden = Assert.Single(Enumerable.Range(1, tagCount),
                    count => Equals(toggle.Content, Texts.T("booru.ui.tags.more", count)));
                string named = string.Join(", ", names.Skip(tagCount - hidden).Take(expected));
                if (hidden > expected) named += "…";
                string tooltip = Texts.T("booru.ui.tags.showAll", named);
                Assert.Equal(tooltip, ToolTip.GetTip(toggle));
                Assert.Equal(tooltip, AutomationProperties.GetHelpText(toggle));
            }
            finally { window.Close(); }

            Button? Toggle() => window.GetVisualDescendants().OfType<Button>().SingleOrDefault(
                button => AutomationProperties.GetAutomationId(button) == BooruWorkbenchView.MoreTagsPrefix + "101");
        });
}
