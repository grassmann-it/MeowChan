using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop.Updates;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class HeaderTaglineLayoutTests
{
    private static readonly double[] WindowWidths = [760, 900, 1100, 1400, 1800];

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void EveryLocalizedTaglineFitsTheActualHeaderAtSeveralWindowWidths(double scale) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        AvaloniaLocator.CurrentMutable.Bind<FontManagerOptions>().ToConstant(ReaderFonts.ManagerOptions());
        using TempDirectory profile = new();
        new Config
        {
            DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1,
            HeaderTagline = TaglinePicker.ModeFixed, HeaderTaglineFixedId = "lightdark"
        }.SaveConfig(profile.Path);
        BooruComponentDispatcher.Wait(Exercise());

        async Task Exercise()
        {
            await using DesktopSessionHost session = new(profile.Path, Array.Empty<MeowChan.Interfaces.IImageBoard>(),
                updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
            await using MainWindow window = new(session) { Height = 600, Classes = { MotionPolicy.OffClass } };
            window.Show();
            window.SetRenderScaling(scale);
            Assert.Equal(scale, window.RenderScaling);
            Panel host = window.FindControl<Panel>("WorkspaceTaglineHost")!;
            TextBlock subtitle = window.FindControl<TextBlock>("WorkspaceSubtitle")!;
            Assert.Equal(FontStyle.Italic, subtitle.FontStyle);
            Assert.True(host.ClipToBounds);

            // Drive the same preference handler the Settings page uses, with the real header and star column.
            Action apply = typeof(MainWindow).GetMethod("ApplyHeaderTaglineSetting", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action>(window);
            var keys = TaglineTexts.Entries.Where(entry => entry.Key.StartsWith("shell.tagline.quip.", StringComparison.Ordinal)).ToArray();
            string[] languages = ["en", "de", .. Texts.BundledPackCodes()];
            Assert.Equal(12, languages.Length);
            try
            {
                foreach (string language in languages)
                {
                    ReaderFonts.Apply(Application.Current!, language);
                    Texts.SetLanguage(language);
                    Assert.Equal(language, Texts.Language);
                    string lead = Assert.IsType<string>(Texts.Own("shell.tagline.lead"));
                    foreach (double width in WindowWidths)
                    {
                        BooruReviewTestPlatform.Resize(window, width);
                        Assert.InRange(window.Bounds.Width, width - 1 / scale, width + 1 / scale);
                        foreach (var (key, _, _) in keys)
                        {
                            var entry = Assert.IsType<string>(Texts.Own(key));
                            session.Config.HeaderTaglineFixedId = key["shell.tagline.quip.".Length..];
                            apply();
                            window.UpdateLayout();
                            string context = $"{language}/{key} at {width} DIP, {scale}×";
                            CheckVisibleForm(window, host, subtitle, lead, entry, context);
                            if (width == WindowWidths[^1])
                                Assert.True(window.TaglineStage == HeaderTaglineStage.Full, $"{context}: stage {window.TaglineStage}, column {host.Bounds.Width} DIP");
                        }
                    }
                }

                ReaderFonts.Apply(Application.Current!, "en");
                Texts.SetLanguage("en");
                session.Config.HeaderTaglineFixedId = "lightdark";
                apply();
                window.UpdateLayout();
                string lightDark = Texts.T("shell.tagline.quip.lightdark"), englishLead = Texts.T("shell.tagline.lead");
                Assert.Equal(HeaderTaglineStage.Full, window.TaglineStage);

                // A column as wide as the untrimmed advance still lacks room for the reserve and italic ink.
                host.MaxWidth = subtitle.TextLayout.WidthIncludingTrailingWhitespace;
                window.UpdateLayout();
                Assert.Equal(HeaderTaglineStage.Quip, window.TaglineStage);
                CheckVisibleForm(window, host, subtitle, englishLead, lightDark, $"lightdark as a quip, {scale}×");

                host.MaxWidth = subtitle.TextLayout.WidthIncludingTrailingWhitespace;
                window.UpdateLayout();
                Assert.Equal(HeaderTaglineStage.None, window.TaglineStage);
                CheckVisibleForm(window, host, subtitle, englishLead, lightDark, $"lightdark hidden, {scale}×");

                host.MaxWidth = double.PositiveInfinity;
                window.UpdateLayout();
                Assert.Equal(HeaderTaglineStage.Full, window.TaglineStage);
                CheckVisibleForm(window, host, subtitle, englishLead, lightDark, $"lightdark restored in full, {scale}×");
            }
            finally
            {
                ReaderFonts.Apply(Application.Current!, "en");
                Texts.SetLanguage("en");
            }
        }
    });

    private static void CheckVisibleForm(MainWindow window, Panel host, TextBlock subtitle, string lead, string quip, string context)
    {
        if (window.TaglineStage == HeaderTaglineStage.None)
        {
            Assert.False(subtitle.IsVisible, context);
            Assert.Null(ToolTip.GetTip(subtitle));
            return;
        }

        string shortText = "— " + quip, fullText = lead + " " + shortText;
        string expected = window.TaglineStage == HeaderTaglineStage.Full ? fullText : shortText;
        Assert.True(subtitle.IsVisible && subtitle.Text == expected, $"{context}: stage {window.TaglineStage}, text '{subtitle.Text}'");
        Assert.Equal(window.TaglineStage == HeaderTaglineStage.Quip ? fullText : null, ToolTip.GetTip(subtitle));
        TextLine line = Assert.Single(subtitle.TextLayout.TextLines);
        Assert.False(line.HasCollapsed, context);

        // Inspect the shaped glyphs the TextBlock actually draws, including runs in the fallback fonts.
        Thickness padding = subtitle.UseLayoutRounding ? LayoutHelper.RoundLayoutThickness(subtitle.Padding, window.RenderScaling) : subtitle.Padding;
        double x = padding.Left + line.Start;
        int shapedLength = 0;
        foreach (ShapedTextRun run in line.TextRuns.OfType<ShapedTextRun>())
        {
            Rect ink = run.GlyphRun.InkBounds.Translate(new Vector(x, padding.Top + line.Baseline - run.Baseline));
            Rect inColumn = ink.Translate(new Vector(subtitle.Bounds.X, subtitle.Bounds.Y));
            Assert.True(new Rect(host.Bounds.Size).Contains(inColumn), $"{context}: ink {inColumn}, column {host.Bounds.Size}");
            Assert.True(!subtitle.ClipToBounds || new Rect(subtitle.Bounds.Size).Contains(ink),
                $"{context}: ink {ink}, TextBlock clip {subtitle.Bounds.Size}");
            x += run.Size.Width;
            shapedLength += run.Length;
        }
        Assert.True(shapedLength == expected.Length, $"{context}: shaped {shapedLength} of {expected.Length} characters");
    }
}
