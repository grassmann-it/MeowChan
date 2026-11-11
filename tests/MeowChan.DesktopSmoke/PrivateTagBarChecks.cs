using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_PRIVATE_TAGS_ONLY. Real host, real store and actual page controls
// on a synthetic offline profile. Routed managed input is NOT OS keyboard input.
internal static class PrivateTagBarChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId Tagged = new("4chan", "g", "1000");
    private static readonly ResourceId Plain = new("4chan", "g", "2000");

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        Require(owner.IsVisible && owner.TryGetPlatformHandle()?.Handle is nint h && h != 0,
            "Private tags require a visible native application owner and real resources.");
        string language = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("private-tags-native-");
        try
        {
            new Config().SaveConfig(profile);
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                await catalog.CaptureAsync(new CapturedThreadSnapshot(Tagged,
                    "https://boards.4chan.org/g/thread/1000", "/rpt/ - offline tag fixture",
                    ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, profile,
                    [new CapturedPost("1000", 0, PostFieldPresence.All, Html: "Welcome to <b>/RPT/</b> and /sdg/ general, see /usr/bin/env"),
                     new CapturedPost("1001", 1, PostFieldPresence.All, Html: "a reply naming /ignored/")]));
                await catalog.CaptureAsync(new CapturedThreadSnapshot(Plain,
                    "https://boards.4chan.org/g/thread/2000", "Plain offline fixture",
                    ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, profile,
                    [new CapturedPost("2000", 0, PostFieldPresence.All, Html: "no markers here")]));
            }
            Texts.SetLanguage("en");
            await JourneyAsync(owner, profile);
            Texts.SetLanguage("de");
            await RestartAndDamageAsync(owner, profile);
            Texts.SetLanguage("en");
            await FilterJourneyAsync(owner);
            await ManagerJourneyAsync(owner);
        }
        finally
        {
            Dispatcher.UIThread.VerifyAccess();
            Texts.SetLanguage(language);
            // Synthetic evidence retained, no user profile or global app setup touched.
        }
    }

    private sealed class Fixture(Window window, ArchivePage page, DesktopSessionHost host) : IAsyncDisposable
    {
        internal ArchivePage Page => page;
        internal DesktopSessionHost Host => host;
        internal T C<T>(string name) where T : Control => page.FindControl<T>(name)
            ?? throw new InvalidOperationException("Missing actual control " + name);
        internal void Click(string name) => Click(C<Button>(name));
        internal void Click(Button button)
        {
            Require(button.IsEffectivelyEnabled, "Disabled fixture action " + (button.Name ?? Label(button)));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        internal static string Label(Control control) => control is Button { Content: TextBlock text } ? text.Text ?? ""
            : control is Button button ? button.Content?.ToString() ?? "" : control is TextBlock block ? block.Text ?? "" : "";
        internal string[] Chips() => C<WrapPanel>("ReaderTagChips").Children.OfType<Button>().Select(Label).ToArray();
        internal Button[] Suggestions() => C<WrapPanel>("ReaderTagSuggestions").Children.OfType<Button>().ToArray();
        internal async Task SettleAsync()
        {
            await page.PrivateTagTask.WaitAsync(Deadline);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        internal PrivateTagStoreSnapshot Stored()
        {
            PrivateTagLoadSnapshot loaded = host.GetPrivateTagSnapshot();
            Require(loaded.State == PrivateTagLoadState.Ready, "Tag index is not readable: " + loaded.Error);
            return loaded.Snapshot!;
        }
        public async ValueTask DisposeAsync()
        {
            page.EndLifetime();
            window.Close();
            await host.DisposeAsync();
        }
    }

    // The Tags column of the thread table, read from the cells the user sees (Advanced columns, Table layout).
    private static async Task ShowTableAsync(Fixture f)
    {
        f.Page.ArchiveColumnVisibilityPreference = new Dictionary<string, bool>();
        f.Page.ArchiveColumnModePreference = "Advanced";
        f.Page.ArchiveLayoutPreference = "Table";
        f.Page.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    // Visible cells only: the grid keeps recycled, hidden rows around, and a hidden column has no visible cell.
    // Visibility is judged inside the table; an open reader may cover the table without touching its cells.
    private static string[] TagCells(Fixture f, ArchiveThreadRow row) => f.C<DataGrid>("ArchiveThreadTable").GetVisualDescendants()
        .OfType<TextBlock>().Where(block => block.DataContext == row && AutomationProperties.GetAutomationId(block) == "ArchiveThreadTagCell"
            && block.IsVisible && block.GetVisualAncestors().TakeWhile(ancestor => ancestor is not DataGrid).All(ancestor => ancestor.IsVisible))
        .Select(block => block.Text ?? string.Empty).ToArray();

    private static string TagCell(Fixture f, ArchiveThreadRow row) => TagCells(f, row) is [var text] ? text
        : throw new InvalidOperationException("Expected one visible tag cell for thread " + row.Summary.Id.Thread + ", found "
            + TagCells(f, row).Length + ": " + string.Join("|", TagCells(f, row)));

    private static async Task SortedAsync(Fixture f, DataGridColumn column, System.ComponentModel.ListSortDirection direction, params string[] expected)
    {
        column.Sort(direction);
        try { await UntilAsync(() => TableOrder(f).SequenceEqual(expected)); }
        catch (TimeoutException) { throw new InvalidOperationException("Tag column " + direction + " order is wrong: " + string.Join(",", TableOrder(f))); }
    }

    private static string[] TableOrder(Fixture f) => f.C<DataGrid>("ArchiveThreadTable").CollectionView!
        .Cast<ArchiveThreadRow>().Select(row => row.Summary.Id.Thread!).ToArray();

    private static async Task<Fixture> OpenAsync(MainWindow owner, string profile, ResourceId thread)
    {
        DesktopSessionHost host = new(profile, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Deadline);
        ArchivePage page = new(host.OpenArchiveReaderAsync,
            (_, _) => throw new InvalidOperationException("No capture mutation permitted."),
            _ => throw new InvalidOperationException("No external launch permitted."));
        page.AttachPrivateTags(host);
        Window window = new() { Width = 1120, Height = 820, Content = page,
            Title = "Synthetic offline private tags", ShowInTaskbar = false };
        window.Show(owner);
        Require(window.TryGetPlatformHandle()?.Handle is nint h && h != 0, "Missing native fixture owner.");
        page.IsAvailable = true;
        await page.LoadThreadAsync(thread).WaitAsync(Deadline);
        await page.MediaLoadTask.WaitAsync(Deadline);
        Require(page.CurrentThread?.Summary.Id == thread, "Offline snapshot was not loaded through the catalog.");
        return new Fixture(window, page, host);
    }

    private static async Task JourneyAsync(MainWindow owner, string profile)
    {
        await using Fixture f = await OpenAsync(owner, profile, Tagged);
        Button open = f.C<Button>("ReaderTagsOpen");
        StackPanel bar = f.C<StackPanel>("ReaderTagBar");
        TextBox input = f.C<TextBox>("ReaderTagInput");
        Require(!bar.IsVisible && Equals(open.Content, "Tags") && open.IsEffectivelyEnabled, "Initial tag button state is wrong.");

        f.Click("ReaderTagsOpen");
        Require(bar.IsVisible && input.IsFocused, "Open did not show the bar and focus its input.");
        Require(f.Chips().Length == 0 && f.C<WrapPanel>("ReaderTagChips").Children.OfType<TextBlock>().Single().Text == Texts.T("tags.none"),
            "An untagged thread must say so.");
        // Title and first post only; duplicates collapse, paths and later posts are ignored.
        Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /rpt/", "+ /sdg/" }), "Unexpected suggestions.");
        Require(f.Stored().Tags.Count == 0 && !File.Exists(Path.Combine(profile, PrivateTagStore.FileName)),
            "Showing suggestions must not create or assign anything.");

        f.Click(f.Suggestions()[0]);
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(new[] { "/rpt/  ✕" }) && Equals(open.Content, "Tags (1)"), "Suggestion click did not tag the thread.");
        Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /sdg/" }), "An accepted suggestion must disappear.");
        PrivateTagStoreSnapshot stored = f.Stored();
        Require(stored.TagsOf(Tagged).Single().Name.Display == "/rpt/" && stored.TagsOf(Plain).Count == 0, "Store does not match the bar.");

        // Typed name with an underscore, committed with Enter from the text field.
        input.Focus();
        input.Text = "my_tag";
        var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
        input.RaiseEvent(enter);
        Require(enter.Handled, "Enter in the tag field was not handled by the bar.");
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(new[] { "/rpt/  ✕", "my_tag  ✕" }), "Typed tag missing or underscore lost: " + string.Join("|", f.Chips()));
        Require(string.IsNullOrEmpty(input.Text) && input.IsFocused && Equals(open.Content, "Tags (2)"), "Add did not clear the field and keep focus.");

        Capture(f.Page, "bar");
        // The same name in another spelling is the same tag: no duplicate, no error.
        input.Text = "MY_TAG";
        f.Click("ReaderTagAdd");
        await f.SettleAsync();
        Require(f.Chips().Length == 2 && f.Stored().Tags.Count == 2, "Case variant created a duplicate tag.");

        long before = f.Stored().Revision;
        input.Text = "bad\tname";
        f.Click("ReaderTagAdd");
        await f.SettleAsync();
        TextBlock status = f.C<TextBlock>("ReaderTagStatus");
        Require(status.IsVisible && status.Text == Texts.T("tags.error.invalidName") && input.Text == "bad\tname",
            "Invalid name must be explained and the draft kept.");
        Require(f.Stored().Revision == before && f.Chips().Length == 2, "Invalid name changed the store.");
        Require(!status.Text!.Contains("bad", StringComparison.Ordinal), "Rejected input was echoed.");

        Button rpt = f.C<WrapPanel>("ReaderTagChips").Children.OfType<Button>().First();
        f.Click(rpt);
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(new[] { "my_tag  ✕" }) && Equals(open.Content, "Tags (1)"), "Chip click did not remove the tag.");
        Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /rpt/", "+ /sdg/" }), "Removed tag must be suggested again.");
        Require(f.Stored().Tags.Count == 2, "Removing an assignment must keep the tag itself.");

        // Applying a search clears the open thread; the bar hides with it rather than act on nothing.
        Require(await f.Page.ApplySearchSpecAsync(new ArchiveSearchSpec()), "List load failed.");
        Require(f.Page.CurrentThread == null && !bar.IsVisible && !open.IsEffectivelyEnabled, "Bar outlived its thread.");
        ArchiveThreadRow[] rows = f.C<ListBox>("ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().ToArray();
        ArchiveThreadRow taggedRow = rows.Single(row => row.Summary.Id == Tagged), plainRow = rows.Single(row => row.Summary.Id == Plain);
        Require(taggedRow.HasTags && taggedRow.TagsText == "Tags: my_tag" && !plainRow.HasTags, "List rows do not show thread tags.");
        // The table column shows the same tags: Advanced shows it, Simple and Barebones leave it to the chooser.
        await ShowTableAsync(f);
        DataGrid table = f.C<DataGrid>("ArchiveThreadTable");
        DataGridColumn tagColumn = table.Columns[7];
        Require(tagColumn.IsVisible && Equals(tagColumn.Header, "Tags") && TagCell(f, taggedRow) == "my_tag" && TagCell(f, plainRow) == "",
            "The table's tag column does not show the thread tags: " + string.Join("|", TagCells(f, taggedRow)));
        f.Page.ArchiveColumnModePreference = "Simple";
        Require(!tagColumn.IsVisible, "The tag column must be chooser-only in Simple.");
        f.Page.ArchiveColumnModePreference = "Barebones";
        Require(!tagColumn.IsVisible, "The tag column must be absent in Barebones.");
        f.Page.ArchiveColumnModePreference = "Advanced";
        Require(tagColumn.IsVisible, "Advanced did not show the tag column again.");
        // List rows follow changes made in the bar of the reopened thread.
        await f.Page.LoadThreadAsync(Tagged).WaitAsync(Deadline);
        await f.Page.MediaLoadTask.WaitAsync(Deadline);
        // The bar the user left open returns with the next opened thread, without taking focus.
        Require(bar.IsVisible && !input.IsFocused && f.Chips().SequenceEqual(new[] { "my_tag  \u2715" }), "Returned bar is not current.");
        f.Click(f.Suggestions()[1]);
        await f.SettleAsync();
        Require(taggedRow.TagsText == "Tags: my_tag, /sdg/" && !plainRow.HasTags, "List row did not follow the bar: " + taggedRow.TagsText);
        // The column follows the bar in place: same row object, no list reload, selection untouched.
        Require(TagCell(f, taggedRow) == "/sdg/, my_tag" && TagCell(f, plainRow) == "", "Table cell did not follow the bar: " + TagCell(f, taggedRow));
        Require(ReferenceEquals(table.ItemsSource, f.C<ListBox>("ArchiveThreads").ItemsSource), "Adding a tag replaced the shown list.");
        Capture(f.Page, "table-column");

        // Another thread: the bar follows it and carries neither draft nor message over.
        input.Text = "draft for the old thread";
        await f.Page.LoadThreadAsync(Plain).WaitAsync(Deadline);
        await f.Page.MediaLoadTask.WaitAsync(Deadline);
        Require(bar.IsVisible && f.Chips().Length == 0 && f.Suggestions().Length == 0 && string.IsNullOrEmpty(input.Text)
            && !status.IsVisible && Equals(open.Content, "Tags"), "Bar did not follow the thread switch cleanly.");
        Require(f.Stored().TagsOf(Plain).Count == 0, "Thread switch tagged the new thread.");

        input.Focus();
        var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        input.RaiseEvent(escape);
        Require(escape.Handled && !bar.IsVisible && open.IsFocused, "Escape did not close the bar and return focus.");
        Console.WriteLine("SMOKE private-tags journey [en]: suggestions, click-to-tag, typed Enter, case variant, invalid name, remove, list rows, thread switch, Escape checked.");
    }

    private static async Task RestartAndDamageAsync(MainWindow owner, string profile)
    {
        await using (Fixture f = await OpenAsync(owner, profile, Tagged))
        {
            // New host and page in German: persisted tags, localized labels.
            Button open = f.C<Button>("ReaderTagsOpen");
            Require(Equals(open.Content, Texts.T("tags.openCount", 2)), "Tags did not survive a restart: " + open.Content);
            f.Click("ReaderTagsOpen");
            Require(f.Chips().SequenceEqual(new[] { "my_tag  ✕", "/sdg/  ✕" }), "Restart lost or reordered tags.");
            Capture(f.Page, "bar-de");
            Require(Equals(f.C<Button>("ReaderTagAdd").Content, "Tag hinzufügen") && f.C<TextBox>("ReaderTagInput").Watermark == "Tagname",
                "German labels missing.");
            Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /rpt/" }), "Restart suggestions wrong.");
        }
        string file = Path.Combine(profile, PrivateTagStore.FileName);
        byte[] valid = File.ReadAllBytes(file);
        File.WriteAllText(file, "{bad");
        await using (Fixture f = await OpenAsync(owner, profile, Tagged))
        {
            f.Click("ReaderTagsOpen");
            TextBlock status = f.C<TextBlock>("ReaderTagStatus");
            Require(status.IsVisible && status.Text == Texts.T("tags.unavailable"), "Damaged index must be reported.");
            Require(!f.C<TextBox>("ReaderTagInput").IsEffectivelyEnabled && !f.C<Button>("ReaderTagAdd").IsEffectivelyEnabled
                && f.Chips().Length == 0 && f.Suggestions().Length == 0, "Damaged index must not be editable or look empty-but-usable.");
            Require(File.ReadAllText(file) == "{bad", "Damaged index was overwritten.");
        }
        File.WriteAllBytes(file, valid);
        await using (Fixture f = await OpenAsync(owner, profile, Tagged))
        {
            // A store that stops being readable must not keep presenting its last tags as current, and must come back.
            Require(await f.Page.ApplySearchSpecAsync(new ArchiveSearchSpec()), "List load failed.");
            await ShowTableAsync(f);
            ArchiveThreadRow[] rows = f.C<ListBox>("ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().ToArray();
            ArchiveThreadRow taggedRow = rows.Single(row => row.Summary.Id == Tagged), plainRow = rows.Single(row => row.Summary.Id == Plain);
            Require(TagCell(f, taggedRow) == "/sdg/, my_tag" && TagCell(f, plainRow) == "", "Restart: table tag column is wrong: " + TagCell(f, taggedRow));
            File.WriteAllText(file, "{bad");
            Require(f.Host.GetPrivateTagSnapshot().State == PrivateTagLoadState.Unavailable, "Damaged index was not detected.");
            await f.Page.LoadThreadAsync(Tagged).WaitAsync(Deadline);
            await f.Page.MediaLoadTask.WaitAsync(Deadline);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(TagCell(f, taggedRow) == "\u2014" && TagCell(f, plainRow) == "\u2014" && !taggedRow.HasTags,
                "Unreadable tag store still shows tags or an empty cell: " + TagCell(f, taggedRow));
            Capture(f.Page, "table-column-unavailable");
            File.WriteAllBytes(file, valid);
            Require(f.Host.GetPrivateTagSnapshot().State == PrivateTagLoadState.Ready, "Restored index was not read.");
            await f.Page.LoadThreadAsync(Tagged).WaitAsync(Deadline);
            await f.Page.MediaLoadTask.WaitAsync(Deadline);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Require(TagCell(f, taggedRow) == "/sdg/, my_tag" && TagCell(f, plainRow) == "", "Tag column did not recover: " + TagCell(f, taggedRow));
        }
        File.WriteAllBytes(file, valid);
        Console.WriteLine("SMOKE private-tags restart [de]: persistence, German labels, damaged index read-only and preserved, tag column dash and recovery checked.");
    }

    private static async Task FilterJourneyAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("private-tag-filter-native-");
        new Config().SaveConfig(profile);
        ResourceId[] threads = Enumerable.Range(1, 4).Select(index => new ResourceId("4chan", "g", "300" + index)).ToArray();
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            foreach (ResourceId id in threads)
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + id.Thread,
                    "Filter fixture " + id.Thread, ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, profile,
                    [new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Html: "plain")]));
        await using Fixture f = await OpenAsync(owner, profile, threads[0]);
        TagId red = (await f.Host.DefinePrivateTagAsync("red")).Tag, blue = (await f.Host.DefinePrivateTagAsync("blue")).Tag;
        await f.Host.DefinePrivateTagAsync("unused");
        await f.Host.SetThreadTagAsync(red, threads[0], true); await f.Host.SetThreadTagAsync(red, threads[1], true);
        await f.Host.SetThreadTagAsync(blue, threads[1], true); await f.Host.SetThreadTagAsync(blue, threads[2], true);

        ArchivePage page = f.Page;
        ComboBox choice = f.C<ComboBox>("ArchiveTagChoice"), mode = f.C<ComboBox>("ArchiveTagMode");
        TextBlock error = f.C<TextBlock>("ArchiveSearchError");
        string[] Rows() => f.C<ListBox>("ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id.Thread!).Order().ToArray();
        string[] Choices() => choice.ItemsSource!.Cast<object>().Select(item => item.ToString()!).ToArray();
        string[] Draft() => f.C<WrapPanel>("ArchiveDraftTags").Children.OfType<Ursa.Controls.ClosableTag>()
            .Select(tag => ((TextBlock)tag.Content!).Text!).ToArray();
        string[] Applied() => f.C<WrapPanel>("ArchiveAppliedFilters").Children.OfType<Ursa.Controls.ClosableTag>()
            .Select(tag => ((TextBlock)tag.Content!).Text!).ToArray();
        void Choose(string label) => choice.SelectedItem = choice.ItemsSource!.Cast<object>().Single(item => item.ToString() == label);
        void RemoveDraft(string label) => f.C<WrapPanel>("ArchiveDraftTags").Children.OfType<Ursa.Controls.ClosableTag>()
            .Single(tag => ((TextBlock)tag.Content!).Text == label).Command!.Execute(null);
        async Task SearchAsync() { await page.ApplySearchAsync(); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }

        Require(await page.ApplySearchSpecAsync(new ArchiveSearchSpec(sort: ArchiveThreadSort.Title)), "Initial list failed.");
        Require(Rows().SequenceEqual(new[] { "3001", "3002", "3003", "3004" }) && Applied().Length == 0, "Unfiltered list is wrong.");
        Require(Choices().SequenceEqual(new[] { Texts.T("tags.filter.choose"), "red", "blue", "unused" }), "Tag choices are wrong: " + string.Join("|", Choices()));

        // Sorting the table by its tag column: by sorted names (elementwise), threads without tags last, header toggles direction.
        await ShowTableAsync(f);
        DataGridColumn tagColumn = f.C<DataGrid>("ArchiveThreadTable").Columns[7];
        Require(TableOrder(f).SequenceEqual(new[] { "3001", "3002", "3003", "3004" }), "Table order before sorting is wrong.");
        await SortedAsync(f, tagColumn, System.ComponentModel.ListSortDirection.Ascending, "3003", "3002", "3001", "3004");
        await SortedAsync(f, tagColumn, System.ComponentModel.ListSortDirection.Descending, "3004", "3001", "3002", "3003");
        f.C<DataGrid>("ArchiveThreadTable").CollectionView!.SortDescriptions.Clear();

        // Include one tag.
        Choose("red"); f.Click("ArchiveAddTagInclude");
        Require(Draft().SequenceEqual(new[] { "Tag: red" }) && Choices().SequenceEqual(new[] { Texts.T("tags.filter.choose"), "blue", "unused" })
            && choice.SelectedIndex == 0, "Include did not move the tag into the draft.");
        Require(Rows().Length == 4, "Editing the draft must not search by itself.");
        await SearchAsync();
        Require(Rows().SequenceEqual(new[] { "3001", "3002" }) && Applied().SequenceEqual(new[] { "Tag: red" }), "Include filter result is wrong: " + string.Join(",", Rows()));
        Require(page.AppliedSearchSpec!.SchemaVersion == 2 && page.AppliedSearchRequest!.TagMembership != null, "Applied request is not tag-bound.");

        // Two included tags: all, then at least one.
        Choose("blue"); f.Click("ArchiveAddTagInclude");
        await SearchAsync();
        Require(Rows().SequenceEqual(new[] { "3002" }), "All-of-two result is wrong: " + string.Join(",", Rows()));
        mode.SelectedIndex = 1;
        await SearchAsync();
        Require(Rows().SequenceEqual(new[] { "3001", "3002", "3003" }) && Applied().Contains(Texts.T("tags.filter.anyChip")), "Any-of-two result is wrong: " + string.Join(",", Rows()));

        // Exclude.
        RemoveDraft("Tag: blue");
        Choose("blue"); f.Click("ArchiveAddTagExclude");
        Require(Draft().SequenceEqual(new[] { "Tag: red", "Without tag: blue" }), "Exclude draft is wrong.");
        await SearchAsync();
        Require(Rows().SequenceEqual(new[] { "3001" }) && Applied().SequenceEqual(new[] { "Tag: red", "Without tag: blue" }), "Exclude result is wrong: " + string.Join(",", Rows()));

        // A chosen but not added tag is an unfinished entry: explained, nothing searched.
        Choose("unused");
        await SearchAsync();
        Require(error.IsVisible && error.Text == Texts.T("tags.filter.pending") && Rows().SequenceEqual(new[] { "3001" }), "Pending tag choice was applied or ignored silently.");
        Require(DataValidationErrors.GetHasErrors(choice), "Pending tag choice is not marked at its control.");
        choice.SelectedIndex = 0;
        Require(!DataValidationErrors.GetHasErrors(choice), "Clearing the choice did not clear its mark.");

        // Gallery, export and reader navigation resolve the very same frozen request.
        await using (ArchiveReader reader = await f.Host.OpenArchiveReaderAsync(CancellationToken.None))
        {
            ResourceId[] resolved = await ArchivePage.ResolveExportResultIdsAsync(reader, page.AppliedSearchRequest!, null, CancellationToken.None);
            Require(resolved.Select(id => id.Thread!).SequenceEqual(new[] { "3001" }), "Result consumers do not see the filtered set.");
        }

        // Removing an applied chip searches again and updates the matching draft.
        Require(await page.RemoveAppliedFilter("tagExclude", blue.Value.ToString("D")), "Chip removal failed.");
        Require(Rows().SequenceEqual(new[] { "3001", "3002" }) && Draft().SequenceEqual(new[] { "Tag: red" }), "Chip removal result or draft is wrong.");

        // A plain refresh re-reads membership: a newly tagged thread appears.
        await f.Host.SetThreadTagAsync(red, threads[3], true);
        Require(Rows().SequenceEqual(new[] { "3001", "3002" }), "Tagging must not silently reshuffle the shown list.");
        await page.ActivateAsync();
        Require(Rows().SequenceEqual(new[] { "3001", "3002", "3004" }), "Refresh did not pick up the new tag: " + string.Join(",", Rows()));

        // The filtered tag is deleted: never widen. Refresh is refused with an explanation, the list stays.
        long revision = f.Stored().Revision;
        await f.Host.DeletePrivateTagAsync(red, revision);
        await page.ActivateAsync();
        Require(error.IsVisible && error.Text == Texts.T("tags.filter.error.unknown"), "Deleted tag was not reported: " + error.Text);
        Require(Rows().SequenceEqual(new[] { "3001", "3002", "3004" }), "A deleted tag widened or emptied the list.");
        Require(Applied().SequenceEqual(new[] { "Tag: " + Texts.T("tags.filter.deleted") }), "Deleted tag chip is not marked: " + string.Join("|", Applied()));
        Require(!await page.ApplySearchSpecAsync(page.AppliedSearchSpec!), "A definition with a deleted tag must not run.");
        Require(await page.RemoveAppliedFilter("tagInclude", red.Value.ToString("D")), "Removing the deleted tag chip failed.");
        Require(Rows().Length == 4 && Applied().Length == 0 && !error.IsVisible && page.AppliedSearchSpec!.SchemaVersion == 1,
            "Removing the deleted tag did not restore a plain search.");

        // A stored definition (as from a saved search) applies and fills the draft.
        ArchiveSearchSpec stored = new ArchiveSearchSpec(sort: ArchiveThreadSort.Title).WithTags(new ArchiveTagFilter(null, [blue.Value]));
        Require(await page.ApplySearchSpecAsync(stored), "Stored tag definition did not apply.");
        Require(Rows().SequenceEqual(new[] { "3001", "3004" }) && Draft().SequenceEqual(new[] { "Without tag: blue" }), "Stored definition result or draft is wrong.");
        Capture(page, "filter");
        f.C<Expander>("ArchiveFilters").IsExpanded = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        mode.BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Capture(page, "filter-editor");
        Console.WriteLine("SMOKE private-tags filter [en]: include, all/any, exclude, pending choice, shared frozen request, chip removal, refresh, deleted tag, stored definition checked.");
    }

    private static async Task ManagerJourneyAsync(MainWindow owner)
    {
        string profile = TemporaryWorkspacePaths.CreateDirectory("private-tag-manager-native-");
        new Config().SaveConfig(profile);
        ResourceId first = new("4chan", "g", "4001"), second = new("4chan", "g", "4002");
        using (ProfileLease lease = ProfileLease.Acquire(profile))
        await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            foreach (ResourceId id in new[] { first, second })
                await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + id.Thread,
                    "Manager fixture " + id.Thread, ThreadSourceState.NotFound, DateTimeOffset.UnixEpoch, profile,
                    [new CapturedPost(id.Thread!, 0, PostFieldPresence.All, Html: "plain")]));
        await using Fixture f = await OpenAsync(owner, profile, first);
        TagId red = (await f.Host.DefinePrivateTagAsync("red")).Tag, blue = (await f.Host.DefinePrivateTagAsync("blue")).Tag;
        await f.Host.DefinePrivateTagAsync("typo");
        await f.Host.SetThreadTagAsync(red, first, true); await f.Host.SetThreadTagAsync(red, second, true);
        await f.Host.SetThreadTagAsync(blue, second, true);
        Assert(MeowChan.Tags.PrivateMediaTagTarget.TryCreate(new MeowChan.Storage.AttachmentRevisionReference(first, "4001", 0, "att-v2:" + new string('a', 64)),
            "m", "original", MeowChan.Transfer.TransferMediaRole.Original, new string('c', 64), out var media, out _));
        await f.Host.SetMediaTagAsync(red, media!, true);
        ArchivePage page = f.Page;
        Require(await page.ApplySearchSpecAsync(new ArchiveSearchSpec(sort: ArchiveThreadSort.Title)), "List load failed.");
        ArchiveThreadRow[] tableRows = f.C<ListBox>("ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().ToArray();
        ArchiveThreadRow firstRow = tableRows.Single(row => row.Summary.Id == first), secondRow = tableRows.Single(row => row.Summary.Id == second);
        await ShowTableAsync(f);
        Require(TagCell(f, firstRow) == "red" && TagCell(f, secondRow) == "blue, red", "Manager fixture: table tags are wrong: " + TagCell(f, secondRow));
        await page.LoadThreadAsync(first).WaitAsync(Deadline);
        await page.MediaLoadTask.WaitAsync(Deadline);
        f.Click("ReaderTagsOpen");
        Require(f.Chips().SequenceEqual(new[] { "red  \u2715" }), "Fixture tag is not shown.");

        async Task<TagManagerDialog> OpenManagerAsync()
        {
            f.Click("ReaderTagManage");
            await UntilAsync(() => page.TagManagerWindow?.IsVisible == true);
            return page.TagManagerWindow!;
        }
        async Task IdleAsync(TagManagerDialog dialog)
        {
            await UntilAsync(() => !dialog.Session.Busy);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        string[] Listed(TagManagerDialog dialog) => dialog.List.ItemsSource!.Cast<TagManagerRow>()
            .Select(row => row.Name + ":" + row.Threads + ":" + row.Media).ToArray();
        void Select(TagManagerDialog dialog, string name) =>
            dialog.List.SelectedItem = dialog.List.ItemsSource!.Cast<TagManagerRow>().Single(row => row.Name == name);

        TagManagerDialog manager = await OpenManagerAsync();
        Require(!f.C<Button>("ReaderTagManage").IsEffectivelyEnabled, "A second manager could be opened.");
        Require(Listed(manager).SequenceEqual(new[] { "red:2:1", "blue:1:0", "typo:0:0" }), "Manager list is wrong: " + string.Join("|", Listed(manager)));
        Require(!manager.RenameButton.IsEffectivelyEnabled && !manager.DeleteButton.IsEffectivelyEnabled, "Actions are enabled without a selection.");

        // Rename, then a colliding rename.
        Select(manager, "typo");
        Require(manager.NameInput.Text == "typo" && manager.RenameButton.IsEffectivelyEnabled, "Selection did not fill the name field.");
        manager.NameInput.Text = "fixed_name";
        manager.RenameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await IdleAsync(manager);
        Require(Listed(manager)[2] == "fixed_name:0:0" && manager.Status.Text == Texts.T("tags.manage.renamed"), "Rename failed: " + manager.Status.Text);
        Select(manager, "blue");
        manager.NameInput.Text = "RED";
        manager.RenameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await IdleAsync(manager);
        Require(Listed(manager)[1] == "blue:1:0" && manager.Status.Text == Texts.T("tags.manage.error.collision"), "Collision was not refused.");

        // Delete asks first, names the uses, and "keep" is the focused default.
        Select(manager, "red");
        manager.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(manager.ConfirmPanel.IsVisible && manager.KeepButton.IsFocused && manager.ConfirmText.Text == Texts.T("tags.manage.confirm", "red", 2, 1),
            "Delete did not ask with counts and a safe default: " + manager.ConfirmText.Text);
        Require(f.Stored().Find(red) != null, "Asking already deleted the tag.");
        var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        manager.KeepButton.RaiseEvent(escape);
        Require(escape.Handled && !manager.ConfirmPanel.IsVisible && manager.IsVisible && f.Stored().Find(red) != null, "Escape on the question must keep the tag and the dialog.");
        manager.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // Selecting another tag withdraws the question instead of redirecting it.
        Select(manager, "blue");
        Require(!manager.ConfirmPanel.IsVisible, "The delete question followed the selection to another tag.");
        Select(manager, "red");
        manager.DeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Capture(manager, "manager-confirm");
        manager.ConfirmDeleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await IdleAsync(manager);
        Require(Listed(manager).SequenceEqual(new[] { "blue:1:0", "fixed_name:0:0" }) && manager.Status.Text == Texts.T("tags.manage.deleted"), "Delete failed: " + manager.Status.Text);
        PrivateTagStoreSnapshot stored = f.Stored();
        Require(stored.Find(red) == null && stored.TagsOf(first).Count == 0 && stored.MediaAssignments.Count == 0 && stored.TagsOf(second).Single().Id == blue,
            "Deleting a tag left uses behind or touched another tag.");

        // Closing brings the page up to date: chips, button count, list rows and filter choices.
        var close = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        manager.List.RaiseEvent(close);
        await page.TagManagerTask.WaitAsync(Deadline);
        Require(page.TagManagerWindow == null && f.C<Button>("ReaderTagManage").IsEffectivelyEnabled, "Manager did not close cleanly.");
        Require(f.Chips().Length == 0 && Equals(f.C<Button>("ReaderTagsOpen").Content, "Tags"), "Tag bar still shows the deleted tag.");
        ArchiveThreadRow[] rows = f.C<ListBox>("ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().ToArray();
        Require(!rows.Single(row => row.Summary.Id == first).HasTags && rows.Single(row => row.Summary.Id == second).TagsText == "Tags: blue", "List rows still show the deleted tag.");
        string[] choices = f.C<ComboBox>("ArchiveTagChoice").ItemsSource!.Cast<object>().Select(item => item.ToString()!).ToArray();
        Require(choices.SequenceEqual(new[] { Texts.T("tags.filter.choose"), "blue", "fixed_name" }), "Filter choices are stale: " + string.Join("|", choices));
        // The table column shows the deletion and then a rename, in the same rows, once the manager closes.
        Require(TagCell(f, firstRow) == "" && TagCell(f, secondRow) == "blue", "Table column still shows the deleted tag: " + TagCell(f, secondRow));
        manager = await OpenManagerAsync();
        Select(manager, "blue");
        manager.NameInput.Text = "navy";
        manager.RenameButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await IdleAsync(manager);
        manager.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await page.TagManagerTask.WaitAsync(Deadline);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(TagCell(f, secondRow) == "navy" && secondRow.TagsText == "Tags: navy" && TagCell(f, firstRow) == "", "Table column did not follow the rename: " + TagCell(f, secondRow));
        Require(ReferenceEquals(f.C<DataGrid>("ArchiveThreadTable").ItemsSource, f.C<ListBox>("ArchiveThreads").ItemsSource)
            && f.C<ListBox>("ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().Contains(secondRow), "Rename reloaded the list.");
        // SHA-256 search is reachable from the reader, for exactly this thread's one media inventory.
        Button hash = f.C<Button>("ReaderHashSearchOpen");
        Require(hash.IsEffectivelyEnabled && Equals(hash.Content, Texts.T("reader.hashSearch.open")), "Hash search entry is missing or disabled.");
        f.Click(hash);
        await UntilAsync(() => page.MediaHashSearchWindow?.IsVisible == true);
        Require(!hash.IsEffectivelyEnabled && !f.C<Button>("ReaderTagManage").IsEffectivelyEnabled, "A second dialog could be opened over the hash search.");
        page.MediaHashSearchWindow!.Close();
        await page.MediaHashSearchTask.WaitAsync(Deadline);
        Require(page.MediaHashSearchWindow == null && hash.IsEffectivelyEnabled, "Hash search did not close cleanly.");
        Console.WriteLine("SMOKE private-tags manager [en]: list with counts, rename, collision, delete question with safe default, Escape, withdrawn question, delete, page refresh checked.");
    }

    private static void Assert(bool condition) => Require(condition, "Fixture construction failed.");

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Private tags UI event deadline.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }

    // Optional visual evidence: MEOWCHAN_PRIVATE_TAGS_SCREENSHOTS names an existing directory.
    private static void Capture(Control control, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("MEOWCHAN_PRIVATE_TAGS_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        control.UpdateLayout();
        Avalonia.PixelSize size = new((int)Math.Ceiling(control.Bounds.Width), (int)Math.Ceiling(control.Bounds.Height));
        if (size.Width <= 0 || size.Height <= 0) return;
        using Avalonia.Media.Imaging.RenderTargetBitmap bitmap = new(size);
        bitmap.Render(control);
        bitmap.Save(Path.Combine(directory, "private-tags-" + name + ".png"));
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
