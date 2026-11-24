using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class StatisticsCurrentRatesLayoutTests
{
    [Theory]
    [InlineData(440, true)]
    [InlineData(960, false)]
    public void CurrentRatesKeepTheirDirectionsAndWrapAsPairs(double width, bool wraps) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(ReaderFonts.ManagerOptions());
        StatisticsPage page = new();
        Window window = new() { Width = width, Height = 600, Content = page };
        try
        {
            window.Show();
            WrapPanel row = page.FindControl<WrapPanel>("CurrentRates")!;
            StackPanel network = page.FindControl<StackPanel>("NetworkRates")!;
            StackPanel disk = page.FindControl<StackPanel>("DiskRates")!;
            StackPanel requests = page.FindControl<StackPanel>("RequestRate")!;
            TextBlock[] values =
                [FindText("NetValue"), FindText("NetUpValue"), FindText("DiskReadValue"), FindText("DiskWriteValue"), FindText("RequestsValue")];
            TextBlock[] captions =
                [FindText("NetCaption"), FindText("NetUpCaption"), FindText("DiskReadCaption"), FindText("DiskWriteCaption"), FindText("RequestsCaption")];
            Assert.All(values, value => Assert.Equal("—", value.Text));

            string[] languages = ["en", "de", .. Texts.BundledPackCodes()];
            IoSeries sampled = new(1, 1, [0], [0], [0], [0], [0]);
            foreach (string language in languages)
            {
                ReaderFonts.Apply(Application.Current!, language);
                Texts.SetLanguage(language);
                Assert.Equal(Texts.T("stats.header.networkTitle"), FindText("NetworkRatesTitle").Text);
                Assert.Equal(Texts.T("stats.header.diskTitle"), FindText("DiskRatesTitle").Text);
                string[] keys = ["stats.header.down", "stats.header.up", "stats.header.read", "stats.header.write", "stats.metric.requests"];
                Assert.Equal(keys.Select(key => Texts.T(key)), captions.Select(caption => caption.Text));

                page.Apply(new IoStatsSnapshot { RetentionDays = 30 });
                CheckValues("—", "—", "—", "—", "—");
                page.Apply(new IoStatsSnapshot { RetentionDays = 30, Series = sampled });
                CheckValues("0 B/s", "0 B/s", "0 B/s", "0 B/s", RequestText(0));

                page.Apply(new IoStatsSnapshot
                {
                    RetentionDays = 30, Series = sampled,
                    Rate = new(NetDown: double.MaxValue, NetUp: 2048, DiskRead: 3 * 1024 * 1024, DiskWrite: 4, Requests: 1234)
                });
                CheckValues(">999 YiB/s", "2 KiB/s", "3 MiB/s", "4 B/s", RequestText(1234));

                page.Apply(new IoStatsSnapshot
                {
                    RetentionDays = 30, Series = sampled,
                    Rate = new(NetDown: 5, NetUp: 1536, DiskRead: 7, DiskWrite: double.MaxValue, Requests: 8.5)
                });
                CheckValues("5 B/s", 1.5.ToString(Texts.Culture) + " KiB/s", "7 B/s", ">999 YiB/s", RequestText(8.5));
            }

            TextBlock FindText(string name) => page.FindControl<TextBlock>(name)!;

            static string RequestText(double rate) => Texts.T("stats.requestsRate", IoStatsDisplay.RequestRate(rate));

            void CheckValues(params string[] expected)
            {
                window.UpdateLayout();
                Assert.Equal(expected, values.Select(value => value.Text));
                if (wraps)
                    Assert.True(disk.Bounds.Top >= network.Bounds.Bottom);
                else
                {
                    Assert.Equal(network.Bounds.Top, disk.Bounds.Top);
                    Assert.Equal(disk.Bounds.Top, requests.Bounds.Top);
                }
                CheckPair(network, values[0], values[1]);
                CheckPair(disk, values[2], values[3]);
                Assert.True(requests.Bounds.Contains(BoundsInRow(values[4])));
                Assert.All(row.Children, group => Assert.True(group.Bounds.Right <= row.Bounds.Width));
                Assert.All(values.Concat(captions), text => Assert.True(text.TextLayout.Width <= text.Bounds.Width + 1));
            }

            void CheckPair(StackPanel group, TextBlock first, TextBlock second)
            {
                Rect leading = BoundsInRow(first), trailing = BoundsInRow(second);
                Assert.Equal(leading.Top, trailing.Top);
                Assert.True(leading.Right <= trailing.Left);
                Assert.True(group.Bounds.Contains(leading));
                Assert.True(group.Bounds.Contains(trailing));
            }

            Rect BoundsInRow(TextBlock text) => new(text.TranslatePoint(default, row)!.Value, text.Bounds.Size);
        }
        finally
        {
            window.Close();
            page.EndLifetime();
        }
    });
}
