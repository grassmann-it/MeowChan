using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Testing;
using Texts = MeowChan.Desktop.Localization.Texts;

namespace MeowChan.DesktopSmoke;

// Reader foundation (plan P0) in a private profile with a detached reader: the "View" menu, "Hide media" and "Only posts
// with media" over a 120-post thread, paging and Up/Down in the shown order, a find hit in a hidden post that reveals it,
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

    // 500..619: even posts carry an attachment (none is saved, so no thumbnail loads), 537 holds the find needle.
    private static CapturedPost[] Posts(int count) => Enumerable.Range(0, count).Select(index =>
    {
        string id = (500 + index).ToString();
        string text = index == 37 ? "A text-only post with the needle." : "Reader view post " + id + ".";
        return new CapturedPost(id, index, PostFieldPresence.All, PostedAt: DateTimeOffset.UnixEpoch.AddMinutes(index), Html: "<p>" + text + "</p>",
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
        Console.WriteLine("SMOKE reader view: View menu, hide media, only posts with media, paging, Up/Down, revealing find hit, reading mark, new posts, placeholder and indentation passed; ReaderPostLayout.MeasurePasses (b10) is not in this source and was not compared.");
    }

    private static async Task<Fixture> OpenAsync(MainWindow owner, int count)
    {
        string profile = Path.Combine(TemporaryWorkspacePaths.TempRoot, "meowchan-reader-view-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        ProfileLease lease = ProfileLease.Acquire(profile);
        ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease);
        Fixture fixture = new() { Profile = profile, Lease = lease, Catalog = catalog, Id = new("4chan", "g", ThreadId) };
        await CaptureAsync(fixture, count);
        ArchivePage page = new(token => ArchiveReader.OpenAsync(profile, token), (_, _) => Task.CompletedTask, _ => { });
        fixture.Page = page;
        Control pane = page.DetachReader();
        fixture.Window = new Window { Width = 1440, Height = 900, Content = pane, Title = "Private reader view" };
        fixture.Window.Show(owner);
        page.IsAvailable = true;
        await page.LoadThreadAsync(fixture.Id);
        await Program.WaitUntilAsync(() => !page.IsBusy && fixture.List.ItemsSource is ArchivePostRow[] { Length: > 0 });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        return fixture;
    }

    private static Task CaptureAsync(Fixture fixture, int count) => fixture.Catalog.CaptureAsync(new CapturedThreadSnapshot(
        fixture.Id, "https://boards.4chan.org/g/thread/" + ThreadId, "Reader view " + ThreadId,
        ThreadSourceState.Active, DateTimeOffset.UtcNow, fixture.Profile, Posts(count)));

    private static async Task ToggleAsync(Fixture fixture, string command)
    {
        Require(fixture.Page.ToggleReaderViewOption(command), "The view option " + command + " is not registered.");
        await Program.WaitUntilAsync(() => !fixture.Page.IsProjectionPending, () => "view still pending after " + command);
        await fixture.Page.WaitForReaderViewAsync();
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
    }

    private static string PageText(Fixture fixture) => fixture.Page.FindControl<TextBlock>("ArchivePostPage")!.Text ?? "";
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
        Require(page.Projection is { IsIdentity: true, Count: 120 } && fixture.Rows.Length == 50 && fixture.Rows[0].Id == "500",
            "The default view is not the chronological identity.");
        Require(PageText(fixture) == Texts.T("archive.page", 1, 50, 120), "Default page label changed: " + PageText(fixture));
        Require(page.ReaderViewOptionIds.SequenceEqual(new[] { "reader.view.hide-media", "reader.view.media-only", "watch.follow-live" }),
            "The View menu lists other options: " + string.Join(", ", page.ReaderViewOptionIds));
        Button open = page.FindControl<Button>("ReaderViewOpen")!;
        Require(open.Content as string == Texts.T("reader.view.menu"), "The View button has no label.");
        ContextMenu menu = page.BuildReaderViewMenu();
        MenuItem[] viewItems = menu.Items.OfType<MenuItem>().ToArray();
        Require(viewItems.Where(item => item.ToggleType == MenuItemToggleType.CheckBox).All(item => item.IsChecked != true)
            && viewItems.Count(item => item.ToggleType == MenuItemToggleType.CheckBox) == page.ReaderViewOptionIds.Count,
            "View options are not unchecked check items by default.");
        // R2: the media rules moved to the Thread menu; View holds only the reader's own look (options as check items, no separator yet).
        Require(viewItems.All(item => !Equals(item.Tag, "rules.edit-scope")) && !menu.Items.OfType<Separator>().Any(),
            "The View menu still carries the media rules or a stray separator.");
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
        Require(page.BuildReaderViewMenu().Items.OfType<MenuItem>().First().IsChecked == true, "The menu did not check Hide media.");
        await CaptureThemesAsync(fixture, "media-hidden");
        await ToggleAsync(fixture, "reader.view.hide-media");
        Require(fixture.Rows.Where(row => row.HasAttachments).All(row => row.ShowsMedia), "Hide media did not switch off.");

        // Only posts with media: 60 posts, two pages, the status names the hidden ones.
        await ToggleAsync(fixture, "reader.view.media-only");
        Require(page.Projection is { IsIdentity: false, Count: 60, HiddenCount: 60 }, "Only posts with media did not keep 60 posts.");
        Require(fixture.Rows.Length == 50 && fixture.Rows.All(row => row.HasAttachments), "A post without media is still shown.");
        Require(PageText(fixture) == Texts.T("archive.page", 1, 50, 60), "Page label: " + PageText(fixture));
        string status = page.FindControl<TextBlock>("ArchiveReaderStatus")!.Text ?? "";
        Require(status.EndsWith(" · " + Texts.Count("reader.view.hiddenCount", 60, 60), StringComparison.Ordinal), "Status lacks the hidden count: " + status);
        await CaptureThemesAsync(fixture, "media-only");
        fixture.Window.Width = 900;
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Program.CaptureWindow(fixture.Window, "smoke-reader-p0-view-media-only-900.png");
        fixture.Window.Width = 1440;

        // Up/Down cross the page boundary in the shown order.
        await page.FocusPostAsync("598");
        await Program.WaitUntilAsync(() => Selected(fixture) == "598");
        await page.NavigateReaderAsync(1, 0);
        await Program.WaitUntilAsync(() => Selected(fixture) == "600", () => "after Down: " + Selected(fixture));
        Require(fixture.Rows.Length == 10 && fixture.Rows[0].Id == "600" && PageText(fixture) == Texts.T("archive.page", 51, 60, 60),
            "Down did not open the second shown page: " + PageText(fixture));
        Require(!page.FindControl<Button>("NextPostsButton")!.IsEnabled && page.FindControl<Button>("PreviousPostsButton")!.IsEnabled,
            "Page buttons do not follow the shown posts.");
        await page.NavigateReaderAsync(-1, 0);
        await Program.WaitUntilAsync(() => Selected(fixture) == "598", () => "after Up: " + Selected(fixture));
        page.FindControl<Button>("NextPostsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(fixture.Rows[0].Id == "600", "Next page did not page the shown order.");

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
        page.FindControl<Button>("ReaderFindClose")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        Console.WriteLine("SMOKE reader view media: hide media, only posts with media, paging, Up/Down, find reveal, reading mark and new posts passed.");
    }

    // Placeholder and indentation come from later packages; a test classifier and a test ordering stand in for them.
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

    private sealed class Staircase : IReaderPostOrdering
    {
        public bool Active;
        public bool IsActive(ResourceId thread) => Active;
        public long Revision => 1;
        // Depth 0, 1, 2, 3, 0, 1, …: each post is the reply of the one before inside a group of four.
        public ReaderOrderEntry[] Order(ArchivedThreadView thread, ReaderQuoteGraph graph, CancellationToken token) =>
            Enumerable.Range(0, thread.PostCount).Select(index => new ReaderOrderEntry(index, index % 4, index % 4 == 0 ? -1 : index - 1)).ToArray();
    }

    private static async Task CheckPlaceholderAndIndentAsync(MainWindow owner)
    {
        await using Fixture fixture = await OpenAsync(owner, 40);
        ArchivePage page = fixture.Page;
        EveryFifthStub stubs = new();
        Staircase staircase = new();
        page.RegisterPostClassifier(stubs);
        page.RegisterPostOrdering(staircase);
        await page.WaitForReaderViewAsync();
        Require(page.Projection is { IsIdentity: true }, "Registering inactive parts changed the view.");

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

        staircase.Active = true;
        page.RequestProjectionRebuild();
        await page.WaitForReaderViewAsync();
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(page.Projection is { IsOrdered: true } && fixture.Rows.Single(row => row.Id == "503") is { Depth: 3, ParentId: "502", SuppressReplyToggle: true },
            "The test ordering did not indent the rows.");
        double baseLeft = Containers(fixture).Single(item => item.DataContext is ArchivePostRow { Id: "500" }).Margin.Left;
        double deepLeft = Containers(fixture).Single(item => item.DataContext is ArchivePostRow { Id: "503" }).Margin.Left;
        Require(Math.Abs(deepLeft - baseLeft - 3 * ArchivePage.ReaderIndentStep) < 0.5, $"Indentation {baseLeft} → {deepLeft}.");
        await CaptureThemesAsync(fixture, "indent");
        staircase.Active = false;
        page.RequestProjectionRebuild();
        await page.WaitForReaderViewAsync();
        await Dispatcher.UIThread.InvokeAsync(fixture.Window.UpdateLayout, DispatcherPriority.Background);
        Require(page.Projection is { IsIdentity: true } && Containers(fixture).All(item => Math.Abs(item.Margin.Left - baseLeft) < 0.5),
            "A recycled container kept its indentation.");
        Console.WriteLine("SMOKE reader view parts: placeholder, highlight, Show and indentation passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
