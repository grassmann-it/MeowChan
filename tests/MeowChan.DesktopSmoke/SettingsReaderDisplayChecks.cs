using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Media.Imaging;
using MeowChan.Archive;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

/// <summary>
/// Native settings journey for Reader P1. It deliberately drives the production editor,
/// save path and live host instead of checking the XAML text alone.
/// </summary>
internal static class SettingsReaderDisplayChecks
{
    private static readonly (string Name, int Value)[] ReaderValues =
    {
        (nameof(SettingsEditor.ReaderThumbnailWidth), 360),
        (nameof(SettingsEditor.ReaderFoldLines), 36),
        (nameof(SettingsEditor.ReaderPreviewLines), 12),
        (nameof(SettingsEditor.ReaderPreviewDelayMs), 450),
        (nameof(SettingsEditor.ReaderReplyStep), 7),
        (nameof(SettingsEditor.ReaderReplyLines), 6),
        (nameof(SettingsEditor.ReaderThreadingDepth), 9),
    };

    internal static async Task RunAsync(MainWindow window)
    {
        SettingsEditor editor = window.SettingsEditor;
        DesktopSessionHost host = window.SessionHost;
        Config original = host.Config.Clone();
        string language = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            Click(window.FindControl<Button>("SettingsNavigationButton")!);
            await Program.WaitUntilAsync(() => editor.IsEffectivelyVisible && editor.Bounds.Width > 0);
            await SettingsSectionChecks.OpenAsync(editor, "settings.reading-media");

            // The reader fields sit in the page's "More settings" group, folded in this work view: open it as a user would. A folded
            // expander takes its content out of the visual tree (Avalonia 12) and a detached control keeps a stale
            // IsEffectivelyVisible, so "shown" is "attached and effectively visible".
            Expander group = editor.FindControl<Expander>("SettingsReadingMediaGroup")!;
            group.IsExpanded = true;
            Expander longPosts = editor.FindControl<Expander>("ReaderLongPostsSettings")!;
            await Program.WaitUntilAsync(() => Shown(longPosts), () => "the reading and media group did not open: " + group.IsExpanded);
            Require(!longPosts.IsExpanded, "The long-posts expander is open by default.");
            Require(editor.FindControl<NumericUpDown>("ReaderThumbnailWidthInput")!.Value == Config.ReaderThumbnailWidthDefault,
                "The reader thumbnail draft did not start at its component default.");
            Require(!Shown(editor.FindControl<NumericUpDown>("GalleryFitMaxUpscalePercentInput")!),
                "The gallery cap is visible while unlimited fitting is selected.");

            longPosts.IsExpanded = true;
            await Program.WaitUntilAsync(() => Shown(editor.FindControl<NumericUpDown>("ReaderFoldLinesInput")!));
            window.UpdateLayout();
            foreach ((string name, int value) in ReaderValues)
                SetNumber(editor, name, value);
            Select(editor.ReaderDateStyleChoices, "SinceOp", item => editor.SelectedReaderDateStyle = item);
            Select(editor.ReaderSpoilersChoices, "Hover", item => editor.SelectedReaderSpoilers = item);

            // Folding takes the fields out of the visual tree; a re-attached number box shows its committed value. A user commits a
            // typed field (Enter, or leaving it for the group header) before folding; do the same.
            NumericUpDown foldLines = editor.FindControl<NumericUpDown>("ReaderFoldLinesInput")!;
            foreach (NumericUpDown typed in longPosts.GetLogicalDescendants().OfType<NumericUpDown>())
            {
                // Enter in the field commits it, as leaving it does.
                Control box = (Control?)typed.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() ?? typed;
                box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, Source = box });
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(foldLines.Value == 36 && editor.ReaderFoldLines == 36, "Committing the fold-lines field did not take its draft: " + foldLines.Value + ", text " + foldLines.Text);
            longPosts.IsExpanded = false;
            await Program.WaitUntilAsync(() => !Shown(foldLines),
                () => "long posts expanded " + longPosts.IsExpanded + ", presenter visible " + longPosts.Presenter?.IsVisible + ", error " + editor.HasError + " " + editor.ErrorMessage);
            longPosts.IsExpanded = true;
            await Program.WaitUntilAsync(() => Shown(foldLines),
                () => "after expanding again: expanded " + longPosts.IsExpanded + ", attached " + foldLines.IsAttachedToVisualTree()
                    + ", group " + editor.FindControl<Expander>("SettingsReadingMediaGroup")?.IsExpanded);
            Require(foldLines.Value == 36 && foldLines.Text == "36", "Collapsing and expanding the long-posts group lost the committed value: " + foldLines.Text);

            Select(editor.GalleryFitUpscaleChoices, "Limited", item => editor.SelectedGalleryFitUpscale = item);
            await Program.WaitUntilAsync(() => Shown(editor.FindControl<NumericUpDown>("GalleryFitMaxUpscalePercentInput")!));
            SetNumber(editor, nameof(SettingsEditor.GalleryFitMaxUpscalePercent), 350);

            await SettingsSectionChecks.OpenAsync(editor, "settings.worker-budgets");
            SetNumber(editor, nameof(SettingsEditor.PostFilterRuleLimit), 333);
            SetNumber(editor, nameof(SettingsEditor.HiddenPostLimit), 12_000);
            Click(editor.FindControl<Button>("SaveSettingsButton")!);
            await Program.WaitUntilAsync(() => !editor.IsBusy && host.Config.ReaderThumbnailWidth == 360,
                () => "reader settings save busy=" + editor.IsBusy + " error=" + editor.ErrorMessage);
            Require(!editor.HasError, "Saving Reader P1 settings reported an error: " + editor.ErrorMessage);

            Config saved = Config.LoadConfig();
            Require(saved.ReaderThumbnailWidth == 360 && saved.ReaderFoldLines == 36 && saved.ReaderPreviewLines == 12
                && saved.ReaderPreviewDelayMs == 450 && saved.ReaderReplyStep == 7 && saved.ReaderReplyLines == 6
                && saved.ReaderThreadingDepth == 9 && saved.GalleryFitMaxUpscalePercent == 350
                && saved.PostFilterRuleLimit == 333 && saved.HiddenPostLimit == 12_000
                && saved.ReaderDateStyle == "SinceOp" && saved.ReaderSpoilers == "Hover" && saved.GalleryFitUpscaleLimited,
                "The saved settings file did not contain the complete Reader P1 draft.");
            Require(host.StoreLimits.PostFilterRules == 333 && host.StoreLimits.HiddenPosts == 12_000,
                "The saved post-filter limits did not reach the live profile lease.");

            ArchivePage page = window.ArchivePage;
            Require(page.ReaderThumbnailWidth == 360 && page.ReaderDateStyle == ReaderDateStyle.SinceOp
                && page.ReaderSpoilerModePreference == ReaderSpoilerMode.Hover && page.ReaderFoldLinesPreference == 36
                && page.QuotePreviewMaxLines == 12 && page.QuotePreviewDelayMilliseconds == 450
                && page.InlineReplyPageSize == 7 && page.ReplyCardMaxLines == 6
                && page.ReaderThreadingDepth == 9,
                "The saved Reader P1 values did not reach the live reader.");
            // The production shell configures both reader and gallery from this same saved config.
            GalleryFitPreferences gallery = ReaderSettingsPreferences.Gallery(saved);
            Require(gallery.Limit && gallery.MaxUpscalePercent == 350,
                "The saved gallery fit preference did not reach its live mapping.");
            await CheckLiveGalleryAsync(window, saved);

            // A valid cap must survive becoming unavailable and an unrelated save.
            SetNumber(editor, nameof(SettingsEditor.GalleryFitMaxUpscalePercent), 725);
            Select(editor.GalleryFitUpscaleChoices, "Unlimited", item => editor.SelectedGalleryFitUpscale = item);
            await SaveAsync(editor);
            Require(!host.Config.GalleryFitUpscaleLimited && Config.LoadConfig().GalleryFitMaxUpscalePercent == 725,
                "Unlimited fitting discarded the valid hidden cap.");
            await CheckLiveGalleryAsync(window, Config.LoadConfig());
            SetNumber(editor, nameof(SettingsEditor.ReaderThumbnailWidth), 365);
            await SaveAsync(editor);
            Require(Config.LoadConfig().GalleryFitMaxUpscalePercent == 725, "An unrelated save discarded the hidden cap.");
            Select(editor.GalleryFitUpscaleChoices, "Limited", item => editor.SelectedGalleryFitUpscale = item);
            Require(editor.GalleryFitMaxUpscalePercent == 725, "Re-enabling the gallery cap did not restore the saved percentage.");

            // A passive language change must re-label the same semantic draft, not reset it.
            SetNumber(editor, nameof(SettingsEditor.ReaderThumbnailWidth), 370);
            Select(editor.ReaderDateStyleChoices, "Long", item => editor.SelectedReaderDateStyle = item);
            Texts.SetLanguage("de");
            await Task.Delay(60);
            Require(editor.FindControl<NumericUpDown>("ReaderThumbnailWidthInput")!.Text == "370"
                && editor.SelectedReaderDateStyle?.Value == "Long"
                && editor.SelectedReaderDateStyle.Label == Texts.T("settings.reader.dateStyle.long"),
                "Changing the language replaced the Reader P1 draft instead of only relabeling it.");
            editor.CancelChanges();
            Require(editor.ReaderThumbnailWidth == host.Config.ReaderThumbnailWidth && editor.SelectedReaderDateStyle?.Value == host.Config.ReaderDateStyle,
                "Discard did not restore the saved Reader P1 values.");

            await CheckReaderLiveApplyAsync(window);
            await InvalidDraftAndSaveFailureAsync(window, host.Config);

            Console.WriteLine("SMOKE settings reader-display: native-window Reader/Gallery settings controls; managed expand/collapse, validation/error reveal, save/discard/failure/retry, hidden cap, language draft retention, live reader and profile limits passed. No OS pointer/keyboard or provider acceptance.");
        }
        finally
        {
            Texts.SetLanguage(language);
            if (!editor.IsBusy)
            {
                try
                {
                    if (!SameReaderSettings(host.Config, original))
                        await host.ApplySettingsAsync(original).WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally { editor.CancelChanges(); }
            }
        }
    }

    private static readonly ResourceId ReaderFixture = new("4chan", "g", "970000");
    private static readonly ResourceId OtherFixture = new("4chan", "g", "980000");
    private static readonly DateTimeOffset FixtureDate = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    // Seed the isolated smoke profile BEFORE session startup. No provider, replacement reader or private-field injection.
    internal static async Task SeedReaderAsync(string profile)
    {
        using ProfileLease lease = ProfileLease.Acquire(profile);
        await using MeowChan.Archive.ArchiveCatalog catalog = await MeowChan.Archive.ArchiveCatalog.OpenAsync(profile, lease);
        foreach (ResourceId id in new[] { ReaderFixture, OtherFixture })
        {
            int first = int.Parse(id.Thread!, CultureInfo.InvariantCulture);
            CapturedPost[] posts = Enumerable.Range(0, 240).Select(index => new CapturedPost(
                (first + index).ToString(CultureInfo.InvariantCulture), index, PostFieldPresence.All,
                PostedAt: FixtureDate.AddMinutes(index), Author: "offline fixture",
                Html: (index == 0 ? "" : "<a class=\"quotelink\" href=\"#p" + (first + index - 1) + "\">&gt;&gt;" + (first + index - 1) + "</a>")
                    + "<p>" + string.Join(" ", Enumerable.Repeat("Local mixed-height reader text.", 2 + index % 9 * 4)) + "</p>",
                Attachments: index % 5 == 0 ? new[] { new CapturedAttachment(0, "unavailable-local-" + index,
                    OriginalFileName: "local-placeholder.png") } : null)).ToArray();
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://offline.invalid/thread/" + id.Thread,
                "Settings live apply fixture", ThreadSourceState.Archived, FixtureDate, profile, posts));
        }
    }

    private static async Task CheckReaderLiveApplyAsync(MainWindow owner)
    {
        ArchivePage page = owner.ArchivePage; // The exact consumer used by the production settings save.
        SettingsEditor editor = owner.SettingsEditor;
        Control pane = page.DetachReader();
        Window reader = new() { Width = 1100, Height = 850, Content = pane, Title = "Offline settings anchor regression" };
        TaskCompletionSource? release = null;
        Exception? failureBeforeCleanup = null;
        try
        {
            reader.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(ReaderFixture);
            await page.WaitForReaderViewAsync();
            ListBox list = page.FindControl<ListBox>("ArchivePosts")!;
            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            Button sentinel = page.FindControl<Button>("ArchiveOpenGallery")!;
            await SettingsSectionChecks.OpenAsync(editor, "settings.reading-media");
            SetNumber(editor, nameof(SettingsEditor.ReaderThumbnailWidth), 240);
            SetNumber(editor, nameof(SettingsEditor.ReaderThreadingDepth), 6);
            Select(editor.ReaderDateStyleChoices, "System", item => editor.SelectedReaderDateStyle = item);
            await SaveAsync(editor);
            await page.WaitForReaderViewAsync();
            await page.FocusPostAsync("970132");
            await SmokeDispatcher.SettleAsync();

            foreach (string style in new[] { "SinceOp", "Long" })
            {
                var before = VisibleAnchor(list);
                string? selection = (list.SelectedItem as ArchivePostRow)?.Id;
                sentinel.Focus();
                int renders = 0;
                void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
                {
                    if (e.Property.Name == "ItemsSource" && list.ItemCount > 0) renders++;
                }
                list.PropertyChanged += Changed;
                try
                {
                    Select(editor.ReaderDateStyleChoices, style, item => editor.SelectedReaderDateStyle = item);
                    await SaveAsync(editor); // Production editor -> host save -> shell -> same open reader.
                    await page.WaitForReaderViewAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    await SmokeDispatcher.SettleAsync();
                    CheckAnchor(list, before, "date-only " + style);
                    Require((list.SelectedItem as ArchivePostRow)?.Id == selection && sentinel.IsFocused && tabs.SelectedIndex == 0,
                        "Date-only apply stole focus, tab or the representable selection: selected " + (list.SelectedItem as ArchivePostRow)?.Id + " (was " + selection
                        + "), sentinel focused " + sentinel.IsFocused + " attached " + sentinel.IsAttachedToVisualTree() + ", focus "
                        + (TopLevel.GetTopLevel(sentinel)?.FocusManager?.GetFocusedElement() as Control)?.Name + ", tab " + tabs.SelectedIndex);
                    Require(renders == 1, "Date-only apply did not coalesce to one render: " + renders);
                    ArchivePostRow row = (ArchivePostRow)list.SelectedItem!;
                    ReaderDateStyle selectedStyle = Enum.Parse<ReaderDateStyle>(style);
                    Require(row.Posted == ReaderDateText.Posted(page.CurrentThread!.Posts[row.ThreadIndex].PostedAt, selectedStyle, FixtureDate)
                        && row.PostedFull == ReaderDateText.PostedFull(page.CurrentThread.Posts[row.ThreadIndex].PostedAt),
                        "Reader date or independent full-date tooltip value was lost.");
                    page.RequestQuotePreview(row.Quotes[0], sentinel, default, immediate: true, pointer: false);
                    CapturedPost quoted = page.CurrentThread.Posts.Single(post => post.PostId == row.Quotes[0].Quote.Target!.PostId);
                    Require(page.IsQuotePreviewOpen && page.FindControl<TextBlock>("QuotePreviewHeader")!.Text!
                        .EndsWith(ReaderDateText.Posted(quoted.PostedAt, selectedStyle, FixtureDate), StringComparison.Ordinal),
                        "QuotePreview ignored the chosen date style / associated OP.");
                    Require((string?)ToolTip.GetTip(page.FindControl<TextBlock>("QuotePreviewHeader")!) == ReaderDateText.PostedFull(quoted.PostedAt),
                        "Quote preview did not retain a full-date tooltip.");
                    page.CloseQuotePreview();
                    await page.SetRepliesExpandedAsync(row, true);
                    Require(row.Replies.Count > 0 && row.Replies[0].ReplyCardHeader.EndsWith(
                        ReaderDateText.Posted(page.CurrentThread.Posts.Single(post => post.PostId == row.Replies[0].Quote.Origin.PostId).PostedAt,
                            selectedStyle, FixtureDate), StringComparison.Ordinal), "Reply excerpt ignored the date style / associated OP.");
                    await page.SetRepliesExpandedAsync(row, false);
                    await SmokeDispatcher.SettleAsync();
                }
                finally { list.PropertyChanged -= Changed; }
            }

            page.ReaderView.Conversation = true;
            await page.WaitForReaderViewAsync();
            await page.FocusPostAsync("970132");
            await SmokeDispatcher.SettleAsync();
            var originalAnchor = VisibleAnchor(list);
            sentinel.Focus();
            int combinedRenders = 0;
            void CombinedChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property.Name == "ItemsSource" && list.ItemCount > 0) combinedRenders++;
            }
            list.PropertyChanged += CombinedChanged;
            try
            {
                SetNumber(editor, nameof(SettingsEditor.ReaderThumbnailWidth), 480);
                SetNumber(editor, nameof(SettingsEditor.ReaderThreadingDepth), 1);
                Select(editor.ReaderDateStyleChoices, "SinceOp", item => editor.SelectedReaderDateStyle = item);
                await SaveAsync(editor);
                await page.WaitForReaderViewAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await SmokeDispatcher.SettleAsync();
                CheckAnchor(list, originalAnchor, "combined width/depth/date");
                ArchivePostRow[] rows = list.ItemsSource!.Cast<ArchivePostRow>().ToArray();
                Require(combinedRenders == 1 && rows.Length == page.Projection!.Count && rows.All(row => row.Depth <= 1 && row.SuppressReplyToggle)
                    && rows.Any(row => row.Depth == 1) && sentinel.IsFocused,
                    "Combined apply rendered an intermediate projection, lost the depth limit or part of the shown order or stole focus.");
            }
            finally { list.PropertyChanged -= CombinedChanged; }

            // Hold the final projection's restore. Real routed wheel input must invalidate it, even without a new jump.
            async Task HoldApplyAsync(string style)
            {
                TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                page.BeforeReaderSettingsRestoreAsync = () => { entered.TrySetResult(); return release.Task; };
                Select(editor.ReaderDateStyleChoices, style, item => editor.SelectedReaderDateStyle = item);
                await SaveAsync(editor);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            await HoldApplyAsync("Long");
            ScrollViewer scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            using (Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true))
            {
                Point point = scroll.TranslatePoint(new Point(40, 40), reader)!.Value;
                scroll.RaiseEvent(new PointerWheelEventArgs(scroll, pointer, reader, point, 0,
                    new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None, new Vector(0, -4)));
            }
            await SmokeDispatcher.SettleAsync();
            var newerAnchor = VisibleAnchor(list);
            object? newerSelection = list.SelectedItem;
            release!.TrySetResult();
            await page.WaitForReaderViewAsync();
            await SmokeDispatcher.SettleAsync();
            CheckAnchor(list, newerAnchor, "new interaction cancels restore");
            Require(ReferenceEquals(newerSelection, list.SelectedItem), "Old settings restore replaced newer selection.");

            await HoldApplyAsync("SinceOp");
            await page.LoadThreadAsync(OtherFixture);
            release!.TrySetResult();
            page.BeforeReaderSettingsRestoreAsync = null;
            await page.ReaderViewTask.WaitAsync(TimeSpan.FromSeconds(15));
            Require(page.CurrentThread?.Summary.Id == OtherFixture
                && list.ItemsSource!.Cast<ArchivePostRow>().All(row => row.Id.StartsWith("98", StringComparison.Ordinal)),
                "A stale settings restore crossed the thread/content guard.");

            foreach (int tab in new[] { 1, 2 })
            {
                tabs.SelectedIndex = tab;
                sentinel.Focus();
                Select(editor.ReaderDateStyleChoices, tab == 1 ? "Long" : "SinceOp", item => editor.SelectedReaderDateStyle = item);
                await SaveAsync(editor);
                await page.WaitForReaderViewAsync();
                Require(tabs.SelectedIndex == tab && sentinel.IsFocused, "Settings activated Posts or stole focus from another reader tab.");
            }
            Console.WriteLine("SMOKE settings reader live apply: offline 240-post production save; date-only/excerpts/full tooltip, combined width/page/depth projection, wheel/thread guards and media/reference tab retention passed (managed input).");
        }
        catch (Exception error) { failureBeforeCleanup = error; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                release?.TrySetResult();
                page.BeforeReaderSettingsRestoreAsync = null;
                await page.WaitForReaderViewAsync().WaitAsync(TimeSpan.FromSeconds(15));
                page.ReaderView.Conversation = false;
                await page.WaitForReaderViewAsync();
                reader.Content = null;
                page.AttachReader(pane);
                reader.Close();
            }, error => Console.Error.WriteLine("SMOKE settings reader live apply cleanup: " + error));
        }
    }

    private static (string Id, double Top) VisibleAnchor(ListBox list) => list.GetVisualDescendants().OfType<ListBoxItem>()
        .Select(item => (Item: item, Top: item.TranslatePoint(default, list)?.Y ?? double.NaN))
        .Where(item => item.Item.DataContext is ArchivePostRow && item.Top < list.Bounds.Height && item.Top + item.Item.Bounds.Height > 0)
        .OrderBy(item => item.Top).Select(item => (((ArchivePostRow)item.Item.DataContext!).Id, item.Top)).First();

    private static void CheckAnchor(ListBox list, (string Id, double Top) before, string context)
    {
        var row = list.ItemsSource!.Cast<ArchivePostRow>().SingleOrDefault(item => item.Id == before.Id);
        Require(row != null && list.ContainerFromItem(row) is Control container
            && container.TranslatePoint(default, list) is Point position && Math.Abs(position.Y - before.Top) <= 2,
            context + ": original anchor post/top lost (2 DIP layout tolerance).");
    }

    // Open through the production shell, not ConfigureFit directly: this proves
    // that a saved setting reaches a subsequently created real gallery as well.
    private static async Task CheckLiveGalleryAsync(MainWindow window, Config config)
    {
        string directory = Path.Combine(window.SessionHost.ProfileDirectory, "reader-p1-gallery");
        Directory.CreateDirectory(directory);
        byte[] bytes;
        using (SKBitmap bitmap = new(24, 24))
        {
            bitmap.Erase(SKColors.CornflowerBlue);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
            bytes = data.ToArray();
        }
        const string fileName = "reader-p1.png";
        await File.WriteAllBytesAsync(Path.Combine(directory, fileName), bytes);
        object record = new { MediaIdentifier = "reader-p1", SourceUrl = "https://offline.invalid/reader-p1", FileName = fileName,
            Length = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), Md5 = Convert.ToBase64String(MD5.HashData(bytes)) };
        await File.WriteAllTextAsync(Path.Combine(directory, ".meowchan-manifest.json"), JsonSerializer.Serialize(new {
            Version = 1, Entries = new Dictionary<string, object> { [fileName] = record } }));
        MediaAssetSnapshot manifest = await MediaAssetReader.OpenAsync(directory);
        MediaAssetReference asset = manifest.GetPage().Single();
        GalleryEntry entry = new(new GalleryAssetKey(new ResourceId("4chan", "g", "reader-p1"), directory, asset.AssetId),
            manifest, asset, null, null, false, false);
        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        GalleryViewer? viewer = null;
        Exception? failureBeforeCleanup = null;
        try
        {
            await window.OpenMediaGalleryAsync([entry], entry.EntryKey, stillRequested: () => true);
            await Program.WaitUntilAsync(() => host.Content is GalleryViewer current
                && current.GetGalleryVisualDescendants().OfType<Image>().Any(image => image.Name == "GalleryImage" && image.Source is Bitmap));
            viewer = (GalleryViewer)host.Content!;
            Grid viewport = viewer.GetGalleryVisualDescendants().OfType<Grid>().Single(control => control.Name == "GalleryViewport");
            await Program.WaitUntilAsync(() =>
            {
                double expected = GalleryGeometry.Fit(24, 24, viewport.Bounds.Width, viewport.Bounds.Height, window.RenderScaling,
                    ReaderSettingsPreferences.Gallery(config).MaxUpscale(window.RenderScaling));
                return expected > 0 && Math.Abs(viewer.CaptureState().PhysicalZoom - expected) < 0.01;
            });
            Require(!viewer.CaptureState().ManualZoom, "A fresh gallery did not use automatic Fit.");
            Console.WriteLine("SMOKE reader P1 live shell gallery: limited=" + config.GalleryFitUpscaleLimited
                + " storedPercent=" + config.GalleryFitMaxUpscalePercent + " physicalZoom=" + viewer.CaptureState().PhysicalZoom);
        }
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                if (viewer != null)
                {
                    Click(viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose"));
                    await Program.WaitUntilAsync(() => host.Content == null && !host.IsVisible);
                }
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in SettingsReaderDisplayChecks: " + exception));
        }
    }

    private static async Task SaveAsync(SettingsEditor editor)
    {
        Click(editor.FindControl<Button>("SaveSettingsButton")!);
        await Program.WaitUntilAsync(() => !editor.IsBusy);
        Require(!editor.HasError && editor.StatusMessage == Texts.T("settings.saved"), "Save failed: " + editor.ErrorMessage);
    }

    private static async Task InvalidDraftAndSaveFailureAsync(MainWindow owner, Config saved)
    {
        bool fail = true;
        int applies = 0, savedEvents = 0;
        Config config = saved.Clone();
        Config? candidate = null;
        SettingsWindow dialog = new(config, draft =>
        {
            applies++;
            candidate = draft;
            return fail ? Task.FromException(new IOException("Reader P1 injected save failure")) : Task.CompletedTask;
        });
        dialog.Editor.Saved += (_, _) => savedEvents++;
        Task shown = dialog.ShowDialog(owner);
        SettingsEditor editor = dialog.Editor;
        try
        {
            await Program.WaitUntilAsync(() => dialog.IsVisible);
            Texts.SetLanguage("en");
            // Every active P1 field participates in raw validation, even on another
            // category or inside the closed long-posts group.
            foreach (string name in ReaderValues.Select(row => row.Name).Concat(new[]
                { nameof(SettingsEditor.GalleryFitMaxUpscalePercent), nameof(SettingsEditor.PostFilterRuleLimit), nameof(SettingsEditor.HiddenPostLimit) }))
            {
                editor.CancelChanges();
                await SettingsSectionChecks.OpenFieldAsync(editor, name + "Input");
                Expander group = editor.FindControl<Expander>("ReaderLongPostsSettings")!;
                group.IsExpanded = true;
                editor.SelectedGalleryFitUpscale = editor.GalleryFitUpscaleChoices.Single(item => item.Value == "Limited");
                dialog.UpdateLayout();
                NumericUpDown input = editor.FindControl<NumericUpDown>(name + "Input")!;
                // The user reaches the field first: the work view folds the page's "More settings" groups, and a folded group's
                // fields are out of the visual tree. Open every group around the field; the long-posts group folds again below.
                foreach (Expander around in input.GetLogicalAncestors().OfType<Expander>()) around.IsExpanded = true;
                await Program.WaitUntilAsync(() => Shown(input), () => name + " did not come into view");
                input.ApplyTemplate();
                input.Text = "invalid reader draft";
                group.IsExpanded = false;
                await SettingsSectionChecks.OpenAsync(editor, "settings.interface");
                Click(editor.FindControl<Button>("SaveSettingsButton")!);
                TextBlock error = editor.FindControl<TextBlock>(name + "InputError")!;
                await Program.WaitUntilAsync(() => !editor.IsBusy && error.IsEffectivelyVisible
                    && input.GetVisualDescendants().OfType<TextBox>().Any(text => text.IsFocused),
                    () => name + ": busy " + editor.IsBusy + ", has error " + editor.HasError + " '" + editor.ErrorMessage + "', field error visible " + error.IsEffectivelyVisible
                        + " attached " + error.IsAttachedToVisualTree() + " text '" + error.Text + "', input text '" + input.Text + "' attached " + input.IsAttachedToVisualTree()
                        + ", focused " + input.GetVisualDescendants().OfType<TextBox>().Any(text => text.IsFocused) + ", applies " + applies + ", group "
                        + editor.FindControl<Expander>("SettingsReadingMediaGroup")?.IsExpanded + ", long posts " + group.IsExpanded);
                Require(applies == 0 && savedEvents == 0 && input.Text == "invalid reader draft",
                    name + ": rejected raw draft was committed or replaced.");
                Require(input.GetVisualDescendants().OfType<TextBox>().Any(text => text.IsFocused), name + ": first invalid field was not focused.");
                Texts.SetLanguage("de");
                await Task.Delay(40);
                Require(input.Text == "invalid reader draft" && AutomationProperties.GetHelpText(input) == error.Text
                    && error.Text!.Contains((string)typeof(SettingsEditor).GetProperty(name + "Label")!.GetValue(editor)!, StringComparison.Ordinal),
                    name + ": language refresh replaced the raw draft or retained stale error/help.");
                Texts.SetLanguage("en");
            }
            editor.CancelChanges();
            await SettingsSectionChecks.OpenAsync(editor, "settings.reading-media");
            SetNumber(editor, nameof(SettingsEditor.ReaderThumbnailWidth), 380);
            editor.SelectedReaderDateStyle = editor.ReaderDateStyleChoices.Single(item => item.Value == "RelativeToNow");
            Click(editor.FindControl<Button>("SaveSettingsButton")!);
            await Program.WaitUntilAsync(() => !editor.IsBusy && editor.HasError);
            Require(applies == 1 && savedEvents == 0 && config.ReaderThumbnailWidth == saved.ReaderThumbnailWidth
                && editor.ReaderThumbnailWidth == 380 && editor.SelectedReaderDateStyle?.Value == "RelativeToNow"
                && candidate?.ReaderThumbnailWidth == 380 && editor.ErrorDetails!.Contains("Reader P1 injected save failure", StringComparison.Ordinal),
                "Failed save changed Config, discarded the draft or emitted Saved.");
            Texts.SetLanguage("de");
            await Task.Delay(40);
            Require(editor.ReaderThumbnailWidth == 380 && editor.SelectedReaderDateStyle?.Value == "RelativeToNow"
                && editor.ErrorMessage == Texts.T("settings.saveProblem"), "Language refresh discarded the failed-save draft.");
            fail = false;
            Click(editor.FindControl<Button>("SaveSettingsButton")!);
            await shown.WaitAsync(TimeSpan.FromSeconds(15));
            Require(applies == 2 && savedEvents == 1 && config.ReaderThumbnailWidth == 380 && config.ReaderDateStyle == "RelativeToNow",
                "Retry did not commit the retained draft exactly once.");
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await shown.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static bool SameReaderSettings(Config left, Config right) =>
        left.ReaderThumbnailWidth == right.ReaderThumbnailWidth && left.ReaderFoldLines == right.ReaderFoldLines
        && left.ReaderPreviewLines == right.ReaderPreviewLines && left.ReaderPreviewDelayMs == right.ReaderPreviewDelayMs
        && left.ReaderReplyStep == right.ReaderReplyStep && left.ReaderReplyLines == right.ReaderReplyLines
        && left.ReaderThreadingDepth == right.ReaderThreadingDepth
        && left.GalleryFitMaxUpscalePercent == right.GalleryFitMaxUpscalePercent
        && left.PostFilterRuleLimit == right.PostFilterRuleLimit && left.HiddenPostLimit == right.HiddenPostLimit
        && left.ReaderDateStyle == right.ReaderDateStyle && left.ReaderSpoilers == right.ReaderSpoilers
        && left.GalleryFitUpscaleLimited == right.GalleryFitUpscaleLimited;

    private static bool Shown(Control control) => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;

    private static void SetNumber(SettingsEditor editor, string name, int value)
    {
        NumericUpDown input = editor.FindControl<NumericUpDown>(name + "Input")!;
        input.Text = value.ToString(CultureInfo.InvariantCulture);
    }

    private static void Select(IReadOnlyList<SettingsChoice> choices, string value, Action<SettingsChoice> assign) =>
        assign(choices.Single(item => item.Value == value));

    private static void Click(Control control) => SmokeActionClick.Execute(control);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
