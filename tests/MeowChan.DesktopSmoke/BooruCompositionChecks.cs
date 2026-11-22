using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Booru;
using MeowChan.Testing;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Root scenario "booru-composition": a real desktop session composes the Danbooru services (search service, original
// queue over the shared download queue, watcher host and scheduler, bounded preview path) over an in-memory Danbooru
// with the Danbooru plugin called in-process. The page is opened from the real Workspace menu of a
// second main window and remains hosted by that shell for the whole check.
// No socket is opened; the stub refuses every host except danbooru.donmai.us and cdn.donmai.us.
internal static class BooruCompositionChecks
{
    private static Button ResultAction(Control root, string postId) => root.GetVisualDescendants().OfType<Button>()
        .SingleOrDefault(button => button.Name == BooruWorkbenchView.LoadPrefix + postId
            || button.Name == BooruWorkbenchView.CancelLoadPrefix + postId
            || button.Name == BooruWorkbenchView.RetryLoadPrefix + postId)
        ?? throw new InvalidOperationException("Result action not found: " + postId);

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string originalLanguage = Texts.Language;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        string shots = Environment.GetEnvironmentVariable("MEOWCHAN_BOORU_SHOTS") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(shots);
        string profile = TemporaryWorkspacePaths.CreateDirectory("booru-composition-");
        string media = Path.Combine(profile, "media");
        // This scenario checks the in-memory page lifetime. Persisted tabs are covered separately.
        new Config { DownloadPath = media, AutomaticUpdateChecks = false, SaveUrlsOnExit = false, FirstRunSetupVersion = 1,
            MinimumFreeSpaceGiB = 0, CloseToTray = false, MinimizeToTray = false, WorkspaceMode = "Advanced", MaxParallelDownloads = 1,
            RestoreBooruSearchTabs = false }
            .SaveConfig(profile);
        Directory.CreateDirectory(media);
        using DanbooruStub stub = new();
        uint[] colors = { 0xFFD9534F, 0xFF5CB85C, 0xFF428BCA, 0xFFF0AD4E };
        for (int index = 0; index < colors.Length; index++)
            stub.Add(new DanbooruStubPost(8004 - index, Rating: index switch { 0 => "g", 1 => "s", 2 => "q", _ => "e" },
                General: "cat_ears solo", Restricted: index == 3, Color: colors[index]));
        stub.Publish("cat_ears", 8004, 8003, 8002, 8001);
        stub.HoldOriginals(); // the explicit load stays visibly queued until the end
        using DanbooruTestBinding binding = new(stub, enabled: false);
        DesktopSessionHost? host = null;
        MainWindow? shell = null;
        BooruPage? page = null;
        try
        {
            Texts.SetLanguage("en");
            await using DesktopSessionHost ownedHost = new(profile, Array.Empty<IImageBoard>())
            {
                BooruBindingForTests = () => binding.Binding,
                BooruSchedulerOptionsForTests = new DanbooruWatcherSchedulerOptions { Interval = TimeSpan.FromMinutes(30), CheckStartSpacing = TimeSpan.Zero }
            };
            host = ownedHost;
            await using MainWindow ownedShell = new(host);
            shell = ownedShell;
            shell.Show(owner);
            await host.StartAsync().WaitAsync(Wait);
            await Program.WaitUntilAsync(() => shell.FindControl<Button>("AddUrlButton")!.IsEnabled);
            // The second main window applies its profile's theme preference; the light screenshots need light.
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            MenuItem menu = shell.FindControl<MenuItem>("WorkspaceMenuItem")!;

            // Plugin disabled: nothing is constructed and the entry stays hidden.
            Require(host.BooruComposition is null && host.BooruServices is null, "Danbooru was composed although the plugin is disabled.");
            await RequireEntryVisibleAsync(menu, visible: false);
            // A Danbooru address in the Add line while the plugin is disabled: the normal "no source" feedback plus the hint, no request.
            await SubmitAddLineAsync(shell, "https://danbooru.donmai.us/posts/8003?q=secret");
            Require(Find<SelectableTextBlock>(shell, "AdmissionRepairText").Text?.Contains("enable the Danbooru plugin under Plugins") == true,
                "The disabled Danbooru address lacks the enable hint: " + Find<SelectableTextBlock>(shell, "AdmissionRepairText").Text);
            Require(stub.Requests.IsEmpty && host.BooruServices is null && page is null, "A disabled Danbooru address caused a request or a page.");
            Require(shell.FindControl<TextBox>("UrlInput")!.Text == "https://danbooru.donmai.us/posts/8003?q=secret", "A refused address was cleared from the Add line.");
            shell.FindControl<TextBox>("UrlInput")!.Text = string.Empty;
            // Enabling the plugin in the running session composes the services and shows the entry.
            binding.SetEnabled(true);
            await Program.WaitUntilAsync(() => host.BooruServices != null, () => "no composition after enabling the plugin");
            Require(host.BooruComposition!.WatcherHost.Scheduler.GetSnapshot().Running, "The watcher scheduler is not running.");
            await RequireEntryVisibleAsync(menu, visible: true);
            Require(stub.Requests.IsEmpty, "Composition or the menu sent a request.");

            page = await OpenAsync(shell, menu);
            Require(ReferenceEquals(shell.BooruPageForTests, page), "The shell did not retain the opened Booru page.");
            Require(ReferenceEquals(shell.FindControl<ContentControl>("BooruPageHost")!.Content, page),
                "The Booru page is not hosted by the shell page host.");
            await Program.WaitUntilAsync(() => shell.ActualThemeVariant == ThemeVariant.Light, () => "light theme not applied");
            Require(stub.Requests.IsEmpty, "Opening the page sent a request.");

            // The presentation switch only hides the hosted page. A page with the same service
            // identity must survive navigation and retain its unsent draft.
            page.ShowQuery("draft_kept_across_hide");
            BooruPage retainedPage = page;
            Click(shell.FindControl<Button>("DownloadsNavigationButton")!);
            await Program.WaitUntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Downloads
                && !shell.FindControl<ContentControl>("BooruPageHost")!.IsVisible,
                () => "ordinary navigation did not leave Booru");
            Require(ReferenceEquals(shell.BooruPageForTests, retainedPage)
                && retainedPage.Model.Search.View.Input == "draft_kept_across_hide",
                "Ordinary navigation replaced the page or discarded its draft.");
            page = await OpenAsync(shell, menu);
            Require(ReferenceEquals(page, retainedPage), "Returning to Booru replaced the same-service page.");
            Require(shell.SessionHost.Config.ShowBooruTools && SmokeNavigation.Route(shell, MainWindow.WorkspacePage.Booru) is "footer" or "more",
                "Booru actions or its navigation entry are missing while the page is open.");
            Require(shell.FindControl<CheckBox>("ShowBooruToolsToggle") == null, "The footer still has the Show Booru check box.");
            await SetBooruActionsAsync(shell, false);
            SmokeNavigation.GoTo(shell, MainWindow.WorkspacePage.Downloads);
            await Program.WaitUntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Downloads
                && !shell.FindControl<ContentControl>("BooruPageHost")!.IsVisible
                && ReferenceEquals(shell.BooruPageForTests, page)
                && !shell.SessionHost.Config.ShowBooruTools
                && SmokeNavigation.Route(shell, MainWindow.WorkspacePage.Booru) is not ("footer" or "more"), () => "hiding Booru did not retain the hosted page");
            Require(page.Model.Search.View.Input == "draft_kept_across_hide",
                "Hiding Booru discarded the draft from the same page composition.");
            await SetBooruActionsAsync(shell, true);
            await Program.WaitUntilAsync(() => shell.SessionHost.Config.ShowBooruTools, () => "showing Booru did not restore the setting");
            Require(SmokeNavigation.Route(shell, MainWindow.WorkspacePage.Booru) is "footer" or "more", "showing Booru did not restore its navigation entry");
            await RequireEntryVisibleAsync(menu, visible: true);
            page = await OpenAsync(shell, menu);
            Require(ReferenceEquals(page, retainedPage), "Showing Booru replaced the same-service page.");
            Require(page.Model.Search.View.Input == "draft_kept_across_hide",
                "Showing Booru did not retain the same-service draft.");
            Require(page.TryClearQuery(), "The retained page no longer accepts draft changes.");

            // A composition identity change must replace, rather than revive, the old page. The
            // binding event supplies the real shell callback; the replacement ports are inert and
            // exist only to make the page identity transition observable without provider traffic.
            BooruPage oldPage = page;
            oldPage.ShowQuery("draft_discarded_on_service_replacement");
            BooruUiHarness replacementHarness = new();
            BooruWorkbenchServices originalServices = host.BooruServices!;
            host.BooruServices = replacementHarness.Services;
            binding.SetEnabled(false);
            await Program.WaitUntilAsync(() => shell.BooruPageForTests is { } candidate
                && !ReferenceEquals(candidate, oldPage)
                && ReferenceEquals(candidate.Services, replacementHarness.Services),
                () => "service replacement did not create a new hosted page");
            page = shell.BooruPageForTests!;
            Require(oldPage.Model.Search.View.Input == "draft_discarded_on_service_replacement"
                && !oldPage.TryClearQuery(), "The old page composition remained live after service replacement.");
            Require(page.Model.Search.View.Input == string.Empty, "A replacement page inherited the old service draft.");
            BooruPage replacementPage = page;
            replacementPage.ShowQuery("draft_discarded_on_service_loss");
            // Removing a test override does not publish BooruChanged. Redeliver the disabled
            // binding notification so the real shell reconciliation sees the actual service loss.
            host.BooruServices = null;
            binding.SetEnabled(false);
            await Program.WaitUntilAsync(() => host.BooruServices is null && shell.BooruPageForTests is null
                && shell.FindControl<ContentControl>("BooruPageHost")!.Content is null
                && !shell.FindControl<ContentControl>("BooruPageHost")!.IsVisible
                && shell.SelectedPage == MainWindow.WorkspacePage.Downloads,
                () => "service loss did not dispose the page and navigate to Downloads");
            Require(replacementPage.Model.Search.View.Input == "draft_discarded_on_service_loss"
                && !replacementPage.TryClearQuery() && !replacementPage.Model.Search.CanSubmit,
                "The unavailable page composition remained live after service loss.");
            await RequireEntryVisibleAsync(menu, visible: false);

            binding.SetEnabled(true);
            await Program.WaitUntilAsync(() => host.BooruComposition?.IsAvailable == true && host.BooruServices != null
                && host.BooruComposition.WatcherHost.Scheduler.GetSnapshot().Running,
                () => "the production Booru composition did not become available after re-enable");
            // A new page is not yet a rendered view. Re-enter through the real menu and wait for
            // its shell host, top level and selected search tab before looking up BooruInput.
            page = await OpenAsync(shell, menu);
            Require(!ReferenceEquals(page, oldPage) && !ReferenceEquals(page, replacementPage)
                && ReferenceEquals(page.Services, originalServices),
                "Recomposition revived an old page or did not restore the production services.");
            Require(page.Model.Search.View.Input == string.Empty, "Recomposition retained a stale page draft.");
            Require(stub.Requests.IsEmpty && replacementHarness.ProviderRequests == 0
                && replacementHarness.Previews.Requests == 0 && replacementHarness.Queue.Loads.Count == 0,
                "Navigation, hide/show, replacement or service loss submitted work.");

            // ---- search through the real adapter, worker and bounded transport ----------------------------------------------
            TextBox searchInput = Find<TextBox>(page, BooruWorkbenchView.InputName);
            searchInput.Text = "cat_ears";
            await Program.WaitUntilAsync(() => AutomationProperties.GetHelpText(searchInput)!.Contains("Draft: cat_ears"),
                () => "the composed workbench did not show the unsent query as a draft");
            int previousSearchTab = page.View.SearchTabs.ActiveId;
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            Require(page.View.SearchTabs.ActiveId != previousSearchTab && page.View.SearchTabs.ExtraCount == 1,
                "The default Search action did not activate a new tab.");
            await Program.WaitUntilAsync(() => Cards(page).Count == 4 && Previews(page) == 4,
                () => $"results: cards={Cards(page).Count} previews={Previews(page)} status={BooruText.SearchStatus(page.Model.Search.View, false)}");
            Require(stub.Requests.Count(url => url.StartsWith("https://danbooru.donmai.us/", StringComparison.Ordinal)) == 1
                && stub.Requests.Contains("https://danbooru.donmai.us/posts.json?tags=cat_ears&page=1&limit=20"),
                "The search was not exactly one provider request with the user's query: " + string.Join(", ", stub.Requests));
            Require(stub.PreviewRequests == 4 && stub.OriginalRequests == 0 && stub.SampleRequests == 0,
                $"Previews were not fetched through the preview path only (previews={stub.PreviewRequests}, originals={stub.OriginalRequests}, samples={stub.SampleRequests}).");
            Require(!Find<Button>(page, BooruWorkbenchView.LoadPrefix + "8001").IsEnabled, "A restricted post can be loaded.");
            Capture(shell, shots, "composition-results-light");

            // ---- copy link through the shell's own top-level clipboard (the harness fixture; opening would start a browser: covered with fakes in booru-workbench) ----
            const string copiedPostUrl = "https://danbooru.donmai.us/posts/8004";
            Button copyLink = Find<Button>(page, BooruWorkbenchView.CopyLinkPrefix + "8004");
            bool copiedLink = await ClipboardChecks.CheckIsolatedCopyAsync(shell, () =>
            {
                Click(copyLink);
                return Task.CompletedTask;
            }, new[] { copiedPostUrl }, new[] { "cat_ears", "draft_kept_across_hide", "draft_discarded_on_service_replacement", "draft_discarded_on_service_loss" }, exactText: copiedPostUrl);
            if (copiedLink)
                await Program.WaitUntilAsync(() => Text(page).Contains("Link copied: " + copiedPostUrl), () => "copy note missing: " + Text(page));
            Require(Find<Button>(page, BooruWorkbenchView.OpenPostPrefix + "8004").IsEffectivelyEnabled, "Open post in browser is missing on the result card.");
            Require(stub.Requests.Count(url => url.StartsWith("https://danbooru.donmai.us/", StringComparison.Ordinal)) == 1, "Copying a link sent a request.");
            Capture(shell, shots, "composition-link-copied-light");

            // ---- one explicit original into the shared queue ----------------------------------------------------------------
            Click(Find<Button>(page, BooruWorkbenchView.LoadPrefix + "8004"));
            await Program.WaitUntilAsync(() => Text(page).Contains("Added to the download queue."), () => "queued notice missing");
            Require(host.BooruComposition.Originals.ActiveCount == 1, "Load original did not admit exactly one queue job.");
            Button queuedAction = ResultAction(page, "8004");
            Require(queuedAction.Name == BooruWorkbenchView.CancelLoadPrefix + "8004" || !queuedAction.IsEnabled,
                "The result action did not enter its queued/cancelled state after loading.");
            BooruPostRecord queued = host.BooruComposition.Catalog.Read().Posts.Single(post => post.PostId == "8004");
            Require(queued.OriginalIntent == BooruOriginalIntent.Requested && queued.Findings.Any(finding => finding.Kind == BooruFindingKind.Search),
                "The catalog does not record the requested original with its search finding.");
            Require(queued.Findings.Single(finding => finding.Kind == BooruFindingKind.Search).Query == "cat_ears",
                "The search finding does not keep the query that found the post.");
            Require(stub.SampleRequests == 0, "A sample was requested.");
            Capture(shell, shots, "composition-queued-light");

            // ---- watcher through the real scheduler -------------------------------------------------------------------------
            Click(Find<Button>(page, BooruWorkbenchView.CreateWatcherName));
            await Program.WaitUntilAsync(() => Text(page).Contains("Watcher created for"), () => "created notice missing");
            Require(host.BooruComposition.Originals.ActiveCount == 1, "Creating the watcher queued existing posts.");
            TabControl tabs = Find<TabControl>(page, BooruWorkbenchView.TabsName);
            object? searchTab = tabs.SelectedItem;
            Click(Find<Button>(page, BooruWorkbenchView.OpenWatchersName));
            await Program.WaitUntilAsync(() => WatcherCards(page).Count == 1, () => "watcher card missing");
            string watcherId = host.BooruComposition.WatcherHost.Scheduler.GetSnapshot().Watchers.Single().WatcherId;
            // The scheduler's own first check sets the baseline (from now); then one explicit check.
            await Program.WaitUntilAsync(() => host.BooruComposition.WatcherHost.Scheduler.GetStatus(watcherId)?.BaselineEstablished == true
                && Find<Button>(page, BooruWorkbenchView.CheckPrefix + watcherId).IsEffectivelyEnabled, () => "baseline not established");
            Click(Find<Button>(page, BooruWorkbenchView.CheckPrefix + watcherId));
            await Program.WaitUntilAsync(() => Text(page).Contains("Check finished: no new posts."), () => "check result missing: " + Text(page));
            Require(host.BooruComposition.Originals.ActiveCount == 1, "The watcher downloaded posts that existed before it.");
            Capture(shell, shots, "composition-watchers-light");

            // ---- dark theme -------------------------------------------------------------------------------------------------
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            await Program.WaitUntilAsync(() => shell.ActualThemeVariant == ThemeVariant.Dark, () => "dark theme not applied");
            Capture(shell, shots, "composition-watchers-dark");
            tabs.SelectedItem = searchTab;
            await Program.WaitUntilAsync(() => Cards(page).Count == 4 && Previews(page) == 4, () => "dark results not shown");
            Capture(shell, shots, "composition-results-dark");

            // ---- Danbooru addresses in the Add line (the shared path of typing, pasting, the clipboard monitor and dropping) --------
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            await Program.WaitUntilAsync(() => shell.ActualThemeVariant == ThemeVariant.Light, () => "light theme not restored for Add-line capture");
            int before = stub.Requests.Count;
            TextBox urlInput = shell.FindControl<TextBox>("UrlInput")!;
            int resultsTab = page.View.SearchTabs.ActiveId;
            // A Safebooru search address opens an editable query with the visible rating:g term.
            await SubmitAddLineAsync(shell, "https://safebooru.donmai.us/posts?tags=cat_ears&page=2");
            await Program.WaitUntilAsync(() => Find<TextBox>(page, BooruWorkbenchView.InputName).Text == "cat_ears rating:g",
                () => "search field: " + Find<TextBox>(page, BooruWorkbenchView.InputName).Text);
            Require(page.View.SearchTabs.ActiveId != resultsTab && page.View.SearchTabs.ViewOf(resultsTab)?.Rows.Length == 4,
                "The address failed to open a new tab while preserving the previous results.");
            Require(stub.Requests.Count == before && urlInput.Text == string.Empty && shell.SelectedPage == MainWindow.WorkspacePage.Booru && shell.BooruPageForTests is not null,
                "A search address requested something, kept its text or opened another page.");
            // A Danbooru address with an unencoded comma list stays one entry; the query is shown exactly as written.
            await SubmitAddLineAsync(shell, "https://danbooru.donmai.us/posts?tags=cat_ears+rating:g,s");
            await Program.WaitUntilAsync(() => Find<TextBox>(page, BooruWorkbenchView.InputName).Text == "cat_ears rating:g,s",
                () => "search field: " + Find<TextBox>(page, BooruWorkbenchView.InputName).Text);
            Require(stub.Requests.Count == before, "A comma search address requested something.");
            // A bare site address: empty field, still nothing requested.
            await SubmitAddLineAsync(shell, "https://danbooru.donmai.us/");
            await Program.WaitUntilAsync(() => Find<TextBox>(page, BooruWorkbenchView.InputName).Text == string.Empty, () => "field not emptied");
            Require(stub.Requests.Count == before, "A site address requested something.");
            // A Safebooru post address with a search context: one post read on Danbooru, one one-shot original in the shared queue.
            await SubmitAddLineAsync(shell, "https://safebooru.donmai.us/posts/8003?q=secret");
            await Program.WaitUntilAsync(() => AdmissionSummary(shell).StartsWith("1 added", StringComparison.Ordinal) && urlInput.Text == string.Empty,
                () => "summary: " + AdmissionSummary(shell) + " | input=" + urlInput.Text + " | requests=" + string.Join(",", stub.Requests.Skip(before)));
            Require(stub.Requests.Skip(before).Count(url => url.StartsWith("https://danbooru.donmai.us/", StringComparison.Ordinal)) == 1
                && stub.Requests.Contains("https://danbooru.donmai.us/posts/8003.json"),
                "The post address was not exactly one post read: " + string.Join(", ", stub.Requests.Skip(before)));
            Require(!stub.Requests.Any(url => url.Contains("secret", StringComparison.Ordinal)),
                "The search context of the post address leaked into a request.");
            Require(host.BooruComposition.Originals.ActiveCount == 2, "The post address did not admit exactly one more queue job.");
            Require(host.BooruComposition.Catalog.Read().Posts.Single(post => post.PostId == "8003").Findings.Any(finding => finding.Kind == BooruFindingKind.OneShot),
                "The post address is not recorded as a one-shot.");
            Require(host.BooruComposition.WatcherHost.Scheduler.GetSnapshot().Watchers.Count == 1, "An address created a watcher.");
            Capture(shell, shots, "composition-add-line-light");

            // ---- the held original completes; the row's local state follows the catalog ------------------------------------
            stub.ReleaseOriginals();
            await Program.WaitUntilAsync(() => host.BooruComposition.Catalog.Read().Posts.Where(post => post.PostId is "8004" or "8003").All(post => post.LocalAssetId != null)
                && host.BooruComposition.Catalog.Read().Posts.Count(post => post.PostId is "8004" or "8003") == 2,
                () => "the originals were not linked");
            Require(stub.OriginalRequests == 2, "An original was transferred more than once.");
            Require(stub.Violations.IsEmpty, "Stub violations: " + string.Join(", ", stub.Violations));
            Require(!Text(page).Contains("booru.ui.") && !Text(page).Contains("booru.watcher."), "A raw key is visible.");

            BooruPage closingPage = page;
            int requests = stub.Requests.Count;
            await CloseAsync(shell);
            Require(shell.BooruPageForTests is null
                && shell.FindControl<ContentControl>("BooruPageHost")!.Content is null
                && !closingPage.TryClearQuery() && !closingPage.Model.Search.CanSubmit,
                "Shell shutdown did not end the hosted page/view lifetime.");
            page = null;
            shell = null;
            await host.DisposeAsync().AsTask().WaitAsync(Wait);
            Require(host.BooruServices is null, "The Danbooru ports outlived the session.");
            host = null;
            Require(stub.Requests.Count == requests, "A request happened during shutdown.");
            Console.WriteLine("SMOKE booru composition: plugin disabled -> nothing composed, entry hidden, a Danbooru address in the Add line only shows the enable hint (no request); Add line with the plugin enabled: Safebooru search address prefills the hosted page with a visible rating:g, a Danbooru comma list stays one entry, a bare site address empties the field, none requests anything; a Safebooru post address with a search context sends exactly one Danbooru post read without the context and queues one one-shot original, no watcher; enabled at runtime -> composed, entry shown; page from the Workspace menu hosted in the shell; one exact search request through the real adapter and in-process plugin; 4 result cards with 4 previews via the bounded preview path (no sample/original); restricted post not loadable; copy link through the shell clipboard path; one Load original -> exactly one queue job, Requested intent with search finding that keeps the query; watcher created, scheduler baseline, Check now -> no new posts; light and dark screenshots in " + shots + "; held original completed with one transfer; navigation and hide/show retained the same page draft; service replacement discarded the old page; service loss disposed it and returned to Downloads; re-enable created a fresh rendered page; orderly shell/page shutdown without requests.");
        }
        finally
        {
            if (shell != null) await CloseAsync(shell);
            if (shell != null) { await shell.DisposeAsync(); shell = null; }
            if (host != null) await host.DisposeAsync().AsTask().WaitAsync(Wait);
            stub.ReleaseOriginals();
            Application.Current!.RequestedThemeVariant = originalTheme;
            Texts.SetLanguage(originalLanguage);
            owner.Activate();
            try { Directory.Delete(profile, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Types into the Add line and presses Add, then waits until the busy state is over.
    private static async Task SubmitAddLineAsync(MainWindow shell, string text)
    {
        // The Add line belongs to Downloads. Return there before every submission so this helper never
        // races a hidden shell control while the hosted Booru page is selected.
        await SmokeNavigation.GoToAsync(shell, MainWindow.WorkspacePage.Downloads);
        shell.FindControl<TextBox>("UrlInput")!.Text = text;
        Button add = shell.FindControl<Button>("AddUrlButton")!;
        await Program.WaitUntilAsync(() => add.IsEffectivelyVisible && add.IsEffectivelyEnabled,
            () => "Add is not ready: visible=" + add.IsEffectivelyVisible + ", enabled=" + add.IsEnabled);
        Click(add);
        await Program.WaitUntilAsync(() => !add.IsEnabled || shell.FindControl<Border>("AdmissionResultsPanel")!.IsVisible
            || (shell.SelectedPage == MainWindow.WorkspacePage.Booru && shell.BooruPageForTests is not null), () => "Add did not start");
        await Program.WaitUntilAsync(() => add.IsEnabled, () => "Add stayed busy");
        await SettleAsync();
    }

    private static string AdmissionSummary(MainWindow shell) => shell.FindControl<TextBlock>("AdmissionResultsTitle")?.Text ?? string.Empty;

    private static MenuItem Entry(MenuItem menu) => menu.Items.OfType<MenuItem>()
        .FirstOrDefault(item => item.Header as string == MeowChan.Desktop.Commands.CommandCatalog.Label("booru.open"))
        ?? throw new InvalidOperationException("The Danbooru menu entry does not exist.");

    private static async Task RequireEntryVisibleAsync(MenuItem menu, bool visible)
    {
        MenuItem entry = Entry(menu);
        menu.IsSubMenuOpen = true;
        await SettleAsync();
        Require(entry.IsVisible == visible, "The Danbooru entry visibility is " + entry.IsVisible + ", expected " + visible + ".");
        menu.IsSubMenuOpen = false;
        await SettleAsync();
    }

    // "Show Booru actions" in Settings > Appearance > Footer (F1), saved like any setting; returns once the shell applied it.
    private static async Task SetBooruActionsAsync(MainWindow shell, bool on)
    {
        SmokeNavigation.GoTo(shell, MainWindow.WorkspacePage.Settings);
        await Program.WaitUntilAsync(() => shell.SettingsPageVisible);
        SettingsEditor settings = shell.SettingsEditor;
        settings.ShowBooruActions = on;
        settings.FindControl<Button>("SaveSettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !settings.IsBusy && shell.SessionHost.Config.ShowBooruTools == on,
            () => "Show Booru actions was not applied: " + shell.SessionHost.Config.ShowBooruTools);
        await SettleAsync(shell);
    }

    private static async Task<BooruPage> OpenAsync(MainWindow shell, MenuItem menu)
    {
        IClassicDesktopStyleApplicationLifetime lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        Window[] windowsBefore = lifetime.Windows.ToArray();
        MenuItem entry = Entry(menu);
        menu.IsSubMenuOpen = true;
        await SettleAsync(shell);
        Require(entry.IsVisible && entry.IsEnabled && entry.Command?.CanExecute(null) == true, "The Danbooru entry is not available.");
        menu.IsSubMenuOpen = false;
        entry.Command!.Execute(null);
        await Program.WaitUntilAsync(() => shell.SelectedPage == MainWindow.WorkspacePage.Booru
            && shell.BooruPageForTests is { IsEffectivelyVisible: true } candidate
            && ReferenceEquals(shell.FindControl<ContentControl>("BooruPageHost")!.Content, candidate), () => "page not shown");
        BooruPage page = shell.BooruPageForTests!;
        await SettleAsync(shell);
        await Program.WaitUntilAsync(() => ReferenceEquals(TopLevel.GetTopLevel(page.View), shell)
            && page.View.GetVisualDescendants().OfType<TextBox>().Any(input => input.Name == BooruWorkbenchView.InputName
                && input.IsEffectivelyVisible), () => "the hosted Booru view/search tab did not materialize");
        Require(ReferenceEquals(page.Content, page.View) && ReferenceEquals(page.Services, shell.SessionHost.BooruServices),
            "The shell hosted another view or service bundle.");
        entry.Command.Execute(null);
        await SettleAsync(shell);
        Require(ReferenceEquals(shell.BooruPageForTests, page)
            && ReferenceEquals(shell.FindControl<ContentControl>("BooruPageHost")!.Content, page),
            "Repeated navigation replaced the same-service page.");
        Require(lifetime.Windows.Count == windowsBefore.Length && windowsBefore.All(window => lifetime.Windows.Contains(window)),
            "The page command opened or closed a top-level window.");
        return page;
    }

    private static async Task CloseAsync(MainWindow shell)
    {
        var close = typeof(MainWindow).GetMethod("CloseApplicationAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)close.Invoke(shell, null)!).WaitAsync(Wait);
        await Program.WaitUntilAsync(() => !shell.IsVisible);
    }

    // Shell XAML names and the programmatic workbench live in different namescopes.
    // Search only the current hosted view, after layout; never read a detached/old page or
    // the inactive TabItem.Content as evidence of a visible control.
    private static IEnumerable<Control> Controls(Control root) =>
        (root is BooruPage page ? page.View : root).GetVisualDescendants().OfType<Control>();

    private static T Find<T>(Control root, string name) where T : Control =>
        Controls(root).OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Control not found: " + name);

    private static List<Border> Cards(Control root) => Named(root, "BooruResult:");
    private static List<Border> WatcherCards(Control root) => Named(root, "BooruWatcher:");

    private static List<Border> Named(Control root, string prefix) => Controls(root).OfType<Border>()
        .Where(border => border.Name is { } name && name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    private static int Previews(Control root) => Controls(root).OfType<Image>().Count(image => image.Source is Bitmap);

    private static string Text(Control root) => string.Join('\n', Controls(root).Where(control => control.IsEffectivelyVisible)
        .Select(control => control switch
        {
            TextBlock block => block.Text,
            ContentControl { Content: string content } => content,
            _ => null
        }).Where(text => !string.IsNullOrEmpty(text)));

    private static void Click(Button button)
    {
        Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "Button is not clickable: " + button.Name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Capture(Window window, string directory, string name)
    {
        window.UpdateLayout();
        double scale = window.RenderScaling;
        PixelSize size = new((int)Math.Ceiling(window.Bounds.Width * scale), (int)Math.Ceiling(window.Bounds.Height * scale));
        using RenderTargetBitmap bitmap = new(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        string path = Path.Combine(directory, "booru-" + name + ".png");
        bitmap.Save(path);
        Console.WriteLine("SMOKE booru screenshot: " + path + " (" + size + ")");
    }

    private static async Task SettleAsync(Window? window = null)
    {
        await Dispatcher.UIThread.InvokeAsync(() => window?.UpdateLayout(), DispatcherPriority.Background);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
