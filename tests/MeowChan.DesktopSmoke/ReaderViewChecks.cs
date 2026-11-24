using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

// Reader foundation (plan P0) in a private profile with a detached reader: the settings popover (formerly the "View" menu), "Hide media" and "Only posts
// with media" over a 120-post thread, continuous scrolling and Up/Down in the shown order, a find hit in a hidden post that reveals it,
// the reading mark and new posts, and a test classifier/ordering for the placeholder and the indentation.
internal static class ReaderViewChecks
{
    private const string ThreadId = "500";

    private sealed class Fixture : IAsyncDisposable
    {
        internal required string Profile { get; init; }
        internal required ProfileLease Lease { get; init; }
        internal required ArchiveCatalog Catalog { get; init; }
        internal required ResourceId Id { get; init; }
        internal bool Staircase { get; init; }
        internal int JumpPost { get; init; }
        internal bool Windowed { get; init; }
        internal ArchivePage Page { get; set; } = null!;
        internal Window Window { get; set; } = null!;
        internal ListBox List => Page.FindControl<ListBox>("ArchivePosts")!;
        internal ArchivePostRow[] Rows => List.ItemsSource?.Cast<ArchivePostRow>().ToArray() ?? [];

        public async ValueTask DisposeAsync()
        {
            Page?.EndLifetime();
            Window?.Close();
            await Catalog.DisposeAsync();
            Lease.Dispose();
            Directory.Delete(Profile, recursive: true);
        }
    }

    // 500..619: even posts carry an attachment (none is saved, so no thumbnail loads), 537 holds the find needle. With staircase quotes
    // each post quotes the one before inside a group of four, for the production conversation view; a reply to the opening post stays
    // at the root, so 501 quotes nothing and 504..507 is the first full staircase (depth 0, 1, 2, 3).
    private static CapturedPost[] Posts(int count, bool staircase, int jumpPost) => Enumerable.Range(0, count).Select(index =>
    {
        string id = (500 + index).ToString();
        if (jumpPost > 0)
        {
            string target = (499 + jumpPost).ToString();
            string body = index == 0 ? $"<a href=\"#p{target}\" class=\"quotelink\">&gt;&gt;{target}</a>" :
                index == jumpPost - 1 ? "Deep continuous-reader find target" : "Continuous reader post " + id;
            int mediaCount = index == 0 ? 1 : index == jumpPost - 1 ? 2 : 0;
            CapturedAttachment[] attachments = Enumerable.Range(0, mediaCount).Select(position =>
                new CapturedAttachment(position, $"media-{id}-{position}", OriginalFileName: $"image-{position}.png")).ToArray();
            return new CapturedPost(id, index, PostFieldPresence.All, Html: "<p>" + body + "</p>", Attachments: attachments);
        }
        string text = index == 37 ? "A text-only post with the needle." : "Reader view post " + id + ".";
        string quote = staircase && index % 4 != 0 && index != 1
            ? "<a href=\"#p" + (499 + index) + "\" class=\"quotelink\">&gt;&gt;" + (499 + index) + "</a><br>" : "";
        return new CapturedPost(id, index, PostFieldPresence.All, PostedAt: DateTimeOffset.UnixEpoch.AddMinutes(index), Html: "<p>" + quote + text + "</p>",
            Attachments: index % 2 == 0 ? new[] { new CapturedAttachment(0, "media-" + id, OriginalFileName: "picture-" + id + ".png") } : null);
    }).ToArray();

    internal static async Task RunAsync(MainWindow owner)
    {
        ThemeVariant? theme = Application.Current!.RequestedThemeVariant;
        try
        {
            await CheckMediaSwitchesAsync(owner);
            await CheckPlaceholderAndIndentAsync(owner);
        }
        finally { Application.Current!.RequestedThemeVariant = theme; }
        Console.WriteLine("SMOKE reader view: settings popover, hide media, only posts with media, continuous scrolling, Up/Down, revealing find hit, reading mark, new posts, placeholder and indentation passed; ReaderPostLayout.MeasurePasses (b10) is not in this source and was not compared.");
    }

    internal static async Task CheckContinuousAsync(MainWindow owner)
    {
        foreach ((int count, int ordinal) in new[] { (93, 80), (1500, 1400) })
        {
            await using Fixture fixture = await OpenAsync(owner, count, jumpPost: ordinal);
            ArchivePage page = fixture.Page;
            ListBox list = fixture.List;
            object? items = list.ItemsSource;
            ScrollViewer scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            string At(int number) => (499 + number).ToString();
            int[] VisiblePositions() => list.GetRealizedContainers()
                .Where(card => card.TranslatePoint(default, list) is { } top && top.Y < list.Bounds.Height
                    && top.Y + card.Bounds.Height > 0)
                .Select(list.IndexFromContainer).Where(index => index >= 0).Order().ToArray();
            bool Visible(int number) => VisiblePositions().Contains(number - 1);
            bool Landed(int number) => Selected(fixture) == At(number) && Visible(number);
            async Task WaitForPost(int number) => await Program.WaitUntilAsync(() => Landed(number),
                () => $"Post {number}/{count} was not in view (selected {Selected(fixture)}, visible {string.Join(",", VisiblePositions())}).");

            Require(list.ItemCount == count && list.ItemsPanelRoot is VirtualizingStackPanel
                && page.FindControl<Control>("ArchivePostNavigation") == null,
                $"The {count}-post thread does not have a single virtualized list.");
            await WaitForPost(1);
            int last = -1, peakRealized = 0, frames = 0;
            Stopwatch elapsed = Stopwatch.StartNew();
            for (; frames < count && last < count - 1; frames++)
            {
                int[] visible = VisiblePositions();
                Require(visible.Length > 0 && visible[0] <= last + 1, $"Scroll skipped a post after {last + 1} of {count}.");
                last = Math.Max(last, visible[^1]);
                peakRealized = Math.Max(peakRealized, list.GetRealizedContainers().Count());
                if (last == count - 1) break;
                scroll.Offset += new Vector(0, scroll.Viewport.Height * 0.75);
                await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
            }
            Console.WriteLine($"SMOKE continuous reader {count} posts, {frames} scroll steps, peak {peakRealized} realized cards, {elapsed.ElapsedMilliseconds} ms under virtual display.");
            Require(last == count - 1 && peakRealized < 50 && ReferenceEquals(items, list.ItemsSource),
                $"Scrolling {count} posts did not reach the tail in the original virtualized list.");

            await page.FocusPostAsync(At(ordinal));
            await WaitForPost(ordinal);
            if (!ReaderMediaSidebarRules.IsOn(page.ReaderMedia, true)) page.ToggleMediaSidebar();
            await Program.WaitUntilAsync(() => page.CanStepMediaSidebar(1));
            MediaPositionPill position = page.MediaSidebar!.PositionPill;
            bool ShowsPosition(int number) => position.IsEffectivelyVisible
                && Equals(ToolTip.GetTip(position), MediaPositionText.Long(new(number, 3, false)));
            await Program.WaitUntilAsync(() => ShowsPosition(2));
            page.StepMediaSidebar(1);
            await Program.WaitUntilAsync(() => ShowsPosition(3));
            page.StepMediaSidebar(-1);
            await Program.WaitUntilAsync(() => ShowsPosition(2));
            Require(Landed(ordinal), "Stepping the media navigator moved the selected post.");
            await Program.WaitUntilAsync(() => (list.SelectedItem as ArchivePostRow)?.BacklinkCount == 1);

            Button next = page.FindControl<Button>("ReaderNextPost")!;
            Button previous = page.FindControl<Button>("ReaderPreviousPost")!;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.ReaderNavigationTask;
            await WaitForPost(ordinal + 1);
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await page.ReaderNavigationTask;
            await WaitForPost(ordinal);
            Require(page.FindControl<TextBlock>("ReaderPostPosition")!.Text == ReaderNavigationLayout.CounterText(ordinal - 1, count)
                && ReferenceEquals(items, list.ItemsSource), "Post arrows used the wrong position or recreated the list.");

            await page.ResumeReadingPositionAsync(new LocalReadingPosition("4chan", "g", ThreadId, At(ordinal), -16, At(ordinal)));
            await WaitForPost(ordinal);
            Require(page.CurrentReaderAnchor is { } restored && restored.PostId == At(ordinal) && Math.Abs(restored.Top + 16) <= 2,
                "Continue reading did not restore the deep anchor and its offset.");
            Action galleryReturn = page.CaptureGalleryOrigin();
            page.SetObscuredByGallery(true);
            await page.LoadThreadAsync(fixture.Id);
            page.SetObscuredByGallery(false);
            galleryReturn();
            await WaitForPost(ordinal);
            await Program.WaitUntilAsync(() => page.CurrentReaderAnchor is { } anchor
                && anchor.PostId == At(ordinal) && Math.Abs(anchor.Top + 16) <= 2);

            TabControl tabs = page.FindControl<TabControl>("ArchiveContentTabs")!;
            tabs.SelectedIndex = 1;
            await page.NavigateReaderAsync(1, 0);
            Require(tabs.SelectedIndex == 1 && Selected(fixture) == At(ordinal + 1), "Post navigation changed the Saved media tab.");
            tabs.SelectedIndex = 0;
            await WaitForPost(ordinal + 1);

            await page.FocusPostAsync(At(1));
            await page.NavigateQuoteAsync(((ArchivePostRow)list.Items[0]!).Quotes.Single());
            await WaitForPost(ordinal);

            await page.FocusPostAsync(At(1));
            page.FindControl<Button>("ReaderFindOpen")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            page.FindControl<TextBox>("ReaderFindQuery")!.Text = "Deep continuous-reader find target";
            await Program.WaitUntilAsync(() => page.FindControl<Button>("ReaderFindNext")!.IsEnabled);
            page.FindControl<Button>("ReaderFindNext")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForPost(ordinal);
            Console.WriteLine($"SMOKE continuous reader {count} posts: jump to {ordinal}, post and media arrows, offset restore, gallery origin, Saved media, quote and Find passed.");
        }
    }

    internal static async Task CheckLongThreadAsync(MainWindow owner)
    {
        const int count = 10_000;
        int firstDisplay = 0;
        await using Fixture fixture = await OpenAsync(owner, count, windowed: true, beforeLoad: opening =>
        {
            void RecordFirstLayout(object? sender, EventArgs args)
            {
                if (opening.List.ItemsSource is not ReaderPostRows rows || !Visible(opening.List, ThreadId)) return;
                firstDisplay = rows.BuiltCount;
                opening.Window.LayoutUpdated -= RecordFirstLayout;
            }
            opening.Window.LayoutUpdated += RecordFirstLayout;
        });
        ArchivePage page = fixture.Page;
        ListBox list = fixture.List;
        ReaderPostRows source = (ReaderPostRows)list.ItemsSource!;
        await Program.WaitUntilAsync(() => firstDisplay > 0 && list.SelectedItem is ArchivePostRow { BacklinkCount: 1 },
            () => $"First visible layout built {firstDisplay} rows; selected {Selected(fixture)} with count {(list.SelectedItem as ArchivePostRow)?.BacklinkCount}.");
        Require(source.Count == count && firstDisplay is > 0 and <= 100 && source.BuiltCount <= 100,
            $"First display built {firstDisplay} rows; {source.BuiltCount} by the first reply count.");
        ArchivePostRow first = (ArchivePostRow)list.SelectedItem!;
        Require(first.Attachments.Single() is { ExpandKey: not null, FavoriteAvailable: not null }, "Opening media was not bound.");
        first.SpoilersRevealed = true;
        Console.WriteLine($"SMOKE {count} posts: {firstDisplay} rows at first display; {source.BuiltCount} after the first reply count.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-10000-first.png");
        page.ReaderExpandRepliesPreference = true;
        await Program.WaitUntilAsync(() => first is { RepliesAutoExpanded: true, RepliesLoaded: true });
        Require(first.Replies.Count == 1, "The realized post did not open its captured reply.");
        await page.SetRepliesExpandedAsync(first, false);

        const string last = "10499";
        await page.FocusPostAsync(last);
        await Program.WaitUntilAsync(() => Selected(fixture) == last && Visible(list, last)
            && list.SelectedItem is ArchivePostRow { BacklinkCount: 1 },
            () => $"Tail {last} visible: {Visible(list, last)}, selection {Selected(fixture)}, count {(list.SelectedItem as ArchivePostRow)?.BacklinkCount}.");
        Require(list.SelectedIndex == count - 1 && ReferenceEquals(source, list.ItemsSource) && source.BuiltCount <= 200
            && source.Materialized.Count() <= 100 && source.IndexOf(first) < 0,
            $"Tail jump selected index {list.SelectedIndex} and built {source.BuiltCount} rows, with {source.Materialized.Count()} resident.");
        Console.WriteLine($"SMOKE tail {last}: {source.BuiltCount} rows built, {source.Materialized.Count()} resident.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-10000-last.png");

        await page.FocusPostAsync(ThreadId);
        Require(list.SelectedItem is ArchivePostRow { SpoilersRevealed: true, RepliesExpanded: false, RepliesLoaded: true } rebuilt
            && !ReferenceEquals(first, rebuilt), "Returning to an evicted post lost its spoiler or reply state.");
        await page.LoadThreadAsync(fixture.Id, resetPage: false);
        await Program.WaitUntilAsync(() => list.SelectedItem is ArchivePostRow { Id: ThreadId, BacklinkCount: 1 });
        Require(list.SelectedItem is ArchivePostRow { RepliesExpanded: false }, "Refreshing the same thread reopened replies the reader had closed.");
        page.ReaderExpandRepliesPreference = false;

        const string anchor = "5500", selection = "5504";
        await page.ResumeReadingPositionAsync(new LocalReadingPosition("4chan", "g", ThreadId, anchor, -16, selection));
        await Program.WaitUntilAsync(AtRetainedAnchor, AnchorDetails);
        await ToggleAsync(fixture, "reader.view.media-only");
        await Program.WaitUntilAsync(() => AtRetainedAnchor() && list.SelectedItem is ArchivePostRow { BacklinkCount: not null }, AnchorDetails);
        Require(list.ItemsSource is ReaderPostRows { Count: 5_001, BuiltCount: <= 100 }, "Media-only rebuilt rows beyond the viewport.");
        Console.WriteLine($"SMOKE filter: selected {Selected(fixture)}, anchor {page.CurrentReaderAnchor}, {list.ItemCount} available posts.");
        Program.CaptureWindow(fixture.Window, "smoke-reader-10000-filter.png");
        await ToggleAsync(fixture, "reader.view.media-only");
        await Program.WaitUntilAsync(AtRetainedAnchor, AnchorDetails);

        ResourceId replacement = fixture.Id with { Thread = "10500" };
        await fixture.Catalog.CaptureAsync(new CapturedThreadSnapshot(replacement, "https://boards.4chan.org/g/thread/10500",
            "Replacement thread", ThreadSourceState.Active, DateTimeOffset.UtcNow, fixture.Profile,
            [new CapturedPost(selection, 0, PostFieldPresence.All, Html: "This post has no inbound reference.")]));
        TaskCompletionSource<CancellationToken> delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        page.BeforeInlineReplyCountsPublicationAsync = async token =>
        {
            if (delayed.TrySetResult(token)) await release.Task;
        };
        try
        {
            await page.LoadThreadAsync(fixture.Id, resetPage: false);
            CancellationToken outdated = await delayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            ArchivePostRow old = (ArchivePostRow)list.SelectedItem!;
            Require(old.Id == selection && old.BacklinkCount == null, "The retained post did not wait for its delayed reply count.");
            await page.LoadThreadAsync(replacement);
            release.TrySetResult();
            await Program.WaitUntilAsync(() => list.SelectedItem is ArchivePostRow { Id: selection, BacklinkCount: 0 });
            Require(outdated.IsCancellationRequested && old.BacklinkCount == null && !ReferenceEquals(old, list.SelectedItem)
                && list.ItemCount == 1, "A delayed reply count crossed thread generations.");
        }
        finally
        {
            page.BeforeInlineReplyCountsPublicationAsync = null;
            release.TrySetResult();
        }
        Console.WriteLine("SMOKE delayed counts: thread switch discarded the old generation.");

        bool AtRetainedAnchor() => Selected(fixture) == selection && page.CurrentReaderAnchor is { } position
            && position.PostId == anchor && Math.Abs(position.Top + 16) <= 2;
        string AnchorDetails() => $"Selected {Selected(fixture)}; anchor {page.CurrentReaderAnchor}.";

        static bool Visible(ListBox list, string id) => list.GetRealizedContainers().Any(card =>
            card.DataContext is ArchivePostRow row && row.Id == id && card.TranslatePoint(default, list) is { } top
            && top.Y < list.Bounds.Height && top.Y + card.Bounds.Height > 0);
    }

    private static async Task<Fixture> OpenAsync(MainWindow owner, int count, bool staircase = false, int jumpPost = 0,
        bool windowed = false, Action<Fixture>? beforeLoad = null)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ProfileLease lease = ProfileLease.Acquire(profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        Fixture fixture = new() { Profile = profile, Lease = lease, Catalog = catalog, Id = new("4chan", "g", ThreadId),
            Staircase = staircase, JumpPost = jumpPost, Windowed = windowed };
        await CaptureAsync(fixture, count);
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
        fixture.Page = page;
        Control pane = page.DetachReader();
        fixture.Window = new Window { Width = 1440, Height = 900, Content = pane, Title = "Private reader view" };
        fixture.Window.Show(owner);
        page.IsAvailable = true;
        beforeLoad?.Invoke(fixture);
        await page.LoadThreadAsync(fixture.Id);
        await Program.WaitUntilAsync(() => !page.IsBusy && fixture.List.ItemCount > 0);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return fixture;
    }

    private static Task CaptureAsync(Fixture fixture, int count) => fixture.Catalog.CaptureAsync(new CapturedThreadSnapshot(
        fixture.Id, "https://boards.4chan.org/g/thread/" + ThreadId, "Reader view " + ThreadId,
        ThreadSourceState.Active, DateTimeOffset.UtcNow, fixture.Profile,
        fixture.Windowed ? LongThreadPosts(count) : Posts(count, fixture.Staircase, fixture.JumpPost)));

    private static CapturedPost[] LongThreadPosts(int count) => Enumerable.Range(0, count).Select(index =>
    {
        string id = (500 + index).ToString();
        string? quote = index switch { 1 => ThreadId, 5005 => "5504", _ when index == count - 2 => (499 + count).ToString(), _ => null };
        string body = quote == null ? string.Empty : $"<a href=\"#p{quote}\" class=\"quotelink\">&gt;&gt;{quote}</a> ";
        return new CapturedPost(id, index, PostFieldPresence.All, Html: $"<p>{body}Captured post {id}.</p>",
            Attachments: index % 2 == 0 || index == count - 1 ? [new(0, "image-" + id, OriginalFileName: "example.png")] : null);
    }).ToArray();

    private static async Task ToggleAsync(Fixture fixture, string command)
    {
        Require(fixture.Page.ToggleReaderViewOption(command), "The view option " + command + " is not registered.");
        await Program.WaitUntilAsync(() => !fixture.Page.IsProjectionPending, () => "view still pending after " + command);
        await fixture.Page.WaitForReaderViewAsync();
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
    }

    private static string? Selected(Fixture fixture) => (fixture.List.SelectedItem as ArchivePostRow)?.Id;

    private static IEnumerable<ListBoxItem> Containers(Fixture fixture) => fixture.List.GetVisualDescendants().OfType<ListBoxItem>()
        .Where(item => item.DataContext is ArchivePostRow);

    private static async Task CaptureThemesAsync(Fixture fixture, string name)
    {
        foreach ((ThemeVariant variant, string suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
            Program.CaptureWindow(fixture.Window, "smoke-reader-p0-view-" + name + "-" + suffix + ".png");
        }
    }

    private static async Task CheckMediaSwitchesAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, 120);
        ArchivePage page = fixture.Page;
        // Default: today's view.
        Require(page.Projection is { IsIdentity: true, Count: 120 } && fixture.Rows.Length == 120 && fixture.Rows[0].Id == "500",
            "The default view is not the chronological identity.");
        Require(page.FindControl<Control>("ArchivePostNavigation") == null, "The reader still declares a page footer.");
        // P0's two media switches lead the menu; the conversation (R3), the media switches (R6), the media sidebar (R8), "Show new posts
        // automatically" (R2) and the reader settings entry (R7) follow. A detached reader has no post filter host, so no "Post filters…".
        Require(page.ReaderViewOptionIds.SequenceEqual(new[] { "reader.view.hide-media", "reader.view.media-only", "reader.view.conversation",
                "reader.view.textless-large", "reader.view.focused-enlarge", "reader.view.hover-enlarge", "reader.sidebar.toggle", "watch.follow-live",
                "reader.quick-settings" }),
            "The View menu lists other options: " + string.Join(", ", page.ReaderViewOptionIds));
        Button open = page.FindControl<Button>("ReaderSettingsOpen")!;
        Require(open.Content as string == Texts.T("reader.settings.button"), "The settings button has no label.");
        // RL1: the former View menu is the settings popover. Its session rows are the three view switches (the post filters action and the
        // live switch follow when the page offers them); the display options are the popover's setting rows, "Larger images for posts
        // without text" on by default (Config.ReaderTextlessLargeImages, R6), every session switch off.
        QuickSettingsSurface popover = await ReaderSettingsProbe.OpenAsync(page);
        Require(popover.SessionCommandIds.Take(3).SequenceEqual(new[] { "reader.view.media-only", "reader.view.hide-media", "reader.view.conversation" }),
            "The popover lists other session rows: " + string.Join(", ", popover.SessionCommandIds));
        Require(new[] { "reader.view.media-only", "reader.view.hide-media", "reader.view.conversation" }
                .All(command => ReaderSettingsProbe.Switch(popover, command) is { IsChecked: not true, IsEnabled: true, IsEffectivelyVisible: true }),
            "The session switches are not off and enabled by default.");
        Require(popover.EditorOf(SettingsFieldId.ReaderTextlessLargeImages) is CheckBox { IsChecked: true }
                && popover.EditorOf(SettingsFieldId.ReaderFocusedMediaEnlarge) is CheckBox { IsChecked: not true },
            "The display options are not in their default state in the popover.");
        await ReaderSettingsProbe.CloseAsync(page);
        // R2: the media rules are in the Thread menu, not among the view options.
        MenuItem[] threadItems = page.BuildReaderThreadMenu().Items.OfType<MenuItem>().ToArray();
        Require(threadItems.Select(item => item.Tag).SequenceEqual(ArchivePage.ReaderThreadMenuIds.Cast<object>())
            && threadItems.Single(item => Equals(item.Tag, "rules.edit-scope")).Header as string == Texts.T("rules.thisThread"),
            "The Thread menu does not list the thread actions with the media rules.");

        // Hide media: the slot gives way to one line and no attachment view is shown.
        await ToggleAsync(fixture, "reader.view.hide-media");
        Require(fixture.Rows.Where(row => row.HasAttachments).All(row => row.MediaHidden && !row.ShowsMedia && row.IsTextOnly),
            "Hide media left a media slot.");
        Require(page.Projection is { IsIdentity: true, Count: 120 }, "Hide media changed the shown posts.");
        Require(!Containers(fixture).SelectMany(item => item.GetVisualDescendants().OfType<ArchiveAttachmentView>()).Any(view => view.IsEffectivelyVisible),
            "A hidden attachment view is still visible.");
        string hiddenLine = Texts.Count("reader.view.mediaHidden", 1, 1);
        Require(Containers(fixture).Any(item => item.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == hiddenLine)),
            "No visible 'Media hidden' line: " + hiddenLine);
        Require(ReaderSettingsProbe.Switch(await ReaderSettingsProbe.OpenAsync(page), "reader.view.hide-media").IsChecked == true, "The popover did not check Hide media.");
        Require(page.IsReaderSettingsMarked, "The gear carries no dot while Hide media is on.");
        await ReaderSettingsProbe.CloseAsync(page);
        await CaptureThemesAsync(fixture, "media-hidden");
        await ToggleAsync(fixture, "reader.view.hide-media");
        Require(fixture.Rows.Where(row => row.HasAttachments).All(row => row.ShowsMedia), "Hide media did not switch off.");

        // Only posts with media: 60 posts, the status names the hidden ones.
        await ToggleAsync(fixture, "reader.view.media-only");
        Require(page.Projection is { IsIdentity: false, Count: 60, HiddenCount: 60 }, "Only posts with media did not keep 60 posts.");
        Require(fixture.Rows.Length == 60 && fixture.Rows.All(row => row.HasAttachments), "A post without media is still shown.");
        string status = page.FindControl<TextBlock>("ArchiveReaderStatus")!.Text ?? "";
        Require(status.EndsWith(" · " + Texts.Count("reader.view.hiddenCount", 60, 60), StringComparison.Ordinal), "Status lacks the hidden count: " + status);
        await CaptureThemesAsync(fixture, "media-only");
        fixture.Window.Width = 900;
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Program.CaptureWindow(fixture.Window, "smoke-reader-p0-view-media-only-900.png");
        fixture.Window.Width = 1440;

        // Up/Down pass the former page break while the list keeps its items and containers.
        await page.FocusPostAsync("598");
        object? rows = fixture.List.ItemsSource;
        await page.NavigateReaderAsync(1, 0);
        Require(Selected(fixture) == "600" && fixture.List.SelectedIndex == 50 && ReferenceEquals(rows, fixture.List.ItemsSource),
            "Down did not reach the next shown post in the same list.");
        await page.NavigateReaderAsync(-1, 0);
        Require(Selected(fixture) == "598" && fixture.List.SelectedIndex == 49, "Up did not return to the previous shown post.");

        // A find hit in a hidden post reveals it; Hide again hides it.
        page.FindControl<Button>("ReaderFindOpen")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.ThreadFindTask;
        Task previous = page.ThreadFindTask;
        page.FindControl<TextBox>("ReaderFindQuery")!.Text = "needle";
        await Program.WaitUntilAsync(() => !ReferenceEquals(previous, page.ThreadFindTask));
        await page.ThreadFindTask;
        page.FindControl<Button>("ReaderFindNext")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => Selected(fixture) == "537", () => "find selected " + Selected(fixture));
        ArchivePostRow revealed = fixture.Rows.Single(row => row.Id == "537");
        Require(revealed.IsRevealed && page.Projection is { Count: 61, HiddenCount: 59 }, "The find hit was not revealed.");
        page.FindControl<Button>("ReaderFindOpen")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ListBoxItem? Revealed() => Containers(fixture).SingleOrDefault(item => item.DataContext is ArchivePostRow { Id: "537" });
        await Program.WaitUntilAsync(() =>
        {
            if (Revealed() != null) return true;
            if (fixture.Rows.FirstOrDefault(row => row.Id == "537") is { } row) fixture.List.ScrollIntoView(row);
            return false;
        }, () => "rows " + string.Join(",", fixture.Rows.Select(row => row.Id).Take(5)) + "…");
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        ListBoxItem card = Revealed()!;
        Require(card.GetVisualDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text == Texts.T("reader.view.revealed")),
            "The revealed post does not say why it is shown.");
        Button hideAgain = card.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("reader-view-hide-again"));
        hideAgain.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.WaitForReaderViewAsync();
        Require(page.Projection is { Count: 60 } && page.Projection.IsHidden("537") && fixture.Rows.All(row => row.Id != "537"),
            "Hide again did not hide the post.");

        // The reading mark sits on the first shown post after the last read one; its count stays the thread's.
        await page.ResumeReadingPositionAsync(new LocalReadingPosition("4chan", "g", ThreadId, "540", 0, null));
        await Program.WaitUntilAsync(() => !page.IsBusy && page.UnreadFirstNewPostId == "542", () => "mark at " + page.UnreadFirstNewPostId);
        await page.WaitForReaderViewAsync();
        Require(fixture.Rows.Single(row => row.UnreadCount > 0) is { Id: "542", UnreadCount: 79 }, "The reading mark is not on 542 with 79 new posts.");

        // New posts: the first new shown post is focused and carries the mark.
        await CaptureAsync(fixture, 125);
        await page.CheckReaderUpdatesAsync();
        Button show = page.FindControl<Button>("ReaderShowNewPosts")!;
        await Program.WaitUntilAsync(() => show.IsVisible);
        show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => page.CurrentThread?.PostCount == 125 && page.UnreadFirstNewPostId == "620" && Selected(fixture) == "620",
            () => "new posts: count " + page.CurrentThread?.PostCount + ", mark " + page.UnreadFirstNewPostId + ", selected " + Selected(fixture));
        Require(page.Projection is { Count: 63 }, "New posts were not classified.");

        // Back to today's view.
        await ToggleAsync(fixture, "reader.view.media-only");
        Require(page.Projection is { IsIdentity: true, Count: 125 } && fixture.Rows.Any(row => !row.HasAttachments),
            "Switching off did not return to the identity.");
        Console.WriteLine("SMOKE reader view media: hide media, only posts with media, continuous scrolling, Up/Down, find reveal, reading mark and new posts passed.");
    }

    // The placeholder comes from a later package; a test classifier stands in for it.
    private sealed class EveryFifthStub : IReaderPostClassifier
    {
        public bool Active;
        public bool IsActive(ResourceId thread) => Active;
        public bool UsesQuoteGraph => false;
        public long Revision => 1;
        public void Classify(ArchivedThreadView thread, ReaderQuoteGraph? graph, ReaderPostVerdict[] verdicts, CancellationToken token)
        {
            for (int index = 1; index < verdicts.Length; index += 5) verdicts[index] = new(ReaderPostVisibility.Stub, ReaderPostMarks.None, "test rule");
            for (int index = 3; index < verdicts.Length; index += 5) verdicts[index] = new(ReaderPostVisibility.Shown, ReaderPostMarks.Highlight, "test highlight");
        }
    }

    private static async Task CheckPlaceholderAndIndentAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, 40, staircase: true);
        ArchivePage page = fixture.Page;
        EveryFifthStub stubs = new();
        // The indentation comes from the production conversation view (R3), the page's one post ordering; the placeholder still
        // from a test classifier.
        page.RegisterPostClassifier(stubs);
        await page.WaitForReaderViewAsync();
        Require(page.Projection is { IsIdentity: true }, "Registering an inactive classifier changed the view.");

        stubs.Active = true;
        page.RequestProjectionRebuild();
        await page.WaitForReaderViewAsync();
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        ArchivePostRow stub = fixture.Rows.Single(row => row.Id == "501");
        Require(stub.IsStub && stub.StubText == Texts.T("reader.view.stub", "501", "test rule") && !stub.HasAttachments,
            "The placeholder row is wrong: " + stub.StubText);
        ListBoxItem stubCard = Containers(fixture).Single(item => ReferenceEquals(item.DataContext, stub));
        ListBoxItem highlighted = Containers(fixture).Single(item => item.DataContext is ArchivePostRow { Id: "503" });
        Require(stubCard.Classes.Contains("reader-stub") && highlighted.Classes.Contains("reader-highlight"), "Placeholder or highlight class missing.");
        string status = page.FindControl<TextBlock>("ArchiveReaderStatus")!.Text ?? "";
        Require(status.EndsWith(Texts.Count("reader.view.hiddenCount", 8, 8), StringComparison.Ordinal), "Placeholders are not counted: " + status);
        await CaptureThemesAsync(fixture, "stub");
        Button showButton = stubCard.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("reader-view-show"));
        showButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.WaitForReaderViewAsync();
        Require(fixture.Rows.Single(row => row.Id == "501") is { IsStub: false, IsRevealed: true } && Selected(fixture) == "501",
            "Show did not reveal the placeholder.");
        stubs.Active = false;

        page.RequestProjectionRebuild();
        await page.WaitForReaderViewAsync();
        await ToggleAsync(fixture, "reader.view.conversation");
        Require(page.Projection is { IsOrdered: true } && fixture.Rows.Single(row => row.Id == "507") is { Depth: 3, ParentId: "506", SuppressReplyToggle: true },
            "The conversation view did not indent the rows: " + string.Join(", ", fixture.Rows.Take(8).Select(row => row.Id + "@" + row.Depth + "<" + row.ParentId)));
        double baseLeft = Containers(fixture).Single(item => item.DataContext is ArchivePostRow { Id: "500" }).Margin.Left;
        await Program.WaitUntilAsync(() =>
        {
            if (Containers(fixture).Any(item => item.DataContext is ArchivePostRow { Id: "507" })) return true;
            fixture.List.ScrollIntoView(fixture.Rows.Single(row => row.Id == "507"));
            return false;
        }, () => "507 was not realized");
        double deepLeft = Containers(fixture).Single(item => item.DataContext is ArchivePostRow { Id: "507" }).Margin.Left;
        Require(Math.Abs(deepLeft - baseLeft - 3 * ArchivePage.ReaderIndentStep) < 0.5, $"Indentation {baseLeft} → {deepLeft}.");
        await CaptureThemesAsync(fixture, "indent");
        await ToggleAsync(fixture, "reader.view.conversation");
        Require(page.Projection is { IsIdentity: true } && Containers(fixture).All(item => Math.Abs(item.Margin.Left - baseLeft) < 0.5),
            "A recycled container kept its indentation.");
        Console.WriteLine("SMOKE reader view parts: placeholder, highlight, Show and indentation passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
