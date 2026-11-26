using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class SettingsSectionChecks
{
    internal static async Task RunAsync(MainWindow owner, string profile)
    {
        Config config = Config.LoadConfig(profile);
        int commits = 0;
        var window = new SettingsWindow(config, _ => { commits++; return Task.CompletedTask; });
        Task dialog = window.ShowDialog(owner);
        try
        {
            await Wait(() => window.IsVisible);
            T Find<T>(string id) where T : Control => window.Editor.GetLogicalDescendants().OfType<T>()
                .Single(control => ControlAutomationPeer.CreatePeerForElement(control)?.GetAutomationId() == id);
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                var timer = Find<NumericUpDown>("RecheckTimerInput");
                var path = Find<TextBox>("DownloadPathInput");
                window.Editor.OpenSectionNavigation();
                Require(window.Editor.CanGoToSection("settings.downloads"), "Downloads section unavailable in active settings.");
                window.Editor.GoToSection("settings.downloads");
                await Wait(() => Find<TextBlock>("SettingsDownloadsHeading").IsFocused);
                timer.ApplyTemplate();
                TextBox numericText = timer.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_TextBox");
                numericText.Focus(); numericText.Text = "invalid section draft";
                path.Text = "relative unfinished folder ü";
                string? raw = timer.Text; decimal? value = timer.Value;
                foreach (string id in new[] { "settings.behavior", "settings.updates", "settings.worker-budgets", "settings.interface", "settings.downloads" })
                {
                    window.Editor.OpenSectionNavigation();
                    Require(window.Editor.CanGoToSection(id), "Expected section was not offered: " + id);
                    window.Editor.GoToSection(id);
                    await Task.Delay(50);
                    Require(timer.Text == raw && timer.Value == value && path.Text == "relative unfinished folder ü" && commits == 0,
                        "A section jump changed draft text/model or saved settings.");
                    Require(!Find<SectionNavigationControl>("SettingsSections").IsExpanded,
                        "Intentional navigation left contents open.");
                }
                Require(numericText.Focus(), "The numeric draft was not focusable before translation.");
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                var scroll = Find<ScrollViewer>("SettingsScrollViewer");
                var presenter = numericText.FindAncestorOfType<ScrollContentPresenter>()!;
                Point beforeOrigin = numericText.TranslatePoint(default, presenter)!.Value;
                Vector before = scroll.Offset;
                Rect visibleBefore = new Rect(beforeOrigin, numericText.Bounds.Size).Intersect(new Rect(presenter.Viewport));
                Require(visibleBefore.Height > 0, "No part of the focused draft was visible before translation.");
                Texts.SetLanguage(language == "en" ? "de" : "en");
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
                Require(numericText.IsFocused, "Passive language refresh stole numeric input focus.");
                Require(timer.Text == raw && timer.Value == value && path.Text == "relative unfinished folder ü" && commits == 0,
                    "Passive language refresh changed raw input, draft values or saved settings.");

                // 35c0d5699 keeps the previously visible part of a focused numeric draft
                // in view when translated labels reflow. Any scroll is bounded by that part.
                Rect draftPart = visibleBefore.Translate(-new Vector(beforeOrigin.X, beforeOrigin.Y))
                    .Intersect(new Rect(numericText.Bounds.Size));
                Point afterOrigin = numericText.TranslatePoint(default, presenter)!.Value;
                double maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                double baseline = Math.Clamp(before.Y, 0, maxOffset);
                double bottom = afterOrigin.Y + scroll.Offset.Y + draftPart.Bottom;
                double top = afterOrigin.Y + scroll.Offset.Y + draftPart.Top;
                double expected = Math.Clamp(baseline, bottom - presenter.Viewport.Height, top);
                expected = Math.Clamp(expected, 0, maxOffset);
                Require(draftPart.Height > 0 && Math.Abs(scroll.Offset.Y - expected) <= 1 && scroll.Offset.X == before.X,
                    $"Passive language refresh scrolled beyond the focused draft: before={before}, expected Y={expected}, actual={scroll.Offset}.");
                Console.WriteLine($"SMOKE settings language viewport {language}→{Texts.Language}: draft focus kept, before={before}, expected Y={expected}, actual={scroll.Offset}.");
            }
            Console.WriteLine("SMOKE settings sections: explicit targets, draft/raw preservation and passive language focus passed.");
        }
        finally
        {
            Texts.SetLanguage("en");
            if (window.IsVisible) window.Close();
            await dialog;
        }
    }
    // Use the public navigation contract: hidden category editors cannot be
    // focused or revealed by BringIntoView alone.
    internal static Task OpenFieldAsync(SettingsEditor editor, string field) => OpenAsync(editor, field switch
    {
        "WorkspaceModeInput" or "LanguageInput" or "ThemeInput" or "CompactDensityInput" => "settings.interface",
        "VideoVolumeInput" or "VideoStartMutedInput" or "ReadingFontSizeInput" or "RememberReadingPositionInput" or "ForgetReadingPositionButton"
            or "ReaderThumbnailWidthInput" or "ReaderDateStyleInput" or "ReaderSpoilersInput" or "ReaderFoldLinesInput"
            or "ReaderPreviewLinesInput" or "ReaderPreviewDelayMsInput" or "ReaderReplyStepInput" or "ReaderReplyLinesInput" or "ReaderExpandRepliesInput"
            or "ReaderThreadingDepthInput" or "ReadingHistoryLimitInput" or "ReadingHistoryMenuEntriesInput" or "ReaderTextlessLargeImagesInput" or "ReaderTextlessMediaWidthInput" or "ReaderFocusedMediaEnlargeInput" or "ReaderFocusedMediaScalePercentInput" or "ReaderHoverEnlargeInput" or "ReaderHoverEnlargeSizeInput" or "ReaderHoverEnlargeDelayMsInput" or "ReaderMediaSidebarInput" or "MediaSidebarInfoInput" or "MediaSidebarInfoMinimumFreeInput" or "ReaderMediaSidebarScopeInput" or "ReaderMediaSidebarSideInput" or "ReaderNavigationModeInput" or "ReaderNavigationShowPositionInput" or "ReaderMediaSidebarWidthInput" or "ReaderMediaSidebarMinimumWidthInput" or "ReaderMediaClickInput" or "ReaderInlineVideoAutoplayMutedInput" or "ReaderKeepImagesExpandedInput" or "ReaderSidebarVideoAutoplayInput" or "ReaderSidebarVideoLoopInput" or "ReaderSidebarVideoStartMutedInput" or "ReaderInlineVideoMaxPlayersInput" or "ReaderInlineVideoMaxFileMiBInput" or "ReaderMediaStripLimitInput" or "ReaderMediaGridInput" or "ReaderMediaGridTextLimitInput" or "ReaderPreviewPrefetchPostsInput" or "ReaderPreviewSizeMemoryInput" or "GalleryFitUpscaleInput" or "GalleryFitMaxUpscalePercentInput" => "settings.reading-media",
        "ReserveInput" or "ProductionWorkersInput" or "WorkerIdleMinutesInput" or "SourceHeapMiBInput" or "FilterHeapMiBInput"
            or "PrivateTagLimitInput" or "PrivateTagFileMiBInput" or "MediaIndexCacheMiBInput" or "PostFilterRuleLimitInput" or "HiddenPostLimitInput"
            or "FavoriteEntryLimitInput" or "FavoriteFileLimitMiBInput" or "PostBookmarkLimitInput" or "SavedSearchLimitInput" or "ReaderGroupLimitInput" or "ProfileRegistryLimitInput" or "SourcesWatchPreviewRowsInput" or "ExportConfirmFilesInput" or "ExportConfirmGiBInput" or "PosterCacheLimitMiBInput" or "PosterCacheFileLimitInput" or "AudioProbeCacheLimitMiBInput" or "AudioProbeCacheEntriesInput" or "ThumbnailCacheTilesInput" or "GalleryImageCacheMiBInput" or "GalleryPreloadAheadInput" or "GalleryPreloadBehindInput" or "ClipboardUrlLimitInput" or "ClipboardTextLimitKiBInput" or "ImportMaxFilesPerBatchInput" or "ImportMaxBatchGiBInput" or "ImportMaxFileMiBInput" or "ImportMaxTotalFilesInput" or "ImportCatalogLimitMiBInput" or "ImportMaxTagsPerBatchInput" or "ImportMaxBatchesInput" or "MediaIndexMaxEntriesInput" or "MediaIndexMaxThreadsInput" => "settings.worker-budgets",
        "AboutSettingsButton" => "settings.behavior",
        "DownloadPathInput" or "BrowseButton" or "RecheckTimerInput" or "BandwidthInput" or "ParallelDownloadsInput" => "settings.downloads",
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "No settings category mapping for this field.")
    });

    /// <summary>The numeric field's editing TextBox after the field was opened. The settings groups are collapsible
    /// Expanders; a field inside a collapsed group is not attached to the visual tree (so it has no TopLevel, layout or
    /// template) until its groups are expanded and laid out.</summary>
    internal static TextBox NumericEditingSurface(SettingsEditor editor, NumericUpDown input)
    {
        for (var parent = input.Parent; parent != null; parent = parent.Parent)
            if (parent is Expander group) group.IsExpanded = true;
        (TopLevel.GetTopLevel(editor) as Avalonia.Layout.Layoutable)?.UpdateLayout();
        input.ApplyTemplate();
        return input.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_TextBox");
    }

    internal static async Task OpenAsync(SettingsEditor editor, string section)
    {
        // A synthetic click can reveal the embedded editor and request navigation in
        // one dispatcher turn. Let its responsive category presentation settle first,
        // as it must before a user can activate the visible navigation control.
        await Dispatcher.UIThread.InvokeAsync(editor.UpdateLayout, DispatcherPriority.Background);
        editor.OpenSectionNavigation();
        Require(editor.CanGoToSection(section), "Settings category unavailable: " + section);
        editor.GoToSection(section);
        string heading = section switch
        {
            "settings.interface" => "SettingsInterfaceHeading",
            "settings.reading-media" => "SettingsReadingMediaHeading",
            "settings.worker-budgets" => "SettingsWorkerBudgetsPageTitle",
            "settings.behavior" => "SettingsBehaviorHeading",
            _ => "SettingsDownloadsHeading"
        };
        try
        {
            await Program.WaitUntilAsync(() => editor.FindControl<Control>(heading) is { IsEffectivelyVisible: true, IsFocused: true });
        }
        catch (TimeoutException error)
        {
            Control? target = editor.FindControl<Control>(heading);
            throw new TimeoutException($"Settings section {section}: editorVisible={editor.IsEffectivelyVisible}, "
                + $"editable={editor.IsEditable}, selected={editor.SelectedSectionId}, "
                + $"targetVisible={target?.IsEffectivelyVisible}, targetFocused={target?.IsFocused}, "
                + $"ownerActive={(TopLevel.GetTopLevel(editor) as Window)?.IsActive}, "
                + $"focus={TopLevel.GetTopLevel(editor)?.FocusManager?.GetFocusedElement()}.", error);
        }
    }

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> ready)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while (!ready()) { if (DateTime.UtcNow > until) throw new TimeoutException("Section navigation did not settle."); await Task.Delay(20); }
    }
}
