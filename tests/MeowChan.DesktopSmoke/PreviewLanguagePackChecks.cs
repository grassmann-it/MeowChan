using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Archive;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopSmoke;

internal static class PreviewLanguagePackChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        var session = window.SessionHost;
        string profile = session.ProfileDirectory;
        CultureInfo processCulture = CultureInfo.CurrentCulture;
        Require(!Directory.Exists(Path.Combine(profile, "LanguagePacks")), "Bundled language smoke must not install a profile pack.");
        // Every embedded pack, as the loader discovered it (es, fr, ru, zh-Hans, and whatever later branches add).
        string[] bundled = Texts.BundledPackCodes().ToArray();
        Require(bundled.Length >= 4 && bundled.Contains("zh-Hans"), "Bundled preview packs are incomplete: " + string.Join(",", bundled));
        Require(Texts.Languages.Where(l => bundled.Contains(l.Code)).All(l => l.NativeName.EndsWith("(Untested)", StringComparison.Ordinal)), "Bundled preview marker missing.");
        byte[] initialSettings = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
        Require(Texts.Warnings.Count == 0 && Texts.Languages.Count == 2 + bundled.Length, "Bundled preview packs did not load through normal initialization.");
        TextBox urls = window.FindControl<TextBox>("UrlInput")!;
        const string draft = "https://unsupported.invalid/été/mañana\npreview draft — not submitted";
        urls.Text = draft;
        foreach (string code in bundled)
        {
            // A passive catalog switch is not a settings commit.
            Texts.SetLanguage(code);
            Require(Texts.Language == code && Texts.Warnings.Count == 0,
                code + ": bundled preview language was not effective without loader warnings.");
            await Program.WaitUntilAsync(() => AutomationProperties.GetName(window.FindControl<Button>("AddUrlButton")!) == Texts.T("action.add"));
            Require(initialSettings.SequenceEqual(File.ReadAllBytes(Path.Combine(profile, "settings.json"))), "Passive switch wrote preferences.");
        }
        Texts.SetLanguage("en");
        foreach (string code in bundled)
        {
            window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => window.SettingsPageVisible);
            SettingsEditor editor = window.SettingsEditor;
            await SettingsSectionChecks.OpenFieldAsync(editor, "LanguageInput");
            ComboBox languages = editor.FindControl<ComboBox>("LanguageInput")!;
            languages.SelectedItem = editor.LanguageChoices.Single(l => l.Code == code);
            editor.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !editor.IsBusy && Config.LoadConfig(profile).Language == code && Texts.Language == code);
            Require(ReferenceEquals(session, window.SessionHost) && urls.Text == draft, "Language commit replaced session or URL draft.");
            Require(CultureInfo.CurrentCulture.Equals(processCulture), "UI locale changed process numeric culture.");
            Require(editor.SettingsTitle == Texts.T("settings.title"), "Settings heading did not refresh.");
            await SettingsSectionChecks.OpenFieldAsync(editor, "DownloadPathInput");
            TextBox path = editor.FindControl<TextBox>("DownloadPathInput")!;
            NumericUpDown timer = editor.FindControl<NumericUpDown>("RecheckTimerInput")!;
            TextBox numeric = SettingsSectionChecks.NumericEditingSurface(editor, timer);
            Config saved = Config.LoadConfig(profile);
            byte[] savedSettings = File.ReadAllBytes(Path.Combine(profile, "settings.json"));
            decimal savedValue = saved.RecheckTimer;
            Require(timer.Value == savedValue && path.Text == saved.DownloadPath, "Committed values not in the retained editor.");
            path.Text = "relative unfinished été mañana";
            numeric.Focus();
            const string invalid = "invalid numeric draft";
            numeric.Text = invalid;
            await Program.WaitUntilAsync(() => numeric.IsFocused && numeric.Text == invalid && timer.Text == invalid);
            Require(timer.Value == savedValue, "Invalid numeric fixture unexpectedly changed the numeric value.");
            Texts.SetLanguage(code == "fr" ? "es" : "fr");
            await Program.WaitUntilAsync(() => editor.SettingsTitle == Texts.T("settings.title"));
            Require(numeric.IsFocused && numeric.Text == invalid && timer.Text == invalid
                && timer.Value == savedValue && path.Text == "relative unfinished été mañana",
                "Passive preview switch replaced invalid input, value or focus.");
            Require(Config.LoadConfig(profile).Language == code, "Passive preview switch committed a language.");
            Texts.SetLanguage(code);
            // Discard draft through actual editor command, without saving its invalid values.
            editor.FindControl<Button>("CancelSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !editor.IsBusy && editor.SelectedLanguage?.Code == code
                && path.Text == saved.DownloadPath && editor.DownloadPath == saved.DownloadPath
                && timer.Value == savedValue && editor.RecheckTimer == saved.RecheckTimer
                && numeric.Text != invalid && timer.Text != invalid);
            Require(decimal.TryParse(timer.Text, timer.ParsingNumberStyle, timer.NumberFormat, out decimal restored)
                && restored == savedValue && numeric.Text == timer.Text, "Discard did not restore the raw numeric value.");
            Require(savedSettings.SequenceEqual(File.ReadAllBytes(Path.Combine(profile, "settings.json"))),
                "Discard or passive preview changes wrote settings.");
            foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                await Program.WaitUntilAsync(() => window.ActualThemeVariant == theme);
                await SettingsSectionChecks.OpenFieldAsync(editor, "LanguageInput");
                window.UpdateLayout();
                Require(languages.Bounds.Width > 100 && languages.IsEffectivelyVisible, "Preview language choice not laid out.");
                Program.CaptureWindow(window, $"smoke-language-{code}-{theme.ToString().ToLowerInvariant()}-settings.png");
            }
            window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
            Require(AutomationProperties.GetName(window.FindControl<Button>("WindowCloseButton")!) == Texts.T("action.close"), "Caption not translated.");
            Require(urls.Text == draft && ReferenceEquals(session, window.SessionHost), "Preview roundtrip lost session or draft.");
            Program.CaptureWindow(window, $"smoke-language-{code}-downloads.png");
            Console.WriteLine($"SMOKE preview language {code}: embedded bundled pack load without profile copy, Settings commit/readback, live labels, retained URL/invalid numeric draft and focus; CurrentCulture={processCulture.Name}.");
            if (ReaderFonts.ScriptOf(code) != null)
                await CheckCjkAsync(window, code);
        }
        // Bundled catalog rediscovery emulates next startup only, not a process restart.
        Texts.Initialize(Path.Combine(profile, "LanguagePacks"));
        Texts.SetLanguage(Config.LoadConfig(profile).Language);
        Require(Texts.Language == bundled[^1] && Texts.Warnings.Count == 0, "Saved preview preference cannot be loaded again.");
        Console.WriteLine("SMOKE preview limits: managed/routed input on this Linux desktop; screenshots for accents/layout and CJK glyph/font-family evidence under the installed Noto Sans CJK; no native linguistic, whole-UI, Windows/macOS or process restart acceptance.");
    }

    /// <summary>
    /// A CJK preview (zh-Hans, later ja): the UI and reading fonts carry the language's regional CJK family first,
    /// real controls shape Han text without .notdef (tofu) glyphs, and the main window, Storage and limits,
    /// the reader with a post and two dialogs are captured in Light and Dark for layout/line-break review.
    /// </summary>
    private static async Task CheckCjkAsync(MainWindow window, string code)
    {
        string preferred = ReaderFonts.FamilyName(ReaderFonts.PreferredScript(code));
        FontFamily ui = (FontFamily)Application.Current!.Resources[ReaderFonts.UiFamilyResource]!;
        FontFamily reading = (FontFamily)Application.Current.Resources[ReaderFonts.ReadingFamilyResource]!;
        Require(ui.FamilyNames.Count >= 5 && ui.FamilyNames.Skip(1).First() == preferred && reading.FamilyNames.Skip(2).First() == preferred,
            code + ": fonts do not put " + preferred + " first: " + ui.Name + " / " + reading.Name);
        Require(window.FindControl<Button>("AddUrlButton")!.FontFamily.Name == ui.Name, "The window font did not follow the dynamic UI family.");
        bool installed = FontManager.Current.SystemFonts.Any(family => family.Name == preferred);
        foreach ((FontFamily family, string sample) in new[] { (ui, Texts.T("settings.title") + " 设置、媒体与归档"), (reading, "帖子正文：图库与阅读器。") })
        {
            TextLayout layout = new(sample, new Typeface(family), 13, Brushes.Black);
            var runs = layout.TextLines.SelectMany(line => line.TextRuns).OfType<ShapedTextRun>().ToArray();
            Require(runs.Length > 0 && runs.All(run => run.GlyphRun.GlyphInfos.All(glyph => glyph.GlyphIndex != 0)), code + ": tofu (.notdef) glyphs in " + sample);
            string[] families = runs.Select(run => run.GlyphRun.GlyphTypeface.FamilyName).Distinct().ToArray();
            Require(!installed || families.Contains(preferred), code + ": Han text was not shaped with " + preferred + " but " + string.Join("/", families));
            Console.WriteLine($"SMOKE cjk fonts {code}: \"{sample}\" shaped with {string.Join("/", families)}; preferred {preferred} installed={installed}.");
        }
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            string suffix = $"{code}-{theme.ToString().ToLowerInvariant()}";
            window.RequestedThemeVariant = theme;
            await Program.WaitUntilAsync(() => window.ActualThemeVariant == theme);
            window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
            window.UpdateLayout();
            Program.CaptureWindow(window, $"smoke-language-{suffix}-main.png");

            window.FindControl<MenuItem>("SettingsMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Program.WaitUntilAsync(() => window.SettingsPageVisible);
            SettingsEditor editor = window.SettingsEditor;
            await SettingsSectionChecks.OpenAsync(editor, "settings.worker-budgets");
            await Program.WaitUntilAsync(() => editor.FindControl<NumericUpDown>("ReserveInput")!.IsEffectivelyVisible);
            window.UpdateLayout();
            Program.CaptureWindow(window, $"smoke-language-{suffix}-settings-storage.png");

            SmokeNavigation.GoTo(window, MainWindow.WorkspacePage.Archive);
            ArchivePage page = window.ArchivePage;
            ListBox threads = page.FindControl<ListBox>("ArchiveThreads")!;
            await Program.WaitUntilAsync(() => page.IsEffectivelyVisible && !page.IsBusy && threads.ItemCount > 0);
            // The reader opens for exactly one selected row; pick the seeded Chinese/Japanese thread.
            threads.SelectedItem = threads.Items.OfType<ArchiveThreadRow>().Single(row => row.Summary.Id == CjkThread);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread?.Summary.Id == CjkThread);
            ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
            page.FindControl<Button>("ArchiveReadPosts")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => reader.IsVisible && !page.IsBusy);
            await page.MediaLoadTask;
            window.UpdateLayout();
            // The longest visible post body is the Chinese one (about 200 Han characters without a space).
            SelectableTextBlock body = reader.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Where(block => block.Classes.Contains("reader-body") && block.IsEffectivelyVisible)
                .OrderByDescending(block => block.Bounds.Height).First();
            Require(body.FontFamily.FamilyNames.Contains(preferred) && body.Bounds.Height > body.FontSize * 2.5,
                code + ": the CJK post body does not use " + preferred + " or did not wrap: " + body.FontFamily + " " + body.Bounds);
            Program.CaptureWindow(window, $"smoke-language-{suffix}-reader.png");
            page.FindControl<Button>("ArchiveCloseReader")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !reader.IsVisible);

            page.FindControl<Button>("ExportArchiveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Border export = page.FindControl<Border>("ArchiveExportDialog")!;
            // Wait for the export history lookup, so the capture shows the settled dialog rather than its spinner.
            await Program.WaitUntilAsync(() => export.IsVisible && export.Bounds.Width > 0
                && page.FindControl<Button>("ArchiveExportStart")?.IsEnabled == true);
            window.UpdateLayout();
            Program.CaptureWindow(window, $"smoke-language-{suffix}-dialog-export.png");
            page.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => !export.IsVisible);

            // A real modal dialog window (no update session: no browser or update requests).
            AboutWindow about = new() { RequestedThemeVariant = theme };
            Task showing = about.ShowDialog(window);
            await Program.WaitUntilAsync(() => about.IsVisible && about.ActualThemeVariant == theme);
            about.UpdateLayout();
            Program.CaptureWindow(about, $"smoke-language-{suffix}-dialog-about.png");
            about.Close();
            await showing;
        }
        window.FindControl<Button>("DownloadsNavigationButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !window.SettingsPageVisible);
        Console.WriteLine($"SMOKE preview language {code}: CJK font order, .notdef-free shaping and Light/Dark captures of main, Storage and limits, reader, export and About.");
    }

    private static readonly ResourceId CjkThread = new("4chan", "g", "2000");

    /// <summary>The reader fixture plus one archived thread whose posts are long Chinese and Japanese text without spaces.</summary>
    internal static async Task SeedAsync(string profile)
    {
        await ReaderUxChecks.SeedAsync(profile);
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(profile, lease);
        string chinese = string.Concat(Enumerable.Repeat("这是一条用于检查自动换行的帖子正文：中文没有空格，所以每个汉字之间都可以断行，标点符号不应出现在行首。", 4));
        string japanese = string.Concat(Enumerable.Repeat("これは折り返しを確認するための投稿本文です。日本語にも空白がないため、文字の間で改行できます。", 4));
        CapturedPost[] posts =
        {
            new("2000", 0, PostFieldPresence.All, Subject: "简体中文换行检查", Html: "<p>" + chinese + "</p>"),
            new("2001", 1, PostFieldPresence.All, Html: "<p>" + japanese + "</p>"),
            new("2002", 2, PostFieldPresence.All, Html: "<p>混合：简体中文 · 日本語 · 한국어 · English 123。</p>")
        };
        string media = Path.Combine(profile, "CJK media");
        Directory.CreateDirectory(media);
        await catalog.CaptureAsync(new CapturedThreadSnapshot(CjkThread, "https://boards.4chan.org/g/thread/2000",
            "简体中文 / 日本語 · 换行检查", ThreadSourceState.Archived, DateTimeOffset.UtcNow, media, posts));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
