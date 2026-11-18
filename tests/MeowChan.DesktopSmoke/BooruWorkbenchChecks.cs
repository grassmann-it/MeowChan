using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopSmoke;

// Root scenario "booru-workbench": the hosted Danbooru page opened through its real app-menu command over
// fake ports. No provider, queue, scheduler or network exists in this run; every fake counts its calls.
internal static partial class BooruWorkbenchChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        string originalLanguage = Texts.Language;
        ThemeVariant? originalTheme = Application.Current!.RequestedThemeVariant;
        string shots = Environment.GetEnvironmentVariable("MEOWCHAN_BOORU_SHOTS") ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(shots);
        BooruPage? page = null;
        // A null override must keep following the production composition after this fixture ends.
        BooruWorkbenchServices? overrideBefore = (BooruWorkbenchServices?)ServicesOverride.GetValue(owner.SessionHost);
        string modeBefore = owner.SessionHost.Config.WorkspaceMode;
        bool showBefore = owner.SessionHost.Config.ShowBooruTools;
        Exception? primaryFailure = null;
        try
        {
            Texts.SetLanguage("en");
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            // Focused fixture scene: MEOWCHAN_BOORU_WORKBENCH_ONLY=1 plus
            // MEOWCHAN_BOORU_VIEWER_CHECKS=only. "skip" replays the older workbench checks
            // without the viewer fixture, so unrelated native-repair failures can be compared.
            string? viewerChecks = Environment.GetEnvironmentVariable("MEOWCHAN_BOORU_VIEWER_CHECKS");
            if (viewerChecks != "skip") await BooruResultViewerChecks.RunAsync(owner);
            if (viewerChecks == "only") return;
            MenuItem menu = owner.FindControl<MenuItem>("WorkspaceMenuItem")!;
            await CheckMenuHiddenWithoutPortsAsync(owner, menu);

            BooruUiHarness h = new();
            h.Serve(pages: 3, perPage: 3);
            h.Watchers.Seed(
                BooruUiFixtures.Watcher("w-active", "cat blue_eyes", clock: h.Clock, hits: 6, queued: 5) with { LatestPostId = "9001" },
                BooruUiFixtures.Watcher("w-paused", "landscape", BooruWatcherStatus.Paused, h.Clock, hits: 2, queued: 2),
                BooruUiFixtures.Watcher("w-cooling", "scenery sky", BooruWatcherStatus.Cooldown, h.Clock,
                    cooldownUntil: h.Clock.GetUtcNow() + TimeSpan.FromMinutes(7), hits: 1, queued: 1),
                BooruUiFixtures.Watcher("w-error", "ruins", BooruWatcherStatus.Error, h.Clock, errorCode: "http-network"));
            owner.SessionHost.BooruServices = h.Services;
            // The old standalone tool exposed all actions. Exercise that same complete surface on
            // the page through its configured Advanced view, never by overriding a visibility gate.
            await owner.SessionHost.SaveViewPreferencesAsync(config =>
            {
                config.WorkspaceMode = "Advanced";
                config.ShowBooruTools = true;
            });
            Reconcile.Invoke(owner, null);

            page = await OpenPageAsync(owner, menu);
            Require(h.Search.Requests == 0 && h.Queue.Loads.Count == 0 && h.Watchers.Checks == 0,
                "Opening the page made a provider, queue or check request.");
            Require(h.Watchers.Calls.All(call => call == "list"), "Opening the page did more than read the watcher list.");
            Require(Text(page).Contains("Nothing is requested until you do."), "Idle state does not say that nothing is requested.");
            Require(Text(page).Contains("adds no rating filter") || Text(page).Contains("no rating filter"),
                "The no-rating-filter note is missing.");
            Require(!Text(page).Contains("18+"), "An 18+ notice appeared.");
            // The former tool caption is now exactly one page heading, not another window's
            // chrome. The body must not repeat it, and the hosted page must fit the shell viewport.
            string title = page.View.TitleText;
            Require(!string.IsNullOrEmpty(title)
                && page.View.Header.GetVisualDescendants().OfType<TextBlock>().Count(block => block.Text == title
                    && block.Classes.Contains("page-title")) == 1
                && !page.View.Body.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == title),
                "The page heading is missing or repeated in the body.");
            ContentControl pageHost = owner.FindControl<ContentControl>("BooruPageHost")!;
            Require(page.Bounds.Width > 0 && page.Bounds.Height > 0
                && page.Bounds.Width <= pageHost.Bounds.Width + 1 && page.Bounds.Height <= pageHost.Bounds.Height + 1,
                "The Danbooru page does not fit its shell viewport: " + page.Bounds + " in " + pageHost.Bounds);
            if (owner.Screens.ScreenFromWindow(owner) is { } screen)
                Require(owner.Bounds.Height * owner.RenderScaling <= screen.WorkingArea.Height + 1
                    && owner.Bounds.Width * owner.RenderScaling <= screen.WorkingArea.Width + 1,
                    "The Danbooru shell is larger than the working area: " + owner.Bounds + " on " + screen.WorkingArea);

            // ---- search: the query is visible exactly as sent -------------------------------------------------------
            TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
            await CheckSearchRowResizeAsync(owner, page, input, h, shots);
            await CheckInvalidInputAsync(page, input);
            input.Text = "cat blue_eyes";
            await Program.WaitUntilAsync(() => Find<SelectableTextBlock>(page, BooruWorkbenchView.QueryName).Text == "Query sent to Danbooru: cat blue_eyes",
                () => "query line: " + Find<SelectableTextBlock>(page, BooruWorkbenchView.QueryName).Text);
            Require(h.Search.Requests == 0, "Typing into the field sent a request.");
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Count == 3 && Previews(page) == 3,
                () => "cards=" + Cards(page, "BooruResult:").Count + " previews=" + Previews(page));
            Require(h.Search.Searches.Single() == ("cat blue_eyes", 1), "The search did not send the exact query.");
            Require(!h.Search.Searches[0].Query.Contains("rating"), "A rating term was added to the query.");
            string text = Text(page);
            foreach (string expected in new[] { "Post #101", "Danbooru rating", "Danbooru score", "Original available",
                "Source tags (from Danbooru, not your own tags)", "Page 1 of at most 5" })
                Require(text.Contains(expected), "Result card lacks '" + expected + "'.");
            // I8: facts as a definition list (values named "term: value"), no address text line, tags as chips in Danbooru order.
            Grid details = Find<Grid>(page, BooruWorkbenchView.DetailsPrefix + "101");
            string[] detailNames = details.Children.OfType<TextBlock>().Select(block => AutomationProperties.GetName(block) ?? string.Empty).ToArray();
            Require(detailNames.Any(name => name.StartsWith("Danbooru rating: General", StringComparison.Ordinal)) && detailNames.Contains("Danbooru score: 12")
                && details.ColumnDefinitions.Count == 2,
                "The post facts are not a definition list: " + string.Join(" | ", detailNames));
            Require(!text.Contains("https://danbooru.donmai.us/posts/101"), "The post address is still shown as a text line.");
            Button[] chips101 = page.View.GetVisualDescendants().OfType<Button>().Where(button => button.Name?.StartsWith(BooruWorkbenchView.ResultTagPrefix + "101:", StringComparison.Ordinal) == true).ToArray();
            Require(chips101.Select(button => button.Name![(BooruWorkbenchView.ResultTagPrefix.Length + 4)..]).SequenceEqual(new[] { "sky", "cat", "blue_eyes" })
                && chips101.All(button => button.Focusable && button.IsTabStop && button.Classes.Contains("chip-source-button")
                    && button.Content is ChipOutline { IsDashed: true })
                && AutomationProperties.GetName(chips101[0]) == "Add source tag sky (Artist) to the search",
                "The source tag chips are not in Danbooru order or not named actions: " + string.Join(",", chips101.Select(button => button.Name)));
            WrapPanel actionRow = Find<WrapPanel>(page, "BooruResultActions:101");
            Button gallery = Find<Button>(page, "BooruView:101");
            Require(actionRow.Children.OfType<Button>().Select(button => button.Name).SequenceEqual(new[]
                    { BooruWorkbenchView.LoadPrefix + "101", BooruWorkbenchView.OpenPostPrefix + "101", BooruWorkbenchView.CopyLinkPrefix + "101" })
                && gallery.Parent is WrapPanel galleryActions && galleryActions.Classes.Contains("workspace-toolbar")
                && Find<Button>(page, BooruWorkbenchView.LoadPrefix + "101").Content as string == "Download original",
                "The result-card actions are not compact, grouped, or clearly named.");
            Require(Find<Button>(page, BooruWorkbenchView.PreviousName).IsVisible == false
                || !Find<Button>(page, BooruWorkbenchView.PreviousName).IsEnabled, "Previous page enabled on page 1.");
            Capture(owner, shots, "search-light");

            // ---- post link actions: canonical page only, explicit clicks, keyboard reachable --------------------------------
            Require(h.Links.Opened.Count == 0 && h.Links.Copied.Count == 0, "Showing results opened or copied a link.");
            Button openPost = Find<Button>(page, BooruWorkbenchView.OpenPostPrefix + "101");
            Button copyPost = Find<Button>(page, BooruWorkbenchView.CopyLinkPrefix + "101");
            Require(openPost.Focusable && openPost.IsTabStop && copyPost.Focusable && copyPost.IsTabStop, "The link buttons are not keyboard reachable.");
            Require(AutomationProperties.GetName(openPost) == "Open post 101 in the browser"
                && AutomationProperties.GetName(copyPost) == "Copy link of post 101", "The link buttons lack their automation names.");
            Require(openPost.Content as string == "Open post in browser" && copyPost.Content as string == "Copy link", "The link buttons lack their labels.");
            Click(openPost);
            await Program.WaitUntilAsync(() => h.Links.Opened.Count == 1, () => "open not requested");
            Require(h.Links.Opened[0] == "https://danbooru.donmai.us/posts/101", "The browser got another URL: " + h.Links.Opened[0]);
            Click(copyPost);
            await Program.WaitUntilAsync(() => h.Links.Copied.Count == 1 && Text(page).Contains("Link copied: https://danbooru.donmai.us/posts/101"),
                () => "copy note missing");
            Require(h.Links.Copied[0] == "https://danbooru.donmai.us/posts/101", "The clipboard got another text: " + h.Links.Copied[0]);
            Require(h.Search.Requests == 1 && h.Queue.Loads.Count == 0, "A link action caused a provider or queue request.");
            Capture(owner, shots, "search-links-light");

            // ---- I8: a source tag chip adds its tag to the search field, never a request -------------------------------
            Click(chips101[0]); // the field already holds two tags: refused and said, the field is unchanged
            await Program.WaitUntilAsync(() => Text(page).Contains("A search takes at most 2 tags"), () => "full search note missing");
            Require(input.Text == "cat blue_eyes", "A refused chip changed the field: " + input.Text);
            input.Text = string.Empty;
            await Program.WaitUntilAsync(() => page.Model.Search.View.Input == string.Empty, () => "field not cleared");
            Click(chips101[0]);
            Click(Find<Button>(page, BooruWorkbenchView.ResultTagPrefix + "101:cat"));
            await Program.WaitUntilAsync(() => input.Text == "sky cat" && page.Model.Search.View.Input == "sky cat", () => "chips did not fill the field: " + input.Text);
            Click(Find<Button>(page, BooruWorkbenchView.ResultTagPrefix + "101:cat"));
            await Program.WaitUntilAsync(() => Text(page).Contains("cat is already in the search."), () => "duplicate note missing");
            Require(h.Search.Requests == 1, "A tag chip sent a request.");
            Capture(owner, shots, "search-tag-chips-light");
            input.Text = "cat blue_eyes";
            await Program.WaitUntilAsync(() => page.Model.Search.View.Input == "cat blue_eyes", () => "field not restored");
            h.Links.OnOpen = _ => throw new InvalidOperationException("no browser");
            Click(Find<Button>(page, BooruWorkbenchView.OpenPostPrefix + "102"));
            await Program.WaitUntilAsync(() => Text(page).Contains("could not be opened in your browser"), () => "open failure not shown");
            h.Links.OnOpen = null;
            Click(Find<Button>(page, BooruWorkbenchView.OpenPostPrefix + "102"));
            await Program.WaitUntilAsync(() => !Text(page).Contains("could not be opened in your browser") && h.Links.Opened.Count == 3,
                () => "open failure note not cleared");
            // The real clipboard path (no injected copier): the shell's own top-level clipboard receives exactly the canonical link.
            BooruWorkbenchServices plain = new(h.Search, h.Queue, h.Watchers, h.Previews, h.Clock);
            Require(plain.Links.Copy is null && ReferenceEquals(plain.Links, BooruLinkActions.System), "The default link actions are not the app launcher and the shell clipboard.");

            // ---- paging is bounded -----------------------------------------------------------------------------------
            Click(Find<Button>(page, BooruWorkbenchView.NextName));
            await Program.WaitUntilAsync(() => Text(page).Contains("Post #201"), () => "page 2 not shown");
            Require(h.Search.Searches[1] == ("cat blue_eyes", 2), "Next page asked for another query/page.");
            Click(Find<Button>(page, BooruWorkbenchView.PreviousName));
            await Program.WaitUntilAsync(() => Text(page).Contains("Post #101"), () => "page 1 not shown again");

            // ---- load original: one explicit handover ----------------------------------------------------------------
            Click(Find<Button>(page, BooruWorkbenchView.LoadPrefix + "101"));
            await Program.WaitUntilAsync(() => Text(page).Contains("Added to the download queue."), () => "queued state not shown");
            Require(h.Queue.Loads.Count == 1 && h.Queue.Loads[0] == ("101", BooruLoadOrigin.Search), "Load original did not hand over exactly once.");
            Button queuedAction = ResultAction(page, "101");
            Require(queuedAction.Name == BooruWorkbenchView.CancelLoadPrefix + "101" || !queuedAction.IsEnabled,
                "The result action did not enter its queued/cancelled state after loading.");
            await CheckResultAnnouncementsAsync(page);

            // ---- a non-loadable original is explained and disabled ----------------------------------------------------
            h.Search.OnSearch = (query, number, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, number, true,
                BooruUiFixtures.Post("301", BooruOriginalAccess.NotOffered), BooruUiFixtures.Post("302", BooruOriginalAccess.Restricted),
                BooruUiFixtures.Post("303", rating: BooruRating.Explicit, rawRating: "e"))));
            input.Text = "scenery";
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Count == 3 && Text(page).Contains("Post #303"), () => "restricted page not shown");
            Require(!Find<Button>(page, BooruWorkbenchView.LoadPrefix + "301").IsEnabled
                && !Find<Button>(page, BooruWorkbenchView.LoadPrefix + "302").IsEnabled, "A post without an original has an enabled load button.");
            Require(Text(page).Contains("Danbooru offers no original for this post") && Text(page).Contains("Original restricted by Danbooru"),
                "Original availability is not explained.");
            Require(Find<Grid>(page, BooruWorkbenchView.DetailsPrefix + "303").Children.OfType<TextBlock>()
                .Any(block => AutomationProperties.GetName(block)?.StartsWith("Danbooru rating: Explicit", StringComparison.Ordinal) == true), "The provider rating is not shown as provider metadata.");
            Require(h.Queue.Loads.Count == 1, "A search or a disabled button queued something.");

            // ---- states ----------------------------------------------------------------------------------------------------
            await CheckStateAsync(page, h, BooruCallStatus.Offline, "Danbooru could not be reached", null, shots, "state-offline-light");
            DateTimeOffset retry = h.Clock.GetUtcNow() + TimeSpan.FromMinutes(5);
            await CheckStateAsync(page, h, BooruCallStatus.RateLimited, "asked for a pause (HTTP 429)", retry, shots, "state-429-light");
            int requests = h.Search.Requests;
            Require(!Find<Button>(page, BooruWorkbenchView.SubmitName).IsEnabled, "Search stayed enabled during the 429 pause.");
            Require(h.Search.Requests == requests, "A request was made during the pause.");
            h.Clock.Advance(TimeSpan.FromMinutes(6));
            await Program.WaitUntilAsync(() => Find<Button>(page, BooruWorkbenchView.SubmitName).IsEnabled, () => "Search not re-enabled after the pause");
            Require(h.Search.Requests == requests, "The end of the pause started a request by itself.");
            await CheckStateAsync(page, h, BooruCallStatus.Denied, "denied access", null, shots, "state-restricted-light");
            await CheckStateAsync(page, h, BooruCallStatus.Failed, "The request failed", null, shots, "state-error-light");
            h.Search.OnSearch = (query, number, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(BooruUiFixtures.Page(query, number, true)));
            input.Text = "nothing_here";
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Text(page).Contains("No posts found for"), () => "empty state not shown");

            // ---- one-shot by post link --------------------------------------------------------------------------------------
            h.Search.OnPost = null;
            input.Text = "https://danbooru.donmai.us/posts/4242";
            await Program.WaitUntilAsync(() => Find<SelectableTextBlock>(page, BooruWorkbenchView.QueryName).Text!.Contains("#4242"),
                () => "post line missing");
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Count == 1 && Text(page).Contains("Post #4242"), () => "one-shot card missing");
            Require(h.Search.PostReads.Single() == "4242", "The post link was not read as a single post.");
            Require(!Find<Button>(page, BooruWorkbenchView.CreateWatcherName).IsEnabled, "A single post can create a watcher.");
            Click(Find<Button>(page, BooruWorkbenchView.LoadPrefix + "4242"));
            await Program.WaitUntilAsync(() => h.Queue.Loads.Count == 2, () => "one-shot load missing");
            Require(h.Queue.Loads[1] == ("4242", BooruLoadOrigin.OneShot), "One-shot load has the wrong origin.");

            // ---- query -> watcher -> watcher list -------------------------------------------------------------------------------
            h.Serve(pages: 2, perPage: 2);
            h.Search.Searches.Clear();
            ComboBox modeBox = Find<ComboBox>(page, BooruWorkbenchView.ModeName);
            string before = "combo=" + modeBox.SelectedIndex + " model=" + page.Model.Search.View.Mode;
            modeBox.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => page.Model.Search.View.Mode == BooruInputMode.Tags,
                () => "mode switch failed; before: " + before + "; after combo=" + modeBox.SelectedIndex + " model=" + page.Model.Search.View.Mode);
            input.Text = "solo moon";
            await Program.WaitUntilAsync(() => Find<SelectableTextBlock>(page, BooruWorkbenchView.QueryName).Text == "Query sent to Danbooru: solo moon",
                () => "query line after the mode switch: " + Find<SelectableTextBlock>(page, BooruWorkbenchView.QueryName).Text);
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Count == 2, () => "second search not shown: cards=" + Cards(page, "BooruResult:").Count + " status=" + BooruText.SearchStatus(page.Model.Search.View, false) + " model=" + page.Model.Search.View.Status + "/" + page.Model.Search.View.Mode + "/" + page.Model.Search.View.Input);
            Click(Find<Button>(page, BooruWorkbenchView.CreateWatcherName));
            await Program.WaitUntilAsync(() => Text(page).Contains("Watcher created for"), () => "created notice missing");
            Require(h.Watchers.Calls.Contains("create:solo moon"), "The watcher was not created for the exact query.");
            Require(h.Watchers.Checks == 0, "Creating a watcher ran a check.");
            Require(h.Queue.Loads.Count == 2, "Creating a watcher downloaded existing posts.");

            TabControl tabs = Find<TabControl>(page, BooruWorkbenchView.TabsName);
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => Cards(page, "BooruWatcher:").Count == 5, () => "watcher cards=" + Cards(page, "BooruWatcher:").Count);
            string watcherText = Text(page);
            foreach (string expected in new[] { "cat blue_eyes", "landscape", "scenery sky", "ruins", "Active", "Paused", "Cooling down", "Error",
                "Last check:", "Next check:", "Hits: 6 · added to the download queue: 5", "Danbooru paused requests until",
                // The app points the code seam at the booru message catalog, so the code reads as a sentence.
                Texts.T("booru.ui.watcher.error", BooruText.CodeText("http-network")), "none while paused", "Removing a watcher only stops watching" })
                Require(watcherText.Contains(expected), "Watcher list lacks '" + expected + "'.");
            Require(Find<Button>(page, BooruWorkbenchView.CheckPrefix + "w-paused").IsEnabled, "Check now is not available for a paused watcher.");
            Require(!Find<Button>(page, BooruWorkbenchView.CheckPrefix + "w-cooling").IsEnabled, "Check now is enabled during a cooldown.");
            Require(Find<Button>(page, BooruWorkbenchView.ResumePrefix + "w-paused").IsEnabled, "Resume is not offered for a paused watcher.");
            Capture(owner, shots, "watchers-light");

            // ---- watcher card: newest seen post link, only where the watcher knows one --------------------------------------
            Require(Exists(page, BooruWorkbenchView.WatcherOpenPrefix + "w-active") && Exists(page, BooruWorkbenchView.WatcherCopyPrefix + "w-active"),
                "The watcher with a newest post lacks the link buttons.");
            Require(!Exists(page, BooruWorkbenchView.WatcherOpenPrefix + "w-paused"), "A watcher without a newest post offers a link.");
            Require(watcherText.Contains("Newest post seen: #9001"), "The newest post is not named.");
            Click(Find<Button>(page, BooruWorkbenchView.WatcherOpenPrefix + "w-active"));
            await Program.WaitUntilAsync(() => h.Links.Opened.Last() == "https://danbooru.donmai.us/posts/9001", () => "watcher open missing");
            Click(Find<Button>(page, BooruWorkbenchView.WatcherCopyPrefix + "w-active"));
            await Program.WaitUntilAsync(() => h.Links.Copied.Last() == "https://danbooru.donmai.us/posts/9001", () => "watcher copy missing");
            Capture(owner, shots, "watchers-links-light");

            // ---- notification after an automatic check that queued originals ----------------------------------------------------
            Require(Toasts(owner).Count == 0, "A notification was visible before any check.");
            h.Watchers.RaiseCheckQueued(new BooruWatcherCheckNotice("w-active", "cat blue_eyes", 3));
            await Program.WaitUntilAsync(() => Toasts(owner).Count == 1, () => "no notification for the queued originals");
            FeedbackToastContent toast = Toasts(owner)[0];
            Require(toast.Title == "Danbooru watcher" && toast.Message == "Watcher “cat blue_eyes” found new posts. Originals added to the download queue: 3.",
                "Unexpected notification text: " + toast.Title + " / " + toast.Message);
            Capture(owner, shots, "watcher-notification-light");
            foreach (NotificationCard card in owner.GetVisualDescendants().OfType<NotificationCard>().ToArray()) card.Close();
            await Program.WaitUntilAsync(() => Toasts(owner).Count == 0, () => "notification not closed");
            h.Watchers.RaiseCheckQueued(new BooruWatcherCheckNotice("w-active", "cat blue_eyes", 0));
            await Task.Delay(200);
            Require(Toasts(owner).Count == 0, "A check that queued nothing was announced.");

            // a paused watcher: one explicit check, it stays paused
            int pausedChecks = h.Watchers.Checks;
            Click(Find<Button>(page, BooruWorkbenchView.CheckPrefix + "w-paused"));
            string expectedPausedCheck = Texts.T("booru.ui.watcher.msg.checkedPaused", 2, 2);
            await Program.WaitUntilAsync(() => h.Watchers.Checks == pausedChecks + 1 && Text(page).Contains(expectedPausedCheck),
                () => "one-time check result missing: " + Text(page));
            Require(Exists(page, BooruWorkbenchView.ResumePrefix + "w-paused") && !Exists(page, BooruWorkbenchView.PausePrefix + "w-paused"),
                "The paused watcher did not stay paused after the one-time check.");
            Require(h.Watchers.Calls.Count(call => call == "check:w-paused") == 1 && !h.Watchers.Calls.Contains("resume:w-paused"),
                "The one-time check was not exactly one request without a resume.");

            // check now / pause / resume
            int checks = h.Watchers.Checks;
            Click(Find<Button>(page, BooruWorkbenchView.CheckPrefix + "w-active"));
            string expectedCheck = Texts.T("booru.ui.watcher.msg.checked", 2, 2);
            await Program.WaitUntilAsync(() => h.Watchers.Checks == checks + 1 && Text(page).Contains(expectedCheck),
                () => "check result missing: " + Text(page));
            await Program.WaitUntilAsync(() => Find<TextBlock>(page, BooruWorkbenchView.WatcherNoticeName).Text?.StartsWith("cat blue_eyes: " + expectedCheck, StringComparison.Ordinal) == true
                    && Find<TextBlock>(page, BooruWorkbenchView.WatcherNoticeName).IsEffectivelyVisible,
                () => "watcher notice line does not repeat the check result: " + Find<TextBlock>(page, BooruWorkbenchView.WatcherNoticeName).Text);
            Require(AutomationProperties.GetLiveSetting(Find<TextBlock>(page, BooruWorkbenchView.WatcherNoticeName)) == AutomationLiveSetting.Polite,
                "The watcher notice line is not a polite live region.");
            RequireSilentCards(page, "BooruWatcher:");
            Click(Find<Button>(page, BooruWorkbenchView.PausePrefix + "w-active"));
            await Program.WaitUntilAsync(() => Exists(page, BooruWorkbenchView.ResumePrefix + "w-active")
                && !Exists(page, BooruWorkbenchView.PausePrefix + "w-active"), () => "pause not reflected");
            Require(h.Watchers.Checks == checks + 1, "Pausing ran a check.");
            Click(Find<Button>(page, BooruWorkbenchView.ResumePrefix + "w-active"));
            await Program.WaitUntilAsync(() => Exists(page, BooruWorkbenchView.PausePrefix + "w-active")
                && Find<Button>(page, BooruWorkbenchView.CheckPrefix + "w-active").IsEnabled, () => "resume not reflected");
            Require(h.Watchers.Checks == checks + 1, "Resuming ran a check.");

            // removal: keyboard focus on the safe answer, Escape answers the question, focus kept after removal
            await CheckRemovalKeyboardAsync(page, h);

            // removal: inline confirmation, notice about local media, nothing deleted
            Click(Find<Button>(page, BooruWorkbenchView.RemovePrefix + "w-error"));
            await Program.WaitUntilAsync(() => Text(page).Contains("Downloaded files, source tags and your own tags are not deleted."),
                () => "removal confirmation missing");
            Require(!h.Watchers.Calls.Contains("remove:w-error"), "Opening the confirmation removed the watcher.");
            Click(Find<Button>(page, BooruWorkbenchView.RemoveCancelPrefix + "w-error"));
            await Program.WaitUntilAsync(() => !Text(page).Contains("Remove the watcher for"), () => "confirmation not closed");
            Require(!h.Watchers.Calls.Contains("remove:w-error"), "Cancelling removed the watcher.");
            Click(Find<Button>(page, BooruWorkbenchView.RemovePrefix + "w-error"));
            await Program.WaitUntilAsync(() => Exists(page, BooruWorkbenchView.RemoveConfirmPrefix + "w-error"), () => "confirm button missing");
            Click(Find<Button>(page, BooruWorkbenchView.RemoveConfirmPrefix + "w-error"));
            await Program.WaitUntilAsync(() => Cards(page, "BooruWatcher:").Count == 4 && Text(page).Contains("Watcher removed. Downloaded files and tags were kept."),
                () => "removal result missing");
            await Program.WaitUntilAsync(() => Focused(page) is Button focusedAfter && Cards(page, "BooruWatcher:").Any(card => card.IsVisualAncestorOf(focusedAfter)),
                () => "focus after removal: " + Describe(Focused(page)));
            Require(h.Queue.Loads.Count == 2, "Removing a watcher touched the download side.");
            int callsAfter = h.Watchers.Calls.Count;
            await Task.Delay(300);
            Require(h.Watchers.Calls.Count == callsAfter, "The watcher port received a hidden call after the commands.");

            // ---- language and theme --------------------------------------------------------------------------------------------
            tabs.SelectedIndex = 0;
            foreach (string language in new[] { "de", "es", "fr", "ru", "en" })
            {
                Texts.SetLanguage(language);
                string expectedTitle = Texts.T("booru.ui.watcher.create");
                await Program.WaitUntilAsync(() => Find<Button>(page, BooruWorkbenchView.CreateWatcherName).Content as string == expectedTitle,
                    () => "language " + language + " not applied");
                Require(!Text(page).Contains("booru.ui."), "A raw key is visible in " + language + ".");
            }
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            await Program.WaitUntilAsync(() => page.ActualThemeVariant == ThemeVariant.Dark, () => "dark theme not applied");
            h.Serve(pages: 3, perPage: 3);
            input.Text = "cat blue_eyes";
            Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
            await Program.WaitUntilAsync(() => Cards(page, "BooruResult:").Count == 3 && Previews(page) == 3, () => "dark search not shown");
            Capture(owner, shots, "search-dark");
            tabs.SelectedIndex = 1;
            await Program.WaitUntilAsync(() => Cards(page, "BooruWatcher:").Count == 4, () => "dark watchers not shown");
            Capture(owner, shots, "watchers-dark");

            // ---- sdk-mismatch repair route: mismatch → review → confirm → restart notice (BooruWorkbenchChecks.Repair) ---------
            tabs.SelectedIndex = 0;
            await CheckRepairRouteAsync(owner, page, h, shots);

            // ---- the service-composition boundary ends the page lifetime (navigation does not) ------------------------------
            Click(owner.FindControl<Button>("DownloadsNavigationButton")!);
            await Program.WaitUntilAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Downloads,
                () => "Downloads navigation was refused");
            Require(ReferenceEquals(owner.BooruPageForTests, page), "Navigation disposed the same-service page.");
            owner.SessionHost.BooruServices = overrideBefore;
            Reconcile.Invoke(owner, null);
            await Program.WaitUntilAsync(() => !ReferenceEquals(owner.BooruPageForTests, page)
                && !ReferenceEquals(pageHost.Content, page), () => "the retired page is still hosted");
            Require(!page.TryClearQuery() && !page.Model.Search.CanSubmit,
                "The retired page still accepts changes after shell disposal.");
            page = null;
            await Task.Delay(100);
            int total = h.ProviderRequests;
            h.Clock.Advance(TimeSpan.FromHours(3));
            await Task.Delay(300);
            Require(h.ProviderRequests == total, "A request happened after the page lifetime ended.");
            Console.WriteLine("SMOKE booru workbench accessibility: invalid input explained under the field and on the disabled Search; load and watcher results repeated once in the persistent polite notice lines, rebuilt cards silent and unnamed; URL line keeps its text; removal question focuses Cancel first, Escape cancels it (shell and page stay), focus returns to Remove and stays in the list after removal.");
            Console.WriteLine("SMOKE booru workbench: menu hidden without ports and shown with fakes; no request on open; exact query visible; 3 result cards with previews, provider rating and original availability; bounded paging; one explicit load; offline, 429 (disabled and not retried), restricted, error and empty states; post link one-shot; canonical post link open/copy on result and watcher cards with failure note; watcher notification for queued originals (none for zero); watcher create/check/pause/resume/remove with confirmation and local-media notice; EN/DE/ES/FR/RU live; light and dark shell screenshots in " + shots + "; navigation retains the page; service replacement disposes it without later requests.");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            await SmokeCleanup.RunAsync(primaryFailure, async () =>
            {
                try
                {
                    // Navigation alone intentionally retains a page. Reconcile the restored raw
                    // override so the shell, not the fixture, disposes the fake model/view.
                    try { Click(owner.FindControl<Button>("DownloadsNavigationButton")!); }
                    finally
                    {
                        owner.SessionHost.BooruServices = overrideBefore;
                        Reconcile.Invoke(owner, null);
                        Require(page == null || !ReferenceEquals(owner.BooruPageForTests, page),
                            "The shell retained the fixture's page during cleanup.");
                        await owner.SessionHost.SaveViewPreferencesAsync(config =>
                        {
                            config.WorkspaceMode = modeBefore;
                            config.ShowBooruTools = showBefore;
                        });
                        Reconcile.Invoke(owner, null);
                    }
                }
                finally
                {
                    Application.Current!.RequestedThemeVariant = originalTheme;
                    Texts.SetLanguage(originalLanguage);
                    await SettleAsync(owner);
                }
            }, failure => Console.Error.WriteLine("Booru workbench cleanup: " + failure));
        }
    }

    private static Button ResultAction(Control root, string postId) => root.GetVisualDescendants().OfType<Button>()
        .SingleOrDefault(button => button.Name == BooruWorkbenchView.LoadPrefix + postId
            || button.Name == BooruWorkbenchView.CancelLoadPrefix + postId
            || button.Name == BooruWorkbenchView.RetryLoadPrefix + postId)
        ?? throw new InvalidOperationException("Result action not found: " + postId);

    private static async Task CheckMenuHiddenWithoutPortsAsync(MainWindow owner, MenuItem menu)
    {
        if (owner.SessionHost.BooruComposition is not null)
        {
            // Danbooru is delivered and enabled in this app: the composed ports are real, so the entry is visible.
            // The hidden-entry case is covered by the booru-composition scenario (plugin disabled).
            Console.WriteLine("SMOKE booru workbench: Danbooru is composed in this app; hidden-entry check left to booru-composition.");
            return;
        }
        Require(owner.SessionHost.BooruServices is null, "Ports are connected in the plain app.");
        MenuItem entry = Entry(menu);
        menu.IsSubMenuOpen = true;
        await SettleAsync();
        Require(!entry.IsVisible, "The Danbooru entry is visible although no ports are connected.");
        menu.IsSubMenuOpen = false;
        await SettleAsync();
    }

    // The entry keeps the header it was created with when the language changes afterwards (observed in the dialog gallery: the
    // German cells still show the English text), so the English catalog label counts as well as the current one. The smoke itself runs in English.
    private static MenuItem Entry(MenuItem menu) => menu.Items.OfType<MenuItem>()
        .FirstOrDefault(item => item.Header as string == CommandLabel()
            || item.Header as string == MeowChan.Desktop.Commands.CommandCatalog.Get("booru.open").LabelEn)
        ?? throw new InvalidOperationException("The Danbooru menu entry does not exist.");

    private static string CommandLabel() => MeowChan.Desktop.Commands.CommandCatalog.Label("booru.open");

    // BooruServices' test override does not publish BooruChanged. Use the normal shell
    // reconciliation callback after swapping it; never mutate navigation/restore/busy guards.
    private static readonly MethodInfo Reconcile = typeof(MainWindow).GetMethod("OnBooruChanged", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The Booru shell composition callback is missing.");
    private static readonly FieldInfo ServicesOverride = typeof(DesktopSessionHost).GetField("_booruServicesOverride", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("The Booru service override seam is missing.");

    private static async Task<BooruPage> OpenPageAsync(MainWindow owner, MenuItem menu)
    {
        IClassicDesktopStyleApplicationLifetime lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        Window[] windowsBefore = lifetime.Windows.ToArray();
        MenuItem entry = Entry(menu);
        menu.IsSubMenuOpen = true;
        await SettleAsync(owner);
        Require(entry.IsVisible && entry.IsEnabled && entry.Command?.CanExecute(null) == true,
            "The Danbooru entry is not available although ports are connected.");
        menu.IsSubMenuOpen = false;
        entry.Command!.Execute(null);
        await Program.WaitUntilAsync(() => owner.SelectedPage == MainWindow.WorkspacePage.Booru
            && owner.BooruPageForTests is { IsEffectivelyVisible: true } candidate
            && ReferenceEquals(owner.FindControl<ContentControl>("BooruPageHost")!.Content, candidate),
            () => "the shell Booru page was not shown");
        BooruPage page = owner.BooruPageForTests!;
        await SettleAsync(owner);
        await Program.WaitUntilAsync(() => ReferenceEquals(TopLevel.GetTopLevel(page.View), owner)
            && page.View.GetVisualDescendants().OfType<TextBox>().Any(input => input.Name == BooruWorkbenchView.InputName
                && input.IsEffectivelyVisible), () => "the hosted Booru view/search tab did not materialize");
        Require(ReferenceEquals(page.Content, page.View) && ReferenceEquals(page.Services, owner.SessionHost.BooruServices)
            && page.View.CurrentWorkView == WorkView.Advanced, "The shell hosted another view/service bundle or work view.");
        entry.Command.Execute(null);
        await SettleAsync(owner);
        Require(ReferenceEquals(owner.BooruPageForTests, page)
            && ReferenceEquals(owner.FindControl<ContentControl>("BooruPageHost")!.Content, page),
            "Repeated navigation replaced the same-service page.");
        Require(lifetime.Windows.Count == windowsBefore.Length && windowsBefore.All(window => lifetime.Windows.Contains(window)),
            "The page command opened or closed a top-level window.");
        return page;
    }

    private static async Task CheckStateAsync(BooruPage page, BooruUiHarness h, BooruCallStatus status, string expected,
        DateTimeOffset? retryAt, string shots, string shot)
    {
        TextBox input = Find<TextBox>(page, BooruWorkbenchView.InputName);
        h.Search.OnSearch = (_, _, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Of(status, "x", retryAt, status == BooruCallStatus.RateLimited ? 429 : null));
        input.Text = "state " + shot.Length;
        Click(Find<Button>(page, BooruWorkbenchView.SubmitName));
        await Program.WaitUntilAsync(() => Text(page).Contains(expected), () => "state '" + expected + "' not shown: " + BooruText.SearchStatus(page.Model.Search.View, false));
        Require(Cards(page, "BooruResult:").Count == 0, "Result cards stayed visible in the " + status + " state.");
        Capture(Shell(page), shots, shot);
    }

    // ---- accessibility (1.1.0 audit H1-H3, M2, M3) ---------------------------------------------------------------------------

    private static async Task CheckSearchRowResizeAsync(MainWindow owner, BooruPage page, TextBox input,
        BooruUiHarness h, string shots)
    {
        double width = owner.Width, height = owner.Height;
        WindowState state = owner.WindowState;
        string language = Texts.Language;
        string? text = input.Text;
        int requests = h.ProviderRequests, loads = h.Queue.Loads.Count, watcherCalls = h.Watchers.Calls.Count;
        ComboBox mode = Find<ComboBox>(page, BooruWorkbenchView.ModeName);
        StackPanel actions = Find<StackPanel>(page, "BooruSearchActions");
        Button submit = Find<Button>(page, BooruWorkbenchView.SubmitName);
        Button create = Find<Button>(page, BooruWorkbenchView.CreateWatcherName);
        Grid row = input.Parent as Grid ?? throw new InvalidOperationException("Search field has no realized input row.");
        try
        {
            owner.WindowState = WindowState.Normal;
            input.Text = "cat blue_eyes";
            foreach (string nextLanguage in new[] { "en", "de" })
            {
                Texts.SetLanguage(nextLanguage);
                await Program.WaitUntilAsync(() => create.Content as string == Texts.T("booru.ui.watcher.create")
                    && submit.IsEffectivelyEnabled && create.IsEffectivelyEnabled,
                    () => "Search resize fixture did not apply language/query: " + nextLanguage);
                double firstWide = 0, widest = 0, minimum = 0;
                foreach (int requestedWidth in new[] { 1800, 1920, 760, 1920 })
                {
                    owner.Width = requestedWidth;
                    await SettleAsync(owner);
                    await Program.WaitUntilAsync(() => owner.WindowState == WindowState.Normal
                        && Math.Abs(owner.ClientSize.Width - requestedWidth) <= 1
                        && Math.Abs(owner.Bounds.Width - owner.ClientSize.Width) <= 1,
                        () => $"Search resize {nextLanguage}/{requestedWidth}: native client={owner.ClientSize}, bounds={owner.Bounds}");
                    await SettleAsync(owner);
                    Rect modeBounds = SearchLayoutBounds(mode, owner);
                    Rect inputBounds = SearchLayoutBounds(input, owner);
                    Rect actionBounds = SearchLayoutBounds(actions, owner);
                    Rect rowBounds = SearchLayoutBounds(row, owner);
                    string measured = $"{nextLanguage}/{requestedWidth}: client={owner.ClientSize}, row={rowBounds}, mode={modeBounds}, input={inputBounds}, actions={actionBounds}";
                    Require(ReferenceEquals(TopLevel.GetTopLevel(input), owner) && input.IsEffectivelyVisible
                        && mode.IsEffectivelyVisible && actions.IsEffectivelyVisible && inputBounds.Width > 0,
                        "Search controls are not realized in the native shell: " + measured);
                    Require(Math.Abs(inputBounds.Left - modeBounds.Right - 8) <= 1
                        && Math.Abs(actionBounds.Left - inputBounds.Right - 8) <= 1
                        && Math.Abs(inputBounds.Width - (actionBounds.Left - modeBounds.Right - 16)) <= 1
                        && Math.Abs(modeBounds.Left - rowBounds.Left) <= 1
                        && Math.Abs(actionBounds.Right - rowBounds.Right) <= 1,
                        "Search field does not fill the space between mode and actions with two 8-DIP gaps: " + measured);
                    Require(rowBounds.Left >= -1 && rowBounds.Right <= owner.ClientSize.Width + 1,
                        "Search row overflows the native viewport: " + measured);
                    foreach (Button action in new[] { submit, create })
                    {
                        Rect bounds = SearchLayoutBounds(action, owner);
                        Require(action.IsEffectivelyVisible && action.IsEffectivelyEnabled && action.Focusable && action.IsTabStop
                            && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= actionBounds.Left - 1
                            && bounds.Right <= actionBounds.Right + 1 && bounds.Left >= 0
                            && bounds.Right <= owner.ClientSize.Width + 1 && bounds.Top >= 0
                            && bounds.Bottom <= owner.ClientSize.Height + 1,
                            "Search action is clipped or unreachable: " + action.Name + " " + bounds + "; " + measured);
                        action.Focus(NavigationMethod.Tab);
                        await Program.WaitUntilAsync(() => ReferenceEquals(Focused(page), action),
                            () => "Search action cannot receive keyboard focus: " + action.Name);
                    }
                    if (requestedWidth > 900)
                        Require(inputBounds.Width > 900, "Wide search field is still capped at 900 DIP: " + measured);
                    if (requestedWidth == 1800) firstWide = inputBounds.Width;
                    else if (requestedWidth == 760) minimum = inputBounds.Width;
                    else if (widest == 0)
                    {
                        widest = inputBounds.Width;
                        Require(Math.Abs(widest - firstWide - 120) <= 2,
                            "Search field did not absorb the 1800-to-1920 resize: " + measured);
                    }
                    else
                        Require(minimum < firstWide && Math.Abs(inputBounds.Width - widest) <= 1,
                            "Search field did not shrink then recover its wide layout: " + measured);
                    Require(h.ProviderRequests == requests && h.Queue.Loads.Count == loads && h.Watchers.Calls.Count == watcherCalls,
                        "Search resize/language/focus caused a provider, queue or watcher call: " + measured);
                    Capture(owner, shots, $"search-layout-{nextLanguage}-{requestedWidth}" + (minimum > 0 && requestedWidth == 1920 ? "-restored" : ""));
                }
            }
            Console.WriteLine("SMOKE booru search layout: realized native EN/DE row at 1800/1920/760/1920 DIP; field exceeds 900 DIP, fills remaining space with 8-DIP gaps, grows/shrinks/recovers; Search and Create watcher visible and keyboard reachable; no port calls.");
        }
        finally
        {
            input.Text = text;
            Texts.SetLanguage(language);
            owner.Width = width;
            owner.Height = height;
            owner.WindowState = state;
            await SettleAsync(owner);
            input.Focus(NavigationMethod.Tab);
        }
    }

    private static Rect SearchLayoutBounds(Control control, MainWindow owner)
    {
        Point origin = control.TranslatePoint(default, owner)
            ?? throw new InvalidOperationException("Search control is not attached to the native shell: " + control.Name);
        return new Rect(origin, control.Bounds.Size);
    }

    private static async Task CheckInvalidInputAsync(BooruPage page, TextBox input)
    {
        TextBlock issue = Find<TextBlock>(page, BooruWorkbenchView.InputIssueName);
        Button submit = Find<Button>(page, BooruWorkbenchView.SubmitName);
        Require(!issue.IsVisible, "An input issue is visible for an empty field.");
        input.Text = "cat blue_eyes red_hair";
        await Program.WaitUntilAsync(() => page.Model.Search.View.Parsed.Kind == BooruInputKind.Invalid && issue.IsEffectivelyVisible,
            () => "invalid input not explained under the field: " + issue.Text);
        string expected = BooruText.Issue(page.Model.Search.View.Parsed.Issue);
        Require(issue.Text == expected && issue.Classes.Contains("workspace-error"), "The visible input issue is wrong: " + issue.Text);
        Require(AutomationProperties.GetLiveSetting(issue) == AutomationLiveSetting.Polite, "The input issue is not a polite live region.");
        Require(!submit.IsEnabled && AutomationProperties.GetHelpText(submit) == expected && ToolTip.GetTip(submit) as string == expected
            && ToolTip.GetShowOnDisabled(submit), "The disabled Search does not name its reason.");
        Require(AutomationProperties.GetHelpText(input) == expected, "The field lost its issue help text.");
        input.Text = string.Empty;
        await Program.WaitUntilAsync(() => !issue.IsVisible && string.IsNullOrEmpty(AutomationProperties.GetHelpText(submit)),
            () => "the input issue stayed after clearing the field");
    }

    private static async Task CheckResultAnnouncementsAsync(BooruPage page)
    {
        TextBlock notice = Find<TextBlock>(page, BooruWorkbenchView.NoticeName);
        await Program.WaitUntilAsync(() => notice.Text == "Post #101: Added to the download queue." && notice.IsEffectivelyVisible,
            () => "the search notice line does not repeat the load result: " + notice.Text);
        Require(AutomationProperties.GetLiveSetting(notice) == AutomationLiveSetting.Polite, "The search notice line is not a polite live region.");
        RequireSilentCards(page, "BooruResult:");
        Control card = Cards(page, "BooruResult:").First(control => control.Name == "BooruResult:101");
        Require(card.GetVisualDescendants().OfType<Control>().Count(control => AutomationProperties.GetName(control) == "Post #101") == 0
            && card.GetVisualDescendants().OfType<TextBlock>().Count(text => text.Text == "Post #101") == 1,
            "\"Post #101\" is named more than once in its card.");
        // I8: the address is not a text line any more; it is the tooltip of the copy button, and the open button names the post.
        Require(!card.GetVisualDescendants().OfType<SelectableTextBlock>().Any(text => text.Text == "https://danbooru.donmai.us/posts/101"),
            "The post address is still a text line in its card.");
        Button copy = card.GetVisualDescendants().OfType<Button>().Single(button => button.Name == BooruWorkbenchView.CopyLinkPrefix + "101");
        Require(ToolTip.GetTip(copy) as string == "https://danbooru.donmai.us/posts/101" && !string.IsNullOrEmpty(AutomationProperties.GetName(copy)),
            "The copy button lacks the address as its tooltip or a name.");
    }

    private static void RequireSilentCards(Control root, string prefix)
    {
        // Rebuilt card lines never announce (their peers are new); the persistent notice lines do it once instead.
        foreach (Control card in Cards(root, prefix))
        {
            // A card that is a keyboard stop (hit and watcher cards) carries the short name the focus reads; the others stay unnamed.
            Require(card.Focusable != string.IsNullOrEmpty(AutomationProperties.GetName(card)),
                "Card " + card.Name + (card.Focusable ? " has no name for its keyboard stop." : " repeats its title as a name."));
            Require(card.GetVisualDescendants().OfType<Control>().All(control => AutomationProperties.GetLiveSetting(control) == AutomationLiveSetting.Off),
                "Card " + card.Name + " carries its own live region.");
        }
    }

    private static async Task CheckRemovalKeyboardAsync(BooruPage page, BooruUiHarness h)
    {
        Button remove = Find<Button>(page, BooruWorkbenchView.RemovePrefix + "w-error");
        remove.Focus(NavigationMethod.Tab);
        await Program.WaitUntilAsync(() => ReferenceEquals(Focused(page), remove), () => "Remove not focused: " + Describe(Focused(page)));
        Click(remove);
        await Program.WaitUntilAsync(() => Focused(page) is Button { Name: { } name } && name == BooruWorkbenchView.RemoveCancelPrefix + "w-error",
            () => "the removal question did not focus Cancel: " + Describe(Focused(page)));
        Button cancel = Find<Button>(page, BooruWorkbenchView.RemoveCancelPrefix + "w-error");
        Button confirm = Find<Button>(page, BooruWorkbenchView.RemoveConfirmPrefix + "w-error");
        Require(cancel.Parent is Panel answers && answers.Children.IndexOf(cancel) < answers.Children.IndexOf(confirm),
            "The destructive answer comes before Cancel.");
        cancel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        await Program.WaitUntilAsync(() => !Exists(page, BooruWorkbenchView.RemoveConfirmPrefix + "w-error")
                && Focused(page) is Button { Name: { } back } && back == BooruWorkbenchView.RemovePrefix + "w-error",
            () => "Escape did not answer the question with Cancel: " + Describe(Focused(page)));
        Require(Shell(page).IsVisible && page.IsEffectivelyVisible && !h.Watchers.Calls.Contains("remove:w-error"), "Escape closed the shell/page or removed the watcher.");
    }

    private static object? Focused(BooruPage page) => Shell(page).FocusManager?.GetFocusedElement();

    private static MainWindow Shell(BooruPage page) => TopLevel.GetTopLevel(page.View) as MainWindow
        ?? throw new InvalidOperationException("The Booru view has no owning shell.");

    private static string Describe(object? focused) => focused is Control { Name: { } name } ? focused.GetType().Name + ":" + name : focused?.GetType().Name ?? "nothing";

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    // The programmatic workbench names are not registered in the shell's XAML namescope.
    // Only inspect the current view's visual tree; inactive TabItem.Content is not a visible surface.
    private static IEnumerable<Control> Controls(Control root) =>
        (root is BooruPage page ? page.View : root).GetVisualDescendants().OfType<Control>();

    private static T Find<T>(Control root, string name) where T : Control =>
        Controls(root).OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException("Control not found: " + name);

    private static List<FeedbackToastContent> Toasts(Window window) => window.GetVisualDescendants().OfType<NotificationCard>()
        .Where(card => !card.IsClosing && !card.IsClosed).Select(card => card.Content).OfType<FeedbackToastContent>().ToList();

    private static bool Exists(Control root, string name) => Controls(root).Any(control => control.Name == name);

    private static List<Control> Cards(Control root, string prefix) => Controls(root).OfType<Border>()
        .Where(border => border.Name is { } name && name.StartsWith(prefix, StringComparison.Ordinal)).Cast<Control>().ToList();

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
