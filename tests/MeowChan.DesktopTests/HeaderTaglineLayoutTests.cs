using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    [InlineData("Off", true)]
    [InlineData("Daily", false)]
    [InlineData("Daily", true)]
    [InlineData("PerStart", false)]
    [InlineData("Interval", false)]
    public void TheMainWindowUsesTheSavedModeAndRotatesOnlyInIntervalMode(string mode, bool updated) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            Version current = TaglineCatalog.ParseVersion(typeof(MainWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion)!;
            Version seen = current;
            if (updated)
                seen = current == new Version(1, 1, 1) ? new Version(1, 1, 0) : new Version(1, 1, 1);
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1, Language = "en",
                HeaderTagline = mode, HeaderTaglineIntervalMinutes = 17, LastTaglineVersion = seen.ToString(3)
            }.SaveConfig(profile.Path);
            BooruComponentDispatcher.Wait(Exercise());

            async Task Exercise()
            {
                await using DesktopSessionHost session = new(profile.Path, [],
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
                await using MainWindow window = new(session) { Height = 600, Classes = { MotionPolicy.OffClass } };
                window.Show();
                await window.StartupTimingRestoreTask;
                BooruReviewTestPlatform.Resize(window, 1800);
                var subtitle = window.FindControl<TextBlock>("WorkspaceSubtitle")!;
                var timer = (DispatcherTimer?)typeof(MainWindow).GetField("_taglineRotationTimer",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                if (mode == TaglinePicker.ModeOff)
                {
                    Assert.Equal(HeaderTaglineStage.None, window.TaglineStage);
                    Assert.False(subtitle.IsVisible);
                    Assert.Null(timer);
                    Assert.Equal(seen.ToString(3), session.Config.LastTaglineVersion);
                    return;
                }

                IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available();
                DateOnly today = DateOnly.FromDateTime(DateTime.Now);
                Assert.True(subtitle.IsVisible);
                string quip = subtitle.Text![(subtitle.Text!.IndexOf("— ", StringComparison.Ordinal) + 2)..];
                if (mode == TaglinePicker.ModeDaily)
                {
                    TaglinePick expected = TaglinePicker.Pick(mode, "cats", pool, today, current, seen, atStart: true, new Random(3));
                    Assert.Equal(updated, expected.ReleaseHighlight);
                    Assert.Equal(expected.Entry!.Text, quip);
                    Assert.Equal(current.ToString(3), session.Config.LastTaglineVersion);
                }
                else Assert.Contains(pool, entry => entry.Text == quip);

                if (mode == TaglinePicker.ModeInterval)
                {
                    Assert.NotNull(timer);
                    Assert.True(timer.IsEnabled);
                    Assert.Equal(TimeSpan.FromMinutes(17), timer.Interval);
                    string? initial = subtitle.Text;
                    // The real dispatcher timer delivers its tick without waiting seventeen minutes in the suite.
                    timer.Interval = TimeSpan.FromMilliseconds(25);
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                    while (subtitle.Text == initial) await Task.Delay(10, timeout.Token);
                    Assert.Equal(17, session.Config.HeaderTaglineIntervalMinutes);
                    await window.DisposeAsync();
                    Assert.False(timer.IsEnabled);
                }
                else Assert.Null(timer);
            }
        });

    [Theory]
    [InlineData("booru", "booru")]
    [InlineData("removed-quip", "cats")]
    public void HostedFixedPickerKeepsTheProfileChoiceAndSavesTheNewQuip(string storedId, string effectiveId) =>
        BooruComponentDispatcher.Run(() =>
        {
            using IDisposable platform = BooruReviewTestPlatform.Enter();
            using TempDirectory profile = new();
            new Config
            {
                DownloadPath = Path.Combine(profile.Path, "Downloads"), FirstRunSetupVersion = 1, Language = "en",
                HeaderTagline = TaglinePicker.ModeFixed, HeaderTaglineFixedId = storedId
            }.SaveConfig(profile.Path);
            BooruComponentDispatcher.Wait(Exercise());

            async Task Exercise()
            {
                await using DesktopSessionHost session = new(profile.Path, [],
                    updateSessionFactory: (path, config) => new UpdateSession(path, config, disableEnvironment: () => "1"));
                await using MainWindow window = new(session) { Height = 800, Classes = { MotionPolicy.OffClass } };
                window.Show();
                BooruReviewTestPlatform.Resize(window, 1400);
                window.SetRenderScaling(1.5);
                var subtitle = window.FindControl<TextBlock>("WorkspaceSubtitle")!;
                Assert.EndsWith(Texts.T(BuiltInTaglineProvider.KeyFor(effectiveId)), subtitle.Text);
                await window.StartupTimingRestoreTask;
                Settle();
                BooruResultActionsTests.Click(window.FindControl<Button>("SettingsNavigationButton")!);
                Assert.True(window.SettingsPageVisible);
                SettingsEditor editor = window.SettingsEditor;
                Assert.True(editor.RevealSetting(nameof(Config.HeaderTaglineFixedId)));
                ComboBox picker = editor.FindControl<ComboBox>("HeaderTaglineFixedIdInput")!;
                Settle();
                Assert.Equal(effectiveId, Assert.IsType<SettingsChoice>(picker.SelectedItem).Value);
                Assert.True(picker.Focus());
                window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null);
                Settle();
                Assert.True(picker.IsDropDownOpen);
                AssertGroups("en");

                try
                {
                    Texts.SetLanguage("de");
                    Settle();
                    Assert.True(picker.IsDropDownOpen);
                    AssertGroups("de");
                    Assert.Equal(effectiveId, Assert.IsType<SettingsChoice>(picker.SelectedItem).Value);
                    Assert.EndsWith(Texts.T(BuiltInTaglineProvider.KeyFor(effectiveId)), subtitle.Text);
                    Texts.SetLanguage("en");
                    Settle();

                    ComboBoxItem fetch = RevealChoice(editor.HeaderTaglineFixedChoices.Single(choice => choice.Value == "fetchthem"));
                    Assert.Contains(fetch.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Gonna fetch them all");
                    BooruResultActionsTests.Click(fetch);
                    Assert.False(picker.IsDropDownOpen);
                    Assert.Equal("fetchthem", Assert.IsType<SettingsChoice>(picker.SelectedItem).Value);
                    BooruResultActionsTests.Click(editor.FindControl<Button>("SaveSettingsButton")!);
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                    while (editor.IsBusy) await Task.Delay(10, timeout.Token);
                    Assert.False(editor.HasError, editor.ErrorMessage);
                    Assert.Equal("fetchthem", Config.LoadConfig(profile.Path).HeaderTaglineFixedId);
                    Assert.EndsWith("Gonna fetch them all", subtitle.Text);
                }
                finally { Texts.SetLanguage("en"); }

                void Settle()
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }

                void AssertGroups(string language)
                {
                    Assert.Equal(language, Texts.Language);
                    Assert.Equal(editor.HeaderTaglineFixedLabel, AutomationProperties.GetName(picker));
                    var groups = TaglineCatalog.Available().ToDictionary(entry => entry.Id, entry => entry.Category);
                    Assert.Equal(groups.Count, editor.HeaderTaglineFixedChoices.Count);
                    Assert.Equal(groups.Count, editor.HeaderTaglineFixedChoices.Select(choice => choice.Value).Distinct().Count());
                    Assert.Equal(Enum.GetValues<TaglineCategory>(), editor.HeaderTaglineFixedChoices.Select(choice => groups[choice.Value]).Distinct());
                    foreach (TaglineCategory category in Enum.GetValues<TaglineCategory>())
                    {
                        SettingsChoice choice = editor.HeaderTaglineFixedChoices.First(item => groups[item.Value] == category);
                        var item = RevealChoice(choice);
                        string label = Texts.T("settings.headerTagline.category." + category.ToString().ToLowerInvariant());
                        var header = Assert.Single(item.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == label);
                        var quip = Assert.Single(item.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == choice.Label);
                        Assert.True(header.Bounds.Height > 0 && quip.Bounds.Height > 0);
                        var origin = header.TranslatePoint(default, item)!.Value;
                        var quipOrigin = quip.TranslatePoint(default, item)!.Value;
                        Assert.True(origin.Y + header.Bounds.Height <= quipOrigin.Y + 1);
                        Assert.InRange(quipOrigin.Y + quip.Bounds.Height, 0, item.Bounds.Height + 1);
                    }
                }

                ComboBoxItem RevealChoice(SettingsChoice choice)
                {
                    picker.ScrollIntoView(choice);
                    Settle();
                    var item = Assert.IsType<ComboBoxItem>(picker.ContainerFromItem(choice));
                    item.BringIntoView();
                    Settle();
                    return item;
                }
            }
        });

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
        Assert.Equal(expected, AutomationProperties.GetName(subtitle));
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
            Point inHost = subtitle.TranslatePoint(ink.Position, host)!.Value;
            Rect inColumn = new(inHost, ink.Size);
            Assert.True(new Rect(host.Bounds.Size).Contains(inColumn), $"{context}: ink {inColumn}, column {host.Bounds.Size}");
            Assert.True(!subtitle.ClipToBounds || new Rect(subtitle.Bounds.Size).Contains(ink),
                $"{context}: ink {ink}, TextBlock clip {subtitle.Bounds.Size}");
            x += run.Size.Width;
            shapedLength += run.Length;
        }
        Assert.True(shapedLength == expected.Length, $"{context}: shaped {shapedLength} of {expected.Length} characters");
    }
}
