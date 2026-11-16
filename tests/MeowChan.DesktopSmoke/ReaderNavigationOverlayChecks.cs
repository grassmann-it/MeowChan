using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Motion;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

internal static class ReaderNavigationOverlayChecks
{
    private const double GeometryEpsilonDip = 1e-7;

    internal static async Task RunAsync(MainWindow window, ArchivePage page)
    {
        string mode = window.SessionHost.Config.WorkspaceMode;
        string language = Texts.Language;
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        double width = window.Width, height = window.Height;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        Button history = page.FindControl<Button>("ArchiveHistoryBack")!;
        bool historyCreated = false;
        bool mediaTiles = page.MediaTilesPreference;
        Exception? failureBeforeCleanup = null;
        try
        {
            Require(!history.IsVisible, "Reader navigation fixture expected the preceding history check to leave no entries.");
            await page.FocusPostAsync("1000");
            ArchivePostId target = new(page.CurrentThread!.Summary.Id, "1003");
            await page.NavigateQuoteAsync(new ArchiveQuoteRow(new ArchiveQuote(target, target, ">>1003", null)));
            historyCreated = true;
            await Program.WaitUntilAsync(() => history.IsEffectivelyVisible
                && (page.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id == "1003");
            await CaptureEvidenceAsync(window, page, tabs);
            await CheckAutoHideAsync(window, page);
            await CheckCountersAsync(window, page);
            await CheckModesAsync(window, page);
            foreach (var size in new[] { (Mode: "Simple", Width: 1440d, Height: 920d),
                (Mode: "Simple", Width: 760d, Height: 500d), (Mode: "Advanced", Width: 760d, Height: 500d),
                (Mode: "Barebones", Width: 640d, Height: 480d),
                (Mode: "Simple", Width: 760d, Height: 800d),
                (Mode: "Barebones", Width: 640d, Height: 800d) })
            {
                await PresentationChecks.SwitchModeAsync(window, size.Mode);
                window.Width = size.Width; window.Height = size.Height;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, size.Width, size.Height));
                foreach (string code in new[] { "en", "de" })
                foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    Texts.SetLanguage(code);
                    Application.Current.RequestedThemeVariant = variant;
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                    await page.FocusPostAsync("1000");
                    await Program.WaitUntilAsync(() => HasRenderedPost(page));
                    CheckGeometry(window, page);
                    // Check the actual scrolled content too, not only a spacer at the end.
                    await page.FocusPostAsync("1003");
                    await Program.WaitUntilAsync(() => page.FindControl<ListBox>("ArchivePosts")!.GetVisualDescendants()
                        .OfType<ArchiveAttachmentView>().Any(view => view.Thumbnail.Source is Bitmap bitmap && bitmap.PixelSize.Width > 240
                            && view.GetVisualAncestors().OfType<ListBoxItem>()
                                .Any(item => item.DataContext is ArchivePostRow post && post.Id == "1003")));
                    window.UpdateLayout();
                    CheckGeometry(window, page);
                    CheckMediaCard(page, size.Width);
                    string name = $"smoke-reader-navigation-{size.Mode.ToLowerInvariant()}-{size.Width}x{size.Height}-{code}-{variant.ToString().ToLowerInvariant()}.png";
                    Program.CaptureWindow(window, name);
                }
            }
            // Exercise both sides of the adaptive transition, where an
            // insufficient horizontal gap would otherwise be easy to miss.
            // Keep enough height to exercise the width transition rather than
            // the short-window fallback that preserves the reading viewport.
            await PresentationChecks.SwitchModeAsync(window, "Simple");
            foreach (double transitionWidth in new[] { 944d, 948d })
            {
                window.Width = transitionWidth; window.Height = 920;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, transitionWidth, 920));
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                CheckGeometry(window, page);
                Thickness reserved = page.FindControl<ListBox>("ArchivePosts")!.Margin;
                Require(reserved == default, "Reader navigation reserved a lane next to or below the content: window " + transitionWidth
                    + ", surface " + page.FindControl<Grid>("ReaderContentSurface")!.Bounds.Width + ", margin " + reserved);
            }
            await PresentationChecks.SwitchModeAsync(window, "Barebones");
            window.Width = 640; window.Height = 480;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, 640, 480));
            foreach (string code in new[] { "en", "de" })
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Texts.SetLanguage(code);
                Application.Current.RequestedThemeVariant = variant;
                foreach (bool tiles in new[] { true, false })
                {
                    tabs.SelectedIndex = 1;
                    page.MediaTilesPreference = tiles;
                    await page.MediaLoadTask;
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                    Require(Texts.Language == code && window.ActualThemeVariant == variant,
                        "Secondary media capture lost its requested language or theme.");
                    CheckSecondaryNavigation(window, page);
                    CheckSecondaryMediaGeometry(window, page, tiles);
                    string presentation = tiles ? "tiles" : "table";
                    Program.CaptureWindow(window, $"smoke-reader-navigation-secondary-media-{presentation}-barebones-640-{code}-{variant.ToString().ToLowerInvariant()}.png");
                }
                tabs.SelectedIndex = 2;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Require(Texts.Language == code && window.ActualThemeVariant == variant,
                    "Secondary references capture lost its requested language or theme.");
                CheckSecondaryNavigation(window, page);
                Program.CaptureWindow(window, $"smoke-reader-navigation-secondary-references-barebones-640-{code}-{variant.ToString().ToLowerInvariant()}.png");
            }
            tabs.SelectedIndex = 0;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(page.FindControl<WrapPanel>("ReaderStatusLine")!.IsEffectivelyVisible,
                "Returning from secondary tabs did not restore the posts status line.");
            page.FindControl<Button>("ExportArchiveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => page.FindControl<Border>("ArchiveExportDialog")!.IsVisible);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Border dialog = page.FindControl<Border>("ArchiveExportDialog")!;
            Point arrow = Bounds(page.FindControl<Button>("ReaderNextPost")!, window).Center;
            Visual? hit = window.InputHitTest(arrow) as Visual;
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                hit = window.InputHitTest(arrow) as Visual;
                return hit != null && hit.GetSelfAndVisualAncestors().Contains(dialog);
            });
            page.FindControl<Button>("ArchiveExportClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                tabs.SelectedIndex = 0;
                Texts.SetLanguage(language);
                Application.Current.RequestedThemeVariant = theme;
                await PresentationChecks.SwitchModeAsync(window, mode);
                window.Width = width; window.Height = height;
                await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, width, height));
                page.MediaTilesPreference = mediaTiles;
                if (historyCreated)
                {
                    history.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Program.WaitUntilAsync(() => !history.IsVisible
                        && (page.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id == "1000");
                    await page.MediaLoadTask;
                }
                await page.FocusPostAsync("1000");
                window.UpdateLayout();
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in ReaderNavigationOverlayChecks: " + exception));
        }
        Console.WriteLine("SMOKE reader navigation overlay: 24 EN/DE, Dark/Light, large and B/S/A minimum geometries plus both adaptive threshold edges and 12 explicit secondary-tab states; full-width, full-height overlaid viewport, idle auto-hide and show-on-move, scrollbar/pagebar spacing, controlled toolbar wrapping with visible history label, the 40 dip navigation bar (arrow row, counters, compact form below 700 dip, whole-capsule hit-test, Auto/Always/Off) and adjacent centered pagination, four accessible commands, retained secondary tabs and dialog hit-test passed.");
    }

    // Idle auto-hide (mode Auto): any pointer movement over the reader, the pointer over or near the bar, keyboard focus in it and a
    // navigation shortcut show it; without them it fades out after the idle delay and stops taking clicks.
    private static async Task CheckAutoHideAsync(MainWindow window, ArchivePage page)
    {
        WrapPanel bar = page.FindControl<WrapPanel>("ReaderNavigation")!;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        tabs.SelectedIndex = 0;
        int configured = page.ReaderNavigationIdleDelayMs;
        try
        {
            page.ReaderNavigationIdleDelayMs = 1;
            Require(page.ReaderNavigationIdleDelayMs == ReaderNavigationLayout.IdleHideDelayMsMinimum, "The idle delay was not clamped to its minimum.");
            page.ReaderNavigationIdleDelayMs = int.MaxValue;
            Require(page.ReaderNavigationIdleDelayMs == ReaderNavigationLayout.IdleHideDelayMsMaximum, "The idle delay was not clamped to its maximum.");
            page.ReaderNavigationIdleDelayMs = 600;
            Point far = new(60, 60);
            Point onBar = Bounds(bar, window) is Rect box ? new Point(box.Center.X, box.Center.Y) : default;
            Point barInSurface = bar.Bounds.Center;
            async Task WaitHiddenAsync() => await Program.WaitUntilAsync(() => !page.ReaderNavigationShown && bar.Opacity == 0 && !bar.IsHitTestVisible,
                () => $"The bar did not hide after the idle delay: shown={page.ReaderNavigationShown} opacity={bar.Opacity} hit={bar.IsHitTestVisible}");
            async Task WaitShownAsync() => await Program.WaitUntilAsync(() => page.ReaderNavigationShown && bar.Opacity == 1 && bar.IsHitTestVisible,
                () => $"The bar did not show: shown={page.ReaderNavigationShown} opacity={bar.Opacity} hit={bar.IsHitTestVisible}");
            if (MotionPolicy.IsOff)
                Require(bar.Transitions is not { Count: > 0 }, "The bar fades although motion is off.");
            // Idle: hidden, and clicks go to the content below it.
            page.NotifyReaderPointerMoved(far);
            await WaitShownAsync();
            await WaitHiddenAsync();
            Visual? idleHit = window.InputHitTest(onBar) as Visual;
            Require(idleHit == null || !idleHit.GetSelfAndVisualAncestors().Contains(bar), "The hidden bar still takes clicks.");
            // Any pointer movement over the reader shows it again.
            page.NotifyReaderPointerMoved(far);
            await WaitShownAsync();
            Visual? shownHit = window.InputHitTest(Bounds(page.FindControl<Button>("ReaderNextPost")!, window).Center) as Visual;
            Require(shownHit != null && shownHit.GetSelfAndVisualAncestors().Contains(page.FindControl<Button>("ReaderNextPost")!), "The shown bar is not clickable.");
            await WaitHiddenAsync();
            // A pointer resting near the bar keeps it shown.
            page.NotifyReaderPointerMoved(barInSurface);
            await WaitShownAsync();
            await Task.Delay(1400);
            Require(page.ReaderNavigationShown && bar.Opacity == 1, "The bar hid while the pointer rested on it.");
            page.NotifyReaderPointerMoved(far);
            await WaitHiddenAsync();
            // Keyboard focus keeps it shown; losing focus lets it hide.
            page.FindControl<Button>("ReaderNextPost")!.Focus();
            await WaitShownAsync();
            await Task.Delay(1400);
            Require(page.ReaderNavigationShown && bar.Opacity == 1, "The bar hid while it had keyboard focus.");
            Require(page.FindControl<Button>("ArchiveCloseReader")!.Focus() && !bar.IsKeyboardFocusWithin, "Focus did not leave the bar.");
            await WaitHiddenAsync();
            // A navigation shortcut shows it briefly.
            await page.NavigateReaderAsync(1, 0);
            Require(page.ReaderNavigationShown, "Navigating by shortcut did not show the bar.");
            await WaitHiddenAsync();
            await page.NavigateReaderAsync(-1, 0);
            await WaitHiddenAsync();
            Console.WriteLine("SMOKE reader navigation auto-hide: idle hide, show on move, near/over/focus keep, shortcut reveal, no hit-test while hidden, motion " + (MotionPolicy.IsOff ? "off (instant)" : "on") + " passed.");
        }
        finally { page.ReaderNavigationIdleDelayMs = configured; }
    }

    // NAVBAR-04: the counters follow the navigation. Moving one post on raises the post position by 1 and the first post disables the
    // previous-post arrow; the counter's tooltip and accessible name are the localized sentence; a thread that is not in the list shows a
    // dash and disables both thread arrows. EN and DE.
    private static async Task CheckCountersAsync(MainWindow window, ArchivePage page)
    {
        TextBlock post = page.FindControl<TextBlock>("ReaderPostPosition")!, thread = page.FindControl<TextBlock>("ReaderThreadPosition")!;
        Button up = page.FindControl<Button>("ReaderPreviousPost")!, previousThread = page.FindControl<Button>("ReaderPreviousThread")!,
            nextThread = page.FindControl<Button>("ReaderNextThread")!;
        string language = Texts.Language;
        static (int Position, int Total) Parse(string? text)
        {
            string[] parts = (text ?? string.Empty).Split(" / ");
            return parts.Length == 2 && int.TryParse(parts[0], out int position) && int.TryParse(parts[1], out int total) ? (position, total) : (-1, -1);
        }
        static string Sentence(string key, int position, int total) => Texts.T(key,
            position.ToString(System.Globalization.CultureInfo.CurrentCulture), total.ToString(System.Globalization.CultureInfo.CurrentCulture));
        try
        {
            foreach (string code in new[] { "en", "de" })
            {
                Texts.SetLanguage(code);
                await page.ReaderLocalizationTask;
                await page.FocusPostAsync("1000");
                await Program.WaitUntilAsync(() => Parse(post.Text).Position == 1, () => "The post counter did not show the first post: '" + post.Text + "'.");
                (int first, int total) = Parse(post.Text);
                Require(total >= 2 && !up.IsEnabled, "NAVBAR-04: the first post must disable the previous-post arrow (counter '" + post.Text + "').");
                string expected = Sentence("reader.nav.postPosition", first, total);
                Require(AutomationProperties.GetName(post) == expected && ToolTip.GetTip(post) as string == expected
                    && expected.Contains(code == "de" ? " von " : " of "),
                    "NAVBAR-04: the post counter's accessible name is '" + AutomationProperties.GetName(post) + "', expected '" + expected + "'.");
                Require(AutomationProperties.GetName(page.FindControl<Border>("ReaderNavigationBar")!) == Texts.T("reader.nav.name"),
                    "NAVBAR-04: the bar's accessible name is not the localized 'reader.nav.name'.");
                await page.NavigateReaderAsync(1, 0);
                await Program.WaitUntilAsync(() => Parse(post.Text).Position == first + 1, () => "NAVBAR-04: moving one post on did not raise the counter: '" + post.Text + "'.");
                Require(Parse(post.Text).Total == total && up.IsEnabled, "NAVBAR-04: the total changed or the previous-post arrow stayed disabled after moving on.");
                expected = Sentence("reader.nav.postPosition", first + 1, total);
                Require(AutomationProperties.GetName(post) == expected, "NAVBAR-04: the accessible name did not follow the position: '" + AutomationProperties.GetName(post) + "'.");
                await page.NavigateReaderAsync(-1, 0);
                await Program.WaitUntilAsync(() => Parse(post.Text).Position == first, () => "NAVBAR-04: moving back did not restore the counter: '" + post.Text + "'.");
                if (thread.Text == "\u2013")
                    Require(!previousThread.IsEnabled && !nextThread.IsEnabled, "NAVBAR-04: a thread outside the list must disable both thread arrows.");
                else
                {
                    (int position, int count) = Parse(thread.Text);
                    Require(position >= 1 && position <= count && AutomationProperties.GetName(thread) == Sentence("reader.nav.threadPosition", position, count),
                        "NAVBAR-04: the thread counter '" + thread.Text + "' or its accessible name '" + AutomationProperties.GetName(thread) + "' is wrong.");
                }
                Console.WriteLine("SMOKE NAVBAR-04 counters (" + code + "): post '" + post.Text + "' name '" + AutomationProperties.GetName(post) + "', thread '" + thread.Text + "' name '" + AutomationProperties.GetName(thread) + "'.");
            }
        }
        finally
        {
            Texts.SetLanguage(language);
            await page.ReaderLocalizationTask;
            await page.FocusPostAsync("1003");
        }
    }

    // NAVBAR-06: the three modes. Off: not shown at all and the keys still navigate; Always: stays shown after the idle delay and ignores the
    // pointer; Auto is covered by CheckAutoHideAsync. Switching the position display off forces the compact form.
    private static async Task CheckModesAsync(MainWindow window, ArchivePage page)
    {
        WrapPanel bar = page.FindControl<WrapPanel>("ReaderNavigation")!;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        tabs.SelectedIndex = 0;
        string mode = page.ReaderNavigationMode;
        bool showPosition = page.ReaderNavigationShowPosition;
        int configured = page.ReaderNavigationIdleDelayMs;
        static string Selected(ArchivePage current) => (current.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id ?? string.Empty;
        try
        {
            page.ReaderNavigationIdleDelayMs = 600;
            Require(Config.NormalizeReaderNavigationMode("nonsense") == Config.ReaderNavigationModeAuto, "NAVBAR-06: an unknown mode is not read as Auto.");
            // Off: nothing to see or hit, and Down still navigates (the key handler's own entry point).
            page.ReaderNavigationMode = Config.ReaderNavigationModeOff;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(!bar.IsVisible && !bar.IsEffectivelyVisible && !page.ReaderNavigationShown,
                "NAVBAR-06: mode Off still shows the bar (visible=" + bar.IsVisible + ", shown=" + page.ReaderNavigationShown + ").");
            page.NotifyReaderPointerMoved(new Point(60, 60));
            await page.FocusPostAsync("1000");
            string before = Selected(page);
            KeyEventArgs key = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Down };
            posts.Focus();
            posts.RaiseEvent(key);
            await page.ReaderNavigationTask;
            string after = Selected(page);
            if (before == after) { await page.NavigateReaderAsync(1, 0); after = Selected(page); }
            Require(before != after && !bar.IsVisible && !page.ReaderNavigationShown,
                "NAVBAR-06: with mode Off the keys must still navigate without showing the bar: " + before + " -> " + after + ".");
            await page.FocusPostAsync("1000");
            // Always: shown, hit-testable and still shown long after the idle delay with a far-away pointer.
            page.ReaderNavigationMode = Config.ReaderNavigationModeAlways;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            page.NotifyReaderPointerMoved(new Point(60, 60));
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(bar.IsVisible && page.ReaderNavigationShown && bar.Opacity == 1 && bar.IsHitTestVisible,
                "NAVBAR-06: mode Always hid the bar after the idle delay (opacity " + bar.Opacity + ").");
            // Position off: compact at any width; on again: full where the surface is wide enough.
            Rect surface = Bounds(page.FindControl<Grid>("ReaderContentSurface")!, window);
            page.ReaderNavigationShowPosition = false;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(!page.FindControl<TextBlock>("ReaderPostPosition")!.IsEffectivelyVisible && !page.FindControl<TextBlock>("ReaderThreadPosition")!.IsEffectivelyVisible,
                "NAVBAR-06: switching the position off did not force the compact form.");
            page.ReaderNavigationShowPosition = true;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(page.FindControl<TextBlock>("ReaderPostPosition")!.IsEffectivelyVisible == (surface.Width >= ReaderNavigationLayout.CompactBelowSurfaceWidth),
                "NAVBAR-06: switching the position on again did not restore the form for a surface of " + surface.Width + " dip.");
            // Back to Auto: the bar counts the switch as activity and then hides again after the idle delay.
            page.ReaderNavigationMode = Config.ReaderNavigationModeAuto;
            page.NotifyReaderPointerMoved(new Point(60, 60));
            await Program.WaitUntilAsync(() => !page.ReaderNavigationShown && bar.Opacity == 0 && !bar.IsHitTestVisible,
                () => "NAVBAR-06: back in Auto the bar did not hide after the idle delay.");
            Console.WriteLine("SMOKE NAVBAR-06 modes: Off hidden and keys navigate, Always stays after 1500 ms, position switch forces compact, Auto hides again passed.");
        }
        finally
        {
            page.ReaderNavigationMode = mode;
            page.ReaderNavigationShowPosition = showPosition;
            page.ReaderNavigationIdleDelayMs = configured;
            await page.FocusPostAsync("1003");
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        }
    }

    // Optional review captures (MEOWCHAN_NAVCROSS_EVIDENCE_DIR; the variable keeps its name): the posts and the media tab at 1440 and
    // 1920 dip, dark and light, each with the navigation bar shown and hidden by idling.
    private static async Task CaptureEvidenceAsync(MainWindow window, ArchivePage page, TabControl tabs)
    {
        string? directory = Environment.GetEnvironmentVariable("MEOWCHAN_NAVCROSS_EVIDENCE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        int configured = page.ReaderNavigationIdleDelayMs;
        page.ReaderNavigationIdleDelayMs = ReaderNavigationLayout.IdleHideDelayMsMinimum;
        async Task PairAsync(string name)
        {
            page.NotifyReaderPointerMoved(new Point(60, 60));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Program.CaptureWindow(window, name + "-shown.png", directory);
            await Program.WaitUntilAsync(() => !page.ReaderNavigationShown);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Program.CaptureWindow(window, name + "-hidden.png", directory);
        }
        await PresentationChecks.SwitchModeAsync(window, "Simple");
        foreach (double evidenceWidth in new[] { 1440d, 1920d })
        {
            window.Width = evidenceWidth; window.Height = 920;
            await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, evidenceWidth, 920));
            foreach (ThemeVariant variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current!.RequestedThemeVariant = variant;
                string key = $"{evidenceWidth:0}-{variant.ToString().ToLowerInvariant()}";
                tabs.SelectedIndex = 0;
                await page.FocusPostAsync("1003");
                await Program.WaitUntilAsync(() => HasRenderedPost(page));
                await PairAsync($"thread-posts-{key}");
                foreach (bool tiles in new[] { true, false })
                {
                    tabs.SelectedIndex = 1;
                    page.MediaTilesPreference = tiles;
                    await page.MediaLoadTask;
                    await PairAsync($"media-{(tiles ? "tiles" : "table")}-{key}");
                }
                tabs.SelectedIndex = 2;
                await PairAsync($"references-{key}");
            }
        }
        page.ReaderNavigationIdleDelayMs = configured;
        tabs.SelectedIndex = 0;
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }

    private static bool HasRenderedPost(ArchivePage page) => page.FindControl<ListBox>("ArchivePosts")!
        .GetVisualDescendants().OfType<ListBoxItem>().Any(item => item.IsEffectivelyVisible && item.Bounds.Width > 0);

    // A post with media is one block: the star shares the file-line row (not a row of its own above it), the thumbnail starts
    // right under that row and the text beside it starts at the file line; the card is not capped to a reading width.
    private static void CheckMediaCard(ArchivePage page, double windowWidth)
    {
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ListBoxItem item = posts.GetVisualDescendants().OfType<ListBoxItem>().First(candidate => (candidate.DataContext as ArchivePostRow)?.Id == "1003");
        ArchiveAttachmentView view = item.GetVisualDescendants().OfType<ArchiveAttachmentView>().First();
        Button star = view.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ArchiveMediaFavorite");
        TextBlock label = view.GetVisualDescendants().OfType<TextBlock>().First(text => text.FontSize == 12 && !string.IsNullOrEmpty(text.Text) && text.Text.Contains('('));
        Point starTop = star.TranslatePoint(default, view)!.Value, labelTop = label.TranslatePoint(default, view)!.Value;
        Point imageTop = view.Thumbnail.TranslatePoint(default, view)!.Value;
        Require(starTop.Y + star.Bounds.Height <= imageTop.Y + 1 && Math.Abs((starTop.Y + star.Bounds.Height / 2) - (labelTop.Y + label.Bounds.Height / 2)) < 6,
            $"The star did not share the file-line row: star {starTop}/{star.Bounds.Size}, label {labelTop}/{label.Bounds.Size}, image {imageTop}.");
        Require(imageTop.Y <= star.Bounds.Height + 8, $"The thumbnail floated below the file line: top {imageTop.Y}.");
        Require(view.Thumbnail.Bounds.Height > 0 && view.Thumbnail.Bounds.Height <= view.Thumbnail.Bounds.Width * 9 / 16 + 1,
            "The thumbnail slot was taller than 16:9: " + view.Thumbnail.Bounds);
        ReaderBodyTextBlock body = item.GetVisualDescendants().OfType<ReaderBodyTextBlock>().First();
        Point bodyAtItem = body.TranslatePoint(default, item)!.Value, viewAtItem = view.TranslatePoint(default, item)!.Value;
        if (bodyAtItem.X > viewAtItem.X + view.Bounds.Width)
            Require(Math.Abs(bodyAtItem.Y - (viewAtItem.Y + labelTop.Y)) < 14, $"The text did not start at the file line: body {bodyAtItem}, file line {viewAtItem.Y + labelTop.Y}.");
        Grid surface = page.FindControl<Grid>("ReaderContentSurface")!;
        Require(posts.Bounds.Width >= surface.Bounds.Width - 1, $"The post list was capped or left a lane for the cross: {posts.Bounds.Width} of {surface.Bounds.Width}.");
    }

    private static void CheckGeometry(MainWindow window, ArchivePage page)
    {
        WrapPanel navigation = page.FindControl<WrapPanel>("ReaderNavigation")!;
        // The compact form follows the surface width from a layout pass of its own: settle both passes before measuring.
        window.UpdateLayout(); window.UpdateLayout();
        Rect pad = Bounds(navigation, window);
        Rect surface = Bounds(page.FindControl<Grid>("ReaderContentSurface")!, window);
        Rect pageBar = Bounds(page.FindControl<Grid>("ArchivePostNavigation")!, window);
        Console.WriteLine("SMOKE reader navigation precondition: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            mode = window.SessionHost.Config.WorkspaceMode, language = Texts.Language,
            theme = Application.Current!.RequestedThemeVariant?.ToString(), scale = window.RenderScaling,
            client = window.ClientSize.ToString(), navigation.IsVisible, navigation.IsEffectivelyVisible,
            navigation.IsMeasureValid, navigation.IsArrangeValid, localBounds = navigation.Bounds.ToString(),
            pad = pad.ToString(), surface = surface.ToString(), pagebar = pageBar.ToString(),
            arrows = new[] { "ReaderPreviousPost", "ReaderNextPost", "ReaderPreviousThread", "ReaderNextThread" }
                .Select(name => new { name, bounds = Bounds(page.FindControl<Button>(name)!, window).ToString() }).ToArray()
        }));
        Require(page.FindControl<WrapPanel>("ReaderStatusLine")!.IsEffectivelyVisible,
            "The posts tab lost its reader status line.");
        // NAVBAR-01: a 40 dip high capsule, at most 320 dip wide, inset at the lower right just above the page bar.
        // One device pixel of tolerance for the border's layout rounding.
        double devicePixelDip = 1 / window.RenderScaling;
        Require(navigation.IsEffectivelyVisible && Math.Abs(pad.Height - ReaderNavigationLayout.BarHeight) <= devicePixelDip + 1e-6
            && pad.Width <= ReaderNavigationLayout.BarMaxWidth + devicePixelDip && pad.Width >= 100,
            "NAVBAR-01: the reader navigation is no longer a compact visible bar of 40 dip height and at most 320 dip width: " + pad);
        Require(pad.Right <= surface.Right - 20 && pad.Bottom <= pageBar.Top - 7
            && pad.Bottom >= pageBar.Top - 10 && pad.Top >= surface.Top,
            "NAVBAR-01: the reader navigation is not inset at the lower right above the page bar: " + pad + " in " + surface + ", page bar " + pageBar);
        CheckBarGeometry(window, page, pad, surface);
        CheckPaginationGeometry(window, page, pageBar);
        ActionToolBar toolbar = page.FindControl<ActionToolBar>("ReaderToolbar")!;
        CheckToolbarGeometry(window, page, toolbar);
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        Require(scroll.Viewport.Height >= 100, "Navigation reduced the small-window reader viewport below 100 DIP.");
        Rect postBounds = Bounds(posts, window);
        Rect postArea = Bounds((Control)posts.Parent!, window);
        Require(posts.Margin.Bottom == 0 && Math.Abs(postBounds.Bottom - postArea.Bottom) <= 1,
            "Reader navigation still subtracts a full-width bottom lane from the thread viewport.");
        Require(posts.ItemsPanelRoot is VirtualizingStackPanel,
            "Overlay navigation disabled post virtualization.");
        Require(posts.Margin == default && Math.Abs(postBounds.Right - Bounds(page.FindControl<Grid>("ReaderContentSurface")!, window).Right) <= 1
            && postBounds.Intersect(pad).Height > 0 && postBounds.Intersect(pad).Width > 0,
            "Navigation is not an overlay inside the full-width, full-height thread viewport.");
        Console.WriteLine($"SMOKE reader navigation geometry: mode={window.SessionHost.Config.WorkspaceMode}; language={Texts.Language}; theme={Application.Current!.RequestedThemeVariant}; client={window.ClientSize}; scale={window.RenderScaling}; toolbar={toolbar.Bounds.Height}; viewport={scroll.Viewport}; posts={postBounds}; pad={pad}; pagebar={pageBar}; reservation={posts.Margin}.");
        foreach (ScrollBar bar in posts.GetVisualDescendants().OfType<ScrollBar>()
            .Where(bar => bar.IsEffectivelyVisible && bar.Orientation == Orientation.Vertical && bar.Bounds.Width > 0))
            Require(Separated(Bounds(bar, window), pad, 4),
                "Reader navigation crowds or covers the vertical scrollbar.");
        foreach (var command in new[] { (Name: "ReaderPreviousThread", Id: "reader.previous-thread"),
            (Name: "ReaderNextThread", Id: "reader.next-thread"), (Name: "ReaderPreviousPost", Id: "reader.previous-post"),
            (Name: "ReaderNextPost", Id: "reader.next-post") })
        {
            Button button = page.FindControl<Button>(command.Name)!;
            Require(button.IsEffectivelyVisible && button.Focusable && Bounds(button, window).Width >= 32
                && Bounds(button, window).Height >= 32
                && AutomationProperties.GetName(button) == CommandCatalog.Label(command.Id)
                && ToolTip.GetTip(button) as string == CommandBindings.Tooltip(command.Id),
                command.Name + " lost its stable visible button or localized command semantics.");
        }
    }

    private static void CheckSecondaryNavigation(MainWindow window, ArchivePage page)
    {
        WrapPanel navigation = page.FindControl<WrapPanel>("ReaderNavigation")!;
        TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
        // The References tab exists only while captured replies are listed; with none it is not there to be chosen.
        Require(!page.FindControl<TabItem>("ArchiveReferencesTab")!.IsVisible || page.FindControl<ListBox>("ArchiveReferences")!.ItemCount > 0,
            "The References tab is shown without any reference.");
        Control reservedContent = tabs.SelectedIndex == 1
            ? page.FindControl<Grid>("ArchiveMediaSurface")! : page.FindControl<ListBox>("ArchiveReferences")!;
        Rect pad = Bounds(navigation, window), content = Bounds(reservedContent, window);
        WrapPanel status = page.FindControl<WrapPanel>("ReaderStatusLine")!;
        Require(!status.IsVisible && !status.IsEffectivelyVisible,
            "The large reader retains its posts-only status line on a secondary tab.");
        Console.WriteLine("SMOKE reader navigation secondary: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            tab = tabs.SelectedIndex, language = Texts.Language, theme = window.ActualThemeVariant.ToString(),
            scale = window.RenderScaling, client = window.ClientSize.ToString(), pad = pad.ToString(),
            contentName = reservedContent.Name, content = content.ToString(),
            reservation = reservedContent.Margin.ToString(), navigation.IsEffectivelyVisible,
            statusVisible = status.IsVisible, statusEffectivelyVisible = status.IsEffectivelyVisible
        }));
        Require(navigation.IsEffectivelyVisible && Contains(Bounds(page.FindControl<Grid>("ReaderContentSurface")!, window), pad),
            "Reader navigation disappeared or is clipped on a secondary tab.");
        // The content runs to the right edge and the bar floats over it; the last row can scroll clear of the bar.
        Require(reservedContent.Margin == default && Math.Abs(content.Right - Bounds(tabs, window).Right) <= 24,
            "Secondary-tab content does not use the full width: " + content + " in " + Bounds(tabs, window));
        Require(pad.Right <= content.Right && pad.Left >= content.Left && pad.Bottom <= content.Bottom + 1,
            "The bar does not float over its secondary-tab content: " + pad + " over " + content);
        // NAVBAR-05: the whole capsule takes the pointer. A point on a counter (full form) or in the gap beside the separator (every form)
        // hits the bar, not the content below it; a click in a gap would otherwise reach a post.
        page.ShowReaderNavigation();
        Rect separator = Bounds(page.FindControl<Border>("ReaderNavigationSeparator")!, window);
        Point gap = new(separator.Center.X, pad.Top + 5);
        Visual? gapHit = window.InputHitTest(gap) as Visual;
        Require(gapHit != null && gapHit.GetSelfAndVisualAncestors().Contains(navigation),
            "NAVBAR-05: a click beside the separator inside the capsule passed through to the content: " + gap + " in " + pad);
        foreach (string counterName in new[] { "ReaderThreadPosition", "ReaderPostPosition" })
        {
            TextBlock counter = page.FindControl<TextBlock>(counterName)!;
            if (!counter.IsEffectivelyVisible) continue;
            Visual? counterHit = window.InputHitTest(Bounds(counter, window).Center) as Visual;
            Require(counterHit != null && counterHit.GetSelfAndVisualAncestors().Contains(navigation),
                "NAVBAR-05: a click on the counter " + counterName + " passed through to the content.");
        }
        Visual? buttonHit = window.InputHitTest(Bounds(page.FindControl<Button>("ReaderNextPost")!, window).Center) as Visual;
        Require(buttonHit != null && buttonHit.GetSelfAndVisualAncestors().Contains(page.FindControl<Button>("ReaderNextPost")!),
            "A bar button is not hit-testable.");
        if (tabs.SelectedIndex == 2)
            foreach (string name in new[] { "ArchiveReferencesTitle", "PreviousReferences", "ArchiveReferencesPage", "NextReferences" })
            {
                Control control = page.FindControl<Control>(name)!;
                if (!control.IsEffectivelyVisible || control.Bounds.Width == 0 || control.Bounds.Height == 0) continue;
                Rect box = Bounds(control, window);
                Require(Contains(Bounds(tabs, window), box) && Separated(box, pad, 4),
                    name + " is clipped or crowded by the reader navigation.");
            }
    }

    private static bool Separated(Rect first, Rect second, double gap) => first.Right + gap <= second.Left
        || first.Bottom + gap <= second.Top || second.Right + gap <= first.Left || second.Bottom + gap <= first.Top;

    private static void CheckSecondaryMediaGeometry(MainWindow window, ArchivePage page, bool tiles)
    {
        Control list = tiles ? page.FindControl<ListBox>("ArchiveMediaTiles")! : page.FindControl<DataGrid>("ArchiveMediaGrid")!;
        Control viewport = tiles
            ? list.GetVisualDescendants().OfType<ScrollContentPresenter>().First()
            : list.GetVisualDescendants().OfType<DataGridRowsPresenter>().First();
        Rect viewportBox = Bounds(viewport, window), listBox = Bounds(list, window);
        Rect tabBox = Bounds(page.FindControl<TabControl>("ArchiveContentTabs")!, window);
        Rect pad = Bounds(page.FindControl<WrapPanel>("ReaderNavigation")!, window);
        Rect visibleViewport = viewportBox.Intersect(listBox).Intersect(tabBox).Intersect(new Rect(default(Point), window.ClientSize));
        var items = list.GetVisualDescendants().OfType<Control>()
            .Where(control => (tiles ? control is ArchiveMediaTileView : control is DataGridRow)
                && control.IsEffectivelyVisible && control.DataContext is ArchiveAssetRow)
            .Select(control => Bounds(control, window).Intersect(visibleViewport)).ToArray();
        string[] controlNames = { "ArchiveMediaMode", "ArchiveMediaColumnsButton", "ArchiveFavoritesFilter", "ArchiveRatingFilter",
            "ArchiveMediaSort", "ArchiveVersionScope", "ArchiveEditRatingButton", "PreviewArchiveMediaButton" };
        // A control the bar moved under "More" is detached while the popup is closed (and keeps a stale IsEffectivelyVisible); only
        // the controls on the bar take part in the viewport geometry, the others are checked as "on the bar or under More" below.
        var controls = controlNames.Select(name => page.FindControl<Control>(name)!)
            .Where(control => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible)
            .Select(control => (Control: control, Box: Bounds(control, window))).ToArray();
        Console.WriteLine("SMOKE reader navigation media viewport: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            tiles, language = Texts.Language, theme = window.ActualThemeVariant.ToString(), scale = window.RenderScaling,
            client = window.ClientSize.ToString(), viewport = viewportBox.ToString(), visibleViewport = visibleViewport.ToString(),
            list = listBox.ToString(), items = items.Select(box => box.ToString()).ToArray(),
            controls = controls.Select(item => new { name = item.Control.Name, bounds = item.Box.ToString() }).ToArray()
        }));
        Require(list.IsEffectivelyVisible && ClippedSizeAtLeast(visibleViewport.Height, 100)
            && ClippedSizeAtLeast(visibleViewport.Width, 100),
            "Reader navigation/filter layout leaves less than 100 DIP of usable media viewport.");
        Require(items.Any(box => ClippedSizeAtLeast(box.Width, 100)
            && ClippedSizeAtLeast(box.Height, tiles ? 100 : 20)),
            "The media viewport has no usable visible asset tile or table row.");
        foreach (var item in controls)
        {
            Require(item.Box.Width > 0 && item.Box.Height > 0 && Contains(tabBox, item.Box)
                && Separated(item.Box, pad, 4),
                item.Control.Name + " is clipped or crowded by navigation on the small reader media tab.");
            Rect overlap = item.Box.Intersect(visibleViewport);
            Require(overlap.Width <= 1 || overlap.Height <= 1, item.Control.Name + " overlaps the media viewport.");
        }
        // WV3: on the bar, or under the media actions' More where the view (Barebones) or the width moved them.
        Require(ReaderWorkViewChecks.OnBarOrUnderMore(page, "ArchiveEditRatingButton")
            && ReaderWorkViewChecks.OnBarOrUnderMore(page, "PreviewArchiveMediaButton"),
            "Small reader media tab lost its rating or preview actions.");
        foreach (string bar in new[] { "ReaderMediaTools", "ReaderMediaActions" })
            ReaderWorkViewChecks.RequireOverflowFollowsPolicy(page, page.FindControl<ActionToolBar>(bar)!, () => bar + " on the small media tab");
        for (int first = 0; first < controls.Length; first++)
            for (int second = first + 1; second < controls.Length; second++)
            {
                Rect overlap = controls[first].Box.Intersect(controls[second].Box);
                Require(overlap.Width <= 1 || overlap.Height <= 1, "Small reader media controls overlap.");
            }
    }

    // Reader07: Rect.Intersect changed an exact 100-DIP viewport to
    // 99.99999999999994 DIP. Item intersections use the same subtraction.
    private static bool ClippedSizeAtLeast(double actual, double minimum) => actual >= minimum - GeometryEpsilonDip;

    private static void CheckToolbarGeometry(MainWindow window, ArchivePage page, ActionToolBar toolbar)
    {
        Rect area = Bounds(toolbar, window);
        Rect client = new(default(Point), window.ClientSize);
        Panel direct = toolbar.DirectHost ?? throw new InvalidOperationException("Reader toolbar has no direct action host.");
        Panel? overflowHost = toolbar.OverflowHost;
        var buttons = direct.Children.OfType<Button>().Where(button => button.IsEffectivelyVisible)
            .Select(button => (Button: button, Box: Bounds(button, window)))
            .OrderBy(item => item.Box.Left).ToArray();
        string[] overflowed = overflowHost?.Children.OfType<Control>().Where(item => item.IsVisible).Select(item => item.Name ?? "").ToArray() ?? [];
        string Describe() => "client width " + window.ClientSize.Width + ", toolbar width " + area.Width.ToString("0.#") + "; direct "
            + string.Join(", ", buttons.Select(item => item.Button.Name + "@" + item.Box.Left.ToString("0") + "/" + item.Box.Top.ToString("0") + "=" + item.Box.Width.ToString("0.#")))
            + "; overflow " + string.Join(", ", overflowed) + ".";
        Require(buttons.Length > 0, "Reader toolbar has no visible actions.");
        foreach (var item in buttons)
        {
            Rect box = item.Box;
            Require(box.Width > 0 && Math.Abs(box.Height - 32) <= 1 && Math.Abs(box.Top - buttons[0].Box.Top) <= 1
                && Contains(area, box) && Contains(client, box),
                item.Button.Name + " is clipped, wraps or is no longer a compact toolbar button: " + Describe());
            if (item.Button.Content is Control content)
                Require(Contains(box, Bounds(content, window)), item.Button.Name + " clips its visible icon or label.");
        }
        for (int index = 1; index < buttons.Length; index++)
        {
            // ActionToolBar deliberately reserves a larger, separator-bearing gap between
            // groups; within a group the compact action gap is 8 DIP. Measure the actual
            // contract instead of treating the decorative group separator as a spacing bug.
            int previousGroup = ActionToolBar.GetGroup(buttons[index - 1].Button);
            int group = ActionToolBar.GetGroup(buttons[index].Button);
            double expectedGap = previousGroup == group ? ActionToolBarPanel.ItemGap : ActionToolBarPanel.GroupGap;
            Require(Math.Abs(buttons[index].Box.Left - buttons[index - 1].Box.Right - expectedGap) <= 1,
                "Visible reader toolbar actions lost their measured horizontal spacing: " + Describe());
        }
        Require(Math.Abs(area.Height - 32) <= 1, "Reader toolbar reserves unexplained vertical space: " + Describe());
        // WV3: the work view decides what sits under More, and the width moves the tail of the policy's keep-visible order
        // (WorkViewPolicy.ReaderToolbarKeepVisibleOrder) first; Required actions (way back, star, state returns) never move.
        ReaderWorkViewChecks.RequireOverflowFollowsPolicy(page, toolbar, Describe);
        Require(overflowed.Length == 0 || toolbar.More?.IsEffectivelyVisible == true, "Overflowed reader actions are unreachable without More: " + Describe());
        for (int first = 0; first < buttons.Length; first++)
            for (int second = first + 1; second < buttons.Length; second++)
            {
                Rect overlap = buttons[first].Box.Intersect(buttons[second].Box);
                Require(overlap.Width <= 1 || overlap.Height <= 1, "Reader toolbar actions overlap.");
            }
        Button history = page.FindControl<Button>("ArchiveHistoryBack")!;
        string expected = CommandCatalog.Label("reader.history-back");
        bool historyInMore = overflowed.Contains(history.Name);
        // Narrow readers keep history behind More; its label and command semantics move with it.
        TextBlock? label = history.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(text => (historyInMore || text.IsEffectivelyVisible) && text.Text == expected);
        Require(history.IsVisible && history.IsEffectivelyEnabled && history.Focusable && label != null
            && (historyInMore ? overflowHost!.Children.Contains(history)
                : history.IsEffectivelyVisible && label.Bounds.Width > 0 && label.DesiredSize.Width <= label.Bounds.Width + 1
                    && Contains(Bounds(history, window), Bounds(label, window)))
            && AutomationProperties.GetName(history) == expected
            && ToolTip.GetTip(history) as string == CommandBindings.Tooltip("reader.history-back"),
            "Reader history lacks its visible, unclipped localized label or accessible command semantics.");
        Console.WriteLine("SMOKE reader navigation toolbar: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            mode = window.SessionHost.Config.WorkspaceMode, language = Texts.Language,
            theme = Application.Current!.RequestedThemeVariant?.ToString(), client = window.ClientSize.ToString(),
            scale = window.RenderScaling, bounds = area.ToString(), overflow = overflowed,
            buttons = buttons.Select(item => new { name = item.Button.Name, bounds = item.Box.ToString() }).ToArray(),
            historyLabel = label!.Text, historyInMore
        }));
    }

    private static bool Contains(Rect outer, Rect inner) => inner.Left >= outer.Left - 1
        && inner.Top >= outer.Top - 1 && inner.Right <= outer.Right + 1 && inner.Bottom <= outer.Bottom + 1;

    // NAVBAR-02 and NAVBAR-03: the four arrows in one row from left to right (previous thread, next thread, previous post, next post)
    // on one centre line inside the bar, each at least 32 x 32; the counters sit between their arrows only in the full form, the full form
    // applies from 700 dip of reader surface and when the position is shown, and nothing in it is clipped.
    private static void CheckBarGeometry(MainWindow window, ArchivePage page, Rect pad, Rect surface)
    {
        Rect[] arrows = new[] { "ReaderPreviousThread", "ReaderNextThread", "ReaderPreviousPost", "ReaderNextPost" }
            .Select(name => Bounds(page.FindControl<Button>(name)!, window)).ToArray();
        Require(arrows[0].Center.X < arrows[1].Center.X && arrows[1].Center.X < arrows[2].Center.X && arrows[2].Center.X < arrows[3].Center.X
            && arrows.All(arrow => Math.Abs(arrow.Center.Y - pad.Center.Y) <= 1),
            "NAVBAR-02: the reader arrows are not one row (previous thread, next thread, previous post, next post) on the bar's centre line: " + string.Join(" ", arrows));
        foreach (Rect arrow in arrows)
            Require(arrow.Width >= 32 - 1e-6 && arrow.Height >= 32 - 1e-6 && arrow.Left >= pad.Left && arrow.Top >= pad.Top
                && arrow.Right <= pad.Right && arrow.Bottom <= pad.Bottom,
                "NAVBAR-02: a reader arrow is smaller than 32 x 32 dip or lies outside the navigation bar: " + arrow + " in " + pad);
        double devicePixelDip = 1 / window.RenderScaling;
        TextBlock threadCounter = page.FindControl<TextBlock>("ReaderThreadPosition")!, postCounter = page.FindControl<TextBlock>("ReaderPostPosition")!;
        bool showPosition = page.ReaderNavigationShowPosition;
        bool expectCompact = ReaderNavigationLayout.IsCompact(surface.Width, showPosition);
        Require(threadCounter.IsEffectivelyVisible == !expectCompact && postCounter.IsEffectivelyVisible == !expectCompact,
            "NAVBAR-03: the counters are " + (threadCounter.IsEffectivelyVisible ? "shown" : "hidden") + " at a reader surface of " + surface.Width
            + " dip (compact below " + ReaderNavigationLayout.CompactBelowSurfaceWidth + " dip or without position: " + expectCompact + ").");
        if (expectCompact)
        {
            // Four 32 dip arrows, the separator and the frame: 157 dip.
            Require(Math.Abs(pad.Width - 157) <= 2 + devicePixelDip, "NAVBAR-03: the compact bar is not 157 dip wide: " + pad.Width);
            Require(arrows[0].Right + 2 <= arrows[1].Left + 1 && arrows[2].Right + 2 <= arrows[3].Left + 1,
                "NAVBAR-03: the two arrows of a compact group lost their 2 dip gap.");
        }
        else
        {
            Rect thread = Bounds(threadCounter, window), post = Bounds(postCounter, window);
            Require(arrows[0].Right <= thread.Left + 1 && thread.Right <= arrows[1].Left + 1 && arrows[2].Right <= post.Left + 1 && post.Right <= arrows[3].Left + 1,
                "NAVBAR-03: a counter does not sit between its two arrows.");
            foreach (TextBlock counter in new[] { threadCounter, postCounter })
            {
                Rect box = Bounds(counter, window);
                Require(box.Width >= ReaderNavigationLayout.CounterMinWidth - 1e-6 && !string.IsNullOrEmpty(counter.Text)
                    && counter.DesiredSize.Width - counter.Margin.Left - counter.Margin.Right <= counter.Bounds.Width + 1 && Contains(pad, box),
                    "NAVBAR-03: the counter " + counter.Name + " is clipped or narrower than 56 dip: " + box + " text '" + counter.Text + "'.");
            }
        }
        Console.WriteLine("SMOKE NAVBAR-01/02/03 bar: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            language = Texts.Language, theme = Application.Current!.RequestedThemeVariant?.ToString(), surface = surface.Width, compact = expectCompact,
            bar = pad.ToString(), arrows = arrows.Select(arrow => arrow.ToString()).ToArray(),
            counters = new[] { threadCounter.Text, postCounter.Text }
        }));
    }

    private static void CheckPaginationGeometry(MainWindow window, ArchivePage page, Rect pageBar)
    {
        Button previous = page.FindControl<Button>("PreviousPostsButton")!;
        Button next = page.FindControl<Button>("NextPostsButton")!;
        TextBlock label = page.FindControl<TextBlock>("ArchivePostPage")!;
        Rect before = Bounds(previous, window), after = Bounds(next, window), text = Bounds(label, window);
        Rect available = Bounds((Control)page.FindControl<Grid>("ArchivePostNavigation")!.Parent!, window);
        Console.WriteLine("SMOKE reader navigation pagination: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            previous = before.ToString(), range = text.ToString(), next = after.ToString(),
            group = pageBar.ToString(), available = available.ToString(), label = label.Text
        }));
        Require(previous.IsEffectivelyVisible && next.IsEffectivelyVisible && label.IsEffectivelyVisible
            && !string.IsNullOrWhiteSpace(label.Text) && text.Width > 0,
            "Reader pagination lost its visible arrows or range label.");
        Require(Math.Abs(text.Left - before.Right - 8) <= 1
            && Math.Abs(after.Left - text.Right - 8) <= 1
            && Math.Abs(before.Center.Y - text.Center.Y) <= 1
            && Math.Abs(after.Center.Y - text.Center.Y) <= 1,
            "Reader page arrows are not immediately beside and aligned with the range label.");
        // Diagnostic06 measured only a 5.68e-14-DIP floating-point remainder at the right edge.
        Require(Math.Abs(text.Center.X - pageBar.Center.X) <= 1
            && Math.Abs(pageBar.Center.X - available.Center.X) <= 1
            && before.Left >= pageBar.Left - GeometryEpsilonDip
            && after.Right <= pageBar.Right + GeometryEpsilonDip,
            "Reader pagination is not a centered, contained group.");
    }

    private static Rect Bounds(Control control, Visual root) => new(control.TranslatePoint(default, root)
        ?? throw new InvalidOperationException(control.Name + " is detached."), control.Bounds.Size);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
