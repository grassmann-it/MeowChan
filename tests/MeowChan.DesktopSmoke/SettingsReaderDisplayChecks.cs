using System.Globalization;
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
        (nameof(SettingsEditor.ReaderPageSize), 80),
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

            Expander longPosts = editor.FindControl<Expander>("ReaderLongPostsSettings")!;
            Require(!longPosts.IsExpanded, "The long-posts expander is open by default.");
            Require(editor.FindControl<NumericUpDown>("ReaderThumbnailWidthInput")!.Value == Config.ReaderThumbnailWidthDefault,
                "The reader thumbnail draft did not start at its component default.");
            Require(!editor.FindControl<NumericUpDown>("GalleryFitMaxUpscalePercentInput")!.IsEffectivelyVisible,
                "The gallery cap is visible while unlimited fitting is selected.");

            longPosts.IsExpanded = true;
            await Program.WaitUntilAsync(() => editor.FindControl<NumericUpDown>("ReaderFoldLinesInput")!.IsEffectivelyVisible);
            window.UpdateLayout();
            foreach ((string name, int value) in ReaderValues)
                SetNumber(editor, name, value);
            Select(editor.ReaderDateStyleChoices, "SinceOp", item => editor.SelectedReaderDateStyle = item);
            Select(editor.ReaderSpoilersChoices, "Hover", item => editor.SelectedReaderSpoilers = item);

            longPosts.IsExpanded = false;
            await Program.WaitUntilAsync(() => !editor.FindControl<NumericUpDown>("ReaderFoldLinesInput")!.IsEffectivelyVisible);
            longPosts.IsExpanded = true;
            await Program.WaitUntilAsync(() => editor.FindControl<NumericUpDown>("ReaderFoldLinesInput")!.IsEffectivelyVisible);

            Select(editor.GalleryFitUpscaleChoices, "Limited", item => editor.SelectedGalleryFitUpscale = item);
            await Program.WaitUntilAsync(() => editor.FindControl<NumericUpDown>("GalleryFitMaxUpscalePercentInput")!.IsEffectivelyVisible);
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
                && saved.ReaderPageSize == 80 && saved.ReaderThreadingDepth == 9 && saved.GalleryFitMaxUpscalePercent == 350
                && saved.PostFilterRuleLimit == 333 && saved.HiddenPostLimit == 12_000
                && saved.ReaderDateStyle == "SinceOp" && saved.ReaderSpoilers == "Hover" && saved.GalleryFitUpscaleLimited,
                "The saved settings file did not contain the complete Reader P1 draft.");
            Require(host.StoreLimits.PostFilterRules == 333 && host.StoreLimits.HiddenPosts == 12_000,
                "The saved post-filter limits did not reach the live profile lease.");

            ArchivePage page = window.ArchivePage;
            Require(page.ReaderThumbnailWidth == 360 && page.ReaderDateStyle == ReaderDateStyle.SinceOp
                && page.ReaderSpoilerModePreference == ReaderSpoilerMode.Hover && page.ReaderFoldLinesPreference == 36
                && page.QuotePreviewMaxLines == 12 && page.QuotePreviewDelayMilliseconds == 450
                && page.InlineReplyPageSize == 7 && page.ReplyCardMaxLines == 6 && page.ReaderPageSize == 80
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
        finally
        {
            if (viewer != null)
            {
                Click(viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryClose"));
                await Program.WaitUntilAsync(() => host.Content == null && !host.IsVisible);
            }
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
                input.ApplyTemplate();
                input.Text = "invalid reader draft";
                group.IsExpanded = false;
                await SettingsSectionChecks.OpenAsync(editor, "settings.interface");
                Click(editor.FindControl<Button>("SaveSettingsButton")!);
                TextBlock error = editor.FindControl<TextBlock>(name + "InputError")!;
                await Program.WaitUntilAsync(() => !editor.IsBusy && error.IsEffectivelyVisible
                    && input.GetVisualDescendants().OfType<TextBox>().Any(text => text.IsFocused));
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
        && left.ReaderPageSize == right.ReaderPageSize && left.ReaderThreadingDepth == right.ReaderThreadingDepth
        && left.GalleryFitMaxUpscalePercent == right.GalleryFitMaxUpscalePercent
        && left.PostFilterRuleLimit == right.PostFilterRuleLimit && left.HiddenPostLimit == right.HiddenPostLimit
        && left.ReaderDateStyle == right.ReaderDateStyle && left.ReaderSpoilers == right.ReaderSpoilers
        && left.GalleryFitUpscaleLimited == right.GalleryFitUpscaleLimited;

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
