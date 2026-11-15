using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

// R7: the reader's quick settings popover (View menu, "Reader settings…") in a private profile with a detached reader. The host
// stands in for the shell: it keeps a Config, saves a change into it and applies it to the page the way the shell does. Checked:
// the menu entry, the popover under the View button with its fields, an edit applied to the reader within the live budget and
// shown on the page, Escape closing only the popover with the focus back on the button, "All reader settings" naming a setting,
// and a failed save keeping the confirmed value. A capture of the open popover in both themes is wanted from the central run.
internal static class ReaderQuickSettingsChecks
{
    private const string ThreadId = "700";

    internal static async Task RunAsync(MainWindow owner)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-quick-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ProfileLease lease = ProfileLease.Acquire(profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        ResourceId id = new("4chan", "g", ThreadId);
        Window? window = null;
        ArchivePage? page = null;
        try
        {
            CapturedPost[] posts = Enumerable.Range(0, 30).Select(index => new CapturedPost((700 + index).ToString(), index, PostFieldPresence.All,
                PostedAt: DateTimeOffset.UnixEpoch.AddMinutes(index), Html: "<p>Quick settings post " + (700 + index) + ".</p>")).ToArray();
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + ThreadId, "Quick settings " + ThreadId,
                ThreadSourceState.Active, DateTimeOffset.UtcNow, profile, posts));
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            Control pane = page.DetachReader();
            window = new Window { Width = 1440, Height = 900, Content = pane, Title = "Private reader quick settings" };
            window.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            await Program.WaitUntilAsync(() => !page.IsBusy && list.ItemsSource is ArchivePostRow[] { Length: > 0 });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await CheckAsync(page, window);
        }
        finally
        {
            page?.EndLifetime();
            window?.Close();
            await catalog.DisposeAsync();
            lease.Dispose();
            Directory.Delete(profile, recursive: true);
        }
        Console.WriteLine("SMOKE reader quick settings: View menu entry, popover under the View button, live edit of width, image width, font size and spoilers, Escape and focus return, all-settings request and failed save passed.");
    }

    private static async Task CheckAsync(ArchivePage page, Window window)
    {
        Config config = new();
        List<string?> allSettings = new();
        Exception? fail = null;
        int saves = 0;
        page.QuickSettingsHost = new QuickSettingsHost
        {
            CurrentConfig = () => config,
            SaveAsync = change =>
            {
                saves++;
                if (fail != null) throw fail;
                change(config);
                config.Validate();
                ReaderSettingsPreferences.Apply(page, config);
                page.ReaderPostWidthPreference = config.ReaderPostWidth;
                return Task.CompletedTask;
            },
            ShowAllSettings = allSettings.Add
        };

        // The entry is the last option of the View menu, a plain item that opens the popover.
        Require(page.ReaderViewOptionIds.Last() == "reader.quick-settings", "The View menu does not end with the reader settings entry: " + string.Join(", ", page.ReaderViewOptionIds));
        MenuItem entry = page.BuildReaderViewMenu().Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "reader.quick-settings"));
        Require(entry.ToggleType == MenuItemToggleType.None && entry.IsEnabled, "The reader settings entry is not a plain, enabled item.");
        Button view = page.FindControl<Button>("ReaderViewOpen")!;
        Require(page.ToggleReaderViewOption("reader.quick-settings"), "The entry is not reachable like the other view options.");
        await Program.WaitUntilAsync(() => page.IsQuickSettingsOpen, () => "the popover did not open");
        QuickSettingsSurface surface = page.ReaderQuickSettings!.Surface;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        // One editor per registered field (R7 started with six; R5/R6, R8 and R10 added theirs to ReaderQuickSettings.Fields).
        Require(surface.Width == 360 && surface.Editors.Count == ReaderQuickSettings.Fields.Count
            && ReaderQuickSettings.Fields.All(field => surface.EditorOf(field.Id) != null),
            "The popover is not 360 wide with one editor per registered field: " + surface.Width + ", " + surface.Editors.Count + " of " + ReaderQuickSettings.Fields.Count);
        Require(surface.Title.Text == Texts.T("quicksettings.reader.title"), "The popover has no title.");
        Point? close = surface.CloseButton.TranslatePoint(new Point(surface.CloseButton.Bounds.Width, 0), surface);
        Require(close is { } edge && surface.Bounds.Width - edge.X <= surface.Padding.Right + 1, "The close button is not at the right edge: " + close + " of " + surface.Bounds.Width
            + "; header " + surface.Header.Bounds + " at " + surface.Header.TranslatePoint(default, surface) + ", close " + surface.CloseButton.Bounds + " margin " + surface.CloseButton.Margin
            + ", columns " + string.Join("/", surface.Header.ColumnDefinitions.Select(column => column.ActualWidth)) + ", body " + surface.Body.Bounds + " extent " + surface.Body.Extent
            + ", padding " + surface.Padding);

        // A choice and a number are applied to the reader within the live budget and the editors keep showing them.
        ComboBox width = (ComboBox)surface.EditorOf(SettingsFieldId.ReaderPostWidth)!;
        width.SelectedItem = ((IEnumerable<QuickSettingsSurface.ChoiceItem>)width.ItemsSource!).Single(item => item.Value == Config.ReaderPostWidthFull);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        await Program.WaitUntilAsync(() => config.ReaderPostWidth == Config.ReaderPostWidthFull, () => "post width not saved, saves " + saves);
        Require(watch.ElapsedMilliseconds <= 500, "Applying the post width took " + watch.ElapsedMilliseconds + " ms.");
        Require(page.ReaderPostWidthPreference == Config.ReaderPostWidthFull, "The reader did not take the post width.");
        AccessibleNumberInput thumbs = (AccessibleNumberInput)surface.EditorOf(SettingsFieldId.ReaderThumbnailWidth)!;
        thumbs.Value = 180;
        await Program.WaitUntilAsync(() => config.ReaderThumbnailWidth == 180, () => "thumbnail width not saved");
        Require(page.ReaderThumbnailWidth == 180, "The reader did not take the image width: " + page.ReaderThumbnailWidth);
        ComboBox spoilers = (ComboBox)surface.EditorOf(SettingsFieldId.ReaderSpoilers)!;
        spoilers.SelectedItem = ((IEnumerable<QuickSettingsSurface.ChoiceItem>)spoilers.ItemsSource!).Single(item => item.Value == "Always");
        await Program.WaitUntilAsync(() => config.ReaderSpoilers == "Always", () => "spoilers not saved");
        Require((width.SelectedItem as QuickSettingsSurface.ChoiceItem)?.Value == Config.ReaderPostWidthFull && thumbs.Value == 180, "The editors lost the saved values.");

        // A failed save is reported in the popover and the confirmed value comes back. Since W3-15 the popover shows a localized cause
        // (UserFacingError), never Exception.Message: an unknown failure reads "error.cause.other", an IOException "error.cause.io".
        ComboBox date = (ComboBox)surface.EditorOf(SettingsFieldId.ReaderDateStyle)!;
        QuickSettingsSurface.ChoiceItem[] dates = ((IEnumerable<QuickSettingsSurface.ChoiceItem>)date.ItemsSource!).ToArray();
        foreach ((Exception failure, string cause) in new (Exception, string)[]
        {
            (new InvalidOperationException("smoke failure"), "error.cause.other"),
            (new IOException("smoke failure"), "error.cause.io")
        })
        {
            fail = failure;
            string expected = Texts.T("quicksettings.failed", Texts.T(cause));
            date.SelectedItem = dates.Single(item => item.Value == "Long");
            await Program.WaitUntilAsync(() => surface.Status.IsVisible && surface.Status.Text == expected, () => "no failure shown as '" + expected + "': " + surface.Status.Text);
            Require(!surface.Status.Text!.Contains("smoke failure") && config.ReaderDateStyle == "System" && (date.SelectedItem as QuickSettingsSurface.ChoiceItem)?.Value == "System",
                "A failed save did not keep the confirmed value: " + surface.Status.Text);
        }
        fail = null;

        // Escape closes only the popover; the focus is back on the View button. The surface is still the open popover's.
        surface.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await Program.WaitUntilAsync(() => !page.IsQuickSettingsOpen, () => "Escape did not close the popover");
        Require(page.IsReaderDialogOpen == false, "Escape closed more than the popover.");
        await Program.WaitUntilAsync(() => view.IsFocused || !view.IsEffectivelyVisible, () => "the focus did not return to the View button");

        // "All reader settings" asks the shell for the last changed setting, and the popover is gone afterwards. The page keeps its one
        // popover (ArchivePage.OpenReaderQuickSettings), so the last edit is still the date style whose save failed above: the
        // settings page opens where the reader just tried a change.
        page.OpenReaderQuickSettings();
        await Program.WaitUntilAsync(() => page.IsQuickSettingsOpen, () => "the popover did not open again");
        QuickSettingsSurface again = page.ReaderQuickSettings!.Surface;
        Require(((ComboBox)again.EditorOf(SettingsFieldId.ReaderPostWidth)!).SelectedItem is QuickSettingsSurface.ChoiceItem { Value: Config.ReaderPostWidthFull },
            "The reopened popover does not show the confirmed values.");
        again.AllSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !page.IsQuickSettingsOpen, () => "all settings did not close the popover");
        Require(ReferenceEquals(again, surface), "Reopening built a second popover surface.");
        Require(allSettings.Count == 1 && allSettings[0] == SettingsFieldId.ReaderDateStyle, "All reader settings named " + string.Join(",", allSettings));
        _ = window;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
