using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

// The embedded reader in a private profile exercises the action group and the view controls, including the Threads switch.
// The same page is then detached to check the large reader's view controls.
//   reader-toolbar-order        the bar's order and groups at 1380 in all three work views, the lines between groups (and none for a group that moved
//                               under More), full screen last on the bar or under More, the overflow following the policy
//   reader-toolbar-read-icon    Large reader carries external-link (the catalog has no expand arrow), the gear, tags and thread their icons
//   reader-settings-sections    the popover's sections, session rows, media rows and a conditional row
//   reader-settings-session-dot a view filter of the session puts the accent dot on the gear (and its tip and accessible name), switching off removes it
//   reader-toolbar-760          Simple and Barebones overflow, and the gear opens its popover from More in Barebones
// Captures wanted from the central run (also written here): smoke-reader-toolbar-<width>-<view>-<theme>.png for 1380 and 760, the three work views, light
// and dark, plus smoke-reader-settings-popover-<theme>.png.
internal static class ReaderToolbarChecks
{
    private const string ThreadId = "7400";

    private static readonly string[] Views = ["Barebones", "Simple", "Advanced"];

    internal static async Task RunAsync(MainWindow owner)
    {
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-toolbar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ProfileLease lease = ProfileLease.Acquire(profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        ResourceId id = new("4chan", "g", ThreadId);
        Window? window = null;
        ArchivePage? page = null;
        try
        {
            CapturedPost[] posts = Enumerable.Range(0, 12).Select(index => new CapturedPost((7400 + index).ToString(), index, PostFieldPresence.All,
                PostedAt: DateTimeOffset.UnixEpoch.AddMinutes(index), Html: "<p>Reader toolbar post " + (7400 + index) + ".</p>",
                Attachments: index % 2 == 0 ? new[] { new CapturedAttachment(0, "media-" + (7400 + index), OriginalFileName: "picture-" + (7400 + index) + ".png") } : null)).ToArray();
            await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + ThreadId, "Reader toolbar " + ThreadId,
                ThreadSourceState.Active, DateTimeOffset.UtcNow, profile, posts));
            page = new ArchivePage(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
            Config config = new();
            page.QuickSettingsHost = ReaderSettingsProbe.StandIn(config);
            window = new Window { Width = 1380, Height = 820, Content = page, Title = "Private reader toolbar" };
            window.Show(owner);
            page.IsAvailable = true;
            await page.LoadThreadAsync(id);
            await Program.WaitUntilAsync(() => !page.IsBusy && page.CurrentThread != null);
            await SizeAsync(page, window, "Advanced", 1380);

            await CheckOrderAsync(page, window);
            Console.WriteLine("SMOKE reader-toolbar-order: Read, Gallery, Find | Tags, Export, Thread | gear in order and groups, lines only between bar-resident groups, full screen last on the bar or under More, overflow follows the policy in three views.");
            CheckReadIcon(page);
            Console.WriteLine("SMOKE reader-toolbar-read-icon: Large reader carries external-link; the gear, Tags and Thread carry their icons.");
            await CheckLocalizedResizeAsync(page, window);
            await CheckSectionsAsync(page, config);
            Console.WriteLine("SMOKE reader-settings-sections: sections, session rows, media rows and the conditional side row passed.");
            await CheckSessionDotAsync(page, window);
            Console.WriteLine("SMOKE reader-settings-session-dot: an active view filter marks the gear (dot, tip, accessible name), switching it off clears it.");
            await CheckNarrowAsync(page, window);
            Console.WriteLine("SMOKE reader-toolbar-760: Read and Gallery stay on the bar, and the Barebones gear opens its popover from More.");
            window.Content = page.DetachReader();
            await SizeAsync(page, window, "Advanced", 1380);
            ActionToolBar bar = Bar(page);
            Require(!Named(page, "ArchiveThreadListToggle").IsVisible && !Named(page, "ArchiveReadPosts").IsVisible,
                "The detached reader shows an embedded-only action.");
            CheckSeparators(bar, Named(page, "ArchiveOpenGallery"), () => "detached Reader at 1380");
            await Program.WaitUntilAsync(() => page.MediaSidebar is { IsEffectivelyVisible: true } sidebar
                && sidebar.HeaderRow.TranslatePoint(default, sidebar) is { Y: >= 7 and <= 9 },
                () => "The detached reader's media header is missing its top inset.");
            await CaptureThemesAsync(window, "smoke-reader-toolbar-detached-1380-advanced");
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = theme;
            page?.EndLifetime();
            window?.Close();
            await catalog.DisposeAsync();
            lease.Dispose();
            Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task SizeAsync(ArchivePage page, Window window, string view, double width)
    {
        page.ArchiveColumnModePreference = view;
        window.Width = width; window.Height = 820;
        await Program.WaitUntilAsync(() => { window.UpdateLayout(); return PresentationChecks.MatchesRequestedSize(window, width, 820); },
            () => "window " + window.Bounds.Size + " is not " + width + " x 820");
        for (int pass = 0; pass < 3; pass++) await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        Require(page.ReaderWorkView.ToString() == view, "The reader is laid out for " + page.ReaderWorkView + ", not " + view + ".");
    }

    private static ActionToolBar Bar(ArchivePage page) => page.FindControl<ActionToolBar>("ReaderToolbar")!;
    private static Button Named(ArchivePage page, string name) => page.FindControl<Button>(name)!;

    private static bool OnBar(ActionToolBar bar, Control item) =>
        bar.OverflowHost?.Children.Contains(item) != true && item.IsVisible && item.IsEffectivelyVisible && item.Bounds.Width > 0;

    private static bool UnderMore(ActionToolBar bar, Control item) => bar.OverflowHost?.Children.Contains(item) == true;

    private static async Task CheckOrderAsync(ArchivePage page, Window window)
    {
        ActionToolBar bar = Bar(page);
        string[] order = ["ArchiveReadPosts", "ArchiveOpenGallery", "ReaderFindOpen", "ReaderTagsOpen", "ExportArchiveButton", "ReaderThreadOpen", "ReaderSettingsOpen"];
        foreach (string view in Views)
        {
            await SizeAsync(page, window, view, 1380);
            string Describe() => view + " at " + window.ClientSize;
            ReaderWorkViewChecks.RequireOverflowFollowsPolicy(page, bar, Describe);
            // What is on the bar stands in the order of the spec, on one row, left to right.
            Button[] shown = order.Select(name => Named(page, name)).Where(button => OnBar(bar, button)).ToArray();
            Require(shown.Length >= 2 && shown.Zip(shown.Skip(1)).All(pair => pair.First.Bounds.Left < pair.Second.Bounds.Left && pair.First.Bounds.Top == pair.Second.Bounds.Top),
                "The bar's order is wrong in " + Describe() + ": " + string.Join(", ", shown.Select(button => button.Name + "@" + button.Bounds.Left)));
            // Review 15.11.: Barebones keeps full screen under More; elsewhere it stands on the bar while it fits and is the first to move.
            Require(Named(page, "ArchiveReaderFullscreen") is { } full && (view == "Barebones"
                    ? UnderMore(bar, full) && bar.More?.IsEffectivelyVisible == true
                    : OnBar(bar, full) || UnderMore(bar, full) && bar.More?.IsEffectivelyVisible == true),
                "Full screen is neither on the bar nor under More as the policy says in " + Describe() + ".");
            Require(OnBar(bar, Named(page, "ArchiveReadPosts")) && OnBar(bar, Named(page, "ArchiveOpenGallery")), "Read and Gallery left the bar in " + Describe() + ".");
            if (view == "Advanced")
            {
                Require(order.All(name => OnBar(bar, Named(page, name))), "The Advanced bar at 1380 has not all seven actions: "
                    + string.Join(", ", order.Where(name => !OnBar(bar, Named(page, name)))));
                Button threads = Named(page, "ArchiveThreadListToggle");
                Button media = Named(page, "ReaderMediaSidebarToggle");
                Require(OnBar(bar, threads) && OnBar(bar, Named(page, "ArchiveReaderFullscreen")) && OnBar(bar, media),
                    "The embedded reader's three view controls do not fit in " + Describe());
                Require(threads.Content is "Threads" && media.Content is "Media", "The sidebar buttons are missing their short labels.");
                Require(threads.Parent is SidebarToggleGroup group && ReferenceEquals(group, media.Parent)
                    && Named(page, "ArchiveReaderFullscreen").TranslatePoint(default, bar)!.Value.X < group.TranslatePoint(default, bar)!.Value.X,
                    "Full screen must precede the joined sidebar switches in " + Describe());
            }
            CheckSeparators(bar, Named(page, "ArchiveReadPosts"), Describe);
            await CaptureThemesAsync(window, "smoke-reader-toolbar-1380-" + view.ToLowerInvariant());
        }
        // The lines follow the groups on the bar: Barebones keeps Read and Gallery (one group), so no line stands between them.
        await SizeAsync(page, window, "Barebones", 1380);
        ActionToolBarPanel panel = PanelOf(bar, Named(page, "ArchiveReadPosts"));
        Require(Named(page, "ArchiveOpenGallery") is { } gallery && OnBar(bar, gallery)
            && !panel.GroupSeparators.Any(line => line.X > Named(page, "ArchiveReadPosts").Bounds.Left && line.X < gallery.Bounds.Right),
            "A line stands between Read and Gallery.");
        await SizeAsync(page, window, "Advanced", 1380);
    }

    private static ActionToolBarPanel PanelOf(ActionToolBar bar, Control onBar) =>
        bar.GetVisualDescendants().OfType<ActionToolBarPanel>().First(panel => panel.Children.Contains(onBar));

    // Check the docked edge and the separators between adjacent visible groups.
    private static void CheckSeparators(ActionToolBar bar, Control anchor, Func<string> describe)
    {
        ActionToolBarPanel panel = PanelOf(bar, anchor);
        Control[] shown = panel.Children.Where(child => child.IsVisible && child.Bounds.Width > 0).ToArray();
        if (ActionToolBar.GetEnd(shown[^1]))
            Require(Math.Abs(panel.Bounds.Width - shown[^1].Bounds.Right) <= 1, "The view group is off the right edge in " + describe());
        IReadOnlyList<int> starts = ActionToolBarPanel.GroupStarts(shown.Select(ActionToolBar.GetGroup).ToArray(), shown.Select(_ => true).ToArray());
        int[] separated = starts.Where(index => !ActionToolBar.GetEnd(shown[index])
            || shown[index].Bounds.Left - shown[index - 1].Bounds.Right <= ActionToolBarPanel.GroupGap + 0.5).ToArray();
        Require(panel.GroupSeparators.Count == separated.Length, "The bar draws " + panel.GroupSeparators.Count + " lines for " + separated.Length + " adjacent group changes in " + describe()
            + ": " + string.Join(", ", shown.Select(item => item.Name + "/" + ActionToolBar.GetGroup(item))));
        for (int index = 0; index < separated.Length; index++)
        {
            Rect line = panel.GroupSeparators[index];
            Control before = shown[separated[index] - 1], after = shown[separated[index]];
            Require(line.X >= before.Bounds.Right - 1 && line.Right <= after.Bounds.X + 1, "A line is not between " + before.Name + " and " + after.Name + " in " + describe() + ": " + line);
        }
    }

    private static void CheckReadIcon(ArchivePage page)
    {
        Require(Named(page, "ArchiveReadPosts").Content is StackPanel { Children: [Border { OpacityMask: ImageBrush icon }, TextBlock] }
            && ReferenceEquals(icon.Source, CommandBindings.IconMask("external-link")), "Large reader does not show its external-link icon before its text.");
        Require(IconButtons.KeyOf(Named(page, "ReaderSettingsOpen")) == "settings", "The gear does not carry the settings icon.");
        Require(IconButtons.KeyOf(Named(page, "ReaderTagsOpen")) == "tag", "Tags do not carry the tag icon: " + IconButtons.KeyOf(Named(page, "ReaderTagsOpen")));
        ActionToolBar bar = Bar(page);
        string[] twins = bar.Items.OfType<Button>().Where(button => IconButtons.KeyOf(button) == "external-link").Select(button => button.Name ?? "?").ToArray();
        Require(twins.Length == 0, "A reader bar button still carries the external-link of 'Open source in browser': " + string.Join(", ", twins));
        Require(Named(page, "ArchiveOpenGallery").Content is StackPanel { Children: [Border { OpacityMask: ImageBrush gallery }, TextBlock] }
            && !ReferenceEquals(gallery.Source, CommandBindings.IconMask("external-link")), "Read and Gallery carry the same icon.");
    }

    private static async Task CheckSectionsAsync(ArchivePage page, Config config)
    {
        QuickSettingsSurface surface = await ReaderSettingsProbe.OpenAsync(page);
        // A heading stands only over rows that are shown; the Thread section holds the live switch, which the page offers for a watched thread.
        string[] headings = surface.SectionHeadings.Where(heading => heading.IsVisible).Select(heading => heading.Text ?? "").ToArray();
        string[] expected = new[] { "view", "media", "postVideo", "sidebarVideo", "text" }
            .Concat(ReaderSettingsProbe.Row(surface, "watch.follow-live").IsEffectivelyVisible ? new[] { "thread" } : [])
            .Select(key => Texts.T("quicksettings.reader.section." + key)).ToArray();
        Require(headings.SequenceEqual(expected), "The popover's sections are " + string.Join(" | ", headings) + ", expected " + string.Join(" | ", expected));
        Require(surface.SessionCommandIds.Take(3).SequenceEqual(new[] { "reader.view.media-only", "reader.view.hide-media", "reader.view.conversation" })
            && surface.SessionCommandIds.Contains("watch.follow-live"), "The session rows are " + string.Join(", ", surface.SessionCommandIds));
        Require(surface.EditorOf(ReaderQuickSettings.MediaClickFieldId) != null, "The popover has no row for what a click on a medium does.");
        Require(surface.EditorOf(SettingsFieldId.ReaderMediaSidebar) != null && surface.EditorOf(SettingsFieldId.ReaderPostWidth) != null, "Media sidebar or post width row is missing.");
        Require(ReaderQuickSettings.Fields.Count == surface.Editors.Count, "The popover has " + surface.Editors.Count + " setting rows, the layout " + ReaderQuickSettings.Fields.Count + ".");
        // A row that depends on another switch is shown only while it is on.
        config.ReaderMediaSidebar = false; surface.SyncFromConfig();
        Require(!surface.IsRowShown(SettingsFieldId.ReaderMediaSidebarSide), "The sidebar side row is shown while the sidebar is off.");
        config.ReaderMediaSidebar = true; surface.SyncFromConfig();
        Require(surface.IsRowShown(SettingsFieldId.ReaderMediaSidebarSide), "The sidebar side row is hidden while the sidebar is on.");
        config.ReaderMediaSidebar = false; surface.SyncFromConfig();
        await CaptureThemesAsync(TopLevel.GetTopLevel(page) as Window, "smoke-reader-settings-popover");
        await ReaderSettingsProbe.CloseAsync(page);
    }

    private static async Task CheckSessionDotAsync(ArchivePage page, Window window)
    {
        Button gear = Named(page, "ReaderSettingsOpen");
        Avalonia.Controls.Shapes.Ellipse? Dot() => gear.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().FirstOrDefault(dot => dot.Name == "ReaderSettingsDot");
        Require(!page.IsReaderSettingsMarked && Dot() is not { IsVisible: true }, "The gear is marked without a view filter.");
        QuickSettingsSurface surface = await ReaderSettingsProbe.OpenAsync(page);
        CheckBox onlyMedia = ReaderSettingsProbe.Switch(surface, "reader.view.media-only");
        onlyMedia.IsChecked = true;
        await Program.WaitUntilAsync(() => page.IsReaderSettingsMarked && !page.IsProjectionPending, () => "the gear was not marked after 'Only posts with media'");
        await page.WaitForReaderViewAsync();
        await ReaderSettingsProbe.CloseAsync(page);
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        Require(Dot() is { IsVisible: true } dot && dot.Bounds.Width > 0, "The gear shows no accent dot while a view filter is on.");
        string active = Texts.T("reader.settings.active");
        Require(ToolTip.GetTip(gear) is string tip && tip.Contains(active, StringComparison.Ordinal)
            && Avalonia.Automation.AutomationProperties.GetName(gear).Contains(active, StringComparison.Ordinal),
            "The marked gear does not say why in its tip and accessible name.");
        await CaptureThemesAsync(window, "smoke-reader-settings-dot");
        // The same switch from the shortcut route clears it again, and the open popover would follow.
        Require(page.ToggleReaderViewOption("reader.view.media-only"), "The view option is not registered.");
        await Program.WaitUntilAsync(() => !page.IsReaderSettingsMarked && !page.IsProjectionPending, () => "the gear stayed marked after switching the filter off");
        await page.WaitForReaderViewAsync();
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
        Require(Dot() is not { IsVisible: true } && !(ToolTip.GetTip(gear) as string ?? "").Contains(active, StringComparison.Ordinal), "The gear is still marked after the filter went off.");
    }

    private static async Task CheckNarrowAsync(ArchivePage page, Window window)
    {
        ActionToolBar bar = Bar(page);
        foreach ((string view, double width) in new[] { ("Simple", 760d), ("Barebones", 1380d), ("Barebones", 760d) })
        {
            await SizeAsync(page, window, view, width);
            string Describe() => view + " at " + width;
            ReaderWorkViewChecks.RequireOverflowFollowsPolicy(page, bar, Describe);
            Require(OnBar(bar, Named(page, "ArchiveReadPosts")) && OnBar(bar, Named(page, "ArchiveOpenGallery")), "Read and Gallery are not on the bar in " + Describe() + ".");
            Require(UnderMore(bar, Named(page, "ArchiveReaderFullscreen")), "Full screen is not under More in " + Describe() + ".");
            if (view == "Barebones")
                Require(UnderMore(bar, Named(page, "ReaderThreadOpen")) && UnderMore(bar, Named(page, "ReaderSettingsOpen")) && bar.More?.IsEffectivelyVisible == true,
                    "The gear and Thread are not under More in " + Describe() + ".");
            CheckSeparators(bar, Named(page, "ArchiveReadPosts"), Describe);
            await CaptureThemesAsync(window, "smoke-reader-toolbar-" + (int)width + "-" + view.ToLowerInvariant());
        }
        // Under More the gear opens the same popover, anchored at More.
        Button gear = Named(page, "ReaderSettingsOpen");
        bar.More!.IsChecked = true;
        await Program.WaitUntilAsync(() => { window.UpdateLayout(); return gear.IsAttachedToVisualTree() && gear.Bounds.Width > 0; }, () => "the gear is not shown in the opened More popup");
        gear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => page.IsQuickSettingsOpen, () => "the gear under More did not open the popover");
        await ReaderSettingsProbe.CloseAsync(page);
        bar.More.IsChecked = false;
        await SizeAsync(page, window, "Advanced", 1380);
    }

    private static async Task CheckLocalizedResizeAsync(ArchivePage page, Window window)
    {
        string language = Texts.Language;
        ReaderMediaPreferences mediaPreferences = page.ReaderMedia;
        ActionToolBar bar = Bar(page);
        Button media = Named(page, "ReaderMediaSidebarToggle");
        Button fullscreen = Named(page, "ArchiveReaderFullscreen");
        try
        {
            // Keep the sidebar narrow so the post column gets enough room for the translated labels on resize.
            page.ApplyReaderMediaPreferences(mediaPreferences with { SidebarWidth = Config.ReaderMediaSidebarWidthMinimum });
            Texts.SetLanguage("pl");
            await page.ReaderLocalizationTask;
            foreach (double width in new[] { 1520d, 760d, 1520d })
            {
                await SizeAsync(page, window, "Advanced", width);
                string Describe() => $"Polish at {width}";
                ReaderWorkViewChecks.RequireOverflowFollowsPolicy(page, bar, Describe);
                CheckSeparators(bar, Named(page, "ArchiveOpenGallery"), Describe);
                Require(media.Content is "Multimedia" && OnBar(bar, media), "The translated Media action is missing in " + Describe());
                Require(media.Parent is SidebarToggleGroup group && (width == 760 ? !group.ShowLabels : group.ShowLabels),
                    "The sidebar group did not switch between icons and labels in " + Describe());
                Require(width == 760 ? UnderMore(bar, fullscreen) : OnBar(bar, fullscreen), "Full screen is misplaced in " + Describe());
                if (width == 760) Program.CaptureWindow(window, "smoke-reader-toolbar-760-advanced-pl.png");
            }
        }
        finally
        {
            page.ApplyReaderMediaPreferences(mediaPreferences);
            Texts.SetLanguage(language);
            await page.ReaderLocalizationTask;
        }
    }

    private static async Task CaptureThemesAsync(Window? window, string name)
    {
        if (window == null) return;
        foreach ((ThemeVariant variant, string suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            Program.CaptureWindow(window, name + "-" + suffix + ".png");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
