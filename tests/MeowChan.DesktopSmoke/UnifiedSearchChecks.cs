using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;
using MeowChan.Storage;
using MeowChan.Testing;
using ArchiveCatalog = MeowChan.Archive.ArchiveCatalog;

namespace MeowChan.DesktopSmoke;

// Root dispatch only: MEOWCHAN_UNIFIED_SEARCH_ONLY. Archive and reader search surfaces in a private
// host window over a synthetic catalog, in Light and Dark. Routed managed input only, no OS keyboard.
internal static class UnifiedSearchChecks
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static readonly ResourceId First = new("4chan", "g", "3000");

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string language = Texts.Language;
        string profile = TemporaryWorkspacePaths.CreateDirectory("unified-search-");
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(profile))
            await using (ArchiveCatalog catalog = await ArchiveCatalog.OpenAsync(profile, lease))
            {
                for (int thread = 0; thread < 12; thread++)
                {
                    ResourceId id = thread == 0 ? First : new("4chan", "g", (3000 + thread * 100).ToString(CultureInfo.InvariantCulture));
                    CapturedPost[] posts = Enumerable.Range(0, 8).Select(i => new CapturedPost(
                        (int.Parse(id.Thread!, CultureInfo.InvariantCulture) + i).ToString(CultureInfo.InvariantCulture), i, PostFieldPresence.All,
                        Subject: i == 0 ? "Search fixture " + thread : null,
                        Html: "Offline post " + i + " about desk lamps and keyboards",
                        Attachments: i % 3 == 0 ? new[] { new CapturedAttachment(0, "m" + thread + "-" + i, OriginalFileName: "lamp_" + thread + "_" + i + ".jpg") } : null)).ToArray();
                    await catalog.CaptureAsync(new CapturedThreadSnapshot(id, "https://boards.4chan.org/g/thread/" + id.Thread,
                        "Search fixture " + thread.ToString("D2", CultureInfo.InvariantCulture), ThreadSourceState.NotFound,
                        DateTimeOffset.UnixEpoch.AddDays(thread), profile, posts));
                }
            }
            MediaAssetSnapshot media = new(profile, MediaManifestStatus.Available, 1, "fixture-generation",
                Array.Empty<MediaAssetReference>(), null);
            Texts.SetLanguage("en");
            ArchivePage page = new(token => ArchiveReader.OpenAsync(profile, token),
                (_, _) => throw new InvalidOperationException("No capture mutation permitted."),
                _ => throw new InvalidOperationException("No external launch permitted."),
                (_, _) => Task.FromResult(media)) { IsAvailable = true };
            Window window = new() { Width = 1280, Height = 820, Content = page, Title = "Unified search — diagnostic", ShowInTaskbar = false };
            try
            {
                window.Show(owner);
                await page.LoadThreadAsync(First).WaitAsync(Deadline);
                await page.MediaLoadTask.WaitAsync(Deadline);
                foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                    await CaptureAsync(window, page, theme);
            }
            finally
            {
                page.EndLifetime();
                window.Close();
            }
            await MainWindowCarryAsync(owner);
            Console.WriteLine("SMOKE unified search: archive and reader search surfaces captured in Light and Dark.");
        }
        finally
        {
            Texts.SetLanguage(language);
        }
    }

    private static T C<T>(ArchivePage page, string name) where T : Control => page.FindControl<T>(name)
        ?? throw new InvalidOperationException("Missing actual control " + name);

    private static async Task SettleAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static async Task CaptureAsync(Window window, ArchivePage page, ThemeVariant theme)
    {
        string name = theme.ToString().ToLowerInvariant();
        window.RequestedThemeVariant = theme;
        await SettleAsync(window);
        Program.CaptureWindow(window, $"smoke-unified-search-reader-{name}.png");
        Button find = C<Button>(page, "ReaderFindOpen");
        Require(find.IsEffectivelyEnabled, "Reader find entry is disabled with an open thread.");
        find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.ThreadFindTask.WaitAsync(Deadline);
        C<TextBox>(page, "ReaderFindQuery").Text = "lamp";
        await SettleAsync(window);
        await page.ThreadFindTask.WaitAsync(Deadline);
        await SettleAsync(window);
        Program.CaptureWindow(window, $"smoke-unified-search-find-{name}.png");
        C<TextBox>(page, "ReaderFindQuery").Text = "";
        C<Button>(page, "ReaderFindClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(window);
        await ScopesAsync(window, page, name);
    }

    private static void Press(Control target, Avalonia.Input.Key key, KeyModifiers modifiers = KeyModifiers.None) =>
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers, Source = target });

    // AP2 after-state: scope chips, scoped boxes, inline SHA-256 validation, dialogs opened with the draft,
    // the saved-search split button, and Primary+F mapped back to the Text scope.
    private static async Task ScopesAsync(Window window, ArchivePage page, string theme)
    {
        SearchScopeChip archiveChip = C<SearchScopeChip>(page, "ArchiveSearchScope");
        SearchScopeChip readerChip = C<SearchScopeChip>(page, "ReaderFindScope");
        Require(archiveChip.Scope == SearchScope.Text && ReferenceEquals(C<SearchBox>(page, "ArchiveSearchText").InnerLeftContent, archiveChip)
            && Avalonia.Automation.AutomationProperties.GetName(archiveChip) == Texts.T("search.scope.name", Texts.T("search.scope.text")),
            "Archive scope chip lacks its Text scope or accessible name.");
        Require(!page.GetLogicalDescendants().OfType<Control>().Any(c => c.Name is "ArchiveFilenameSearchOpen" or "ReaderFilenameSearchOpen" or "ReaderHashSearchOpen" or "ArchiveSaveSearch"),
            "A separate file name / SHA-256 / save-search toolbar entry is still present.");

        // The chip menu lists the three scopes as radio entries.
        SearchBox archiveTextBox = C<SearchBox>(page, "ArchiveSearchText");
        archiveTextBox.Focus();
        Press(archiveTextBox, Avalonia.Input.Key.Down, KeyModifiers.Alt); // the field opens its chip menu
        await SettleAsync(window);
        Require(archiveChip.OpenMenu is { IsOpen: true } menu && menu.Items.OfType<MenuItem>().Select(item => item.Tag).SequenceEqual(
            new object[] { SearchScope.Text, SearchScope.FileName, SearchScope.Sha256 }), "Scope menu does not list Text, File name, SHA-256.");
        Program.CaptureWindow(window, $"smoke-unified-search-scope-menu-{theme}.png");
        MenuItem fileName = archiveChip.OpenMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, SearchScope.FileName));
        fileName.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        archiveChip.OpenMenu?.Close();
        await SettleAsync(window);
        SearchBox archiveFile = C<SearchBox>(page, "ArchiveFilenameQuery");
        Require(archiveChip.Scope == SearchScope.FileName && archiveFile.IsEffectivelyVisible && !C<SearchBox>(page, "ArchiveSearchText").IsEffectivelyVisible
            && ReferenceEquals(archiveFile.ScopeChip, archiveChip) && ReferenceEquals(archiveFile.InnerLeftContent, archiveChip)
            && C<TextBlock>(page, "ArchiveSearchScopeHint").Text == Texts.T("search.scope.filename.hint") && C<Button>(page, "SearchArchiveButton").IsEnabled,
            "File name scope did not swap the box, hint and Search action.");
        archiveFile.Text = "lamp_0";
        await SettleAsync(window);
        Program.CaptureWindow(window, $"smoke-unified-search-archive-filename-{theme}.png");

        // Enter in the File name scope opens the existing dialog with the draft already searched.
        Press(archiveFile, Avalonia.Input.Key.Enter);
        await WaitAsync(() => page.FilenameSearchWindow is { IsVisible: true });
        FilenameSearchDialog dialog = page.FilenameSearchWindow!;
        await WaitAsync(() => dialog.Session.Current is { Busy: false, StatusKey: "filename.search.ready" });
        Require(dialog.Session.Current.Text == "lamp_0" && dialog.Session.Current.Total > 0, "File name dialog did not search the field's draft.");
        dialog.Close();
        await page.FilenameSearchTask.WaitAsync(Deadline);

        // Primary+F maps back to the Text scope of the archive field.
        page.FocusArchiveTextSearch();
        await SettleAsync(window);
        Require(archiveChip.Scope == SearchScope.Text && C<SearchBox>(page, "ArchiveSearchText").IsFocused && archiveFile.Text == "lamp_0",
            "Primary+F did not return to the Text scope, or the file name draft was lost.");

        // Reader bar: SHA-256 scope validates inline with the dialog's own rule.
        Button search = UnifiedSearchEntry.ReaderScope(page, SearchScope.Sha256);
        SearchBox hash = C<SearchBox>(page, "ReaderHashQuery");
        Require(readerChip.Scope == SearchScope.Sha256 && hash.IsEffectivelyVisible && search.IsEffectivelyVisible
            && !C<Button>(page, "ReaderFindNext").IsEffectivelyVisible, "Reader SHA-256 scope did not swap box and actions.");
        hash.Text = "abc123";
        hash.Focus();
        Press(hash, Avalonia.Input.Key.Enter);
        await SettleAsync(window);
        Require(page.MediaHashSearchWindow == null && C<TextBlock>(page, "ReaderFindStatus").Text == MediaHashSearchCatalog.T("mediahash.invalid"),
            "An invalid SHA-256 draft opened the dialog or showed no reason.");
        Program.CaptureWindow(window, $"smoke-unified-search-reader-hash-invalid-{theme}.png");
        hash.Text = new string('a', 64);
        await SettleAsync(window);
        Require(C<TextBlock>(page, "ReaderFindStatus").Text == Texts.T("search.scope.hash.hint"), "Editing did not clear the refused-draft message.");
        Press(hash, Avalonia.Input.Key.Enter);
        await WaitAsync(() => page.MediaHashSearchWindow is { IsVisible: true });
        MediaHashSearchDialog hashDialog = page.MediaHashSearchWindow!;
        await WaitAsync(() => hashDialog.Operation.Current.Status is not (MediaHashSearchOperation.Status.Ready or MediaHashSearchOperation.Status.Searching));
        hashDialog.Close();
        await page.MediaHashSearchTask.WaitAsync(Deadline);
        Require(C<StackPanel>(page, "ReaderFindBar").IsVisible && hash.Text == new string('a', 64), "The scoped reader bar lost its draft after the dialog.");
        // Primary+F / Find in saved thread maps the reader bar back to Text.
        C<Button>(page, "ReaderFindClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        C<Button>(page, "ReaderFindOpen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.ThreadFindTask.WaitAsync(Deadline);
        Require(readerChip.Scope == SearchScope.Text && C<TextBox>(page, "ReaderFindQuery").IsFocused, "Reader find did not reopen in the Text scope.");
        C<Button>(page, "ReaderFindClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // Saved searches: one split button whose arrow holds "Save applied search…".
        SplitButton split = C<SplitButton>(page, "ArchiveSavedSearches");
        Require(Equals(split.Content, MeowChan.Desktop.Commands.CommandCatalog.Label("archive.saved-searches.open-manager"))
            && ((MenuFlyout)split.Flyout!).Items.OfType<MenuItem>().Select(item => item.Name).SequenceEqual(new[] { "ArchiveSaveSearch", "ArchiveSavedSearchesOpen" }),
            "Saved-search split button lacks its label or menu entries.");
        await SettleAsync(window);
        await CarryAsync(window, page, theme);
        Program.CaptureWindow(window, $"smoke-unified-search-final-{theme}.png");
        Console.WriteLine($"SMOKE unified search scopes [{theme}]: chip menu, File name dialog with draft, Primary+F to Text, SHA-256 inline validation and dialog, split button checked.");
    }

    private static SearchCarryAction Carry(SearchScopeChip chip, string name) =>
        chip.CarryActions!.Invoke().Single(action => action.Name == name);

    // Carry-over between the archive field and the reader bar: an empty target takes the query, a different one is offered.
    private static async Task CarryAsync(Window window, ArchivePage page, string theme)
    {
        SearchScopeChip archiveChip = C<SearchScopeChip>(page, "ArchiveSearchScope"), readerChip = C<SearchScopeChip>(page, "ReaderFindScope");
        SearchBox archiveText = C<SearchBox>(page, "ArchiveSearchText"), readerText = C<SearchBox>(page, "ReaderFindQuery");
        SearchCarryOffer readerOffer = C<SearchCarryOffer>(page, "ReaderFindCarryOffer"), archiveOffer = C<SearchCarryOffer>(page, "ArchiveSearchCarryOffer");
        archiveChip.Scope = SearchScope.Text;
        archiveText.Text = "";
        Require(!Carry(archiveChip, "ArchiveSearchInThread").IsEnabled, "Carrying an empty archive draft was offered.");
        archiveText.Text = "lamp";
        SearchCarryAction toThread = Carry(archiveChip, "ArchiveSearchInThread");
        Require(toThread.IsEnabled && toThread.Label == Texts.T("search.carry.toThread", "lamp"), "Archive -> thread carry missing: " + toThread.Label);
        toThread.Execute();
        await page.ThreadFindTask.WaitAsync(Deadline);
        await SettleAsync(window);
        Require(C<StackPanel>(page, "ReaderFindBar").IsVisible && readerChip.Scope == SearchScope.Text && readerText.Text == "lamp" && !readerOffer.IsVisible,
            "An empty reader bar did not take the archive query.");
        readerText.Text = "desk";
        Carry(archiveChip, "ArchiveSearchInThread").Execute();
        await SettleAsync(window);
        Require(readerOffer.IsVisible && readerOffer.PendingQuery == "lamp" && readerText.Text == "desk",
            "A different reader query was overwritten instead of offered.");
        Program.CaptureWindow(window, $"smoke-unified-search-carry-offer-{theme}.png");
        ((Button)readerOffer.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SearchCarryUse")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await page.ThreadFindTask.WaitAsync(Deadline);
        Require(readerText.Text == "lamp" && !readerOffer.IsVisible, "Use it did not apply the offered query.");
        // Reader -> archive: the archive draft "lamp" equals, so it applies; a different one is offered and Keep keeps it.
        readerText.Text = "keyboards";
        archiveText.Text = "desk";
        Carry(readerChip, "ReaderSearchInArchive").Execute();
        await SettleAsync(window);
        Require(archiveOffer.IsVisible && archiveOffer.PendingQuery == "keyboards" && archiveText.Text == "desk", "Reader -> archive overwrote a different draft.");
        ((Button)archiveOffer.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SearchCarryKeep")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(!archiveOffer.IsVisible && archiveText.Text == "desk", "Keep current changed the archive draft.");
        archiveText.Text = "";
        Carry(readerChip, "ReaderSearchInArchive").Execute();
        await SettleAsync(window);
        await WaitAsync(() => !page.IsBusy);
        Require(archiveText.Text == "keyboards" && archiveChip.Scope == SearchScope.Text && page.AppliedSearchSpec?.Text == "keyboards",
            "An empty archive field did not take and apply the reader query.");
        archiveText.Text = "";
        await page.ApplySearchAsync();
        C<Button>(page, "ReaderFindClose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await SettleAsync(window);
        Console.WriteLine($"SMOKE unified search carry-over [{theme}]: archive -> thread (empty takes, different offered, Use it), reader -> archive (Keep current, empty takes and applies) checked.");
    }

    // Carry-over between the real Archive and Media pages of the application window.
    internal static async Task MainWindowCarryAsync(MainWindow owner)
    {
        ArchivePage archive = owner.ArchivePage;
        MediaPage media = owner.MediaPageForTests ?? throw new InvalidOperationException("No media page.");
        SearchBox name = media.NameFilter;
        SearchScopeChip mediaChip = media.SearchScopeChip;
        Require(mediaChip.Scopes.SequenceEqual(new[] { SearchScope.FileName }) && mediaChip.Scope == SearchScope.FileName
            && ReferenceEquals(name.ScopeChip, mediaChip), "Media chip is not the one-scope file name chip inside the field.");
        string before = name.Text ?? "";
        name.Text = "";
        archive.SearchInMediaRequested!("lamp_1");
        await WaitAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Media);
        await WaitAsync(() => name.Text == "lamp_1");
        owner.UpdateLayout();
        Program.CaptureWindow(owner, "smoke-unified-search-media-carried.png");
        SearchCarryAction toArchive = Carry(mediaChip, "MediaSearchInArchive");
        Require(toArchive.IsEnabled && toArchive.Label == Texts.T("search.carry.toArchive", "lamp_1"), "Media -> archive carry missing.");
        SearchBox archiveFile = C<SearchBox>(archive, "ArchiveFilenameQuery");
        string archiveFileBefore = archiveFile.Text ?? "";
        archiveFile.Text = "";
        toArchive.Execute();
        await WaitAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Archive);
        SearchScopeChip archiveChip = C<SearchScopeChip>(archive, "ArchiveSearchScope");
        Require(archiveChip.Scope == SearchScope.FileName && archiveFile.Text == "lamp_1", "Media -> archive did not choose File name with the query.");
        // A different media query is offered, not replaced.
        name.Text = "other";
        archive.SearchInMediaRequested!("lamp_2");
        await WaitAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Media);
        SearchCarryOffer offer = media.SearchCarryOffer;
        Require(offer.IsVisible && offer.PendingQuery == "lamp_2" && name.Text == "other", "A different media query was overwritten.");
        offer.Hide();
        name.Text = before;
        archiveFile.Text = archiveFileBefore;
        archiveChip.Scope = SearchScope.Text;
        Console.WriteLine("SMOKE unified search carry-over [main window]: Archive -> Media, Media -> Archive (File name scope), different media query offered.");
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Unified search UI condition timed out.");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
    }
}
