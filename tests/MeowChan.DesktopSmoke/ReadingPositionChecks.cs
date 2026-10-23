using System.Reflection;
using Pointer = Avalonia.Input.Pointer;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class ReadingPositionChecks
{
    internal static async Task RunAsync(MainWindow window, string profile)
    {
        ArchivePage page = window.ArchivePage;
        ContentControl reader = window.FindControl<ContentControl>("ReaderHost")!;
        Require(!reader.IsVisible, "Startup automatically opened the reader.");
        Require(window.SelectedPage == MainWindow.WorkspacePage.Downloads, "Startup changed the existing start page.");
        if (File.Exists(Path.Combine(profile, "reading-restart-ready")))
        {
            await ReadingPositionAnchorChecks.CheckRestartAsync(window, profile);
            return;
        }
        await CheckSettingsSemanticsAsync(window);
        Require(!window.SessionHost.Config.RememberReadingPosition, "New profile opted in automatically.");
        await CheckEmptyFooterResumeAsync(window);
        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        await Program.WaitUntilAsync(() => !page.IsBusy && page.FindControl<ListBox>("ArchiveThreads")!.ItemCount > 0);
        page.FindControl<ComboBox>("ArchiveSort")!.SelectedIndex = 2;
        await page.ApplySearchAsync();
        await page.LoadThreadAsync(new ResourceId("4chan", "g", "1000"));
        Require(await window.SessionHost.ReadReadingPositionAsync() == null, "Passive preview wrote a reading position.");
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        await Program.WaitUntilAsync(() => reader.IsVisible && SelectedPost(page) == "1000");
        Require(await window.SessionHost.ReadReadingPositionAsync() == null, "Reader wrote before opt-in.");
        await CheckWheelCaptureBatchAsync(window, page, expectedCaptures: 0);
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        Click(window.FindControl<Button>("SettingsNavigationButton")!);
        window.SettingsEditor.RememberReadingPosition = true;
        Click(window.SettingsEditor.FindControl<Button>("SaveSettingsButton")!);
        await Program.WaitUntilAsync(() => !window.SettingsEditor.IsBusy && window.SessionHost.Config.RememberReadingPosition);
        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        await Program.WaitUntilAsync(() => !page.IsBusy);
        await page.LoadThreadAsync(new ResourceId("4chan", "g", "1000"));
        Require(await window.SessionHost.ReadReadingPositionAsync() == null, "Enabling or passive preview created an entry.");
        Click(page.FindControl<Button>("ArchiveReadPosts")!);
        await Program.WaitUntilAsync(() => reader.IsVisible && SelectedPost(page) == "1000");
        await CheckWheelCaptureBatchAsync(window, page, expectedCaptures: 1);
        Click(page.FindControl<Button>("NextPostsButton")!);
        await WaitForAsync(async () => (await window.SessionHost.ReadReadingPositionAsync())?.PostId == "1050");
        await page.FocusPostAsync("1022");
        await WaitForAsync(async () => (await window.SessionHost.ReadReadingPositionAsync())?.SelectedPostId == "1022");
        LocalReadingPosition? captured = await window.SessionHost.ReadReadingPositionAsync();
        Require(captured?.Thread == "1000", "Reader movement captured a different thread.");
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        FooterNavigationChecks.RequireResumeAction(window);
        Click(window.FindControl<Button>("ContinueReadingNavigationButton")!);
        await WaitForFooterResumeAsync(window, "1022");
        Require(await window.SessionHost.ReadReadingPositionAsync() == captured,
            "Explicit footer resume changed the saved reading position.");
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        Click(window.FindControl<Button>("SettingsNavigationButton")!);
        string positionFile = Path.Combine(profile, "reading-position.json");
        string retained = positionFile + ".test-retained";
        File.Move(positionFile, retained);
        Directory.CreateDirectory(positionFile);
        try
        {
            Click(window.SettingsEditor.FindControl<Button>("ForgetReadingPositionButton")!);
            await Program.WaitUntilAsync(() => !window.SettingsEditor.IsBusy && window.SettingsEditor.HasError);
            await Program.WaitUntilAsync(() => window.SettingsEditor.FindControl<TextBlock>("SettingsError")!.IsEffectivelyVisible);
            window.UpdateLayout();
            Program.CaptureWindow(window, "smoke-settings-reading-position-error.png");
            Require(File.Exists(retained), "Failed forgetting destroyed the retained fixture.");
        }
        finally { Directory.Delete(positionFile); File.Move(retained, positionFile); }
        Click(window.SettingsEditor.FindControl<Button>("ForgetReadingPositionButton")!);
        await Program.WaitUntilAsync(() => !window.SettingsEditor.IsBusy && window.SettingsEditor.StatusMessage == Texts.T("resume.forgotten"));
        Require(window.SessionHost.Config.RememberReadingPosition, "Forget unexpectedly disabled the option.");
        await CheckEmptyFooterResumeAsync(window);
        Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
        await Program.WaitUntilAsync(() => !page.IsBusy);
        await page.LoadThreadAsync(new ResourceId("4chan", "g", "1001"), resetPage: false);
        Require(await window.SessionHost.ReadReadingPositionAsync() == null, "Refresh restored a forgotten position.");
        await window.SessionHost.RememberReadingPositionAsync(new LocalReadingPosition("4chan", "g", "1000", "missing", -25, "missing"), window.SessionHost.ReadingPositionGeneration);
        FooterNavigationChecks.RequireResumeAction(window);
        Click(window.FindControl<Button>("ContinueReadingNavigationButton")!);
        await WaitForFooterResumeAsync(window, "1000");
        Require(page.FindControl<TextBlock>("ArchiveMessage")!.Text == Texts.T("resume.fallback"), "Missing-post replacement was not explained.");
        Require((await window.SessionHost.ReadReadingPositionAsync())?.PostId == "missing", "Fallback silently replaced the saved anchor.");
        Program.CaptureWindow(window, "smoke-reading-position-fallback.png");
        await page.FocusPostAsync("1022");
        await WaitForAsync(async () => (await window.SessionHost.ReadReadingPositionAsync())?.SelectedPostId == "1022");
        Click(page.FindControl<Button>("ArchiveCloseReader")!);
        SemaphoreSlim commands = (SemaphoreSlim)typeof(DesktopSessionHost).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.SessionHost)!;
        await commands.WaitAsync();
        Task delayedResume = window.ResumeLastReadingAsync();
        try { await page.LoadThreadAsync(new ResourceId("4chan", "g", "1001")); }
        finally { commands.Release(); }
        await delayedResume;
        Require(!reader.IsVisible && page.CurrentThread?.Summary.Id.Thread == "1001", "A delayed resume replaced a newer chosen thread.");
        await commands.WaitAsync();
        delayedResume = window.ResumeLastReadingAsync();
        try
        {
            Click(window.FindControl<Button>("DownloadsNavigationButton")!);
            Click(window.FindControl<MenuItem>("ArchiveMenuItem")!);
            await Program.WaitUntilAsync(() => !page.IsBusy);
        }
        finally { commands.Release(); }
        await delayedResume;
        Require(!reader.IsVisible, "A delayed resume survived navigating away and back.");
        await window.ResumeLastReadingAsync();
        await Program.WaitUntilAsync(() => reader.IsVisible && SelectedPost(page) == "1022");
        await commands.WaitAsync();
        delayedResume = window.ResumeLastReadingAsync();
        Func<bool>? pagedReaderUnchanged = null;
        try
        {
            Click(page.FindControl<Button>("NextPostsButton")!);
            await Program.WaitUntilAsync(() => page.FindControl<ListBox>("ArchivePosts")!.ItemsSource is IEnumerable<ArchivePostRow> rows
                && rows.FirstOrDefault()?.Id == "1050");
            pagedReaderUnchanged = page.CaptureReadingRequestGuard();
        }
        finally { commands.Release(); }
        await delayedResume;
        await WaitForAsync(async () =>
        {
            LocalReadingPosition? position = await window.SessionHost.ReadReadingPositionAsync();
            return int.TryParse(position?.PostId, out int post) && post is >= 1050 and <= 1054;
        });
        Require(pagedReaderUnchanged?.Invoke() == true, "The stale resume rebuilt the newly chosen reader page.");
        Require(reader.IsVisible && page.FindControl<ListBox>("ArchivePosts")!.ItemsSource is IEnumerable<ArchivePostRow> pagedRows
            && pagedRows.FirstOrDefault()?.Id == "1050", "A delayed resume replaced a newer reader page.");
        Program.CaptureWindow(window, "smoke-reading-position-delayed-pagination.png");
        await page.FocusPostAsync("1022");
        await WaitForAsync(async () => (await window.SessionHost.ReadReadingPositionAsync())?.SelectedPostId == "1022");
        await ReaderLiveChecks.RunAsync(window);
        await ReadingPositionAnchorChecks.PrepareRestartAsync(window, profile);
        File.WriteAllText(Path.Combine(profile, "reading-restart-ready"), "second process must explicitly restore the recorded nonzero visible anchor");
        Console.WriteLine("SMOKE reading position: default off, passive preview, explicit local resume, reader movement, forget/refresh and missing-post fallback passed.");
    }

    private static async Task CheckEmptyFooterResumeAsync(MainWindow window)
    {
        string languageBefore = window.SessionHost.Config.Language;
        try
        {
            foreach (string language in new[] { "en", "de" })
            {
                await FooterNavigationChecks.PersistLanguageAsync(window, language);
                FooterNavigationChecks.RequireLanguage(window, language);
                FooterNavigationChecks.RequireResumeAction(window);
                Click(window.FindControl<Button>("ContinueReadingNavigationButton")!);
                string expectedEmpty = language == "de"
                    ? "Keine Lesestelle gespeichert. In den Einstellungen einschalten und anschließend den Reader bewusst öffnen."
                    : "No reading position is saved. Enable it in Settings, then consciously open the reader.";
                await Program.WaitUntilAsync(() => window.SelectedPage == MainWindow.WorkspacePage.Archive
                    && window.ArchivePage.FindControl<TextBlock>("ArchiveMessage")!.Text == expectedEmpty);
                FooterNavigationChecks.RequireLanguage(window, language);
                Require(!window.FindControl<ContentControl>("ReaderHost")!.IsVisible,
                    "Continue reading without a saved position opened the reader.");
                Require(await window.SessionHost.ReadReadingPositionAsync() == null,
                    "Continue reading without a saved position created one.");
            }
        }
        finally { await FooterNavigationChecks.PersistLanguageAsync(window, languageBefore); }
    }

    private static async Task WaitForFooterResumeAsync(MainWindow window, string selectedPost)
    {
        ArchivePage page = window.ArchivePage;
        FieldInfo resuming = typeof(ArchivePage).GetField("_resumingPosition", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // The reader becomes visible before anchor restoration and its final
        // fallback message complete. Synchronize with the existing operation
        // lifetime before asserting its observable result or navigating away.
        await Program.WaitUntilAsync(() => window.FindControl<ContentControl>("ReaderHost")!.IsVisible
            && SelectedPost(page) == selectedPost && resuming.GetValue(page) is false);
    }

    private static async Task CheckSettingsSemanticsAsync(MainWindow window)
    {
        Click(window.FindControl<Button>("SettingsNavigationButton")!);
        SettingsEditor editor = window.SettingsEditor;
        await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible);
        string initialLanguage = Texts.Language;
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            window.UpdateLayout();
            foreach ((string control, string label) in new[]
            {
                ("WorkspaceModeInput", "WorkspaceModeFieldLabel"), ("LanguageInput", "LanguageFieldLabel"),
                ("ThemeInput", "ThemeFieldLabel"), ("CompactDensityInput", "DensityFieldLabel"),
                ("ReadingFontSizeInput", "ReadingFontSizeFieldLabel"), ("VideoVolumeInput", "VideoVolumeFieldLabel"),
                ("DownloadPathInput", "DownloadPathFieldLabel"), ("RecheckTimerInput", "RecheckFieldLabel"),
                ("BandwidthInput", "BandwidthFieldLabel"), ("ParallelDownloadsInput", "ParallelFieldLabel")
            })
            {
                Control input = editor.FindControl<Control>(control)!;
                TextBlock caption = editor.FindControl<TextBlock>(label)!;
                Require(ReferenceEquals(AutomationProperties.GetLabeledBy(input), caption)
                    && AutomationProperties.GetName(input) == caption.Text, language + " field has no semantic localized label: " + control);
            }
            foreach (string name in new[] { "RecheckTimerInput", "BandwidthInput", "VideoVolumeInput" })
                Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(editor.FindControl<Control>(name)!)), "Missing unit help: " + name);
            Require(AutomationProperties.GetName(editor.FindControl<Button>("CancelSettingsButton")!) == Texts.T("rules.discard"), "Embedded Settings still says Cancel.");
            Program.CaptureWindow(window, "smoke-settings-labels-" + language + ".png");
        }
        Texts.SetLanguage(initialLanguage);
        bool initial = editor.RememberReadingPosition;
        editor.RememberReadingPosition = !initial;
        Click(editor.FindControl<Button>("CancelSettingsButton")!);
        Require(editor.RememberReadingPosition == initial, "Discard did not revert the reading preference draft.");
        SettingsWindow separate = new(window.SessionHost.Config);
        Require(AutomationProperties.GetName(separate.Editor.FindControl<Button>("CancelSettingsButton")!) == Texts.T("settings.cancel"), "Separate Settings window lost close semantics.");
        separate.Editor.EndLifetime();
        Console.WriteLine("SMOKE Settings V04/V32: localized label relations and units in EN/DE; embedded discard vs separate Cancel passed (native speech not measured).");
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        for (int attempt = 0; attempt < 100; ++attempt)
        {
            if (await condition()) return;
            await Task.Delay(30);
        }
        throw new TimeoutException("Reading position was not persisted.");
    }

    private static async Task CheckWheelCaptureBatchAsync(MainWindow window, ArchivePage page, int expectedCaptures)
    {
        // Settle opening/focus work before counting only the wheel input below.
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        ListBox posts = page.FindControl<ListBox>("ArchivePosts")!;
        ScrollViewer scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().First();
        ScrollContentPresenter viewport = scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().First();
        double before = scroll.Offset.Y;
        int captures = 0;
        void Captured(LocalReadingPosition _) => ++captures;
        page.ConsciousReadingPositionChanged += Captured;
        try
        {
            using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
            Point point = viewport.TranslatePoint(new Point(20, 20), window)!.Value;
            // Both inputs run in this dispatcher turn: their final visible anchor
            // should be captured once, after normal scrolling/layout has settled.
            for (int index = 0; index < 2; ++index)
                viewport.RaiseEvent(new PointerWheelEventArgs(viewport, pointer, window, point, 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
                    KeyModifiers.None, new Vector(0, -1)));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Require(scroll.Offset.Y > before + 10, "The reading-position batch fixture did not actually scroll.");
            Require(captures == expectedCaptures,
                $"Reader wheel batch published {captures} conscious positions; expected {expectedCaptures} with opt-in {(expectedCaptures == 0 ? "off" : "on")}.");
        }
        finally { page.ConsciousReadingPositionChanged -= Captured; }
    }

    private static string? SelectedPost(ArchivePage page) => (page.FindControl<ListBox>("ArchivePosts")!.SelectedItem as ArchivePostRow)?.Id;
    private static void Click(Button control) => control.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Click(MenuItem control) => control.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
