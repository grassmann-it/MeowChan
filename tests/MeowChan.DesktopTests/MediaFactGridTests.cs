using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class MediaFactGridTests
{
    [Theory]
    [InlineData("en", 240)]
    [InlineData("de", 280)]
    [InlineData("en", 360)]
    [InlineData("de", 480)]
    public void FactsStackAtNarrowWidthsAndFileNamesRetainTheExtension(string language, int width) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            Texts.Initialize();
            Texts.SetLanguage(language);
            const string longName = "A-very-long-file-name-that-keeps-going-without-spaces-and-has-a-long-ending.png";
            MediaFactGrid facts = new("Facts") { Width = width - 32 };
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
                    if (width < 292)
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
                MenuItem copy = Assert.IsType<MenuItem>(Assert.Single(values[0].ContextMenu!.Items));
                Assert.Equal(Texts.T("diagnostics.copy"), copy.Header);
            }
            finally
            {
                window.Close();
                Texts.SetLanguage("en");
            }
        });
}
