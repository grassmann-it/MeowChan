using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input.Platform;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaFactGridTests
{
    [Theory]
    [InlineData("en", 206)]
    [InlineData("de", 246)]
    [InlineData("en", 259)]
    [InlineData("de", 260)]
    [InlineData("en", 326)]
    [InlineData("de", 446)]
    public void FactsStackAtNarrowWidthsAndFileNamesRetainTheExtension(string language, int width) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Texts.Initialize();
            Texts.SetLanguage(language);
            const string longName = "A-very-long-file-name-that-keeps-going-without-spaces-and-has-a-long-ending.png";
            MediaFactGrid facts = new("Facts") { Width = width };
            facts.Add(Texts.T("media.fact.file"), longName, fileName: true);
            facts.Add(Texts.T("media.fact.saved"), MediaProvenanceText.FactSize(1476233));
            facts.Add(Texts.T("media.fact.source"), MediaProvenanceText.FactSize(1476233));
            facts.Add(Texts.T("media.fact.author"), "");
            Window window = new() { Width = 520, Height = 320, Content = facts };
            try
            {
                window.Show();
                window.UpdateLayout();
                SelectableTextBlock[] values = facts.Children.OfType<SelectableTextBlock>().ToArray();
                Assert.Equal(3, values.Length);
                Control[] labels = facts.Children.Where((_, index) => index % 2 == 0).ToArray();
                Assert.Equal(3, labels.Length);
                foreach ((SelectableTextBlock value, Control label) in values.Zip(labels))
                {
                    Assert.Equal(values[0].FontSize, value.FontSize);
                    Assert.Equal(values[0].FontWeight, value.FontWeight);
                    Assert.Equal(values[0].Bounds.Left, value.Bounds.Left, 0.5);
                    if (width < 260)
                    {
                        Assert.Equal(label.Bounds.Left, value.Bounds.Left, 0.5);
                        Assert.Equal(label.Bounds.Bottom + 2, value.Bounds.Top, 0.5);
                    }
                    else
                    {
                        Assert.InRange(label.Bounds.Width, 0, 120);
                        Assert.Equal(label.Bounds.Top, value.Bounds.Top, 0.5);
                    }
                }
                Assert.Contains("…", values[0].Text);
                Assert.EndsWith(".png", values[0].Text);
                Assert.Equal(longName, ToolTip.GetTip(values[0]));
                ContextGestures.RightClick(values[0]);
                ContextMenu menu = Assert.IsType<ContextMenu>(values[0].ContextMenu);
                Assert.True(menu.IsOpen);
                Assert.Equal(Texts.T("diagnostics.copy"), Assert.IsType<MenuItem>(Assert.Single(menu.Items)).Header);
                Assert.Single(MenuProbe.Entries(menu)).Activate();
                Assert.Equal(longName, BooruComponentDispatcher.Wait(window.Clipboard!.TryGetTextAsync()));
            }
            finally
            {
                window.Close();
                Texts.SetLanguage("en");
            }
        });

    [Fact]
    public void TheFullLocationAndHashAreReachableFromTheKeyboardAndMacControlClick() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        const string location = "日本語 /研究/ · 123 · #456 ― 保存された投稿の保存場所";
        string hash = new('a', 64);
        MediaFactGrid facts = new("Facts") { Width = 206 };
        facts.Add(Texts.T("media.fact.location"), location);
        facts.Add("SHA-256", hash);
        Window window = new() { Width = 480, Height = 320, Content = facts };
        ContextClick.MacOSForTests = true;
        try
        {
            window.Show();
            ContextGestures.Settle(window);
            SelectableTextBlock[] values = facts.Children.OfType<SelectableTextBlock>().ToArray();
            VerifyCopy(values[0], location, ContextGestures.ShiftF10);
            VerifyCopy(values[1], hash, target => ContextGestures.ControlClick(target));
        }
        finally { ContextClick.MacOSForTests = null; window.Close(); }

        void VerifyCopy(SelectableTextBlock value, string expected, Action<Control> open)
        {
            Assert.Equal(expected, AutomationProperties.GetName(value));
            open(value);
            ContextMenu menu = Assert.IsType<ContextMenu>(value.ContextMenu);
            Assert.True(menu.IsOpen);
            Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == "panel.copy-value").Activate();
            Assert.Equal(expected, BooruComponentDispatcher.Wait(window.Clipboard!.TryGetTextAsync()));
            menu.Close();
            ContextGestures.Settle(window);
        }
    });
}
