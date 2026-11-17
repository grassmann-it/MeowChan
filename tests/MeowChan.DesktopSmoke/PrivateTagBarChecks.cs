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
using MeowChan.Gallery;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Tags;
using MeowChan.Testing;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_PRIVATE_TAGS_ONLY. Real host, real store and actual page controls
// on a synthetic offline profile. Routed managed input is NOT OS keyboard input.
internal static partial class PrivateTagBarChecks
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
            await QuickTagEnterAsync(owner);
            await CaretEntryAsync(owner);
            await JourneyAsync(owner, profile);
            await ThreadMenuJourneyAsync(owner, profile);
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
        internal Window Window => window;
        internal T C<T>(string name) where T : Control => page.FindControl<T>(name)
            ?? throw new InvalidOperationException("Missing actual control " + name);
        internal void Click(string name) => Click(C<Button>(name));
        internal void Click(Button button)
        {
            Require(button.IsEffectivelyEnabled, "Disabled fixture action " + (button.Name ?? Label(button)));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        internal static string Label(Control control) => control is Button { Content: StackPanel removable } ? MeowChan.Desktop.Controls.WorkspaceChips.RemovableName(removable) ?? ""
            : control is Button { Content: TextBlock text } ? text.Text ?? ""
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
        IdentitySuggestionBox input = f.C<IdentitySuggestionBox>("ReaderTagInput");
        Require(!bar.IsVisible && Equals(open.Content, "Tags") && open.IsEffectivelyEnabled, "Initial tag button state is wrong.");

        f.Click("ReaderTagsOpen");
        Require(bar.IsVisible && input.IsKeyboardFocusWithin, "Open did not show the bar and focus its input.");
        Require(f.Chips().Length == 0 && f.C<WrapPanel>("ReaderTagChips").Children.OfType<TextBlock>().Single().Text == Texts.T("tags.none"),
            "An untagged thread must say so.");
        // Title and first post only; duplicates collapse, paths and later posts are ignored.
        Require(f.Suggestions().Select(Fixture.Label).SequenceEqual(new[] { "+ /rpt/", "+ /sdg/" }), "Unexpected suggestions.");
        Require(f.Stored().Tags.Count == 0 && !File.Exists(Path.Combine(profile, PrivateTagStore.FileName)),
            "Showing suggestions must not create or assign anything.");

        f.Click(f.Suggestions()[0]);
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(new[] { "/rpt/" }) && Equals(open.Content, "Tags (1)"), "Suggestion click did not tag the thread.");
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
        Require(f.Chips().SequenceEqual(new[] { "/rpt/", "my_tag" }), "Typed tag missing or underscore lost: " + string.Join("|", f.Chips()));
        Require(string.IsNullOrEmpty(input.Text) && input.IsKeyboardFocusWithin && Equals(open.Content, "Tags (2)"), "Add did not clear the field and keep focus.");

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
        Require(f.Chips().SequenceEqual(new[] { "my_tag" }) && Equals(open.Content, "Tags (1)"), "Chip click did not remove the tag.");
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
        Require(bar.IsVisible && !input.IsKeyboardFocusWithin && f.Chips().SequenceEqual(new[] { "my_tag" }),
            "Returned bar is not current: visible " + bar.IsVisible + ", field focused " + input.IsKeyboardFocusWithin + ", chips " + string.Join("|", f.Chips()));
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
        await AutocompleteAsync(f, input, bar);
        await CaretAutocompleteAsync(f, input);

        input.Focus();
        bool listBefore = input.IsDropDownOpen;
        if (listBefore)
        {
            // The emptied field still lists the thread's suggestions: the first Escape closes only that list (one Escape order).
            var listEscape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            input.RaiseEvent(listEscape);
            Require(listEscape.Handled && !input.IsDropDownOpen && bar.IsVisible, "The first Escape must close only the open suggestion list.");
        }
        var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        input.RaiseEvent(escape);
        Require(escape.Handled && !bar.IsVisible && open.IsFocused, "Escape did not close the bar and return focus: list open before " + listBefore + ", text '" + input.Text + "', dialog " + f.Page.IsReaderDialogOpen + ", capturing " + MeowChan.Desktop.Commands.ShortcutService.IsCapturing + ", handled " + escape.Handled
            + ", bar visible " + bar.IsVisible + ", list open " + input.IsDropDownOpen + ", open visible " + open.IsEffectivelyVisible
            + ", focused " + ((owner.FocusManager?.GetFocusedElement() as Control)?.Name ?? "none"));
        Console.WriteLine("SMOKE private-tags journey [en]: suggestions, click-to-tag, typed Enter, case variant, invalid name, remove, list rows, thread switch, Escape checked.");
    }

    // "Tags…" in the thread list menu: one selected thread is shown in the reader pane and its tag bar opens there,
    // with the input focused; a batch offers no such entry. Opening tags nothing and writes nothing.
    // This window is too narrow for a preview beside the table, so choosing the entry also has to load the thread.
    private static async Task ThreadMenuJourneyAsync(MainWindow owner, string profile)
    {
        await using Fixture f = await OpenAsync(owner, profile, Plain);
        Require(await f.Page.ApplySearchSpecAsync(new ArchiveSearchSpec()), "List load failed.");
        await ShowTableAsync(f);
        DataGrid table = f.C<DataGrid>("ArchiveThreadTable");
        StackPanel bar = f.C<StackPanel>("ReaderTagBar");
        IdentitySuggestionBox input = f.C<IdentitySuggestionBox>("ReaderTagInput");
        ArchiveThreadRow[] rows = table.ItemsSource!.Cast<ArchiveThreadRow>().ToArray();
        ArchiveThreadRow taggedRow = rows.Single(row => row.Summary.Id == Tagged), plainRow = rows.Single(row => row.Summary.Id == Plain);
        long revision = f.Stored().Revision;
        Require(!bar.IsVisible && f.Page.CurrentThread == null, "The bar must start closed and no thread shown.");

        async Task<ContextMenu> OpenMenuAsync(params ArchiveThreadRow[] selection)
        {
            if (!table.IsEffectivelyVisible)
            {
                f.Click("ArchiveBackButton");
                await UntilAsync(() => table.IsEffectivelyVisible);
            }
            table.SelectedItems.Clear();
            foreach (ArchiveThreadRow row in selection) table.SelectedItems.Add(row);
            await UntilAsync(() => table.SelectedItems.Count == selection.Length && !f.Page.IsBusy);
            ContextMenu menu = table.ContextMenu!;
            table.Focus();
            table.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            await UntilAsync(() => menu.IsOpen);
            return menu;
        }
        static MenuItem? TagsItem(ContextMenu menu) => menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Tag, "thread.tags"));
        async Task ChooseTagsAsync(ContextMenu menu, ResourceId thread)
        {
            MenuItem item = TagsItem(menu) ?? throw new InvalidOperationException("The thread menu offers no Tags… entry.");
            object?[] expectedBeforeTags = { "thread.favorite-toggle", "thread.read", "gallery.open", "thread.details" };
            // Continue Reading is an optional action inserted after Read when this thread has a saved
            // reading position. It is part of the current menu contract and must not make the older
            // Tags smoke reject an otherwise valid menu.
            if (menu.Items.OfType<MenuItem>().Any(entry => Equals(entry.Tag, "navigation.continue-reading")))
                expectedBeforeTags = new object?[] { "thread.favorite-toggle", "thread.read", "navigation.continue-reading", "gallery.open", "thread.details" };
            bool menuContract = item.Command?.CanExecute(null) == true && item.Header?.ToString()?.StartsWith(Texts.T("command.thread.tags"), StringComparison.Ordinal) == true
                && menu.Items.OfType<MenuItem>().Select(entry => entry.Tag).TakeWhile(tag => !Equals(tag, "thread.tags"))
                    .Where(tag => !Equals(tag, "thread.show-in-media"))
                    .SequenceEqual(expectedBeforeTags);
            string actualMenu = string.Join(", ", menu.Items.OfType<MenuItem>().Select(entry => $"{entry.Tag}:{entry.Header}:enabled={entry.Command?.CanExecute(null)}"));
            Require(menuContract, "The Tags… entry is disabled, mislabeled or misplaced: " + item.Header + "; menu=" + actualMenu);
            item.Command!.Execute(null);
            menu.Close();
            await UntilAsync(() => f.Page.CurrentThread?.Summary.Id == thread && bar.IsVisible && input.IsKeyboardFocusWithin);
        }

        // Tagged thread, not shown yet: it is loaded, its bar opens on its own tags and takes focus.
        await ChooseTagsAsync(await OpenMenuAsync(taggedRow), Tagged);
        Require(f.Chips().SequenceEqual(new[] { "my_tag", "/sdg/" }), "The bar does not show the tags of the chosen thread: " + string.Join("|", f.Chips()));

        // Another thread: the bar follows it and shows its own (empty) tags.
        f.Click("ReaderTagClose");
        Require(!bar.IsVisible, "The bar did not close.");
        await ChooseTagsAsync(await OpenMenuAsync(plainRow), Plain);
        Require(f.Chips().Length == 0, "The bar did not open for the untagged thread.");

        // A second use with the bar already open keeps it open (it never toggles shut).
        await ChooseTagsAsync(await OpenMenuAsync(plainRow), Plain);
        Require(bar.IsVisible, "A second Tags… closed the open bar.");

        // A batch gets no Tags… entry: tags edit exactly one thread.
        ContextMenu batch = await OpenMenuAsync(plainRow, taggedRow);
        Require(TagsItem(batch) == null, "A batch selection offered Tags….");
        batch.Close();
        Require(f.Stored().Revision == revision, "Opening the bar through the menu changed the tag store.");
        Console.WriteLine("SMOKE private-tags thread menu [en]: Tags… loads and opens the bar for a tagged and an untagged thread, kept open, absent for a batch, store untouched.");
    }

    private static string[] Shown(IdentitySuggestionBox input) =>
        input.ItemsSource?.OfType<IdentitySuggestion>().Select(item => item.Label).ToArray() ?? [];

    // T4: own tags of every thread are offered while typing; keys follow the list first, then the bar.
    private static async Task AutocompleteAsync(Fixture f, IdentitySuggestionBox input, StackPanel bar)
    {
        static KeyEventArgs Press(Key key) => new() { RoutedEvent = InputElement.KeyDownEvent, Key = key };
        async Task OpenAsync(string text, string[] expected)
        {
            input.Focus();
            input.Text = text;
            await Program.WaitUntilAsync(() => input.IsDropDownOpen && Shown(input).Length > 0,
                () => "tag suggestions did not open for '" + text + "': " + string.Join("|", Shown(input)));
            Require(Shown(input).SequenceEqual(expected), "Unexpected tag suggestions for '" + text + "': " + string.Join("|", Shown(input)));
        }
        int tags = f.Stored().Tags.Count;
        // A synthetic Danbooru catalog: its source tags form their own group below the own tags.
        static BooruPostRecord Post(string id, params string[] names) => new(BooruMediaIdentity.Source, id,
            new BooruPostClaim(BooruMediaIdentity.Source, id, BooruPostClaimPresence.None,
                Tags: names.Select(name => new BooruPostClaimTag(name, BooruPostClaimTagCategory.General)).ToArray()),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        BooruCatalogSnapshot catalog = new(BooruCatalogSnapshot.CurrentSchemaVersion,
            [Post("1", "silver_hair", "my_tag"), Post("2", "silver_hair")], []);
        f.Page.SourceTagSuggestions.Read = () => catalog;
        f.Page.SourceTagSuggestions.Load();
        await f.Page.SourceTagSuggestions.LoadTask.WaitAsync(Deadline);
        // Word start match with its use count; the one real match is highlighted at once without touching the draft, Enter adds it.
        await OpenAsync("sd", ["/sdg/ (1)", "New tag: \u201csd\u201d"]);
        Require(AutomationProperties.GetHelpText(input) == Texts.Count("tags.suggest.count", 2, 2, TagSuggestionIndex.VisibleLimit), "The list size was not announced.");
        Require(input.HighlightedCandidate?.Value == "/sdg/" && input.Text == "sd", "The one match must be highlighted without changing the draft.");
        KeyEventArgs accept = Press(Key.Enter);
        input.RaiseEvent(accept);
        Require(accept.Handled, "Enter on a highlighted suggestion was not handled.");
        await Program.WaitUntilAsync(() => f.Chips().SequenceEqual(["/sdg/"]) && string.IsNullOrEmpty(input.Text),
            () => "Enter did not add the highlighted suggestion: " + string.Join("|", f.Chips()) + " text=" + input.Text);
        await f.SettleAsync();
        // An assigned tag is not offered again. The first Escape closes the list and keeps the bar.
        await OpenAsync("G", ["my_tag (1)", "New tag: \u201cG\u201d"]);
        Capture(f.Page, "autocomplete");
        KeyEventArgs listEscape = Press(Key.Escape);
        input.RaiseEvent(listEscape);
        Require(listEscape.Handled && !input.IsDropDownOpen && bar.IsVisible && input.Text == "G", "Escape must close only the suggestion list.");
        // Tab completes the first suggestion in place; a comma at the end then adds the completed name.
        await OpenAsync("my", ["my_tag (1)", "New tag: \u201cmy\u201d"]);
        KeyEventArgs tab = Press(Key.Tab);
        input.RaiseEvent(tab);
        Require(tab.Handled && input.Text == "my_tag" && !input.IsDropDownOpen, "Tab did not complete the suggestion: " + input.Text);
        var comma = new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "," };
        input.RaiseEvent(comma);
        Require(comma.Handled, "A trailing comma was typed instead of adding the tag.");
        await f.SettleAsync();
        Require(f.Chips().SequenceEqual(["my_tag", "/sdg/"]) && string.IsNullOrEmpty(input.Text) && f.Stored().Tags.Count == tags,
            "Comma did not reuse the completed tag: " + string.Join("|", f.Chips()));
        // A source tag is offered as its own labelled group and creates an own tag when chosen; a source tag that already
        // is an own tag (my_tag above) is never listed twice.
        await OpenAsync("hair", ["silver_hair · Danbooru (2)", "New tag: \u201chair\u201d"]);
        IdentitySuggestion source = input.ItemsSource!.OfType<IdentitySuggestion>().First();
        Require(source.GroupHeader == Texts.T("tags.suggest.sourceGroup") && source.Source == TagSuggestionIndex.SourceGroup
            && source.Description == Texts.T("tags.suggest.sourceName", "silver_hair", 2), "The source tag group is not labelled: " + source);
        Capture(f.Page, "autocomplete-source");
        Require(input.HighlightedCandidate == source, "The one source match was not highlighted: " + input.HighlightedCandidate?.Value);
        input.RaiseEvent(Press(Key.Enter));
        await Program.WaitUntilAsync(() => f.Chips().Contains("silver_hair"), () => "Source tag was not added: " + string.Join("|", f.Chips()));
        await f.SettleAsync();
        Require(f.Stored().Tags.Count == tags + 1 && f.Stored().TagsOf(Plain).Any(tag => tag.Name.Display == "silver_hair"),
            "Choosing a source tag must create exactly one own tag on the thread.");
        // Leave the thread and the vocabulary as the journey expects them.
        for (int round = 0; round < 5 && f.C<WrapPanel>("ReaderTagChips").Children.OfType<Button>().FirstOrDefault() is { } chip; round++)
        {
            f.Click(chip);
            await f.SettleAsync();
        }
        PrivateTagStoreSnapshot created = f.Stored();
        await f.Host.DeletePrivateTagAsync(created.Tags.Single(tag => tag.Name.Display == "silver_hair").Id, created.Revision).WaitAsync(Deadline);
        await f.SettleAsync();
        Require(f.Chips().Length == 0 && f.Stored().TagsOf(Plain).Count == 0 && f.Stored().Tags.Count == tags,
            "Autocomplete cleanup left tags on the plain thread or in the vocabulary.");
        Console.WriteLine("SMOKE private-tags autocomplete [en]: word-start match with uses, the one match highlighted and Enter adds it, assigned tag hidden, Escape closes list first, Tab completes, comma adds, Danbooru source tag group creates an own tag.");
    }

    private static async Task RestartAndDamageAsync(MainWindow owner, string profile)
    {
        await using (Fixture f = await OpenAsync(owner, profile, Tagged))
        {
            // New host and page in German: persisted tags, localized labels.
            Button open = f.C<Button>("ReaderTagsOpen");
            Require(Equals(open.Content, Texts.T("tags.openCount", 2)), "Tags did not survive a restart: " + open.Content);
            f.Click("ReaderTagsOpen");
            Require(f.Chips().SequenceEqual(new[] { "my_tag", "/sdg/" }), "Restart lost or reordered tags.");
            await Program.WaitUntilAsync(() => Equals(f.C<Button>("ReaderTagAdd").Content, Texts.T("tags.add"))
                && f.C<IdentitySuggestionBox>("ReaderTagInput").PlaceholderText == Texts.T("tags.input.several"),
                () => $"German labels missing: add={f.C<Button>("ReaderTagAdd").Content}, placeholder={f.C<IdentitySuggestionBox>("ReaderTagInput").PlaceholderText}");
            Capture(f.Page, "bar-de");
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
            Require(!f.C<IdentitySuggestionBox>("ReaderTagInput").IsEffectivelyEnabled && !f.C<Button>("ReaderTagAdd").IsEffectivelyEnabled
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
        MeowChan.Desktop.Controls.TagFilterPicker choice = f.C<MeowChan.Desktop.Controls.TagFilterPicker>("ArchiveTagChoice");
        DataValidationErrors choiceHost = f.C<DataValidationErrors>("ArchiveTagChoiceHost");
        ComboBox mode = f.C<ComboBox>("ArchiveTagMode");
        TextBlock error = f.C<TextBlock>("ArchiveSearchError");
        string[] Rows() => f.C<ListBox>("ArchiveThreads").ItemsSource!.Cast<ArchiveThreadRow>().Select(row => row.Summary.Id.Thread!).Order().ToArray();
        string[] Choices() => page.AddableDraftTagNames;
        // The popover's typed route: text into the search field, then Enter (Shift+Enter or a leading "-" excludes).
        void Type(string text, KeyModifiers modifiers = KeyModifiers.None)
        {
            choice.Search.Text = text; choice.Search.FlushQuery();
            choice.Search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = modifiers });
        }
        string[] Draft() => f.C<WrapPanel>("ArchiveDraftTags").Children.OfType<Ursa.Controls.ClosableTag>()
            .Select(ArchivePage.FilterTagLabel).ToArray();
        Ursa.Controls.ClosableTag DraftChip(string label) => f.C<WrapPanel>("ArchiveDraftTags").Children.OfType<Ursa.Controls.ClosableTag>()
            .Single(tag => ArchivePage.FilterTagLabel(tag) == label);
        string[] Applied() => f.C<WrapPanel>("ArchiveAppliedFilters").Children.OfType<Ursa.Controls.ClosableTag>()
            .Select(ArchivePage.FilterTagLabel).ToArray();
        void RemoveDraft(string label) => DraftChip(label).Command!.Execute(null);
        async Task SearchAsync() { await page.ApplySearchAsync(); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }

        Require(await page.ApplySearchSpecAsync(new ArchiveSearchSpec(sort: ArchiveThreadSort.Title)), "Initial list failed.");
        Require(Rows().SequenceEqual(new[] { "3001", "3002", "3003", "3004" }) && Applied().Length == 0, "Unfiltered list is wrong.");
        Require(Choices().SequenceEqual(new[] { "blue", "red", "unused" }), "Tag choices are wrong: " + string.Join("|", Choices()));
        Require(choice.IsEnabled && Avalonia.Automation.AutomationProperties.GetName(choice) == Texts.T("tags.filter.add")
            && f.C<TextBlock>("ArchiveTagChoiceLabel").Text == Texts.T("tags.filter.label"), "The tag picker is not named or enabled.");

        // Sorting the table by its tag column: by sorted names (elementwise), threads without tags last, header toggles direction.
        await ShowTableAsync(f);
        DataGridColumn tagColumn = f.C<DataGrid>("ArchiveThreadTable").Columns[7];
        Require(TableOrder(f).SequenceEqual(new[] { "3001", "3002", "3003", "3004" }), "Table order before sorting is wrong.");
        await SortedAsync(f, tagColumn, System.ComponentModel.ListSortDirection.Ascending, "3003", "3002", "3001", "3004");
        await SortedAsync(f, tagColumn, System.ComponentModel.ListSortDirection.Descending, "3004", "3001", "3002", "3003");
        f.C<DataGrid>("ArchiveThreadTable").CollectionView!.SortDescriptions.Clear();

        // Include one tag.
        Type("red");
        Require(Draft().SequenceEqual(new[] { "Tag: red" }) && Choices().SequenceEqual(new[] { "blue", "unused" })
            && !choice.HasPending && !choice.Search.HasText, "Include did not move the tag into the draft.");
        Require(Rows().Length == 4, "Editing the draft must not search by itself.");
        await SearchAsync();
        Require(Rows().SequenceEqual(new[] { "3001", "3002" }) && Applied().SequenceEqual(new[] { "Tag: red" }), "Include filter result is wrong: " + string.Join(",", Rows()));
        Require(page.AppliedSearchSpec!.SchemaVersion == 2 && page.AppliedSearchRequest!.TagMembership != null, "Applied request is not tag-bound.");

        // Two included tags: all, then at least one.
        Require(page.AddDraftTagByName("blue", include: true) == null, "Including blue failed.");
        await SearchAsync();
        Require(Rows().SequenceEqual(new[] { "3002" }), "All-of-two result is wrong: " + string.Join(",", Rows()));
        mode.SelectedIndex = 1;
        await SearchAsync();
        Require(Rows().SequenceEqual(new[] { "3001", "3002", "3003" }) && Applied().Contains(Texts.T("tags.filter.anyChip")), "Any-of-two result is wrong: " + string.Join(",", Rows()));

        // Exclude.
        RemoveDraft("Tag: blue");
        Type("-blue");
        Require(Draft().SequenceEqual(new[] { "Tag: red", "Without tag: blue" }) && DraftChip("Without tag: blue").Classes.Contains("chip-exclude")
            && !DraftChip("Tag: red").Classes.Contains("chip-exclude"), "Exclude draft is wrong.");
        // The chip's toggle switches the state in the draft only (nothing is searched); the close mark removes.
        Ursa.Controls.ClosableTag ToggleOf(string label) => DraftChip(label);
        void Press(string label) => ((Button)((StackPanel)ToggleOf(label).Content!).Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Press("Without tag: blue");
        Require(Draft().SequenceEqual(new[] { "Tag: red", "Tag: blue" }) && !DraftChip("Tag: blue").Classes.Contains("chip-exclude")
            && Rows().SequenceEqual(new[] { "3001", "3002", "3003" }), "The toggle did not include the excluded tag, or it searched: " + string.Join("|", Draft()));
        Press("Tag: blue");
        Require(Draft().SequenceEqual(new[] { "Tag: red", "Without tag: blue" }) && DraftChip("Without tag: blue").Classes.Contains("chip-exclude")
            && Choices().SequenceEqual(new[] { "unused" }), "The toggle did not exclude the tag again: " + string.Join("|", Draft()));
        Require(page.AddDraftTagByName("red", include: false) is { } both && both == Texts.T("tags.filter.error.both"), "A tag in the other list was not refused.");
        await SearchAsync();
        Require(Rows().SequenceEqual(new[] { "3001" }) && Applied().SequenceEqual(new[] { "Tag: red", "Without tag: blue" }), "Exclude result is wrong: " + string.Join(",", Rows()));

        // An applied chip's toggle switches include/exclude and searches again at once; the draft follows; toggling back restores.
        Button AppliedToggle(string label) => (Button)((StackPanel)f.C<WrapPanel>("ArchiveAppliedFilters").Children.OfType<Ursa.Controls.ClosableTag>()
            .Single(tag => ArchivePage.FilterTagLabel(tag) == label).Content!).Children[0];
        Require(Avalonia.Automation.AutomationProperties.GetName(AppliedToggle("Without tag: blue")) == Texts.T("tags.filter.toInclude", "blue"),
            "The applied chip's toggle does not name its action.");
        Require(await page.ToggleAppliedTagByName("blue") == null, "Toggling the applied chip failed.");
        Require(Rows().SequenceEqual(new[] { "3002" }) && Applied().Order().SequenceEqual(new[] { "Tag: blue", "Tag: red" })
            && Draft().Order().SequenceEqual(new[] { "Tag: blue", "Tag: red" }), "The applied toggle did not include blue and search again: " + string.Join(",", Rows()));
        Require(await page.ToggleAppliedTagByName("blue") == null && Rows().SequenceEqual(new[] { "3001" })
            && Applied().SequenceEqual(new[] { "Tag: red", "Without tag: blue" }), "The applied toggle did not switch back.");

        // A chosen but not added tag is an unfinished entry: explained, nothing searched. Unlike a source or
        // board (added on Search), the app cannot guess Include or Exclude. The request to choose stands once,
        // under the tag choice in the opened filter surface, not again above the toolbar.
        // With the popover the unfinished entry is a tag typed into its search field and not added (Enter / Shift+Enter).
        choice.Search.Text = "unused"; choice.Search.FlushQuery();
        Require(choice.HasPending, "Typed text naming a tag is not an unfinished entry.");
        await SearchAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        Require(Rows().SequenceEqual(new[] { "3001" }) && DataValidationErrors.GetHasErrors(choiceHost)
            && DataValidationErrors.GetErrors(choiceHost)?.OfType<object>().Select(item => item.ToString()).SequenceEqual(new[] { Texts.T("tags.filter.pending") }) == true,
            "Pending tag choice was applied or ignored silently.");
        TextBlock[] shownUnderChoice = choiceHost.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Text == Texts.T("tags.filter.pending") && text.IsEffectivelyVisible).ToArray();
        Console.WriteLine("SMOKE private-tags pending-choice: " + System.Text.Json.JsonSerializer.Serialize(new
        {
            surfaceOpen = page.IsFilterSurfaceOpen, choiceVisible = choice.IsEffectivelyVisible, shownUnderChoice = shownUnderChoice.Length,
            validationPresenters = choice.GetVisualDescendants().OfType<DataValidationErrors>().Count(), topError = error.Text, topVisible = error.IsVisible
        }));
        Require(page.IsFilterSurfaceOpen && choice.IsEffectivelyVisible && shownUnderChoice.Length == 1 && !error.IsVisible,
            "The Include/Exclude request is not shown exactly once, under the tag choice: surface open " + page.IsFilterSurfaceOpen
            + ", choice visible " + choice.IsEffectivelyVisible + ", shown under choice " + shownUnderChoice.Length + ", top error '" + error.Text + "' visible " + error.IsVisible);
        choice.Search.Text = string.Empty; choice.Search.FlushQuery();
        Require(!choice.HasPending && !DataValidationErrors.GetHasErrors(choiceHost), "Clearing the entry did not clear its mark.");

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
        f.C<Avalonia.Controls.Primitives.ToggleButton>("ArchiveFilters").IsChecked = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        mode.BringIntoView();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Capture(page, "filter-editor");
        Console.WriteLine("SMOKE private-tags filter [en]: popover include/exclude (typed Enter, leading dash), chip toggle, all/any, exclude, pending entry, shared frozen request, chip removal, refresh, deleted tag, stored definition checked.");
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
        Require(f.Chips().SequenceEqual(new[] { "red" }), "Fixture tag is not shown.");

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
        // Most used first is the manager's default; A–Z is one choice away and back.
        Require(manager.SortChoice.SelectedIndex == 1, "The manager must start with the most used tags first.");
        manager.SortChoice.SelectedIndex = 0;
        Require(Listed(manager).SequenceEqual(new[] { "blue:1:0", "red:2:1", "typo:0:0" }), "A–Z order is wrong: " + string.Join("|", Listed(manager)));
        manager.SortChoice.SelectedIndex = 1;
        Require(Listed(manager)[0] == "red:2:1", "Most-used order did not come back.");
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
        string[] choices = page.AddableDraftTagNames;
        Require(choices.SequenceEqual(new[] { "blue", "fixed_name" }), "Filter choices are stale: " + string.Join("|", choices));
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
        // Merge: two selected tags, the question with the most used tag as target, preview and Cancel as the default; then merge,
        // the merged name as an alias of the target, and the one-step undo.
        manager = await OpenManagerAsync();
        Require(manager.MergeButton.IsVisible && !manager.MergeButton.IsEffectivelyEnabled, "Merge is offered without two selected tags.");
        manager.List.SelectedItems!.Clear();
        foreach (TagManagerRow row in manager.List.ItemsSource!.Cast<TagManagerRow>()) manager.List.SelectedItems.Add(row);
        Require(manager.MergeButton.IsEffectivelyEnabled, "Merge is not enabled for two selected tags.");
        manager.MergeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(manager.MergePanel.IsVisible && manager.MergeCancelButton.IsFocused
            && (manager.MergeTarget.SelectedItem as TagManagerRow)?.Name == "navy"
            && manager.MergePreview.Text == Texts.T("tags.merge.preview", 0, 0, "navy", Texts.T("tags.merge.quoted", "fixed_name")),
            "Merge did not ask with target, preview and a safe default: " + manager.MergePreview.Text);
        Require(f.Stored().Tags.Count == 2, "Asking already merged.");
        Capture(manager, "manager-merge");
        manager.MergeConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await IdleAsync(manager);
        Require(Listed(manager).SequenceEqual(new[] { "navy:1:0" }) && manager.Status.Text == Texts.T("tags.merge.done", "navy")
            && f.Stored().Aliases.Single().Name.Display == "fixed_name" && manager.UndoMergeButton.IsVisible
            && manager.AliasPanel.IsVisible && manager.AliasList.ItemCount == 1, "Merge failed: " + manager.Status.Text);
        manager.UndoMergeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await IdleAsync(manager);
        Require(f.Stored().Tags.Count == 2 && f.Stored().Aliases.Count == 0 && !manager.UndoMergeButton.IsVisible
            && manager.Status.Text == Texts.T("tags.merge.undone"), "Undo merge failed: " + manager.Status.Text);
        manager.List.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await page.TagManagerTask.WaitAsync(Deadline);
        // SHA-256 search is reachable from the reader, for exactly this thread's one media inventory:
        // AP2 makes it the SHA-256 scope of the reader find bar (empty draft opens the dialog idle).
        Button hash = UnifiedSearchEntry.ReaderScope(page, MeowChan.Desktop.Controls.SearchScope.Sha256);
        Require(hash.IsEffectivelyEnabled && Equals(hash.Content, Texts.T("archive.search"))
            && f.C<TextBlock>("ReaderFindStatus").Text == Texts.T("search.scope.hash.hint"), "Hash search scope is missing or disabled.");
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
